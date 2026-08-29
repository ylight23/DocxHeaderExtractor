using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Blocker audit for partial-result preservation's promotion decision. Lane A's counterfactual on 003
/// found 9/82 emitted blocks that don't correspond to any of 003's 230 silver headingOccurrences - an
/// 11.0% false-positive rate, materially higher than 001's 1.8%. This does not recompute that number;
/// it asks what it actually is, per candidate: a real semantic false positive the completed-span
/// preservation would expose in production, or an artifact of the silver label, the join, span
/// resolution, or grounding/alignment - each of which has a very different promotion implication.
/// <para>
/// Taxonomy: <c>TRUE_MODEL_FALSE_POSITIVE</c> (the analyst called a genuine non-heading
/// <c>HeadingTopic</c>, and nothing upstream explains it away), <c>SILVER_REFERENCE_DISAGREEMENT</c>
/// (the candidate looks like a real Điều/Chương/Mục heading by the same structural marker silver used
/// elsewhere, but silver's own artifact does not list it - silver's coverage gap, not a model error),
/// <c>OCCURRENCE_JOIN_MISMATCH</c> (the candidate's lines overlap a real silver occurrence's required
/// lines without exactly containing them - the join's strict containment check is what failed, not
/// necessarily the candidate), <c>SPAN_OVERREACH</c> (the resolved span/candidate captures real heading
/// text plus extra trailing content that changed its identity), <c>VALIDATOR_ACCEPTED_WRONG_ROLE</c>
/// (a deterministic structural signal already available - domain role, structural scope, evidence
/// origin - was inconsistent with heading status and the validator did not use it),
/// <c>GROUNDING_ALIGNMENT_MISASSOCIATION</c> (the emitted DOCX paragraph does not correspond to this
/// candidate's actual PDF location), and <c>UNRESOLVED</c>.
/// </para>
/// </summary>
public sealed class PdfC1003RecoveredFalsePositiveAuditProbe
{
    [Fact]
    public void WriteAudit()
    {
        var output = Environment.GetEnvironmentVariable("C1_003_FP_AUDIT_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var report = BuildReport(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void CommittedAuditReproduces()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var path = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "003-recovered-fp-audit.v1.json");
        if (!File.Exists(path)) return;

        var expected = JsonSerializer.Serialize(BuildReport(root), new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(expected.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    private static object BuildReport(string root)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var checkpointPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "checkpoints", "003-n2-s.jsonl");
        var checkpoint = File.ReadLines(checkpointPath)
            .Select(line => JsonSerializer.Deserialize<CheckpointEntry>(line, options))
            .Where(e => e is not null).Cast<CheckpointEntry>().ToArray();

        using var silver = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n0", "silver-labels", "003-n1.2-silver-model-assisted.v1.json")));
        var occurrences = silver.RootElement.GetProperty("headingOccurrences").EnumerateArray()
            .Select(o => (
                StableId: o.TryGetProperty("goldStableId", out var g) ? g.GetString()! : o.GetProperty("silverStableId").GetString()!,
                Marker: o.GetProperty("marker").GetString()!,
                Kind: o.GetProperty("kind").GetString()!,
                Page: o.GetProperty("page").GetInt32(),
                LineIds: o.GetProperty("sourceLineIds").EnumerateArray().Select(l => l.GetString()!).ToHashSet(StringComparer.Ordinal)))
            .ToArray();

        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy", "003_Luat_Doanh_nghiep_59-2020-QH14.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var selected = PdfLayoutEvidenceOutline.SelectRankedCandidates(snapshot.CandidateBlocks, snapshot.Audit.Candidates, 160).Selected;
        var contexts = PdfCandidateContextBuilder.Build(selected, snapshot.Annotations);
        var candidatesById = selected.ToDictionary(b => b.Id, StringComparer.Ordinal);
        var roles = checkpoint.Where(e => e.Lane == "semantic").SelectMany(e => e.Payload.Blocks).ToDictionary(b => b.Id, StringComparer.Ordinal);
        var spans = checkpoint.Where(e => e.Lane == "span").SelectMany(e => e.Payload.Blocks)
            .Where(b => b.Resolved && b.Start is not null && b.End is not null).ToDictionary(b => b.Id, StringComparer.Ordinal);

        var decisions = selected.Select(block =>
        {
            if (!roles.TryGetValue(block.Id, out var role))
                return new PdfBlockDecision(block.Id, PdfBlockRole.Uncertain, 0, "missing-checkpoint-role");
            var parsed = Enum.TryParse<PdfBlockRole>(role.Role, true, out var value) ? value : PdfBlockRole.Uncertain;
            return spans.TryGetValue(block.Id, out var span)
                ? new PdfBlockDecision(block.Id, parsed, role.Confidence, role.Reason ?? "checkpoint", new TextOffsetSpan(span.Start!.Value, span.End!.Value))
                : new PdfBlockDecision(block.Id, parsed, role.Confidence, role.Reason ?? "checkpoint");
        }).ToArray();

        var validated = PdfProposalValidator.Validate(contexts, decisions);
        var validatedIds = validated.Select(v => v.SourceId).ToHashSet(StringComparer.Ordinal);
        var excluded = snapshot.Annotations.Where(a => a.ExcludeFromSemanticSamples).Select(a => a.Line).ToHashSet();
        var profile = PdfStyleClusterProfile.Learn(snapshot.Annotations.Where(a => !a.ExcludeFromSemanticSamples).Select(a => a.Line).ToArray());
        var samples = PdfSemanticClusterAnalyst.BuildSamples(profile, snapshot.Lines, excluded);
        var grounded = PdfBlockGrounder.Ground(selected, validated.Select(item => decisions.Single(d => d.Id == item.SourceId)).ToArray(),
            profile, samples, [], requireLearnedCandidateStyle: false);
        var groundedIds = grounded.Headings.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        var slim = new DocxSlimExtractor().Extract(docx);
        var alignment = PdfLayoutEvidenceOutline.BuildBroadAlignmentForCandidateIds(docx, PolicyStateFixture.FromSlim(slim), groundedIds);
        var traceByCandidate = alignment.Trace.ToDictionary(t => t.SourceBlockId, StringComparer.Ordinal);

        var bySource = snapshot.Provenance;
        bool CoversAnySilverOccurrence(string candidateId) =>
            bySource.TryGetValue(candidateId, out var provenance) &&
            occurrences.Any(o => o.LineIds.All(l => provenance.LineIds.Contains(l, StringComparer.Ordinal)));

        var fullyCoveredStableIds = occurrences
            .Where(o => alignment.Headings.Any(h => bySource.TryGetValue(h.SourceId!, out var p) && o.LineIds.All(l => p.LineIds.Contains(l, StringComparer.Ordinal))))
            .Select(o => o.StableId)
            .ToHashSet(StringComparer.Ordinal);

        var falsePositives = alignment.Headings
            .Where(h => !CoversAnySilverOccurrence(h.SourceId!))
            .Select(h =>
            {
                var candidateId = h.SourceId!;
                var block = candidatesById[candidateId];
                var provenance = bySource.TryGetValue(candidateId, out var p) ? p : null;
                var role = roles.TryGetValue(candidateId, out var r) ? r : null;
                var span = spans.TryGetValue(candidateId, out var s) ? s : null;
                var ctx = contexts.TryGetValue(candidateId, out var c) ? c : null;
                var page = block.Page;

                // Overlap short of full containment - the join's strict "covers every required line"
                // test can fail even when the candidate and a real occurrence share source lines.
                var partialOverlap = provenance is null ? [] : occurrences
                    .Where(o => o.LineIds.Any(l => provenance.LineIds.Contains(l, StringComparer.Ordinal)) &&
                                !o.LineIds.All(l => provenance.LineIds.Contains(l, StringComparer.Ordinal)))
                    .Select(o => new { o.StableId, o.Marker, o.Kind, sharedLines = o.LineIds.Count(l => provenance!.LineIds.Contains(l, StringComparer.Ordinal)), requiredLines = o.LineIds.Count })
                    .ToArray();

                // Same-page silver occurrences, for a human-legible neighbourhood even without overlap.
                var samePage = occurrences.Where(o => o.Page == page).Select(o => new { o.StableId, o.Marker, o.Kind }).ToArray();

                var trace = traceByCandidate.TryGetValue(candidateId, out var t) ? t : null;
                var overlapsAnAlreadyFullyEmittedOccurrence = partialOverlap.Any(o => fullyCoveredStableIds.Contains(o.StableId));

                var classification = Classify(block.Text, role, span, ctx, partialOverlap.Length, samePage.Length);

                return new
                {
                    candidateId,
                    page,
                    blockText = Truncate(block.Text),
                    displayText = Truncate(block.DisplayText),
                    lineIds = provenance?.LineIds,
                    representationKind = provenance?.RepresentationKind.ToString(),
                    roleDecision = role is null ? null : new { role.Role, role.Confidence, role.Reason },
                    spanDecision = span is null ? null : new { span.Start, span.End },
                    validated = validatedIds.Contains(candidateId),
                    grounded = groundedIds.Contains(candidateId),
                    groundingEvidence = grounded.Headings.FirstOrDefault(gh => gh.Id == candidateId)?.Evidence,
                    emitted = true,
                    alignmentBranch = trace?.Branch.ToString(),
                    alignmentAccepted = trace?.Accepted,
                    structuralScope = ctx?.Source.StructuralScope,
                    domainRole = ctx?.Source.DomainRole,
                    deterministicExclusionReason = ctx is null ? null : PdfExtractorQualityBenchmarkProbe.DeterministicExclusionReason(ctx),
                    partialOverlapWithSilverOccurrence = partialOverlap,
                    isDuplicateOfAnAlreadyFullyEmittedOccurrence = overlapsAnAlreadyFullyEmittedOccurrence,
                    samePageSilverOccurrences = samePage,
                    classification,
                };
            })
            .OrderBy(x => x.page).ThenBy(x => x.candidateId, StringComparer.Ordinal)
            .ToArray();

        var tally = falsePositives.GroupBy(f => f.classification, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new
        {
            schemaVersion = 1,
            artifactKind = "c1_003_recovered_false_positive_audit",
            usesModel = false,
            scope = "Only the 9 emitted-but-not-in-any-silver-occurrence blocks from Lane A's partial-preserve counterfactual (82 emitted total, 73 match a silver occurrence).",
            acceptanceGate = new[]
            {
                "1. recurrence - PASS (001, 003 independently)",
                "2. material recovery - PASS (33.3%, 56.3%)",
                "3. complete-lane neutrality - PASS (057 reconstruction byte-identical to live)",
                "4. fail-closed semantics - PASS (unchanged locks)",
                "5. no extra provider calls - PASS (all offline)",
                "6. false-positive collateral - this artifact's job: classify causally, not just recompute the rate",
                "7. no new cross-document regression - not yet checked beyond 001/003",
            },
            falsePositiveCount = falsePositives.Length,
            emittedTotal = 82,
            classificationTally = tally,
            falsePositives,
        };
    }

    /// <summary>
    /// Best-effort offline classification from the evidence actually available. Not a model call and
    /// not a silver re-review - a case this cannot resolve from structural/checkpoint facts alone is
    /// UNRESOLVED, not guessed into a bucket.
    /// </summary>
    private static string Classify(
        string blockText, CheckpointBlock? role, CheckpointBlock? span, PdfCandidateContext? ctx,
        int partialOverlapCount, int samePageSilverCount)
    {
        if (role is null) return "UNRESOLVED";

        // Overlap with a real silver occurrence's required lines - even without full containment -
        // means this candidate names the same heading the silver artifact already reviewed. That is a
        // join/containment artifact, not disagreement about whether the heading exists; checked before
        // the weaker "looks like a legal marker" heuristic below, which cannot distinguish an actual
        // silver miss from a candidate that simply under-captured a heading silver already has.
        if (partialOverlapCount > 0) return "OCCURRENCE_JOIN_MISMATCH";

        var readable = PdfTextUtilities.HeadingReadable(blockText);
        var looksLikeLegalMarker = System.Text.RegularExpressions.Regex.IsMatch(readable, @"^(Điều|Chương|Mục)\s+\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (looksLikeLegalMarker) return "SILVER_REFERENCE_DISAGREEMENT";

        if (ctx is not null)
        {
            var exclusion = PdfExtractorQualityBenchmarkProbe.DeterministicExclusionReason(ctx);
            if (exclusion is not null) return "VALIDATOR_ACCEPTED_WRONG_ROLE";
        }

        if (span is not null && span.End - span.Start < blockText.Length - 5) return "SPAN_OVERREACH";

        return "TRUE_MODEL_FALSE_POSITIVE";
    }

    private static string Truncate(string value) => value.Length <= 160 ? value : value[..160] + "...";

    private sealed record CheckpointEntry(string Lane, CheckpointPayload Payload);
    private sealed record CheckpointPayload(IReadOnlyList<CheckpointBlock> Blocks);
    private sealed record CheckpointBlock(string Id, string? Role, double Confidence, string? Reason, bool Resolved, int? Start, int? End);
}
