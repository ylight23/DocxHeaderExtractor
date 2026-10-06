using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Transport adapter for the normal DOCX route. The classifier is only a
/// provider transport seam; semantic output crosses into the Core vNext contract before binding,
/// graph resolution, or projection. It never supplies coordinates or hierarchy truth.
/// </summary>
internal static class CanonicalSemanticDocxAuthorityAdapter
{
    public static async Task<StructuralAuthorityResult> RunAsync(
        SourceDocument sourceDocument,
        IHeaderClassifier? transport,
        CancellationToken cancellationToken,
        CanonicalSemanticExperiment? experiment = null)
    {
        ArgumentNullException.ThrowIfNull(sourceDocument);
        var source = DocxSourceOccurrenceAdapter.BuildForAudit(sourceDocument);
        if (source.Count == 0)
            return new StructuralAuthorityResult(new ValidatedStructure([]), null, "empty-docx-source");

        var universe = source.Universe;
        var catalog = universe.Catalog;
        var sourceHash = universe.SourceSha256;
        var evidence = universe.Evidence;
        var input = new CanonicalSemanticTextProductionInput(
            catalog,
            null,
            sourceHash,
            sourceHash,
            sourceDocument.DocumentId,
            evidence,
            evidence.Select(item => $"[{item.SourceAlias}] {item.ExactSourceText}").ToArray(),
            [],
            evidence.SelectMany(item => item.LocalBefore.Concat(item.LocalAfter)).ToArray())
        {
            OwnedAliases = null,
        };

        CanonicalSemanticTextProductionResult result;
        CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel? canonicalModel = null;
        if (transport is null)
        {
            // No model, no semantic claims: the harness does not declare headings from style,
            // outline level or numbering on its own. Same as the PDF lane.
            result = CanonicalSemanticTextProductionEntryPoint.Run(input with { SemanticProposals = [] });
        }
        else
        {
            canonicalModel = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
                transport,
                SemanticCoordinateContract.DocxAliasSpan,
                SemanticEvidencePackingPolicies.FixedOwnedCount120,
                experiment ?? CanonicalSemanticExperiment.Baseline);
            result = await CanonicalSemanticTextProductionEntryPoint.RunAsync(
                input, canonicalModel,
                requestId: $"docx:{sourceDocument.DocumentId}",
                cancellationToken: cancellationToken);
        }

        var decisions = result.TextPipeline.BoundHeadings.Select(item => new HeadingExtentDecision(
            item.SourceId,
            "canonical-vnext-semantic-contract",
            new TextOffsetSpan(item.Start, item.End),
            SemanticFunction: item.SemanticRole)).ToArray();
        var validated = HeadingProposalValidator.Validate(source.HeadingContexts, decisions);
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
            : await HeadingPlacementCoordinator.PlaceUnresolvedHeadingsAsync(
                result.TextPipeline.BoundHeadings, transport, cancellationToken);
        var derived = HeadingHierarchyResolver
            .DeriveHierarchyFromModelRelations(boundHeadings)
            .Where(item => source.Contexts.ContainsKey(item.SourceId))
            .ToArray();
        var structures = derived
            .ToDictionary(item => item.SourceId, item =>
            {
                var facts = source.Contexts[item.SourceId].HeadingContext;
                return new ResolvedHeadingPlacement(
                    item.SourceId, item.Level, item.ParentSourceId, item.Resolution, "requires_review")
                {
                    StructuralScope = facts.StructuralScope,
                };
            }, StringComparer.Ordinal);
        // PDF hierarchy inventory requires physical page and geometry evidence. DOCX deliberately
        // does not fabricate those facts merely to reuse a PDF diagnostic.
        IReadOnlyList<HeadingHierarchyFactAudit> hierarchyFacts = [];
        // The outline carries one entry per semantic section. Repeated occurrences stay in the
        // canonical graph and in the route audit; collapsing them is the projection's job, and it
        // collapses only what the model declared to be the same node.
        var primarySourceIds = derived
            .Where(item => item.IsPrimaryOccurrence)
            .Select(item => item.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        // Straight to the one materializer, in the same shape the PDF lane hands it. This used to
        // go through a DocxSourceOccurrenceAdapter wrapper whose only remaining work was this mapping;
        // a lane-named entry point in front of a shared owner is how the two drift apart again.
        // Every validated heading here is a bound model claim.
        var structuralAuthority = CanonicalStructureMaterializer.Materialize(
            validated,
            structures,
            source.Contexts.ToDictionary(
                pair => pair.Key,
                pair => new CanonicalSourceOccurrence(
                    pair.Value.Source.SourceId,
                    pair.Value.Source.SourceOrdinal,
                    pair.Value.Source.Text,
                    pair.Value.Source.Style.StyleId,
                    sourceDocument.SourceKind,
                    "docx-source-pointer-span"),
                StringComparer.Ordinal),
            "docx", StructuralDecisionOrigin.Model, primarySourceIds);
        var audit = CanonicalRouteAuditBoundary.Create(
            "docx-canonical-vnext",
            source.Count,
            source.Count,
            0,
            0,
            source.Contexts.Values.Select(context => new RouteBlockAudit(context.Source.SourceId, 0, context.Source.Text)).ToArray(),
            source.Contexts.Values.Select(context => new RouteBlockAudit(context.Source.SourceId, 0, context.Source.Text)).ToArray(),
            decisions.Select(decision => new RouteBlockDecisionAudit(
                decision.Id, decision.SemanticFunction)).ToArray(),
            validated.Select(item => item.SourceId).ToArray()) with
        {
            RawAnalystResponses = canonicalModel?.RawResponses ?? [],
            ModelInputContracts = canonicalModel is null ? [] : [canonicalModel.Contract.ProtocolVersion],
            SourceStageTraces = source.Contexts.Values.Select(context =>
                new HeadingSourceStageTrace(
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
                result.ConflictNormalization.NormalizedProposals,
                universe.Aliases,
                    result.ConflictNormalization.Conflicts),
                0,
                0,
                result.TextPipeline.BoundHeadings.Select(item => item.SourceId)
                    .ToHashSet(StringComparer.Ordinal)),
            SemanticLane = new RouteLaneExecutionAudit("complete", source.Count,
                validated.Count, 0, 0),
            SpanLane = new RouteLaneExecutionAudit("canonical-binder", result.TextPipeline.BoundHeadings.Count,
                result.TextPipeline.BoundHeadings.Count, 0, result.TextPipeline.BindingFailureCount),
            BatchTelemetry = new HeadingAuthorityBatchTelemetry(
                source.Count,
                source.Count,
                result.TextModelCalls == 0 ? 0 : 1,
                result.TextModelCalls,
                0,
                0,
                0,
                result.TextPipeline.BoundHeadings.Count,
                0,
                0,
                0,
                result.TextPipeline.Graph.Occurrences.Count,
                0,
                result.TextModelCalls,
                canonicalModel?.RawResponses.Count ?? 0,
                0),
        };
        return new StructuralAuthorityResult(
            structuralAuthority,
            audit,
            "docx-canonical-vnext-semantic-authority");
    }

}
