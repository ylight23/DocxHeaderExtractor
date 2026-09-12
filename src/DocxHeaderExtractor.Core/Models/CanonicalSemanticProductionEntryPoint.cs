namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// The executable vNext orchestration boundary. Evidence preparation and model inference are
/// supplied by the caller; this entry point owns the order of modality profiling, attention,
/// context packing, semantic validation, binding, reconciliation, graph resolution, and
/// projection. Gold is intentionally absent from this contract.
/// </summary>
public sealed record CanonicalSemanticProductionInput(
    DocumentSourceCatalog SourceCatalog,
    IReadOnlyList<CanonicalSemanticProposal>? SemanticProposals,
    string SourceSha256,
    IReadOnlyList<CanonicalSemanticPageEvidence> Pages,
    IReadOnlyList<SemanticCandidateAttentionHint> CandidateHints,
    IReadOnlyList<string> TargetEvidence,
    IReadOnlyList<string> LocalContext,
    IReadOnlyList<string> GlobalContext,
    IReadOnlyList<CanonicalSemanticVisualBlock>? VisualBlocks = null,
    IReadOnlyList<CanonicalSemanticVisualProposal>? VisualProposals = null,
    IReadOnlyList<CanonicalSemanticVisualPageEvidence>? VisualPages = null,
    string? ExpectedSourceSha256 = null,
    string? DocumentId = null);

public sealed record CanonicalSemanticInferenceTelemetry(
    string? ActualProvider = null,
    string? FinishReason = null,
    int? InputTokens = null,
    int? ReasoningTokens = null,
    int? OutputTokens = null);

public sealed record CanonicalSemanticTextInferenceResult(
    IReadOnlyList<CanonicalSemanticProposal> Proposals,
    CanonicalSemanticInferenceTelemetry Telemetry);

public interface ICanonicalSemanticTextModel
{
    Task<CanonicalSemanticTextInferenceResult> InferAsync(
        CanonicalSemanticProductionInput input,
        SemanticContextPacket packedContext,
        string requestId,
        CancellationToken cancellationToken = default);
}

public sealed record CanonicalSemanticVisualInferenceResult(
    IReadOnlyList<CanonicalSemanticVisualBlock> Blocks,
    IReadOnlyList<CanonicalSemanticVisualProposal> Proposals,
    CanonicalSemanticInferenceTelemetry Telemetry);

public interface ICanonicalSemanticVisualModel
{
    Task<CanonicalSemanticVisualInferenceResult> InferAsync(
        CanonicalSemanticProductionInput input,
        SemanticContextPacket packedContext,
        IReadOnlyList<CanonicalSemanticVisualOccurrence> recoveredOccurrences,
        string requestId,
        CancellationToken cancellationToken = default);
}

public sealed record CanonicalSemanticProductionResult(
    CanonicalSemanticModalityProfile ModalityProfile,
    SemanticContextPacket ContextPacket,
    IReadOnlyList<SemanticCandidateAttentionHint> CandidateHints,
    CanonicalSemanticPipelineResult TextPipeline,
    IReadOnlyList<CanonicalSemanticVisualOccurrence> VisualOccurrences,
    IReadOnlyList<CanonicalSemanticVisualBoundHeading> VisualHeadings,
    IReadOnlyList<CanonicalSemanticUnifiedOccurrence> UnifiedOccurrences,
    IReadOnlyList<SemanticTransitionLedgerEntry> StageLedger,
    IReadOnlyList<CanonicalSemanticProposal> ModelProposals,
    CanonicalSemanticInferenceTelemetry TextModelTelemetry,
    CanonicalSemanticInferenceTelemetry VisualModelTelemetry,
    int TextModelCalls,
    int VisualModelCalls)
{
    public CanonicalSemanticGraph CanonicalGraph { get; init; } = new([], []);

    public IReadOnlyList<CanonicalSemanticGraphOccurrence> CanonicalOccurrences =>
        CanonicalGraph.Occurrences;

    public IReadOnlyList<CanonicalSemanticGraphOccurrence> Projection =>
        CanonicalGraph.OutlineProjection;
}

public static class CanonicalSemanticProductionEntryPoint
{
    public static CanonicalSemanticProductionResult Run(CanonicalSemanticProductionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.SemanticProposals is null)
            throw new InvalidOperationException("LIVE_INFERENCE_REQUIRES_RUN_ASYNC");
        return RunPostInference(input, input.SemanticProposals, input.VisualProposals ?? [],
            new(), new(), 0, 0);
    }

    public static async Task<CanonicalSemanticProductionResult> RunAsync(
        CanonicalSemanticProductionInput input,
        ICanonicalSemanticTextModel textModel,
        ICanonicalSemanticVisualModel? visualModel = null,
        string requestId = "canonical-semantic-production",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(textModel);
        var pages = input.Pages ?? throw new ArgumentNullException(nameof(input.Pages));
        var profile = ModalityProfiler.Profile(pages);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(input.SourceCatalog);

        // Candidate hints are attention metadata only. Every owned alias remains eligible.
        foreach (var alias in aliases)
            _ = SemanticCandidatePolicy.CanAcceptOwnedOccurrence(alias.Alias, input.CandidateHints);

        var context = SemanticContextPacker.Pack(
            input.TargetEvidence, input.LocalContext, input.GlobalContext);
        var textInference = await textModel.InferAsync(input, context, requestId, cancellationToken);
        var visualBlocks = input.VisualBlocks is { Count: > 0 }
            ? VisualRecovery.Recover(input.VisualBlocks)
            : [];
        CanonicalSemanticVisualInferenceResult visualInference = new([], [], new());
        if (profile.Pages.Any(page => page.UseVisualRecovery))
        {
            if (visualModel is null)
                throw new InvalidOperationException("VISUAL_MODEL_REQUIRED_FOR_PROFILE");
            visualInference = await visualModel.InferAsync(
                input, context, visualBlocks, requestId + ":visual", cancellationToken);
        }
        var visualInput = visualInference.Blocks.Count > 0
            ? input with { VisualBlocks = visualInference.Blocks }
            : input;
        return RunPostInference(visualInput with { SemanticProposals = textInference.Proposals },
            textInference.Proposals, visualInference.Proposals,
            textInference.Telemetry, visualInference.Telemetry, 1,
            profile.Pages.Any(page => page.UseVisualRecovery) ? 1 : 0);
    }

    private static CanonicalSemanticProductionResult RunPostInference(
        CanonicalSemanticProductionInput input,
        IReadOnlyList<CanonicalSemanticProposal> semanticProposals,
        IReadOnlyList<CanonicalSemanticVisualProposal> visualProposals,
        CanonicalSemanticInferenceTelemetry textTelemetry,
        CanonicalSemanticInferenceTelemetry visualTelemetry,
        int textModelCalls,
        int visualModelCalls)
    {
        var pages = input.Pages ?? throw new ArgumentNullException(nameof(input.Pages));
        var profile = ModalityProfiler.Profile(pages);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(input.SourceCatalog);
        foreach (var alias in aliases)
            _ = SemanticCandidatePolicy.CanAcceptOwnedOccurrence(alias.Alias, input.CandidateHints);
        var context = SemanticContextPacker.Pack(input.TargetEvidence, input.LocalContext, input.GlobalContext);
        var text = CanonicalSemanticPipeline.Run(input.SourceCatalog, semanticProposals,
            input.SourceSha256, input.ExpectedSourceSha256);

        var visualOccurrences = input.VisualBlocks is { Count: > 0 }
            ? VisualRecovery.Recover(input.VisualBlocks)
            : [];
        var visualHeadings = visualProposals.Count > 0
            ? CanonicalSemanticVisualBinder.Bind(
                visualProposals, visualOccurrences)
            : [];

        var aliasesBySourceId = aliases.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var pagesById = pages.ToDictionary(item => item.PageId, StringComparer.OrdinalIgnoreCase);
        var textEvidence = text.BoundHeadings
            .SelectMany(heading => heading.Parts.Count == 0
                ? [CreateTextEvidence(heading.SourceId, heading.Start, heading.End, heading.Text, aliasesBySourceId, pagesById)]
                : heading.Parts.Select(part => CreateTextEvidence(
                    part.SourceId, part.Start, part.End, part.Text, aliasesBySourceId, pagesById)))
            .ToArray();
        var visualEvidence = visualHeadings
            .SelectMany(heading => heading.Bindings)
            .ToArray();
        var unified = CanonicalSemanticCrossModalReconciler.Reconcile(
            textEvidence, visualEvidence);
        var canonicalGraph = CombineGraphs(text.Graph, visualHeadings, unified, aliases);

        var ledger = new[]
        {
            new SemanticTransitionLedgerEntry("SOURCE_IDENTITY", "PRESERVED", aliases.Count, aliases.Count),
            new SemanticTransitionLedgerEntry("MODALITY_PROFILER", "PRESERVED", pages.Count, profile.Pages.Count),
            new SemanticTransitionLedgerEntry("SOURCE_EVIDENCE", "PRESERVED", aliases.Count, aliases.Count),
            new SemanticTransitionLedgerEntry("CANDIDATE_ATTENTION", "PRESERVED", aliases.Count, aliases.Count),
            new SemanticTransitionLedgerEntry("CONTEXT_PACKING", "PRESERVED", context.VisibleEvidence.Count, context.VisibleEvidence.Count),
            new SemanticTransitionLedgerEntry("SEMANTIC_CONTRACT", "PRESERVED", semanticProposals.Count, semanticProposals.Count),
            new SemanticTransitionLedgerEntry("TEXT_UTF16_BINDING", "PRESERVED", semanticProposals.Count, text.BoundHeadings.Count),
            new SemanticTransitionLedgerEntry("VISUAL_RECOVERY", "PRESERVED", input.VisualBlocks?.Count ?? 0, visualOccurrences.Count),
            new SemanticTransitionLedgerEntry("VISUAL_REGION_BINDING", "PRESERVED", visualProposals.Count, visualHeadings.Count),
            new SemanticTransitionLedgerEntry("CROSS_MODAL_RECONCILIATION", "PRESERVED", textEvidence.Length + visualEvidence.Length, unified.Count),
            new SemanticTransitionLedgerEntry("GLOBAL_RESOLUTION", "PRESERVED", text.BoundHeadings.Count + visualHeadings.Count, canonicalGraph.Occurrences.Count),
            new SemanticTransitionLedgerEntry("CANONICAL_GRAPH", "PRESERVED", canonicalGraph.Occurrences.Count, canonicalGraph.Occurrences.Count),
            new SemanticTransitionLedgerEntry("SEMANTIC_BOUNDARY", "PRESERVED", canonicalGraph.Occurrences.Count, canonicalGraph.Occurrences.Count),
            new SemanticTransitionLedgerEntry("TASK_PROJECTION", "PRESERVED", canonicalGraph.Occurrences.Count, canonicalGraph.OutlineProjection.Count),
        };

        return new(profile, context, input.CandidateHints, text,
            visualOccurrences, visualHeadings, unified, ledger, semanticProposals,
            textTelemetry, visualTelemetry, textModelCalls, visualModelCalls)
        { CanonicalGraph = canonicalGraph };
    }

    private static CanonicalSemanticGraph CombineGraphs(
        CanonicalSemanticGraph textGraph,
        IReadOnlyList<CanonicalSemanticVisualBoundHeading> visualHeadings,
        IReadOnlyList<CanonicalSemanticUnifiedOccurrence> unified,
        IReadOnlyList<SemanticSourceAlias> aliases)
    {
        var aliasesBySourceId = aliases.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var occurrences = textGraph.Occurrences
            .Select(item => item with
            {
                DocumentOrder = TextDocumentOrder(item, aliasesBySourceId)
            })
            .ToList();
        var reconciledVisualAliases = unified
            .Where(item => item.TextEvidence.Count > 0 && item.VisualEvidence.Count > 0)
            .SelectMany(item => item.VisualEvidence)
            .Select(item => item.VisualAlias)
            .ToHashSet(StringComparer.Ordinal);
        var visualBindings = visualHeadings
            .SelectMany(heading => heading.Bindings.Select(binding => (heading, binding)))
            .Where(item => !reconciledVisualAliases.Contains(item.binding.VisualAlias))
            .OrderBy(item => ParsePage(item.binding.PageId))
            .ThenBy(item => item.binding.BoundingBox.Top)
            .ThenBy(item => item.binding.BoundingBox.Left)
            .ThenBy(item => item.binding.BlockOrdinal)
            .ThenBy(item => item.binding.VisualAlias, StringComparer.Ordinal)
            .ToArray();
        foreach (var (heading, binding) in visualBindings)
        {
            var pageNumber = ParsePage(binding.PageId);
            var occurrence = new CanonicalSemanticGraphOccurrence(
                $"visual-occurrence:{occurrences.Count + 1:0000}",
                CanonicalSemanticGraphResolver.CreatePhysicalNodeId(binding),
                binding.VisualAlias, $"visual:{binding.PageId}", pageNumber,
                binding.RecoveredTranscript, heading.SemanticRole, heading.StructuralType,
                heading.Scope, binding.BlockOrdinal, binding.BlockOrdinal, "PRIMARY", null)
            {
                BindingMode = "VISUAL_REGION",
                DocumentOrder = new CanonicalSemanticDocumentOrder(
                    pageNumber, binding.BoundingBox.Top, binding.BoundingBox.Left, 1, binding.BlockOrdinal)
            };
            occurrences.Add(occurrence);
        }
        var ordered = occurrences
            .OrderBy(item => item.DocumentOrder?.Page ?? item.SourceOrdinal)
            .ThenBy(item => item.DocumentOrder?.Top ?? 0)
            .ThenBy(item => item.DocumentOrder?.Left ?? 0)
            .ThenBy(item => item.DocumentOrder?.Layer ?? 0)
            .ThenBy(item => item.DocumentOrder?.LocalOrdinal ?? item.Start)
            .ThenBy(item => item.OccurrenceId, StringComparer.Ordinal).ToArray();
        var projection = ordered.GroupBy(item => item.SemanticNodeId, StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        return new CanonicalSemanticGraph(ordered, projection);
    }

    private static CanonicalSemanticTextEvidenceBinding CreateTextEvidence(
        string sourceId,
        int start,
        int end,
        string text,
        IReadOnlyDictionary<string, SemanticSourceAlias> aliases,
        IReadOnlyDictionary<string, CanonicalSemanticPageEvidence> pages)
    {
        aliases.TryGetValue(sourceId, out var alias);
        var page = alias?.SourceAnchor?.Page is { } pageNumber
            ? $"P{pageNumber:0000}"
            : TryParsePage(sourceId) is { } sourcePage ? $"P{sourcePage:0000}" : null;
        var box = alias?.SourceAnchor?.BoundingBox is { } sourceBox
            ? NormalizePdfBox(sourceBox, page, pages)
            : null;
        return new(sourceId, start, end, text, $"{sourceId}:{start}:{end}", page, null, box);
    }

    private static CanonicalSemanticVisualBoundingBox NormalizePdfBox(
        PdfBoundingBox sourceBox,
        string? pageId,
        IReadOnlyDictionary<string, CanonicalSemanticPageEvidence> pages)
    {
        if (pageId is not null && pages.TryGetValue(pageId, out var page) &&
            page.SourceWidth is > 0 and var sourceWidth &&
            page.SourceHeight is > 0 and var sourceHeight &&
            page.RasterWidth is > 0 and var rasterWidth &&
            page.RasterHeight is > 0 and var rasterHeight)
        {
            var scaleX = rasterWidth / sourceWidth;
            var scaleY = rasterHeight / sourceHeight;
            return new(
                sourceBox.Left * scaleX,
                (sourceHeight - sourceBox.Top) * scaleY,
                (sourceBox.Right - sourceBox.Left) * scaleX,
                (sourceBox.Top - sourceBox.Bottom) * scaleY);
        }
        return new(sourceBox.Left, sourceBox.Bottom,
            sourceBox.Right - sourceBox.Left, sourceBox.Top - sourceBox.Bottom);
    }

    private static CanonicalSemanticDocumentOrder TextDocumentOrder(
        CanonicalSemanticGraphOccurrence item,
        IReadOnlyDictionary<string, SemanticSourceAlias> aliases)
    {
        aliases.TryGetValue(item.SourceId, out var alias);
        var page = alias?.SourceAnchor?.Page ?? TryParsePage(item.SourceId) ?? item.SourceOrdinal;
        var box = alias?.SourceAnchor?.BoundingBox;
        return new(page, box?.Bottom ?? 0, box?.Left ?? 0, 0, item.Start);
    }

    private static int ParsePage(string pageId) => TryParsePage(pageId) ?? int.MaxValue;

    private static int? TryParsePage(string value) =>
        int.TryParse(value.TrimStart('P', 'p'), out var page) ? page : null;
}
