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

        var textEvidence = text.BoundHeadings
            .SelectMany(heading => heading.Parts.Count == 0
                ? [new CanonicalSemanticTextEvidenceBinding(
                    heading.SourceId, heading.Start, heading.End, heading.Text)]
                : heading.Parts.Select(part => new CanonicalSemanticTextEvidenceBinding(
                    part.SourceId, part.Start, part.End, part.Text)))
            .ToArray();
        var visualEvidence = visualHeadings
            .SelectMany(heading => heading.Bindings)
            .ToArray();
        var unified = CanonicalSemanticCrossModalReconciler.Reconcile(
            textEvidence, visualEvidence);
        var canonicalGraph = CombineGraphs(text.Graph, visualHeadings, textEvidence);

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
        IReadOnlyList<CanonicalSemanticTextEvidenceBinding> textEvidence)
    {
        var occurrences = textGraph.Occurrences.ToList();
        var textKeys = textEvidence.Select(item => item.Text).ToHashSet(StringComparer.Ordinal);
        foreach (var heading in visualHeadings)
        {
            foreach (var binding in heading.Bindings)
            {
                if (textKeys.Contains(binding.RecoveredTranscript)) continue;
                var pageNumber = int.TryParse(binding.PageId.TrimStart('P', 'p'), out var parsedPage) ? parsedPage : 0;
                var ordinal = int.MaxValue - pageNumber;
                var occurrence = new CanonicalSemanticGraphOccurrence(
                    $"visual-occurrence:{occurrences.Count + 1:0000}",
                    $"visual-node:{heading.SemanticRole}:{binding.RecoveredTranscript}",
                    binding.VisualAlias, $"visual:{binding.PageId}", ordinal,
                    binding.RecoveredTranscript, heading.SemanticRole, heading.StructuralType,
                    heading.Scope, 0, 0, "PRIMARY", null)
                { BindingMode = "VISUAL_REGION" };
                occurrences.Add(occurrence);
            }
        }
        var ordered = occurrences.OrderBy(item => item.SourceOrdinal).ThenBy(item => item.Start)
            .ThenBy(item => item.OccurrenceId, StringComparer.Ordinal).ToArray();
        var projection = ordered.GroupBy(item => item.SemanticNodeId, StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        return new CanonicalSemanticGraph(ordered, projection);
    }
}
