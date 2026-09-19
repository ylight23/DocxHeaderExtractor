using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The PDF lane. Same semantic stage as the DOCX lane, different source parser.
/// <para>
/// A PDF upload is extracted from that PDF and nothing else. It never consults a DOCX, and its
/// result is not reconciled with one: two uploads are two documents, and merging them is something
/// a user has to ask for, not something a lane decides.
/// </para>
/// <para>
/// Everything after source occurrences is <see cref="CanonicalSemanticEngine"/>, shared verbatim
/// with the DOCX lane - the prompt, segmentation, contract, binder, hierarchy resolver and
/// placement pass. What differs is only how the source is read: OOXML gives paragraphs, a PDF
/// gives text blocks grouped from positioned lines.
/// </para>
/// </summary>
internal static class CanonicalSemanticPdfAuthorityAdapter
{
    public static async Task<StructuralAuthorityResult> RunAsync(
        string pdfPath,
        IHeaderClassifier? transport,
        CancellationToken cancellationToken,
        CanonicalSemanticExperiment? experiment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);

        var universe = PdfCanonicalSourceUniverseBuilder.Build(pdfPath);
        if (universe.ParserLineCount == 0)
            return new StructuralAuthorityResult(new ValidatedStructure([]), null, "pdf-no-text-layer");
        if (universe.Blocks.Count == 0)
            return new StructuralAuthorityResult(new ValidatedStructure([]), null, "pdf-no-source-blocks");
        var input = universe.CreateProductionInput(Path.GetFileNameWithoutExtension(pdfPath));

        CanonicalSemanticProductionResult result;
        CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel? canonicalModel = null;
        if (transport is null)
        {
            result = CanonicalSemanticProductionEntryPoint.Run(input with { SemanticProposals = [] });
        }
        else
        {
            canonicalModel = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
                transport, experiment ?? CanonicalSemanticExperiment.Baseline);
            result = await CanonicalSemanticProductionEntryPoint.RunAsync(
                input, canonicalModel,
                requestId: $"pdf:{Path.GetFileNameWithoutExtension(pdfPath)}",
                cancellationToken: cancellationToken);
        }

        var decisions = result.TextPipeline.BoundHeadings.Select(item => new PdfBlockDecision(
            item.SourceId,
            PdfBlockRole.HeadingTopic,
            1,
            "canonical-vnext-semantic-contract",
            new TextOffsetSpan(item.Start, item.End),
            SemanticRole: CanonicalSemanticEngine.ParseSemanticRole(item.SemanticRole))).ToArray();
        var validated = PdfSemanticProposalBinder.BindAndValidate(universe, decisions);

        var boundHeadings = transport is null
            ? result.TextPipeline.BoundHeadings
            : await CanonicalSemanticEngine.PlaceUnresolvedHeadingsAsync(
                result.TextPipeline.BoundHeadings, transport, cancellationToken);

        var derived = ModelRelationHierarchyResolver
            .DeriveHierarchyFromModelRelations(boundHeadings)
            .Where(item => universe.Contexts.ContainsKey(item.SourceId))
            .ToArray();
        var structures = derived.ToDictionary(item => item.SourceId, item =>
        {
            var facts = universe.Contexts[item.SourceId].Source;
            return new PdfValidatedStructure(
                item.SourceId, item.Level, item.ParentSourceId, item.Resolution, "requires_review")
            {
                DomainRole = facts.DomainRole,
                StructuralScope = facts.StructuralScope,
                DomainExclusionProposed = facts.DomainEvidence.ProposesOutlineExclusion,
            };
        }, StringComparer.Ordinal);

        var primarySourceIds = derived
            .Where(item => item.IsPrimaryOccurrence)
            .Select(item => item.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        var occurrences = universe.Contexts.ToDictionary(
            pair => pair.Key,
            pair => new CanonicalSourceOccurrence(
                pair.Value.Source.SourceId,
                universe.OrdinalBySourceId.GetValueOrDefault(pair.Key),
                pair.Value.Source.RawText,
                null),
            StringComparer.Ordinal);

        var structure = CanonicalStructureMaterializer.Materialize(
            validated.Where(item => primarySourceIds.Contains(item.SourceId)).ToArray(),
            structures, occurrences, "pdf");

        var audit = new RouteExecutionAudit(
            "pdf-canonical-vnext",
            universe.Blocks.Count,
            universe.Blocks.Count,
            0,
            0,
            universe.Blocks.Select(block => new RouteBlockAudit(block.Id, block.Page, block.DisplayText)).ToArray(),
            universe.Blocks.Select(block => new RouteBlockAudit(block.Id, block.Page, block.DisplayText)).ToArray(),
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
            Route = "pdf-canonical-vnext",
            RawAnalystResponses = canonicalModel?.RawResponses ?? [],
            ModelInputContracts = canonicalModel is null ? [] : [CanonicalSemanticContract.ProtocolVersion],
            ValidatedStructures = structures.Values.ToArray(),
            HierarchyFacts = PdfHierarchyFactsInventory.Inspect(validated, universe.Contexts),
            ConflictCensus = SemanticConflictCensus.Take(
                result.ConflictNormalization,
                CanonicalSemanticGlobalConflictDetector.Detect(
                    result.NormalizedModelProposals,
                    universe.Aliases,
                    result.SemanticConflicts),
                result.SemanticAdjudicationCalls,
                result.GlobalReopenCalls,
                result.TextPipeline.BoundHeadings.Select(item => item.SourceId)
                    .ToHashSet(StringComparer.Ordinal)),
            SemanticLane = new RouteLaneExecutionAudit("complete", universe.Blocks.Count, validated.Count, 0, 0),
            SpanLane = new RouteLaneExecutionAudit("canonical-binder", result.TextPipeline.BoundHeadings.Count,
                result.TextPipeline.BoundHeadings.Count, 0, result.TextPipeline.BindingFailureCount),
        };

        return new StructuralAuthorityResult(structure, audit, "pdf-canonical-vnext")
        {
            EmittedElementIds = structure.Elements.Select(element => element.Id).ToHashSet(StringComparer.Ordinal),
            // The catalog the model was shown and the binder bound against, handed out rather than
            // left to be reconstructed. The audit beside it records readable text for a person;
            // these are the occurrences themselves.
            SourceCatalog = universe.Catalog,
        };
    }
}
