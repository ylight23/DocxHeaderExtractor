using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Compatibility transport adapter for the normal DOCX route. The legacy classifier is only a
/// provider transport seam; semantic output crosses into the Core vNext contract before binding,
/// graph resolution, or projection. It never supplies coordinates or hierarchy truth.
/// </summary>
internal static class CanonicalSemanticDocxAuthorityAdapter
{
    public static async Task<StructuralAuthorityResult> RunAsync(
        DocxPolicyState policyState,
        DocumentModeReport mode,
        IHeaderClassifier? transport,
        CancellationToken cancellationToken,
        CanonicalSemanticExperiment? experiment = null)
    {
        ArgumentNullException.ThrowIfNull(policyState);
        var source = DocxAuthorityPipeline.BuildForAudit(policyState, mode);
        if (source.Blocks.Count == 0)
            return new StructuralAuthorityResult(new ValidatedStructure([]), null, "empty-docx-source");

        var catalog = DocumentSourceCatalogBuilder.FromSourceDocument(policyState.Source);
        var aliasesBySourceId = SemanticSourceAliasCatalog.FromCatalog(catalog)
            .ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var sourceHash = CanonicalSemanticSourceHash.Compute(policyState.Source.SourcePath);
        var evidence = source.Contexts.Values
            .OrderBy(item => item.Source.SourceOrdinal)
            .Select(item => EvidenceOf(item, aliasesBySourceId[item.Source.SourceId].Alias))
            .ToArray();
        var input = new CanonicalSemanticProductionInput(
            catalog,
            null,
            sourceHash,
            [new CanonicalSemanticPageEvidence("DOCX", true, 0, "docx-source")],
            evidence.Select(item => item.CandidateAttention).ToArray(),
            evidence.Select(item => $"[{item.SourceAlias}] {item.ExactSourceText}").ToArray(),
            [],
            evidence.SelectMany(item => item.LocalBefore.Concat(item.LocalAfter)).ToArray(),
            DocumentId: policyState.Source.DocumentId,
            SourceEvidence: evidence)
        {
            ExpectedSourceSha256 = sourceHash,
            OwnedAliases = null,
        };

        CanonicalSemanticProductionResult result;
        CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel? canonicalModel = null;
        if (transport is null)
        {
            result = CanonicalSemanticProductionEntryPoint.Run(
                input with { SemanticProposals = DeterministicProposals(policyState, source, aliasesBySourceId) });
        }
        else
        {
            canonicalModel = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
                transport, experiment ?? CanonicalSemanticExperiment.Baseline);
            result = await CanonicalSemanticProductionEntryPoint.RunAsync(
                input, canonicalModel,
                requestId: $"docx:{policyState.Source.DocumentId}",
                cancellationToken: cancellationToken);
        }

        var decisions = result.TextPipeline.BoundHeadings.Select(item => new PdfBlockDecision(
            item.SourceId,
            PdfBlockRole.HeadingTopic,
            1,
            "canonical-vnext-semantic-contract",
            new TextOffsetSpan(item.Start, item.End),
            SemanticRole: CanonicalSemanticEngine.ParseSemanticRole(item.SemanticRole))).ToArray();
        var validated = PdfProposalValidator.Validate(source.ModelContexts, decisions);
        // The alias catalog spans the whole document while Contexts holds only the paragraphs this
        // route carries, so a bound heading can name a source this route has no context for. Such
        // a heading cannot be materialized; drop it here instead of indexing a missing key.
        // Reasoning surface #3. The first pass answers three questions at once — is this a
        // heading, what does it mean, where does it sit — and measurably trades placement away
        // when the prompt grows. Rather than crowd that prompt further, headings it left
        // unresolved come back in one narrow follow-up that asks only about position, against a
        // heading list that is already settled. Bounded to a single round: an unresolved heading
        // is a legitimate outcome, not something to keep re-asking about.
        var boundHeadings = transport is null
            ? result.TextPipeline.BoundHeadings
            : await CanonicalSemanticPlacementCoordinator.PlaceUnresolvedHeadingsAsync(
                result.TextPipeline.BoundHeadings, transport, cancellationToken);
        var derived = ModelRelationHierarchyResolver
            .DeriveHierarchyFromModelRelations(boundHeadings)
            .Where(item => source.Contexts.ContainsKey(item.SourceId))
            .ToArray();
        var structures = derived
            .ToDictionary(item => item.SourceId, item =>
            {
                var facts = source.Contexts[item.SourceId].ModelContext.Source;
                return new PdfValidatedStructure(
                    item.SourceId, item.Level, item.ParentSourceId, item.Resolution, "requires_review")
                {
                    DomainRole = facts.DomainRole,
                    StructuralScope = facts.StructuralScope,
                    DomainExclusionProposed = facts.DomainEvidence.ProposesOutlineExclusion,
                };
            }, StringComparer.Ordinal);
        var hierarchyFacts = PdfHierarchyFactsInventory.Inspect(validated, source.ModelContexts);
        // The outline carries one entry per semantic section. Repeated occurrences stay in the
        // canonical graph and in the route audit; collapsing them is the projection's job, and it
        // collapses only what the model declared to be the same node.
        var primarySourceIds = derived
            .Where(item => item.IsPrimaryOccurrence)
            .Select(item => item.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        // Straight to the one materializer, in the same shape the PDF lane hands it. This used to
        // go through a DocxAuthorityPipeline wrapper whose only remaining work was this mapping;
        // a lane-named entry point in front of a shared owner is how the two drift apart again.
        var structuralAuthority = CanonicalStructureMaterializer.Materialize(
            validated.Where(item => primarySourceIds.Contains(item.SourceId)).ToArray(),
            structures,
            source.Contexts.ToDictionary(
                pair => pair.Key,
                pair => new CanonicalSourceOccurrence(
                    pair.Value.Source.SourceId,
                    pair.Value.Source.SourceOrdinal,
                    pair.Value.Source.Text,
                    pair.Value.Source.Style.StyleId),
                StringComparer.Ordinal),
            "docx");
        var audit = new RouteExecutionAudit(
            "docx-canonical-vnext",
            source.Blocks.Count,
            source.Blocks.Count,
            0,
            0,
            source.Blocks.Select(block => new RouteBlockAudit(block.Id, 0, block.DisplayText)).ToArray(),
            source.Blocks.Select(block => new RouteBlockAudit(block.Id, 0, block.DisplayText)).ToArray(),
            [],
            decisions.Select(decision => new RouteBlockDecisionAudit(
                decision.Id, decision.Role.ToString(), decision.Confidence)
            {
                SemanticRole = decision.SemanticRole.ToString(),
                ProposedSourceSpan = decision.ProposedSourceSpan,
            }).ToArray(),
            validated.Select(item => item.SourceId).ToArray(),
            [],
            validated.Select(item => item.SourceId).ToArray())
        {
            Route = "docx-canonical-vnext",
            RawAnalystResponses = canonicalModel?.RawResponses ?? [],
            ModelInputContracts = canonicalModel is null ? [] : [CanonicalSemanticContract.ProtocolVersion],
            ModelRequests = result.PrimaryTextModelCalls == 0
                ? []
                : [new RouteModelRequestAudit(
                    $"docx:{policyState.Source.DocumentId}:primary",
                    "canonical-primary-semantic",
                    source.Blocks.Select(block => block.Id).ToArray(),
                    true,
                    canonicalModel?.RawResponses.Count > 0,
                    canonicalModel?.RawResponses.Count > 0 ? "complete" : "failed")],
            CandidateStageTraces = source.Contexts.Values.Select(context =>
                new PdfCandidateStageTrace(
                    context.Source.SourceId,
                    context.Scope,
                    context.Source.SourceId is not null && validated.Any(item => item.SourceId == context.Source.SourceId)
                        ? "canonical-semantic"
                        : "not-heading",
                    "source-grounded",
                    validated.Any(item => item.SourceId == context.Source.SourceId) ? "valid" : "not-selected",
                    null)).ToArray(),
            ValidatedStructures = structures.Values.ToArray(),
            HierarchyFacts = hierarchyFacts,
            ConflictCensus = SemanticConflictCensus.Take(
                result.ConflictNormalization,
                CanonicalSemanticGlobalConflictDetector.Detect(
                    result.NormalizedModelProposals,
                    SemanticSourceAliasCatalog.FromCatalog(catalog),
                    result.SemanticConflicts),
                result.SemanticAdjudicationCalls,
                result.GlobalReopenCalls,
                result.TextPipeline.BoundHeadings.Select(item => item.SourceId)
                    .ToHashSet(StringComparer.Ordinal)),
            SemanticLane = new RouteLaneExecutionAudit("complete", source.Blocks.Count,
                validated.Count, 0, 0),
            SpanLane = new RouteLaneExecutionAudit("canonical-binder", result.TextPipeline.BoundHeadings.Count,
                result.TextPipeline.BoundHeadings.Count, 0, result.TextPipeline.BindingFailureCount),
            BatchTelemetry = new PdfPipelineBatchTelemetry(
                source.Blocks.Count,
                source.Blocks.Count,
                result.PrimaryTextModelCalls == 0 ? 0 : 1,
                result.PrimaryTextModelCalls,
                0,
                0,
                0,
                result.TextPipeline.BoundHeadings.Count,
                0,
                0,
                0,
                result.CanonicalOccurrences.Count,
                0,
                result.TotalModelCalls,
                canonicalModel?.RawResponses.Count ?? 0,
                0),
        };
        return new StructuralAuthorityResult(
            structuralAuthority,
            audit,
            "docx-canonical-vnext-semantic-authority");
    }

    private static IReadOnlyList<CanonicalSemanticProposal> DeterministicProposals(
        DocxPolicyState policyState,
        DocxAuthoritySource source,
        IReadOnlyDictionary<string, SemanticSourceAlias> aliasesBySourceId) =>
        source.Contexts.Values
            .Where(context => context.Paragraph.HasBuiltInHeadingStyle ||
                context.Source.Style.OutlineLevel is >= 0 and <= 8 ||
                context.Paragraph.NumberingStyleLevel is >= 1 and <= 9)
            .Select(context =>
            {
                var alias = aliasesBySourceId[context.Source.SourceId];
                return new CanonicalSemanticProposal(
                    alias.Alias,
                    true,
                    alias.Text,
                    SemanticRole: "SECTION",
                    StructuralType: "Heading",
                    Scope: context.Scope,
                    SelectionMode: CanonicalSemanticSelectionMode.WholeAlias);
            }).ToArray();

    /// <summary>
    /// Numbering/marker observations handed to the model as evidence. They carry no hierarchy
    /// authority here: the model decides parent relations, the harness derives level from them.
    /// </summary>
    private static CanonicalSemanticSourceEvidence EvidenceOf(
        DocxAuthorityContext context,
        string alias)
    {
        var paragraph = context.Paragraph;
        var source = context.Source;
        return new CanonicalSemanticSourceEvidence(
            alias,
            source.SourceId,
            source.SourceOrdinal,
            source.Text,
            context.Scope,
            source.Layout.TableDepth,
            source.Layout.SectionIndex,
            source.Layout.InContentControl,
            source.InTableOfContents,
            ["docx-parser-source", $"scope:{context.Scope}"],
            new { source.Style.StyleId, source.Style.StyleName, source.Style.OutlineLevel, source.Style.Bold },
            new { source.Numbering.NumberingId, source.Numbering.NumberingLevel, source.Numbering.NumberLabel },
            source.TextSpans.Select(span => (object)new { span.Start, span.End, span.Bold, span.Italic, span.Underline }).ToArray(),
            CanonicalSemanticEngine.MarkerFactsOf(context.ModelContext.Source),
            [paragraph.IsCandidate ? "candidate-attention" : "source-visible"],
            context.ModelContext.PreviousBlocks,
            context.ModelContext.NextBlocks,
            new SemanticCandidateAttentionHint(alias, paragraph.IsCandidate, paragraph.IsCandidate ? "policy-candidate" : "policy-non-candidate"))
        {
            ActiveStructuralAncestors = context.ModelContext.ActiveHeadingStack,
        };
    }
}
