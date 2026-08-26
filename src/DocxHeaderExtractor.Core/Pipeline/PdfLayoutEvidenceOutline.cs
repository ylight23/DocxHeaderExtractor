using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Vision;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Key-guided diagnostic only; never participates in PDF candidate selection.</summary>
public sealed record PdfCandidateRetrievalTrace(
    string ExpectedText,
    bool FoundInRawWindow,
    IReadOnlyList<string> RawFilterReasons,
    bool FoundInStandardBlock,
    bool FoundInBroadCandidate,
    bool FoundInWideCandidate,
    bool FoundInSupplementCandidate,
    string FirstLossStage,
    string? RawWindowText,
    bool FoundExactSourceLine = false);

/// <summary>Key-guided observability of source facts through grouping and candidate producers.
/// It is diagnostic only and is never consumed by extraction or ranking.</summary>
public sealed record PdfCandidateConstructionTrace(
    string ExpectedText,
    IReadOnlyList<PdfCandidateConstructionSourceLine> SourceLines,
    IReadOnlyList<string> PreGroupCandidateIds,
    IReadOnlyList<string> PostGroupCandidateIds,
    IReadOnlyList<PdfCandidateConstructionBlock> PostGroupBlocks,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ProducerCandidateIds,
    IReadOnlyDictionary<string, string> ProducerDecisions,
    string GroupOperation,
    string FirstLoss);

public sealed record PdfCandidateConstructionSourceLine(string SourceId, int Page, string Text,
    string FilterReason, bool ExcludedFromSemanticSamples, bool ExcludedFromCandidateGrouping,
    string? MatchText);

public sealed record PdfCandidateConstructionBlock(string Id, int Page, int LineCount, string Text,
    string CanonicalText, bool HasKerningJoinEvidence,
    IReadOnlyList<PdfCandidateConstructionBlockLine> Lines);

/// <summary>Raw PDF line facts inside a grouped block, exposed only by the diagnostic trace.</summary>
public sealed record PdfCandidateConstructionBlockLine(
    string Text,
    string? MatchText,
    double FontSize,
    double BoldRatio,
    string FontName,
    string FillColor,
    double Left,
    double Y);

/// <summary>Audit-only result of the semantic recovery branch; it cannot write an outline.</summary>
public sealed record PdfSemanticRecoveryAudit(
    string Status,
    int RepresentedBlocks,
    int DeterministicCandidateBlocks,
    int EligibleUnresolvedBlocks,
    int HeadingRoleProposals,
    int CanonicalUniqueProposals,
    int ValidatorAccepted,
    IReadOnlyList<string> RoleResponses,
    IReadOnlyList<string> PointerResponses,
    IReadOnlyList<PdfSemanticRecoveryDecisionAudit> Decisions)
{
    public string Profile { get; init; } = "current_v6";
    public string EligibleIdsSha256 { get; init; } = "";
    public IReadOnlyList<string> RoleRequestSha256 { get; init; } = [];
}

public sealed record PdfSemanticRecoveryDecisionAudit(
    string Id,
    string SourceBlockId,
    int SourceLineIndex,
    int Page,
    string SourceText,
    string Role,
    double Confidence,
    int? SpanStart,
    int? SpanEnd,
    string? CanonicalSpan,
    bool CanonicalUnique,
    string ValidationStatus,
    string? Reason)
{
    public string ContextSha256 { get; init; } = "";
}

/// <summary>
/// Language-neutral PDF navigation-outline extractor. It learns the body baseline and visual
/// outliers from the current PDF, removes repeated/table-like lines, groups nearby lines into
/// blocks, and grounds accepted blocks back to the DOCX source. It intentionally abstains when
/// visual candidates are dense: a deep content/table index is not a navigation outline.
/// </summary>
public static class PdfLayoutEvidenceOutline
{
    // These are deliberately not deterministic-declared `const Basis` values: both routes are
    // experimental until their precision is measured against independent keys.
    public static readonly string Basis = "pdf_layout_evidence";
    public static readonly string AnalystBasis = "pdf_layout_block_grounded";
    private const int MaximumAnalystBlocks = 40;
    private static readonly Regex LooseLabelledMarkerRx = new(
        @"^\s*(\p{L}{2,24})\s+((?:\d\s*){1,3}|[IVXLCDM]{1,7})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LooseLabelledMarkerAnywhereRx = new(
        @"(\p{L}{2,24})\s+((?:\d\s*){1,3}|[IVXLCDM]{1,7})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ClauseStartAfterTitleRx = new(
        @"\s+\d{1,2}\.\s+\p{Lu}",
        RegexOptions.Compiled);

    /// <summary>
    /// Traces a known title through the PDF retrieval pipeline. This is deliberately key-guided
    /// observability, not a key-guided extraction rule: callers use it to discover general losses.
    /// </summary>
    public static IReadOnlyList<PdfCandidateRetrievalTrace> TraceCandidateRetrieval(
        string originalInputPath,
        IEnumerable<string> expectedTitles)
    {
        var expected = expectedTitles.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToArray();
        if (expected.Length == 0) return [];
        var pdf = PdfTextbookOutline.FindSiblingPdf(originalInputPath);
        if (pdf is null) return expected.Select(t => new PdfCandidateRetrievalTrace(
            t, false, [], false, false, false, false, "no-sibling-pdf", null)).ToArray();

        try
        {
            using var document = PdfDocument.Open(pdf);
            var annotations = PdfLineBlockFilter.Analyze(PdfLineExtraction.ExtractLines(document));
            var standard = PdfSemanticBlockGrouper.Build(annotations);
            var profile = PdfStyleClusterProfile.Learn(annotations.Where(a => !a.ExcludeFromSemanticSamples).Select(a => a.Line).ToArray());
            var broad = BuildBroadCandidates(standard, profile);
            var wide = BuildWideAuditCandidates(standard);
            var supplement = BuildSupplementCandidates(annotations, broad);
            return expected.Select(title => TraceTitle(title, annotations, standard, broad, wide, supplement)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return expected.Select(t => new PdfCandidateRetrievalTrace(
                t, false, [], false, false, false, false, "pdf-read-failed", null)).ToArray();
        }
    }

    /// <summary>
    /// Traces the narrow construction boundary for known text. This exposes whether a represented
    /// source fact was dropped or absorbed by semantic grouping, or survived grouping but never
    /// entered a candidate producer. It does not alter any source fact or candidate list.
    /// </summary>
    public static IReadOnlyList<PdfCandidateConstructionTrace> TraceCandidateConstruction(
        string originalInputPath, IEnumerable<string> expectedTitles)
    {
        var expected = expectedTitles.Where(title => !string.IsNullOrWhiteSpace(title)).Distinct(StringComparer.Ordinal).ToArray();
        if (expected.Length == 0) return [];
        var pdf = PdfTextbookOutline.FindSiblingPdf(originalInputPath);
        if (pdf is null) return expected.Select(title => new PdfCandidateConstructionTrace(title, [], [], [],
            [], EmptyProducers(), EmptyProducerDecisions(), "not_available", "no_sibling_pdf")).ToArray();
        try
        {
            using var document = PdfDocument.Open(pdf);
            var annotations = PdfLineBlockFilter.Analyze(PdfLineExtraction.ExtractLines(document));
            var standard = PdfSemanticBlockGrouper.Build(annotations);
            var profile = PdfStyleClusterProfile.Learn(annotations.Where(annotation => !annotation.ExcludeFromSemanticSamples)
                .Select(annotation => annotation.Line).ToArray());
            var broad = BuildBroadCandidates(standard, profile);
            var wide = BuildWideAuditCandidates(standard);
            var supplement = BuildSupplementCandidates(annotations, broad);
            return expected.Select(title => TraceConstructionTitle(title, annotations, standard, profile, broad, wide, supplement)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return expected.Select(title => new PdfCandidateConstructionTrace(title, [], [], [],
                [], EmptyProducers(), EmptyProducerDecisions(), "not_available", "pdf_read_failed")).ToArray();
        }
    }

    /// <summary>
    /// Thin semantic recovery experiment for represented facts that normal deterministic candidate
    /// producers did not admit. It reuses the normal role, pointer-span, and validator contracts;
    /// it is audit-only and never emits a HeadingRecord.
    /// </summary>
    public static async Task<PdfSemanticRecoveryAudit> RunSemanticRecoveryAuditAsync(
        string originalInputPath,
        IHeaderClassifier analyst,
        PdfSemanticRecoveryOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= PdfSemanticRecoveryOptions.CurrentV6;
        var pdf = PdfTextbookOutline.FindSiblingPdf(originalInputPath);
        if (pdf is null)
            return new PdfSemanticRecoveryAudit("no_sibling_pdf", 0, 0, 0, 0, 0, 0, [], [], []);

        try
        {
            using var document = PdfDocument.Open(pdf);
            var annotations = PdfLineBlockFilter.Analyze(PdfLineExtraction.ExtractLines(document));
            var represented = PdfSemanticBlockGrouper.Build(annotations);
            var profile = PdfStyleClusterProfile.Learn(annotations.Where(annotation => !annotation.ExcludeFromSemanticSamples)
                .Select(annotation => annotation.Line).ToArray());
            var broad = BuildBroadCandidates(represented, profile);
            var supplement = BuildSupplementCandidates(annotations, broad);
            var deterministic = MergeCandidateSets(broad, supplement);
            var selection = PdfSemanticRecoverySelector.Select(represented, deterministic, annotations, options);
            if (selection.EligibleBlocks.Count == 0)
                return new PdfSemanticRecoveryAudit("no_eligible_unresolved", selection.RepresentedBlockCount,
                    selection.DeterministicCandidateCount, 0, 0, 0, 0, [], [], []);

            var contexts = selection.EligibleBlocks.ToDictionary(block => block.Id, block => selection.Contexts[block.Id],
                StringComparer.Ordinal);
            var roleLane = SemanticLaneOptions.Default with { MaxBatchSize = options.RoleBatchSize };
            var roles = await PdfBlockAnalyst.AnalyzeAsync(analyst, selection.EligibleBlocks, contexts, ct, roleLane);
            var spans = await PdfBlockAnalyst.ResolveHeadingSpansAsync(analyst, selection.EligibleBlocks,
                roles.Decisions, contexts, ct);
            var traces = PdfProposalValidator.Trace(contexts, spans.Decisions)
                .ToDictionary(trace => trace.Id, StringComparer.Ordinal);
            var accepted = PdfProposalValidator.Validate(contexts, spans.Decisions)
                .Select(heading => heading.SourceId).ToHashSet(StringComparer.Ordinal);
            var decisionById = spans.Decisions.ToDictionary(decision => decision.Id, StringComparer.Ordinal);
            var canonicalLineCounts = represented.SelectMany(block => block.Lines)
                .Select(CanonicalForMatching)
                .Where(canonical => canonical.Length >= 4)
                .GroupBy(canonical => canonical, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var audits = selection.EligibleBlocks.Select(block =>
            {
                decisionById.TryGetValue(block.Id, out var decision);
                var span = decision?.HeadingSpan;
                var spanText = span is not null && span.Start >= 0 && span.End <= block.Text.Length && span.End > span.Start
                    ? block.Text[span.Start..span.End]
                    : null;
                var canonical = spanText is null ? null : PdfTextUtilities.CanonicalForMatch(spanText);
                var unique = canonical is { Length: >= 4 } && canonicalLineCounts.TryGetValue(canonical, out var count) && count == 1;
                var trace = traces.GetValueOrDefault(block.Id);
            var origin = selection.Origins[block.Id];
            return new PdfSemanticRecoveryDecisionAudit(block.Id, origin.SourceBlockId, origin.SourceLineIndex, block.Page, block.Text,
                    decision?.Role.ToString() ?? "Uncertain", decision?.Confidence ?? 0,
                    span?.Start, span?.End, canonical, unique,
                    accepted.Contains(block.Id) ? "accepted" : trace?.ValidationStatus ?? "unresolved",
                    trace?.Reason ?? decision?.Reason)
                { ContextSha256 = Sha256(PdfBlockAnalyst.BuildUserPrompt([block], contexts)) };
            }).ToArray();
        return new PdfSemanticRecoveryAudit("complete", selection.RepresentedBlockCount,
            selection.DeterministicCandidateCount, selection.EligibleBlocks.Count,
            audits.Count(audit => audit.Role == PdfBlockRole.HeadingTopic.ToString()),
            audits.Count(audit => audit.Role == PdfBlockRole.HeadingTopic.ToString() && audit.CanonicalUnique),
            audits.Count(audit => audit.ValidationStatus == "accepted"), roles.RawResponses,
            spans.RawResponses, audits)
        {
            Profile = options.Name,
            EligibleIdsSha256 = Sha256(string.Join("\n", selection.EligibleBlocks.Select(block => block.Id))),
            RoleRequestSha256 = roles.InputContracts.Select(Sha256).ToArray(),
        };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new PdfSemanticRecoveryAudit("pdf_read_failed", 0, 0, 0, 0, 0, 0, [], [], []);
        }
    }

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static PdfTextbookOutlineResult TryBuild(string originalInputPath, SlimDocument slim)
    {
        var context = TryBuildContext(originalInputPath, slim, out var reason);
        if (context is null) return PdfTextbookOutlineResult.NotApplicable(reason);

        var alignment = AlignToDocx(context.Candidates, slim, context.Profile, Basis);
        if (alignment.Headings.Count < Math.Max(3, (int)Math.Ceiling(context.Candidates.Count * 0.65)))
            return PdfTextbookOutlineResult.NotApplicable($"low-docx-alignment:{alignment.Headings.Count}/{context.Candidates.Count}");

        return new PdfTextbookOutlineResult(
            alignment.Headings,
            $"pdf={Path.GetFileName(context.Pdf)}, styles={context.HeadingStyles.Count}, aligned={alignment.Headings.Count}/{context.Candidates.Count}");
    }

    /// <summary>
    /// Slow lane: only already-filtered visual blocks are sent to the language model. The returned
    /// roles are grounded back to the same blocks before DOCX alignment; the model cannot invent a
    /// title or a source span.
    /// </summary>
    public static async Task<PdfTextbookOutlineResult> TryBuildWithAnalystAsync(
        string originalInputPath,
        SlimDocument slim,
        IHeaderClassifier analyst,
        CancellationToken ct = default)
    {
        var context = TryBuildContext(originalInputPath, slim, out var reason);
        if (context is null) return PdfTextbookOutlineResult.NotApplicable(reason);

        var selection = SelectAnalystCandidates(context.Candidates, MaximumAnalystBlocks);
        var candidates = selection.Selected;
        var excluded = context.Annotations.Where(a => a.ExcludeFromSemanticSamples).Select(a => a.Line).ToHashSet();
        var samples = PdfSemanticClusterAnalyst.BuildSamples(context.Profile, context.Lines, excluded);
        var clusters = await PdfSemanticClusterAnalyst.AnalyzeAsync(analyst, context.Profile, context.Lines, ct);
        var candidateContexts = PdfCandidateContextBuilder.Build(candidates, context.Annotations);
        var blockAnalysis = await PdfBlockAnalyst.AnalyzeAsync(analyst, candidates, candidateContexts, ct);
        var eligibleDecisions = blockAnalysis.Decisions.Where(decision =>
            candidateContexts.TryGetValue(decision.Id, out var candidateContext) &&
            PdfProposalValidator.IsEligibleHeading(decision, candidateContext)).ToArray();
        var grounded = PdfBlockGrounder.Ground(candidates, eligibleDecisions, context.Profile, samples, clusters.Decisions);
        var acceptedIds = grounded.Headings.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        var accepted = candidates.Where(b => acceptedIds.Contains(b.Id)).ToArray();
        if (accepted.Length < 3)
            return PdfTextbookOutlineResult.NotApplicable($"analyst-grounded-too-few:{accepted.Length}/{candidates.Count}");

        var alignment = AlignToDocx(accepted, slim, context.Profile, AnalystBasis);
        if (alignment.Headings.Count < Math.Max(3, (int)Math.Ceiling(accepted.Length * 0.65)))
            return PdfTextbookOutlineResult.NotApplicable($"analyst-low-docx-alignment:{alignment.Headings.Count}/{accepted.Length}");

        var summary = $"pdf={Path.GetFileName(context.Pdf)}, candidateBlocks={candidates.Count}/{selection.Available}, " +
                      $"pages={selection.SelectedPages}/{selection.AvailablePages}, grounded={accepted.Length}, aligned={alignment.Headings.Count}/{accepted.Length}";
        return new PdfTextbookOutlineResult(
            alignment.Headings,
            summary,
            new RouteExecutionAudit(
                summary,
                selection.Available,
                candidates.Count,
                selection.AvailablePages,
                selection.SelectedPages,
                context.Candidates.Select(ToAudit).ToArray(),
                candidates.Select(ToAudit).ToArray(),
                context.Candidates.Where(b => !candidates.Any(selected => selected.Id == b.Id)).Select(ToAudit).ToArray(),
                blockAnalysis.Decisions.Select(d => new RouteBlockDecisionAudit(d.Id, d.Role.ToString(), d.Confidence)).ToArray(),
                accepted.Select(b => b.Id).ToArray(),
                grounded.Rejected.Select(r => new RouteBlockRejectionAudit(r.Id, r.Role, r.Confidence, r.Reason)).ToArray(),
                alignment.AlignedBlockIds.ToArray()));
    }

    /// <summary>
    /// Freezes the broad PDF-first candidate pool and returns a lossless, feature-only processing
    /// plan. This is an audit operation: no LLM is invoked and no candidate is discarded.
    /// </summary>
    /// <summary>
    /// Runs the production alignment once and returns it with a passive trace of how each block
    /// found its paragraph. Nothing here re-implements matching.
    /// </summary>
    internal static PdfDocxAlignmentSnapshot BuildDocxAlignmentSnapshot(
        string originalInputPath, SlimDocument slim,
        PdfDocxAlignmentPopulation population = PdfDocxAlignmentPopulation.NarrowRoute)
    {
        var context = population == PdfDocxAlignmentPopulation.NarrowRoute
            ? TryBuildContext(originalInputPath, slim, out var reason)
            : TryBuildBroadAuditContext(originalInputPath, includeAllVisualStyles: true,
                includeSupplementCandidates: true, out reason);
        if (context is null)
            return new PdfDocxAlignmentSnapshot(reason, 0, [], [], [], slim);
        var trace = new List<PdfDocxAlignmentTrace>();
        var haystacks = new List<PdfDocxCanonicalParagraph>();
        var alignment = AlignToDocx(context.Candidates, slim, context.Profile, Basis, trace, haystacks);
        // TryBuild rejects the route below this ratio, so an audit that ignored it could describe an
        // alignment production never uses.
        var status = alignment.Headings.Count < Math.Max(3, (int)Math.Ceiling(context.Candidates.Count * 0.65))
            ? $"low-docx-alignment:{alignment.Headings.Count}/{context.Candidates.Count}"
            : "aligned";
        return new PdfDocxAlignmentSnapshot(status, context.Candidates.Count, alignment.Headings, trace,
            haystacks, slim);
    }

    public static PdfCandidateRankingAudit BuildCandidateRankingAudit(string originalInputPath) =>
        BuildCandidateRankingSnapshot(originalInputPath).Audit;

    /// <summary>
    /// The same ranking computation, with a passive record of which source lines each candidate was
    /// built from.
    /// <para>
    /// Evaluation needs that provenance to ask whether a candidate represents a particular heading
    /// occurrence rather than merely containing its words. Rebuilding the candidate graph on the
    /// evaluation side to obtain it would create a second construction path that can drift from this
    /// one, so the provenance is taken from the blocks this invocation already materialized. Nothing
    /// here participates in ranking or selection; production calls
    /// <see cref="BuildCandidateRankingAudit"/> and never sees these fields.
    /// </para>
    /// </summary>
    internal static PdfCandidateRankingSnapshot BuildCandidateRankingSnapshot(
        string originalInputPath,
        IReadOnlySet<int>? withheldTableLikeLines = null)
    {
        var context = TryBuildBroadAuditContext(originalInputPath, includeAllVisualStyles: true,
            includeSupplementCandidates: true, out var reason, withheldTableLikeLines);
        if (context is null)
            return new PdfCandidateRankingSnapshot(new PdfCandidateRankingAudit(reason, 0, []),
                new Dictionary<string, PdfCandidateProvenance>(StringComparer.Ordinal), [], [], []);
        var contexts = PdfCandidateContextBuilder.Build(context.Candidates, context.Annotations);
        var ranked = PdfCandidateRanker.Rank(context.Candidates, contexts);
        return new PdfCandidateRankingSnapshot(
            new PdfCandidateRankingAudit("ranked", ranked.Count, ranked),
            BuildProvenance(context), context.Candidates, context.Annotations, context.Lines);
    }

    private static IReadOnlyDictionary<string, PdfCandidateProvenance> BuildProvenance(LayoutContext context)
    {
        var indexByLineId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < context.Lines.Count; index++)
            indexByLineId[PdfCandidateProvenance.LineId(context.Lines[index])] = index;

        var provenance = new Dictionary<string, PdfCandidateProvenance>(StringComparer.Ordinal);
        foreach (var block in context.Candidates)
        {
            var lineIds = block.Lines.Select(PdfCandidateProvenance.LineId).ToArray();
            var lineIndexes = lineIds
                .Select(id => indexByLineId.TryGetValue(id, out var index) ? index : -1)
                .Where(index => index >= 0)
                .ToArray();
            // The producer that made the block decides its kind. Reading it back off the id prefix
            // would turn a naming convention into authority, and rename it and the meaning is gone.
            var kind = context.SupplementCandidateRanks.ContainsKey(block.Id)
                ? PdfCandidateRepresentationKind.WindowFragment
                : PdfCandidateRepresentationKind.StandardBlock;
            var candidate = new PdfCandidateProvenance(block.Id, lineIndexes, lineIds, kind);
            if (provenance.TryGetValue(block.Id, out var existing))
            {
                // Two blocks under one id with different lines would let evaluation silently read
                // the provenance of the other representation.
                if (!existing.LineIndexes.SequenceEqual(candidate.LineIndexes))
                    throw new InvalidOperationException(
                        $"ambiguous_candidate_provenance: '{block.Id}' appears with two different source line sets.");
                continue;
            }
            provenance[block.Id] = candidate;
        }
        return provenance;
    }

    /// <summary>
    /// Audit-only PDF-first lane. It deliberately starts from the broader candidate generator used
    /// by <c>pdf-clusters</c>, rather than the sparse-style production experiment above. DOCX is
    /// only used after PDF selection to map an accepted PDF block to a stable writeback span.
    /// This method is intentionally not called by <see cref="HeaderExtractionPipeline"/>.
    /// </summary>
    public static async Task<PdfTextbookOutlineResult> TryBuildBroadAuditWithAnalystAsync(
        string originalInputPath,
        SlimDocument slim,
        IHeaderClassifier analyst,
        int maximumAnalystBlocks = 0,
        bool includeAllVisualStyles = false,
        bool includeSupplementCandidates = false,
        IPdfVisualQuestion? visualAnalyst = null,
        int visualDpi = 120,
        int maximumVisualRegions = 0,
        string? visualProducer = null,
        bool scheduleVisualRegions = false,
        CancellationToken ct = default,
        SemanticLaneOptions? semanticLaneOptions = null,
        string? checkpointPath = null,
        bool resume = false,
        int visualMaxConcurrency = 1,
        bool includeSemanticHierarchyFallback = true)
    {
        if (maximumAnalystBlocks < 0)
            return PdfTextbookOutlineResult.NotApplicable("invalid-analyst-block-budget");

        var context = TryBuildBroadAuditContext(
            originalInputPath, includeAllVisualStyles, includeSupplementCandidates, out var reason);
        if (context is null) return PdfTextbookOutlineResult.NotApplicable(reason);
        var checkpoint = string.IsNullOrWhiteSpace(checkpointPath) ? null : new PdfStageCheckpoint(checkpointPath, resume, Path.GetFileName(context.Pdf));

        // Visual scheduling is source-fact-only: it must not wait for a slow semantic batch.
        // Its own canonical source validator remains the authority before any heading is emitted.
        var visualRecoveryTask = visualAnalyst is null
            ? Task.FromResult(new PdfVisualTextRecoveryResult([], [], [], [], [], []))
            : PdfVisualTextRecovery.RecoverAsync(context.Pdf, context.Lines, slim, [], visualAnalyst,
                visualDpi, maximumVisualRegions, visualProducer, scheduleVisualRegions, ct,
                checkpoint?.CompletedVisualRegions,
                checkpoint is null ? null : (trace, token) => checkpoint.RecordVisualRegionAsync(trace, token),
                checkpoint?.CompletedVisualTraces, visualMaxConcurrency);

        var semanticOptions = (semanticLaneOptions ?? SemanticLaneOptions.Default) with
        {
            DeadlineUtc = DateTimeOffset.UtcNow.Add((semanticLaneOptions ?? SemanticLaneOptions.Default).LaneDeadline),
        };
        var effectiveBudget = maximumAnalystBlocks == 0 ? context.Candidates.Count : maximumAnalystBlocks;
        var allCandidateContexts = PdfCandidateContextBuilder.Build(context.Candidates, context.Annotations);
        var ranked = PdfCandidateRanker.Rank(context.Candidates, allCandidateContexts);
        var selection = SelectRankedCandidates(context.Candidates, ranked, effectiveBudget);
        var selected = selection.Selected;
        var excluded = context.Annotations.Where(a => a.ExcludeFromSemanticSamples).Select(a => a.Line).ToHashSet();
        var candidateContexts = selected.ToDictionary(block => block.Id, block => allCandidateContexts[block.Id], StringComparer.Ordinal);
        var samples = PdfSemanticClusterAnalyst.BuildSamples(context.Profile, context.Lines, excluded);
        var semanticRun = await PdfLaneExecution.RunAsync(async laneCt =>
        {
            var clusterResult = await PdfSemanticClusterAnalyst.AnalyzeAsync(analyst, context.Profile, context.Lines, laneCt);
            var roleResult = await PdfBlockAnalyst.AnalyzeAsync(analyst, selected, candidateContexts, laneCt, semanticOptions, checkpoint);
            return (Clusters: clusterResult, Roles: roleResult);
        }, semanticOptions.LaneDeadline, ct);
        var semanticTimedOut = semanticRun.TimedOut;
        var clusters = !semanticTimedOut && semanticRun.Fault is null
            ? semanticRun.Value.Clusters
            : new PdfSemanticClusterAnalysis(samples, []);
        var roleAnalysis = !semanticTimedOut && semanticRun.Fault is null
            ? semanticRun.Value.Roles
            : new PdfBlockAnalysis(selected,
            selected.Select(block => new PdfBlockDecision(block.Id, PdfBlockRole.Uncertain, 0, "semantic_lane_timeout")).ToArray(), []);
        var visualCandidates = SelectVisualEvidenceCandidates(selected, ranked, roleAnalysis.Decisions);
        var visual = visualAnalyst is null || visualCandidates.Count == 0
            ? new PdfVisualBlockAnalysis([], [])
            : await PdfVisualBlockAnalyst.AnalyzeAsync(visualAnalyst, context.Pdf, visualCandidates, context.Lines, visualDpi,
                candidateContexts, ct);
        var resolvedRoles = PdfProposalConflictResolver.Resolve(roleAnalysis.Decisions, visual.Decisions, candidateContexts);
        // The span lane's own outcome is kept rather than collapsed into the decisions it returns. A
        // heading cannot validate without a resolved span, so this lane failing looks identical to a
        // healthy run once the artifact is written - which is exactly what C1.4 measured on 001.
        PdfBlockAnalysis spanAnalysis;
        string spanLaneStatus;
        if (semanticTimedOut)
        {
            spanAnalysis = roleAnalysis;
            // Not "complete": the lane never executed, and saying otherwise would be the misreport
            // this field exists to prevent.
            spanLaneStatus = "not_run";
        }
        else
        {
            var spanRun = await PdfLaneExecution.RunAsync(
                laneCt => PdfBlockAnalyst.ResolveHeadingSpansAsync(analyst, selected, resolvedRoles.Decisions,
                    candidateContexts, laneCt, checkpoint),
                semanticOptions.RemainingOr(semanticOptions.RequestTimeout), ct);
            spanLaneStatus = spanRun.TimedOut ? "partial_timeout" : "complete";
            spanAnalysis = spanRun switch
            {
                { TimedOut: true } => roleAnalysis with
                {
                    Decisions = roleAnalysis.Decisions.Select(decision => decision with
                    {
                        Role = PdfBlockRole.Uncertain,
                        Confidence = 0,
                        Reason = "semantic_request_timeout",
                    }).ToArray(),
                },
                { Value: { } Value } => Value,
                _ => roleAnalysis,
            };
        }
        var blockAnalysis = roleAnalysis with
        {
            Decisions = spanAnalysis.Decisions,
            RawResponses = roleAnalysis.RawResponses.Concat(spanAnalysis.RawResponses).Concat(visual.RawResponses).ToArray(),
        };
        var stageTraces = PdfProposalValidator.Trace(candidateContexts, blockAnalysis.Decisions);
        var validated = PdfProposalValidator.Validate(candidateContexts, blockAnalysis.Decisions);
        var markerStructures = PdfHierarchyResolver.Resolve(validated, candidateContexts);
        var hierarchyFacts = PdfHierarchyFactsInventory.Inspect(validated, candidateContexts);
        var hierarchyRun = semanticTimedOut || !includeSemanticHierarchyFallback
            ? new PdfLaneExecution.Result<PdfSemanticHierarchyResult>(null, true, false)
            : await PdfLaneExecution.RunAsync(
                laneCt => PdfSemanticHierarchyFallback.ResolveAsync(analyst, validated, markerStructures, candidateContexts, laneCt),
                semanticOptions.RemainingOr(semanticOptions.RequestTimeout), ct);
        var semanticHierarchy = hierarchyRun.Value ?? new PdfSemanticHierarchyResult(markerStructures, [], [], []);
        var structures = semanticHierarchy.Structures;
        blockAnalysis = blockAnalysis with
        {
            RawResponses = blockAnalysis.RawResponses.Concat(semanticHierarchy.RawResponses).ToArray(),
            InputContracts = roleAnalysis.InputContracts.Concat(spanAnalysis.InputContracts).Concat(semanticHierarchy.InputContracts).ToArray(),
        };
        var eligibleIds = validated.Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
        var eligibleDecisions = blockAnalysis.Decisions.Where(decision => eligibleIds.Contains(decision.Id)).ToArray();
        // PDF-first retrieval intentionally includes every plausible visual style and reconstructed
        // fragment. The old sparse-style gate is a retrieval heuristic, not a validation fact;
        // retaining it here would silently discard a 9B proposal after it passed source/scope/span
        // validation. The legacy narrow route keeps that gate.
        var grounded = PdfBlockGrounder.Ground(
            selected, eligibleDecisions, context.Profile, samples, clusters.Decisions,
            requireLearnedCandidateStyle: false);
        var acceptedIds = grounded.Headings.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        var accepted = selected.Where(b => acceptedIds.Contains(b.Id)).ToArray();
        var alignment = AlignToDocx(accepted, slim, context.Profile, AnalystBasis);
        var visualRecovery = await visualRecoveryTask;
        var recoveredHeadings = alignment.Headings.Concat(visualRecovery.Headings)
            .GroupBy(heading => (heading.Index, Start: heading.HeadingSpan?.Start ?? -1))
            .Select(group => group.First())
            .OrderBy(heading => heading.Index).ThenBy(heading => heading.HeadingSpan?.Start ?? 0).ToArray();
        foreach (var heading in recoveredHeadings)
        {
            heading.DecisionStatus = HeadingDecisionStatus.RequiresReview;
            if (heading.ConfidenceBasis != "pdf-visual-sourcefacts-requires-review")
                heading.ConfidenceBasis = "pdf_layout_block_grounded_requires_review";
        }
        var hierarchyChanges = PdfMarkerHierarchyResolver.Apply(recoveredHeadings);
        structures = structures.Concat(visualRecovery.Structures).ToArray();

        var lane = includeAllVisualStyles ? "wide" : "broad";
        if (includeSupplementCandidates) lane += "+supplement";
        var semanticLaneStatus = semanticTimedOut || (includeSemanticHierarchyFallback && hierarchyRun.TimedOut)
            ? "partial_timeout"
            : "complete";
        var semanticTimedOutBlocks = blockAnalysis.Decisions.Count(decision => decision.Reason is "semantic_batch_timeout" or "semantic_lane_timeout" or "semantic_request_timeout");
        var summary = $"audit-only {lane} PDF lane; pdf={Path.GetFileName(context.Pdf)}, candidateBlocks={selected.Count}/{selection.Available}, " +
                      $"pages={selection.SelectedPages}/{selection.AvailablePages}, grounded={accepted.Length}, aligned={alignment.Headings.Count}/{accepted.Length}, visualRecovered={visualRecovery.Headings.Count}, markerHierarchy={hierarchyChanges}";
        var audit = new RouteExecutionAudit(
            summary,
            selection.Available,
            selected.Count,
            selection.AvailablePages,
            selection.SelectedPages,
            context.Candidates.Select(ToAudit).ToArray(),
            selected.Select(ToAudit).ToArray(),
            context.Candidates.Where(b => !selected.Any(choice => choice.Id == b.Id)).Select(ToAudit).ToArray(),
            blockAnalysis.Decisions.Select(d => new RouteBlockDecisionAudit(d.Id, d.Role.ToString(), d.Confidence, d.Reason)).ToArray(),
            accepted.Select(b => b.Id).ToArray(),
            grounded.Rejected.Select(r => new RouteBlockRejectionAudit(r.Id, r.Role, r.Confidence, r.Reason)).ToArray(),
            alignment.AlignedBlockIds.ToArray())
        {
            RawAnalystResponses = blockAnalysis.RawResponses.Concat(visualRecovery.RawResponses).ToArray(),
            ModelInputContracts = roleAnalysis.InputContracts.Concat(spanAnalysis.InputContracts).ToArray(),
            CandidateStageTraces = stageTraces,
            ValidatedStructures = structures,
            RankedCandidates = ranked,
            ProposalResolutions = resolvedRoles.Audit,
            HierarchyProposals = semanticHierarchy.Audit,
            HierarchyFacts = hierarchyFacts,
            TextLayerRecoveries = alignment.TextLayerRecoveries.Concat(visualRecovery.Audit).ToArray(),
            VisualEvidence = visual.Decisions.Select(decision => new RouteVisualEvidenceAudit(
                decision.Id, decision.Role.ToString(), decision.Confidence, decision.Evidence,
                decision.ContextLinesAbove, decision.ContextLinesBelow)).Concat(visualRecovery.Evidence).ToArray(),
            VisualRecoveries = visualRecovery.Traces,
            SemanticLane = new RouteLaneExecutionAudit(
                semanticLaneStatus, selected.Count,
                Math.Max(0, selected.Count - semanticTimedOutBlocks), semanticTimedOutBlocks,
                semanticTimedOut ? Math.Max(0, selected.Count - semanticTimedOutBlocks) : 0,
                semanticLaneStatus == "partial_timeout" ? "timeout" : null),
            SpanLane = new RouteLaneExecutionAudit(
                spanLaneStatus,
                spanLaneStatus == "not_run" ? 0 : selected.Count,
                spanLaneStatus == "complete" ? spanAnalysis.Decisions.Count(d => d.HeadingSpan is not null) : 0,
                spanLaneStatus == "partial_timeout" ? selected.Count : 0,
                spanLaneStatus == "not_run" ? selected.Count : 0,
                spanLaneStatus == "partial_timeout" ? "timeout" : null),
            VisualLane = new RouteLaneExecutionAudit(
                visualRecovery.Traces.Any(trace => trace.Status == "visual-region-unavailable") ? "partial_timeout" : "complete",
                visualRecovery.Traces.Count(trace => !trace.Status.EndsWith("excluded", StringComparison.Ordinal)),
                visualRecovery.Traces.Count(trace => !trace.Status.EndsWith("excluded", StringComparison.Ordinal) &&
                    trace.Status is not "visual-region-unavailable"),
                visualRecovery.Traces.Count(trace => trace.Status == "visual-region-unavailable"),
                0,
                visualRecovery.Traces.Any(trace => trace.Status == "visual-region-unavailable") ? "region_failure" : null),
        };

        // Audit must preserve partial output and every loss even when the production acceptance
        // thresholds would abstain. Otherwise the stage that lost a key title is unobservable.
        var auditReason = accepted.Length < 3
            ? $"audit-only:analyst-grounded-too-few:{accepted.Length}/{selected.Count}"
            : recoveredHeadings.Length < Math.Max(3, (int)Math.Ceiling(accepted.Length * 0.65))
                ? $"audit-only:analyst-low-docx-alignment:{recoveredHeadings.Length}/{accepted.Length}"
                : summary;
        return new PdfTextbookOutlineResult(recoveredHeadings, auditReason, audit);
    }

    /// <summary>
    /// Shared PDF-first broad candidate generator. It keeps PDF line filtering and title-shape
    /// safeguards, but does not require a sparse visual style: semantic selection belongs to the
    /// analyst and grounding stages that follow.
    /// </summary>
    internal static IReadOnlyList<PdfSemanticBlock> BuildBroadCandidates(
        IReadOnlyList<PdfSemanticBlock> blocks,
        PdfStyleClusterProfile profile) =>
        blocks.Where(b => profile.CandidateStyles.Contains(b.PrimaryStyle) && LooksLikeBroadCandidateBlock(b))
            .OrderBy(b => b.Page)
            .ThenByDescending(b => b.TopY)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Wide audit candidate generation deliberately removes learned style as a precondition. It is
    /// for measuring whether an LLM can supply semantics for blocks that layout clustering missed;
    /// it must not be used by production routing without independent precision evidence.
    /// </summary>
    internal static IReadOnlyList<PdfSemanticBlock> BuildWideAuditCandidates(IReadOnlyList<PdfSemanticBlock> blocks) =>
        blocks.Where(LooksLikeWideAuditBlock)
            .OrderBy(b => b.Page)
            .ThenByDescending(b => b.TopY)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Builds a second, longer grouping over the same filtered PDF lines. It is a retrieval repair:
    /// no DOCX text is introduced and the LLM can only classify a recovered PDF block.
    /// </summary>
    internal static IReadOnlyList<PdfSemanticBlock> BuildSupplementCandidates(
        IReadOnlyList<PdfLineBlockAnnotation> annotations,
        IReadOnlyList<PdfSemanticBlock> existing)
    {
        var existingCanonical = existing.Select(b => b.CanonicalText).ToHashSet(StringComparer.Ordinal);
        var atomic = annotations
            .Where(a => !a.ExcludeFromSemanticSamples)
            .Select((a, index) => new PdfSemanticBlock(
                $"s-line-{index + 1}", [a.Line], PdfStyleClusterProfile.StyleOf(a.Line), a.Line.Page,
                a.Line.Y, a.Line.Y, a.Line.Left, a.Line.Right, PdfTextUtilities.Readable(a.Line.Text)))
            .Where(LooksLikeSupplementBlock);
        var loose = PdfSemanticBlockGrouper.Build(annotations, maxLinesPerBlock: 12, allowSemicolonContinuation: true)
            .Where(LooksLikeSupplementBlock)
            .Where(b => !existingCanonical.Contains(b.CanonicalText))
            .Select((b, index) => b with { Id = $"s-block-{index + 1}" });
        var fragments = BuildAdjacentFragmentWindows(annotations).Where(LooksLikeSupplementBlock);
        var seen = new HashSet<string>(existingCanonical, StringComparer.Ordinal);
        return atomic.Concat(loose).Concat(fragments)
            .Where(b => seen.Add(b.CanonicalText))
            .ToArray();
    }

    private static IEnumerable<PdfSemanticBlock> BuildAdjacentFragmentWindows(
        IReadOnlyList<PdfLineBlockAnnotation> annotations)
    {
        var id = 1;
        foreach (var page in annotations.Where(a => !a.ExcludeFromSemanticSamples)
                     .OrderBy(a => a.Line.Page).ThenByDescending(a => a.Line.Y).ThenBy(a => a.Line.Left)
                     .GroupBy(a => a.Line.Page))
        {
            var lines = page.ToArray();
            for (var start = 0; start < lines.Length; start++)
            {
                var window = new List<PdfLine> { lines[start].Line };
                for (var offset = 1; offset < 4 && start + offset < lines.Length; offset++)
                {
                    var next = lines[start + offset].Line;
                    if (window[^1].Y - next.Y is <= 0 or > 34) break;
                    window.Add(next);
                    yield return CreateFragmentBlock($"s-window-{id++}", window);
                }
            }
        }
    }

    private static PdfSemanticBlock CreateFragmentBlock(string id, IReadOnlyList<PdfLine> lines)
    {
        var primary = lines.GroupBy(line => PdfStyleClusterProfile.StyleOf(line))
            .OrderByDescending(g => g.Sum(line => PdfTextUtilities.Readable(line.Text).Length))
            .Select(g => g.Key).First();
        return new PdfSemanticBlock(
            id, lines, primary, lines[0].Page, lines.Max(line => line.Y), lines.Min(line => line.Y),
            lines.Min(line => line.Left), lines.Max(line => line.Right),
            PdfTextUtilities.Readable(string.Join(" ", lines.Select(line => line.Text))));
    }

    /// <summary>
    /// Bounded analyst work must cover the document before spending a second slot on an earlier
    /// page. Taking the first N blocks systematically hid late chapters in long PDFs.
    /// </summary>
    internal static PdfAnalystCandidateSelection SelectAnalystCandidates(
        IReadOnlyList<PdfSemanticBlock> candidates,
        int maximum,
        IReadOnlySet<string>? priorityIds = null,
        IReadOnlyDictionary<string, int>? supplementalRanks = null)
    {
        var ordered = candidates
            .OrderBy(b => b.Page)
            .ThenByDescending(b => b.TopY)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToArray();
        var byPage = ordered
            .GroupBy(b => b.Page)
            .Select(g => g.ToArray())
            .ToArray();
        if (maximum <= 0 || ordered.Length == 0)
            return new PdfAnalystCandidateSelection([], ordered.Length, byPage.Length, 0);
        if (ordered.Length <= maximum)
            return new PdfAnalystCandidateSelection(ordered, ordered.Length, byPage.Length, byPage.Length);

        var selected = priorityIds is { Count: > 0 }
            ? SelectAcrossPages(ordered.Where(b => priorityIds.Contains(b.Id)).ToArray(), maximum)
            : [];
        if (selected.Count < maximum)
        {
            var selectedIds = selected.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
            if (supplementalRanks is { Count: > 0 })
            {
                foreach (var rank in supplementalRanks.Values.Distinct().OrderDescending())
                {
                    if (selected.Count == maximum) break;
                    var tier = ordered.Where(b =>
                        !selectedIds.Contains(b.Id) &&
                        supplementalRanks.TryGetValue(b.Id, out var candidateRank) &&
                        candidateRank == rank).ToArray();
                    var picked = SelectAcrossPages(tier, maximum - selected.Count);
                    selected.AddRange(picked);
                    selectedIds.UnionWith(picked.Select(b => b.Id));
                }
            }

            if (selected.Count < maximum)
                selected.AddRange(SelectAcrossPages(
                    ordered.Where(b => !selectedIds.Contains(b.Id)).ToArray(), maximum - selected.Count));
        }

        return new PdfAnalystCandidateSelection(
            selected.OrderBy(b => b.Page).ThenByDescending(b => b.TopY).ThenBy(b => b.Id, StringComparer.Ordinal).ToArray(),
            ordered.Length,
            byPage.Length,
            selected.Select(b => b.Page).Distinct().Count());
    }

    /// <summary>
    /// Applies a budget to a precomputed plan. The plan itself remains complete in audit output;
    /// this only marks the next work slice and never mutates candidate retrieval.
    /// </summary>
    internal static PdfAnalystCandidateSelection SelectRankedCandidates(
        IReadOnlyList<PdfSemanticBlock> candidates,
        IReadOnlyList<RankedCandidate> ranked,
        int maximum)
    {
        var byId = candidates.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var selected = ranked.Take(Math.Max(0, maximum))
            .Where(item => byId.ContainsKey(item.SourceId))
            .Select(item => byId[item.SourceId])
            .ToArray();
        return new PdfAnalystCandidateSelection(
            selected,
            candidates.Count,
            candidates.Select(block => block.Page).Distinct().Count(),
            selected.Select(block => block.Page).Distinct().Count());
    }

    /// <summary>
    /// Vision confirms uncertainty; it is not a second classifier for every strong semantic and
    /// marker-backed proposal. Ordering by escalation keeps the bounded visual budget focused on
    /// genuine disagreements rather than early-page candidates.
    /// </summary>
    internal static IReadOnlyList<PdfSemanticBlock> SelectVisualEvidenceCandidates(
        IReadOnlyList<PdfSemanticBlock> selected,
        IReadOnlyList<RankedCandidate> ranked,
        IReadOnlyList<PdfBlockDecision> roleDecisions)
    {
        var ranking = ranked.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var decisions = roleDecisions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        return selected.Where(block =>
            decisions.TryGetValue(block.Id, out var decision) &&
            ranking.TryGetValue(block.Id, out var candidate) &&
            (decision.Role == PdfBlockRole.HeadingTopic &&
             (IsMarkerOnlySource(block) ||
             (candidate.CandidateScore < 0.70 ||
              candidate.EscalationScore >= 0.75 && candidate.CandidateScore < 0.85))))
            .OrderByDescending(block => ranking[block.Id].EscalationScore)
            .ThenByDescending(block => ranking[block.Id].CandidateScore)
            .ThenBy(block => block.Page)
            .ThenBy(block => block.Id, StringComparer.Ordinal)
            .Take(PdfVisualBlockAnalyst.MaximumVisualBlocks)
            .ToArray();
    }

    private static bool IsMarkerOnlySource(PdfSemanticBlock block)
    {
        var marker = TryParseLooseLabelledMarker(block.Text);
        return marker is not null && block.CanonicalText.Length < marker.Value.Canonical.Length + 6;
    }

    private static List<PdfSemanticBlock> SelectAcrossPages(
        IReadOnlyList<PdfSemanticBlock> ordered,
        int maximum)
    {
        if (maximum <= 0 || ordered.Count == 0) return [];
        if (ordered.Count <= maximum) return ordered.ToList();

        var byPage = ordered
            .GroupBy(b => b.Page)
            .Select(g => g.ToArray())
            .ToArray();
        var selected = new List<PdfSemanticBlock>(maximum);
        for (var slot = 0; selected.Count < maximum; slot++)
        {
            var added = false;
            foreach (var page in byPage)
            {
                if (slot >= page.Length) continue;
                selected.Add(page[slot]);
                added = true;
                if (selected.Count == maximum) break;
            }

            if (!added) break;
        }
        return selected;
    }

    private static LayoutContext? TryBuildContext(string originalInputPath, SlimDocument slim, out string reason)
    {
        reason = "";
        if (DocumentStructureEvidence.HasNativeSemanticStructure(slim)) { reason = "docx-structure-present"; return null; }
        var pdf = PdfTextbookOutline.FindSiblingPdf(originalInputPath);
        if (pdf is null) { reason = "no-pdf"; return null; }

        IReadOnlyList<PdfLine> lines;
        try
        {
            using var document = PdfDocument.Open(pdf);
            lines = PdfLineExtraction.ExtractLines(document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            reason = "pdf-read-failed";
            return null;
        }

        var annotations = PdfLineBlockFilter.Analyze(lines);
        var semanticLines = annotations.Where(a => !a.ExcludeFromSemanticSamples).Select(a => a.Line).ToList();
        if (semanticLines.Count < 3) { reason = "too-few-semantic-lines"; return null; }
        var profile = PdfStyleClusterProfile.Learn(semanticLines);
        var headingStyles = SelectNavigationStyles(profile);
        if (headingStyles.Count == 0) { reason = "no-sparse-visual-style"; return null; }
        var candidates = PdfSemanticBlockGrouper.Build(annotations)
            .Where(b => headingStyles.Contains(b.PrimaryStyle) && LooksLikeTopicBlock(b))
            .OrderBy(b => b.Page).ThenByDescending(b => b.TopY).ToList();
        var pages = Math.Max(1, lines.Select(l => l.Page).Distinct().Count());
        if (candidates.Count < 3) { reason = $"too-few-layout-blocks:{candidates.Count}"; return null; }
        if (candidates.Count > pages * 2) { reason = $"layout-candidates-too-dense:{candidates.Count}/{pages}"; return null; }
        return new LayoutContext(
            pdf, lines, annotations, profile, headingStyles, candidates,
            new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, int>(StringComparer.Ordinal));
    }

    private static LayoutContext? TryBuildBroadAuditContext(
        string originalInputPath,
        bool includeAllVisualStyles,
        bool includeSupplementCandidates,
        out string reason,
        IReadOnlySet<int>? withheldTableLikeLines = null)
    {
        reason = "";
        var pdf = PdfTextbookOutline.FindSiblingPdf(originalInputPath);
        if (pdf is null) { reason = "no-pdf"; return null; }

        IReadOnlyList<PdfLine> lines;
        try
        {
            using var document = PdfDocument.Open(pdf);
            lines = PdfLineExtraction.ExtractLines(document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            reason = "pdf-read-failed";
            return null;
        }

        var annotations = PdfLineBlockFilter.Analyze(lines, withheldTableLikeLines);
        var semanticLines = annotations.Where(a => !a.ExcludeFromSemanticSamples).Select(a => a.Line).ToList();
        if (semanticLines.Count < 3) { reason = "too-few-semantic-lines"; return null; }
        var profile = PdfStyleClusterProfile.Learn(semanticLines);
        var blocks = PdfSemanticBlockGrouper.Build(annotations);
        var broadCandidates = BuildBroadCandidates(blocks, profile);
        var primaryCandidates = includeAllVisualStyles
            ? BuildWideAuditCandidates(blocks)
            : broadCandidates;
        var supplemental = includeSupplementCandidates
            ? BuildSupplementCandidates(annotations, primaryCandidates)
            : Array.Empty<PdfSemanticBlock>();
        var candidates = includeSupplementCandidates
            ? MergeCandidateSets(primaryCandidates, supplemental)
            : primaryCandidates;
        if (candidates.Count == 0) { reason = "no-broad-layout-blocks"; return null; }
        // Broad candidates are measured seeds. Supplemental blocks are intentionally a second
        // retrieval tier: selecting all of them with equal page priority would crowd seeds out.
        var priorityIds = includeAllVisualStyles || includeSupplementCandidates
            ? broadCandidates.Select(b => b.Id).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var supplementRanks = supplemental
            .Where(block => candidates.Any(candidate => candidate.Id == block.Id))
            .ToDictionary(block => block.Id, ScoreSupplementForAnalyst, StringComparer.Ordinal);
        return new LayoutContext(
            pdf, lines, annotations, profile, profile.CandidateStyles, candidates, priorityIds, supplementRanks);
    }

    /// <summary>
    /// Orders lossless retrieval candidates for bounded analyst attention. This is intentionally
    /// only a ranking signal: a low-scoring block remains available as a later fallback.
    /// </summary>
    internal static int ScoreSupplementForAnalyst(PdfSemanticBlock block)
    {
        var text = block.DisplayText.Trim();
        var score = 0;
        if (NumberingAudit.Parse(text) is not null) score += 100;
        if (block.LineCount is >= 2 and <= 4) score += 12;
        if (text.Length is >= 4 and <= 180) score += 8;

        var letters = text.Where(char.IsLetter).ToArray();
        if (letters.Length >= 4 && letters.Count(char.IsUpper) / (double)letters.Length >= 0.55)
            score += 40;
        if (!text.EndsWith('.') && !text.EndsWith(';')) score += 4;
        return score;
    }

    private static IReadOnlyList<PdfSemanticBlock> MergeCandidateSets(
        IReadOnlyList<PdfSemanticBlock> primary,
        IReadOnlyList<PdfSemanticBlock> supplemental)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return primary.Concat(supplemental)
            .Where(b => seen.Add(b.CanonicalText))
            .OrderBy(b => b.Page).ThenByDescending(b => b.TopY).ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static HashSet<PdfStyleKey> SelectNavigationStyles(PdfStyleClusterProfile profile)
    {
        var body = profile.Clusters.FirstOrDefault(c => c.Style == profile.BodyStyle);
        if (body is null || body.Lines == 0) return [];
        var bodyAverageLength = body.Characters / (double)body.Lines;
        var pageCount = Math.Max(1, profile.Clusters.Max(c => c.Pages));

        return profile.Clusters
            .Where(c => c.Style != profile.BodyStyle && profile.CandidateStyles.Contains(c.Style))
            .Where(c => c.Lines <= pageCount * 1.5)
            .Where(c => c.Characters / (double)Math.Max(1, c.Lines) <= Math.Max(120, bodyAverageLength * 0.85))
            .Where(c => IsVisuallyDistinct(c.Style, profile.BodyStyle))
            .Select(c => c.Style)
            .ToHashSet();
    }

    private static bool IsVisuallyDistinct(PdfStyleKey candidate, PdfStyleKey body) =>
        candidate.FontSizeBucket >= body.FontSizeBucket + 0.5 ||
        !string.Equals(candidate.FontName, body.FontName, StringComparison.Ordinal) ||
        !string.Equals(candidate.FillColorKey, body.FillColorKey, StringComparison.Ordinal);

    private static bool LooksLikeTopicBlock(PdfSemanticBlock block)
    {
        var text = block.DisplayText;
        if (text.Length is < 3 or > 160 || !text.Any(char.IsLetter)) return false;
        if (text.Count(char.IsDigit) > text.Length * 0.25) return false;
        if (block.LineCount > 3) return false;
        if (text.Length >= 70 && Regex.IsMatch(text, @"[.!?]\s*$")) return false;
        if (text.Count(c => c is '.' or ';') >= 2) return false;
        return true;
    }

    private static bool LooksLikeBroadCandidateBlock(PdfSemanticBlock block)
    {
        var text = block.DisplayText.Trim();
        if (text.Length is < 3 or > 180 || !text.Any(char.IsLetter)) return false;
        if (block.LineCount > 3 && text.Length > 120) return false;
        if (text.Count(c => c is '.' or ';') >= 2) return false;
        if (text.Length >= 80 && text.EndsWith('.')) return false;
        if (text.Length >= 40 && Regex.IsMatch(text, @"^(?:\d{1,3}|\*)\s+\S")) return false;
        if (LooksLikeSpacedLogoFragment(text) && !HasKerningFragmentationForAudit(block)) return false;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length < 24;
    }

    private static bool LooksLikeWideAuditBlock(PdfSemanticBlock block)
    {
        var text = block.DisplayText.Trim();
        if (text.Length is < 3 or > 320 || !text.Any(char.IsLetter)) return false;
        if (block.LineCount > 8) return false;
        if (LooksLikeSpacedLogoFragment(text) && !HasKerningFragmentationForAudit(block)) return false;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length < 56;
    }

    private static bool LooksLikeSupplementBlock(PdfSemanticBlock block)
    {
        var text = block.DisplayText.Trim();
        if (text.Length is < 3 or > 900 || !text.Any(char.IsLetter) || block.LineCount > 12) return false;
        if (LooksLikeSpacedLogoFragment(text) && !HasKerningFragmentationForAudit(block)) return false;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 140) return false;
        if (NumberingAudit.Parse(text) is not null) return true;
        if (Regex.IsMatch(text, @"^\s*(?:chapter|chương|section|article|điều)\b", RegexOptions.IgnoreCase)) return true;
        var letters = text.Where(char.IsLetter).ToArray();
        return letters.Length >= 4 && letters.Count(char.IsUpper) / (double)letters.Length >= 0.55;
    }

    // Diagnostic predicate only. It is intentionally not a production producer until a separate
    // style/geometry discontinuity is measured: kerning evidence alone widened the 076 pool.
    internal static bool LooksLikeKerningRepairCandidate(PdfSemanticBlock block)
    {
        var text = block.DisplayText.Trim();
        return block.LineCount == 1 &&
               text.Length is >= 4 and <= 180 &&
               text.Any(char.IsLetter) &&
               !Regex.IsMatch(text, @"[.!?;:]\s*$") &&
               HasKerningFragmentationForAudit(block);
    }

    private static PdfCandidateRetrievalTrace TraceTitle(
        string expected,
        IReadOnlyList<PdfLineBlockAnnotation> annotations,
        IReadOnlyList<PdfSemanticBlock> standard,
        IReadOnlyList<PdfSemanticBlock> broad,
        IReadOnlyList<PdfSemanticBlock> wide,
        IReadOnlyList<PdfSemanticBlock> supplement)
    {
        var target = PdfTextUtilities.CanonicalForMatch(expected);
        var rawWindow = FindRawWindow(annotations, target);
        var foundRaw = rawWindow.Count > 0;
        var relevantLines = rawWindow
            .Where(a =>
            {
                var line = CanonicalForMatching(a.Line);
                return line.Length >= 4 && (target.Contains(line, StringComparison.Ordinal) || line.Contains(target, StringComparison.Ordinal));
            })
            .ToArray();
        if (relevantLines.Length == 0) relevantLines = rawWindow.ToArray();
        var foundExactLine = relevantLines.Any(annotation =>
            string.Equals(CanonicalForMatching(annotation.Line), target, StringComparison.Ordinal));
        var reasons = relevantLines.Select(a => a.Reason).Where(r => r != "semantic-candidate").Distinct().ToArray();
        bool Contains(IReadOnlyList<PdfSemanticBlock> blocks) => blocks.Any(b => b.CanonicalText.Contains(target, StringComparison.Ordinal));
        var inStandard = Contains(standard);
        var inBroad = Contains(broad);
        var inWide = Contains(wide);
        var inSupplement = Contains(supplement);
        var firstLoss = !foundRaw ? "absent-from-raw-windows"
            : reasons.Length > 0 ? "line-filtered:" + string.Join(",", reasons)
            : !inStandard ? "semantic-block-grouping"
            : !inBroad ? "broad-style-or-shape-gate"
            : "candidate-available";
        var rawText = foundRaw ? string.Join(" ", relevantLines.Select(a => PdfTextUtilities.Readable(a.Line.Text))) : null;
        if (rawText is { Length: > 360 }) rawText = rawText[..360];
        return new PdfCandidateRetrievalTrace(
            expected, foundRaw, reasons, inStandard, inBroad, inWide, inSupplement, firstLoss, rawText, foundExactLine);
    }

    private static PdfCandidateConstructionTrace TraceConstructionTitle(string expected,
        IReadOnlyList<PdfLineBlockAnnotation> annotations, IReadOnlyList<PdfSemanticBlock> standard,
        PdfStyleClusterProfile profile,
        IReadOnlyList<PdfSemanticBlock> broad, IReadOnlyList<PdfSemanticBlock> wide,
        IReadOnlyList<PdfSemanticBlock> supplement)
    {
        var target = PdfTextUtilities.CanonicalForMatch(expected);
        var rawWindow = FindRawWindow(annotations, target);
        var relevant = rawWindow.Where(annotation =>
        {
            var canonical = CanonicalForMatching(annotation.Line);
            return canonical.Length >= 4 && (target.Contains(canonical, StringComparison.Ordinal) ||
                canonical.Contains(target, StringComparison.Ordinal));
        }).ToArray();
        if (relevant.Length == 0) relevant = rawWindow.ToArray();
        var keys = relevant.Select(annotation => ConstructionLineId(annotation.Line)).ToHashSet(StringComparer.Ordinal);
        var matchingStandard = standard.Where(block => block.CanonicalText.Contains(target, StringComparison.Ordinal)).ToArray();
        var sharedStandard = standard.Where(block => block.Lines.Any(line => keys.Contains(ConstructionLineId(line)))).ToArray();
        IReadOnlyList<string> MatchingIds(IReadOnlyList<PdfSemanticBlock> blocks) => blocks
            .Where(block => block.CanonicalText.Contains(target, StringComparison.Ordinal)).Select(block => block.Id).ToArray();
        var producers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["broad"] = MatchingIds(broad),
            ["wide"] = MatchingIds(wide),
            ["supplement"] = MatchingIds(supplement),
        };
        var producerDecisions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["broad"] = matchingStandard.Length == 0 ? "no_standard_match" :
                !matchingStandard.Any(block => profile.CandidateStyles.Contains(block.PrimaryStyle)) ? "style_not_selected" :
                !matchingStandard.Any(LooksLikeBroadCandidateBlock) ? "shape_rejected" : "created",
            ["wide"] = matchingStandard.Length == 0 ? "no_standard_match" :
                !matchingStandard.Any(LooksLikeWideAuditBlock) ? "shape_rejected" : "created",
            ["supplement"] = matchingStandard.Length == 0 ? "no_standard_match" :
                !matchingStandard.Any(LooksLikeSupplementBlock) ? "shape_rejected" : "created_or_deduped",
        };
        var filteredBeforeGrouping = relevant.Length > 0 && relevant.All(annotation => annotation.ExcludeFromCandidateGrouping);
        var firstLoss = relevant.Length == 0 ? "representation_missing" :
            filteredBeforeGrouping ? "line_filter_gate" :
            matchingStandard.Length == 0 ? "semantic_block_grouping" :
            producers.All(pair => pair.Value.Count == 0) ? "candidate_producer" : "candidate_available";
        var operation = relevant.Length == 0 ? "not_represented" :
            filteredBeforeGrouping ? "filtered_before_grouping" :
            matchingStandard.Length > 0 ? "preserved" :
            sharedStandard.Length > 0 ? "absorbed_or_span_truncated" : "dropped_during_grouping";
        return new PdfCandidateConstructionTrace(expected,
            relevant.Select(annotation => new PdfCandidateConstructionSourceLine(ConstructionLineId(annotation.Line),
                annotation.Line.Page, PdfTextUtilities.Readable(annotation.Line.Text), annotation.Reason,
                annotation.ExcludeFromSemanticSamples, annotation.ExcludeFromCandidateGrouping,
                annotation.Line.MatchText)).ToArray(),
            relevant.Select(annotation => ConstructionLineId(annotation.Line)).ToArray(),
            matchingStandard.Select(block => block.Id).Concat(sharedStandard.Select(block => block.Id)).Distinct(StringComparer.Ordinal).ToArray(),
            matchingStandard.Concat(sharedStandard).DistinctBy(block => block.Id).Select(ToConstructionBlock).ToArray(),
            producers, producerDecisions, operation, firstLoss);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyProducers() =>
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["broad"] = [], ["wide"] = [], ["supplement"] = [],
        };

    private static IReadOnlyDictionary<string, string> EmptyProducerDecisions() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["broad"] = "not_run", ["wide"] = "not_run", ["supplement"] = "not_run",
        };

    private static PdfCandidateConstructionBlock ToConstructionBlock(PdfSemanticBlock block) =>
        new(block.Id, block.Page, block.LineCount, block.DisplayText, block.CanonicalText, block.HasKerningJoinEvidence,
            block.Lines.Select(line => new PdfCandidateConstructionBlockLine(
                PdfTextUtilities.Readable(line.Text), line.MatchText, line.FontSize, line.BoldRatio,
                line.FontName, line.FillColorKey, line.Left, line.Y)).ToArray());

    private static string ConstructionLineId(PdfLine line) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"p{line.Page}:y{line.Y:R}:x{line.Left:R}");

    private static string CanonicalForMatching(PdfLine line) => line.CanonicalMatchText ??
        PdfTextUtilities.CanonicalForMatch(line.Text);

    private static IReadOnlyList<PdfLineBlockAnnotation> FindRawWindow(
        IReadOnlyList<PdfLineBlockAnnotation> annotations,
        string target)
    {
        if (target.Length == 0) return [];
        var ordered = annotations.OrderBy(a => a.Line.Page).ThenByDescending(a => a.Line.Y).ThenBy(a => a.Line.Left).ToArray();
        for (var start = 0; start < ordered.Length; start++)
        {
            var window = new List<PdfLineBlockAnnotation>(12);
            for (var offset = 0; offset < 12 && start + offset < ordered.Length; offset++)
            {
                var current = ordered[start + offset];
                if (current.Line.Page != ordered[start].Line.Page) break;
                window.Add(current);
                var text = string.Concat(window.Select(a => CanonicalForMatching(a.Line)));
                if (text.Contains(target, StringComparison.Ordinal)) return window;
            }
        }

        return [];
    }

    private static bool LooksLikeSpacedLogoFragment(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4) return false;
        return tokens.Count(t => t.Length == 1 && char.IsLetter(t[0])) / (double)tokens.Length >= 0.70;
    }

    // A source block still keeps its observed text. This only prevents kerning-fragmented words
    // with measured glyph-join evidence from being mistaken for a spaced logo by candidate shape
    // gates; it is not a text rewrite and does not itself accept a heading.
    internal static bool HasKerningFragmentationForAudit(PdfSemanticBlock block) =>
        block.HasKerningJoinEvidence && Regex.IsMatch(block.DisplayText,
            @"\b\p{Lu}\s+\p{Ll}{1,2}\s+\p{Ll}{3,}\b", RegexOptions.CultureInvariant);

    private static PdfLayoutAlignmentResult AlignToDocx(
        IReadOnlyList<PdfSemanticBlock> candidates,
        SlimDocument slim,
        PdfStyleClusterProfile profile,
        string confidenceBasis) =>
        AlignToDocx(candidates, slim, profile, confidenceBasis, trace: null);

    /// <summary>
    /// The alignment production runs, with an optional passive record of how each block reached the
    /// paragraph it was grounded to. The sink observes this run; it never re-runs the matching, so an
    /// audit cannot end up describing a second implementation that has drifted from this one.
    /// </summary>
    private static PdfLayoutAlignmentResult AlignToDocx(
        IReadOnlyList<PdfSemanticBlock> candidates,
        SlimDocument slim,
        PdfStyleClusterProfile profile,
        string confidenceBasis,
        List<PdfDocxAlignmentTrace>? trace,
        List<PdfDocxCanonicalParagraph>? haystacks = null)
    {
        var paragraphs = slim.Paragraphs
            .Where(p => p.Role != ParagraphRole.Empty && !p.InTableOfContents && !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Index)
            .Select(p => new CanonParagraph(p, CanonicalMap(p.Text)))
            .Where(p => p.Map.Text.Length > 0)
            .ToList();
        // The exact strings the matcher searched. Recorded rather than recomputed, so an audit of how
        // ambiguous a needle was cannot answer from a canonicalisation that has drifted from this one.
        haystacks?.AddRange(paragraphs.Select(p =>
            new PdfDocxCanonicalParagraph(p.Paragraph.Index, p.Map.Text)));
        var styles = candidates.Select(b => b.PrimaryStyle).Distinct()
            .OrderByDescending(s => s.FontSizeBucket)
            .ThenBy(s => s.FontName, StringComparer.Ordinal)
            .ThenBy(s => s.FillColorKey, StringComparer.Ordinal)
            .Select((style, index) => (style, level: index + 1))
            .ToDictionary(x => x.style, x => x.level);

        var result = new List<HeadingRecord>();
        var alignedBlockIds = new HashSet<string>(StringComparer.Ordinal);
        var textLayerRecoveries = new List<PdfTextLayerRecoveryAudit>();
        // PDF blocks arrive in page order. Keep an occurrence occupied only for the same visual
        // style: a repeated page title must advance to the next DOCX page blob, while a group label
        // and its title may legitimately share one source span when they have different PDF styles.
        var seen = new HashSet<(int Index, int Start, PdfStyleKey Style)>();
        var occupiedSpans = new HashSet<(int Index, int Start)>();
        var cursor = 0;
        foreach (var block in candidates)
        {
            var needle = block.CanonicalText;
            if (needle.Length < 4) continue;
            var branch = PdfDocxMatchBranch.Unmatched;
            var match = FindMatch(paragraphs, needle, cursor, block.PrimaryStyle, seen, occupiedSpans, requireFreshSpan: true);
            if (match is not null) branch = PdfDocxMatchBranch.CursorFresh;
            if (match is null && FindMatch(paragraphs, needle, 0, block.PrimaryStyle, seen, occupiedSpans, requireFreshSpan: true) is { } fromZeroFresh)
            {
                match = fromZeroFresh;
                branch = PdfDocxMatchBranch.FromZeroFresh;
            }
            if (match is null && FindMatch(paragraphs, needle, cursor, block.PrimaryStyle, seen, occupiedSpans, requireFreshSpan: false) is { } cursorRelaxed)
            {
                match = cursorRelaxed;
                branch = PdfDocxMatchBranch.CursorRelaxed;
            }
            if (match is null && FindMatch(paragraphs, needle, 0, block.PrimaryStyle, seen, occupiedSpans, requireFreshSpan: false) is { } fromZeroRelaxed)
            {
                match = fromZeroRelaxed;
                branch = PdfDocxMatchBranch.FromZeroRelaxed;
            }
            var directMatch = match;
            var reconstructed = directMatch is null
                ? FindMarkerReconstruction(paragraphs, block, cursor, occupiedSpans) ??
                  FindMarkerReconstruction(paragraphs, block, 0, occupiedSpans)
                : null;
            match ??= reconstructed?.Match;
            if (match is null)
            {
                var status = ParseLooseLabelledMarkerForAudit(block.Text) is null
                    ? "no-marker-for-reconstruction"
                    : "marker-reconstruction-unresolved";
                textLayerRecoveries.Add(new PdfTextLayerRecoveryAudit(block.Id, block.Page, status));
                trace?.Add(new PdfDocxAlignmentTrace(block.Id, needle, null, null, null,
                    PdfDocxMatchBranch.Unmatched, false));
                continue;
            }
            if (reconstructed is not null)
                textLayerRecoveries.Add(new PdfTextLayerRecoveryAudit(block.Id, block.Page,
                    reconstructed.MarkerOnly ? "marker-only-span-reconstructed" : "marker-span-reconstructed"));
            if (reconstructed is not null) branch = PdfDocxMatchBranch.MarkerReconstruction;
            trace?.Add(new PdfDocxAlignmentTrace(block.Id, needle, match.Value.Paragraph.Index,
                match.Value.Start, match.Value.End - match.Value.Start, branch,
                !seen.Contains((match.Value.Paragraph.Index, match.Value.Start, block.PrimaryStyle))));
            if (!seen.Add((match.Value.Paragraph.Index, match.Value.Start, block.PrimaryStyle))) continue;
            occupiedSpans.Add((match.Value.Paragraph.Index, match.Value.Start));

            result.Add(new HeadingRecord
            {
                Index = match.Value.Paragraph.Index,
                StableId = match.Value.Paragraph.StableId,
                SourceId = block.Id,
                Level = styles[block.PrimaryStyle],
                Text = reconstructed?.HeadingText ?? block.DisplayText,
                OriginalText = match.Value.Paragraph.Text,
                HeadingSpan = new TextOffsetSpan(match.Value.Start, match.Value.End),
                BoundarySource = reconstructed is null ? "pdf-layout-evidence" :
                    reconstructed.MarkerOnly ? "pdf-marker-only-span-reconstruction" : "pdf-marker-span-reconstruction",
                StyleId = match.Value.Paragraph.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0.90,
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                ConfidenceBasis = confidenceBasis,
            });
            alignedBlockIds.Add(block.Id);
            cursor = match.Value.Paragraph.Index;
        }

        return new PdfLayoutAlignmentResult(
            result.OrderBy(h => h.Index).ThenBy(h => h.HeadingSpan?.Start ?? 0).ToList(),
            alignedBlockIds,
            textLayerRecoveries);
    }

    private static MatchResult? FindMatch(
        IReadOnlyList<CanonParagraph> paragraphs,
        string needle,
        int minimumIndex,
        PdfStyleKey style,
        IReadOnlySet<(int Index, int Start, PdfStyleKey Style)> occupied,
        IReadOnlySet<(int Index, int Start)> occupiedSpans,
        bool requireFreshSpan)
    {
        foreach (var paragraph in paragraphs.Where(p => p.Paragraph.Index >= minimumIndex))
        {
            var offset = 0;
            while (offset <= paragraph.Map.Text.Length - needle.Length)
            {
                var at = paragraph.Map.Text.IndexOf(needle, offset, StringComparison.Ordinal);
                if (at < 0) break;
                var start = paragraph.Map.SourceIndexes[at];
                var sameStyleOccupied = occupied.Contains((paragraph.Paragraph.Index, start, style));
                var anyStyleOccupied = occupiedSpans.Contains((paragraph.Paragraph.Index, start));
                if (!sameStyleOccupied && (!requireFreshSpan || !anyStyleOccupied))
                {
                    return new MatchResult(
                        paragraph.Paragraph,
                        start,
                        paragraph.Map.SourceIndexes[at + needle.Length - 1] + 1);
                }

                offset = at + 1;
            }
        }

        return null;
    }

    /// <summary>
    /// PDF text can corrupt a few glyphs while retaining a labelled marker and a long prefix of
    /// the title. Recover when that marker maps to one unoccupied DOCX source paragraph and the
    /// source-derived text agrees beyond the marker. A marker-only source is permitted only when
    /// it has exactly one unoccupied DOCX occurrence; it remains review-only in the output policy.
    /// This never consults answer keys.
    /// </summary>
    private static MarkerReconstruction? FindMarkerReconstruction(
        IReadOnlyList<CanonParagraph> paragraphs,
        PdfSemanticBlock block,
        int minimumIndex,
        IReadOnlySet<(int Index, int Start)> occupiedSpans)
    {
        var marker = TryParseLooseLabelledMarker(block.Text);
        if (marker is null) return null;
        var markerOnly = block.CanonicalText.Length < marker.Value.Canonical.Length + 6;
        var candidates = new List<MarkerReconstruction>();

        foreach (var paragraph in paragraphs.Where(p => p.Paragraph.Index >= minimumIndex))
        {
            foreach (Match sourceMatch in LooseLabelledMarkerAnywhereRx.Matches(paragraph.Paragraph.Text))
            {
                var sourceMarker = MarkerFromMatch(sourceMatch);
                if (sourceMarker is null || !string.Equals(marker.Value.Canonical, sourceMarker.Value.Canonical, StringComparison.Ordinal))
                    continue;
                var end = FindMarkerHeadingEnd(paragraph.Paragraph.Text, sourceMatch);
                if (end <= sourceMatch.Index || end - sourceMatch.Index > 360) continue;
                if (occupiedSpans.Contains((paragraph.Paragraph.Index, sourceMatch.Index))) continue;
                var sourceCanonical = PdfTextUtilities.CanonicalForMatch(paragraph.Paragraph.Text[sourceMatch.Index..end]);
                if (!markerOnly && CommonPrefixLength(block.CanonicalText, sourceCanonical) < marker.Value.Canonical.Length + 6)
                    continue;

                candidates.Add(new MarkerReconstruction(
                    new MatchResult(paragraph.Paragraph, sourceMatch.Index, end),
                    PdfTextUtilities.HeadingReadable(paragraph.Paragraph.Text[sourceMatch.Index..end]), markerOnly));
            }
        }
        if (candidates.Count == 0) return null;
        return markerOnly && candidates.Count != 1 ? null : candidates[0];
    }

    internal static string? ParseLooseLabelledMarkerForAudit(string text) =>
        TryParseLooseLabelledMarker(text)?.Canonical;

    internal static TextOffsetSpan? FindMarkerHeadingSpanForAudit(string sourceText, string markerText)
    {
        var marker = TryParseLooseLabelledMarker(markerText);
        if (marker is null) return null;
        foreach (Match sourceMatch in LooseLabelledMarkerAnywhereRx.Matches(sourceText))
        {
            var sourceMarker = MarkerFromMatch(sourceMatch);
            if (sourceMarker is null || sourceMarker.Value.Canonical != marker.Value.Canonical) continue;
            var end = FindMarkerHeadingEnd(sourceText, sourceMatch);
            return end > sourceMatch.Index ? new TextOffsetSpan(sourceMatch.Index, end) : null;
        }
        return null;
    }

    private static LooseLabelledMarker? TryParseLooseLabelledMarker(string text)
    {
        var match = LooseLabelledMarkerRx.Match(text);
        return match.Success ? MarkerFromMatch(match) : null;
    }

    private static LooseLabelledMarker? MarkerFromMatch(Match match)
    {
        if (!match.Success) return null;
        var label = PdfTextUtilities.CanonicalForMatch(match.Groups[1].Value);
        var numeral = Regex.Replace(match.Groups[2].Value, @"\s+", "").ToLowerInvariant();
        if (label.Length == 0 || numeral.Length == 0) return null;
        return new LooseLabelledMarker($"{label}:{numeral}");
    }

    private static int FindMarkerHeadingEnd(string sourceText, Match marker)
    {
        var bodyStart = ClauseStartAfterTitleRx.Match(sourceText, marker.Index + marker.Length);
        var end = bodyStart.Success ? bodyStart.Index : sourceText.Length;
        while (end > marker.Index && char.IsWhiteSpace(sourceText[end - 1])) end--;
        return end;
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < length && left[index] == right[index]) index++;
        return index;
    }

    private static CanonMap CanonicalMap(string text)
    {
        var canonical = new StringBuilder(text.Length);
        var indexes = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLetterOrDigit(text[i])) continue;
            canonical.Append(char.ToLowerInvariant(text[i]));
            indexes.Add(i);
        }
        return new CanonMap(canonical.ToString(), indexes);
    }

    private sealed record CanonMap(string Text, IReadOnlyList<int> SourceIndexes);
    private sealed record CanonParagraph(SlimParagraph Paragraph, CanonMap Map);
    private readonly record struct LooseLabelledMarker(string Canonical);
    private sealed record MarkerReconstruction(MatchResult Match, string HeadingText, bool MarkerOnly);
    private readonly record struct MatchResult(SlimParagraph Paragraph, int Start, int End);
    private sealed record PdfLayoutAlignmentResult(
        IReadOnlyList<HeadingRecord> Headings,
        IReadOnlySet<string> AlignedBlockIds,
        IReadOnlyList<PdfTextLayerRecoveryAudit> TextLayerRecoveries);
    private sealed record LayoutContext(
        string Pdf,
        IReadOnlyList<PdfLine> Lines,
        IReadOnlyList<PdfLineBlockAnnotation> Annotations,
        PdfStyleClusterProfile Profile,
        IReadOnlySet<PdfStyleKey> HeadingStyles,
        IReadOnlyList<PdfSemanticBlock> Candidates,
        IReadOnlySet<string> PriorityCandidateIds,
        IReadOnlyDictionary<string, int> SupplementCandidateRanks);

    private static RouteBlockAudit ToAudit(PdfSemanticBlock block) =>
        new(block.Id, block.Page, block.DisplayText);
}

public sealed record PdfTextLayerRecoveryAudit(string Id, int Page, string Status);

internal sealed record PdfAnalystCandidateSelection(
    IReadOnlyList<PdfSemanticBlock> Selected,
    int Available,
    int AvailablePages,
    int SelectedPages);

/// <summary>Diagnostic view over one ranking build. Production consumes only the audit.</summary>
internal sealed record PdfCandidateRankingSnapshot(
    PdfCandidateRankingAudit Audit,
    IReadOnlyDictionary<string, PdfCandidateProvenance> Provenance,
    IReadOnlyList<PdfSemanticBlock> CandidateBlocks,
    IReadOnlyList<PdfLineBlockAnnotation> Annotations,
    IReadOnlyList<PdfLine> Lines);

/// <summary>
/// Which source lines a candidate was built from. Only observed facts: no scoring, no text, and no
/// judgement about whether the candidate is a heading.
/// </summary>
internal sealed record PdfCandidateProvenance(
    string CandidateSourceId,
    IReadOnlyList<int> LineIndexes,
    IReadOnlyList<string> LineIds,
    PdfCandidateRepresentationKind RepresentationKind)
{
    public static string LineId(PdfLine line) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{line.Page}|{line.Y:R}|{line.Left:R}|{line.Right:R}|{line.Text}");

    /// <summary>
    /// A candidate represents an occurrence when it was built from the lines that occurrence is made
    /// of. It may carry more - a window also holding the body text that follows still represents the
    /// heading - so this is containment of the required lines, never a text comparison.
    /// </summary>
    public bool Covers(IEnumerable<int> requiredLineIndexes) =>
        requiredLineIndexes.All(LineIndexes.Contains);
}

internal enum PdfCandidateRepresentationKind
{
    StandardBlock,
    WindowFragment,
}

/// <summary>One production alignment run plus a passive record of how it reached each paragraph.</summary>
internal sealed record PdfDocxAlignmentSnapshot(
    string Status,
    int CandidateCount,
    IReadOnlyList<HeadingRecord> Headings,
    IReadOnlyList<PdfDocxAlignmentTrace> Trace,
    IReadOnlyList<PdfDocxCanonicalParagraph> Haystacks,
    SlimDocument Document);

/// <summary>A paragraph exactly as the matcher saw it.</summary>
internal sealed record PdfDocxCanonicalParagraph(int Index, string CanonicalText);

/// <summary>
/// What the matcher was given and what it chose. Observed facts only: whether the choice was a good
/// one is a judgement for evaluation to derive, not a field to assert here.
/// </summary>
internal sealed record PdfDocxAlignmentTrace(
    string SourceBlockId,
    string Needle,
    int? ParagraphIndex,
    int? Start,
    int? Length,
    PdfDocxMatchBranch Branch,
    bool Accepted);

/// <summary>
/// Which population the matcher was run over. The analyst lanes decide their accepted blocks with a
/// model, so no offline run can reproduce one; <see cref="RetrievalPopulation"/> exercises the same
/// matcher over the retrieval superset instead. Rates measured there describe the matcher, not
/// production - production aligns a subset of it.
/// </summary>
internal enum PdfDocxAlignmentPopulation
{
    NarrowRoute,
    RetrievalPopulation,
}

/// <summary>Which of the matcher's existing attempts produced the match.</summary>
internal enum PdfDocxMatchBranch
{
    Unmatched,
    CursorFresh,
    FromZeroFresh,
    CursorRelaxed,
    FromZeroRelaxed,
    MarkerReconstruction,
}
