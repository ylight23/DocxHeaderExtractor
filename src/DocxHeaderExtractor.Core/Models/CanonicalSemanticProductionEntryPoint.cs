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
    string? DocumentId = null,
    IReadOnlyList<CanonicalSemanticSourceEvidence>? SourceEvidence = null)
{
    /// <summary>Aliases visible to this execution segment. Null means the complete source set.</summary>
    public IReadOnlySet<string>? OwnedAliases { get; init; }

    /// <summary>Explicit global semantic conflicts supplied by a resolver; never inferred here.</summary>
    public IReadOnlyList<CanonicalSemanticGlobalConflict> GlobalConflicts { get; init; } = [];
}

/// <summary>Compact parser-owned evidence attached to one canonical source occurrence. It contains
/// observations only; it does not contain candidate gating, Gold, hierarchy, or model decisions.</summary>
public sealed record CanonicalSemanticSourceEvidence(
    string SourceAlias,
    string SourceId,
    int SourceOrdinal,
    string ExactSourceText,
    string StructuralScope,
    int TableDepth,
    int SectionIndex,
    bool InContentControl,
    bool InTableOfContents,
    IReadOnlyList<string> ContainerFacts,
    object StyleFacts,
    object NumberingFacts,
    IReadOnlyList<object> RunFormattingFacts,
    IReadOnlyList<string> MarkerFacts,
    IReadOnlyList<string> ObservedEvidence,
    IReadOnlyList<string> LocalBefore,
    IReadOnlyList<string> LocalAfter,
    SemanticCandidateAttentionHint CandidateAttention)
{
    /// <summary>
    /// Structural state already open at this occurrence, from parser-owned marker evidence.
    /// Context only: it reports what a reader would already have seen, never who anything parents to.
    /// </summary>
    public IReadOnlyList<string> ActiveStructuralAncestors { get; init; } = [];
}

public sealed record CanonicalSemanticInferenceTelemetry(
    string? ActualProvider = null,
    string? FinishReason = null,
    int? InputTokens = null,
    int? ReasoningTokens = null,
    int? OutputTokens = null);

public sealed record CanonicalSemanticTextInferenceResult(
    IReadOnlyList<CanonicalSemanticProposal> Proposals,
    CanonicalSemanticInferenceTelemetry Telemetry)
{
    /// <summary>Provider/parser contract issues captured before any binder is allowed to run.</summary>
    public IReadOnlyList<SemanticContractIssue> ContractIssues { get; init; } = [];
}

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

// ICanonicalSemanticAdjudicationModel is declared with the control plane that drives it, in
// CanonicalSemanticClosedLoopControlPlane. Both sides of this merge had added the same interface
// independently, character for character; the copy that lives beside its caller is the one kept.

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

    /// <summary>Raw model proposals retained for forensic telemetry and provenance.</summary>
    public SemanticConflictNormalizationResult ConflictNormalization { get; init; } =
        new([], [], 0, 0, 0, 0);

    public IReadOnlyList<CanonicalSemanticProposal> NormalizedModelProposals =>
        ConflictNormalization.NormalizedProposals;

    public IReadOnlyList<SemanticProposalConflict> SemanticConflicts =>
        ConflictNormalization.Conflicts;

    public int SemanticProposalInputCount =>
        ConflictNormalization.SemanticProposalInputCount;

    public int SemanticProposalNormalizedCount =>
        ConflictNormalization.SemanticProposalNormalizedCount;

    public int ExactSemanticDuplicatesCollapsed =>
        ConflictNormalization.ExactSemanticDuplicatesCollapsed;

    public int SemanticConflictProposalCount =>
        ConflictNormalization.SemanticConflictProposalCount;

    public int SemanticConflictCount =>
        ConflictNormalization.Conflicts.Count;

    public int SemanticAttributeConflictCount =>
        ConflictNormalization.AttributeConflicts.Count;

    public IReadOnlyList<SemanticAttributeConflict> AttributeConflicts =>
        ConflictNormalization.AttributeConflicts;

    public IReadOnlyList<CanonicalSemanticGraphOccurrence> CanonicalOccurrences =>
        CanonicalGraph.Occurrences;

    public IReadOnlyList<CanonicalSemanticGraphOccurrence> Projection =>
        CanonicalGraph.OutlineProjection;

    public IReadOnlyList<SemanticContractIssue> ContractIssues { get; init; } = [];
    public int ContractValidProposalCount { get; init; }
    public int ContractInvalidProposalCount { get; init; }
    public int SemanticAdjudicationCalls { get; init; }
    public int ResolvedConflictCount { get; init; }
    public int UnresolvedConflictCount { get; init; }
    public int InvalidAdjudicationCount { get; init; }
    public int GlobalReopenCalls { get; init; }
    public int PrimaryTextModelCalls => TextModelCalls;
    public int TotalModelCalls => TextModelCalls + VisualModelCalls + SemanticAdjudicationCalls + GlobalReopenCalls;
}

public static class CanonicalSemanticProductionEntryPoint
{
    public static CanonicalSemanticProductionResult Run(CanonicalSemanticProductionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.SemanticProposals is null)
            throw new InvalidOperationException("LIVE_INFERENCE_REQUIRES_RUN_ASYNC");
        var aliases = SemanticSourceAliasCatalog.FromCatalog(input.SourceCatalog);
        var validation = CanonicalSemanticContractValidator.ValidateProposals(
            input.SemanticProposals, aliases.ToDictionary(item => item.Alias, StringComparer.Ordinal), input.OwnedAliases);
        var normalization = SemanticConflictNormalizer.Normalize(validation.ValidProposals, aliases);
        var result = RunPostInference(input, normalization.BindingReadyProposals, input.SemanticProposals,
            input.VisualProposals ?? [], normalization, new(), new(), 0, 0, validation, 0, 0, 0, 0, 0);
        return result with
        {
            ContractIssues = validation.Issues,
            ContractValidProposalCount = validation.ValidProposals.Count,
            ContractInvalidProposalCount = input.SemanticProposals.Count - validation.ValidProposals.Count,
        };
    }

    public static async Task<CanonicalSemanticProductionResult> RunAsync(
        CanonicalSemanticProductionInput input,
        ICanonicalSemanticTextModel textModel,
        ICanonicalSemanticVisualModel? visualModel = null,
        string requestId = "canonical-semantic-production",
        CancellationToken cancellationToken = default,
        ICanonicalSemanticAdjudicationModel? adjudicationModel = null,
        ICanonicalSemanticAdjudicationModel? globalReopenModel = null)
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
        var primaryValidation = CanonicalSemanticContractValidator.ValidateProposals(
            textInference.Proposals,
            aliases.ToDictionary(item => item.Alias, StringComparer.Ordinal),
            input.OwnedAliases);
        var allContractIssues = (textInference.ContractIssues ?? [])
            .Concat(primaryValidation.Issues)
            .ToArray();
        var normalization = SemanticConflictNormalizer.Normalize(primaryValidation.ValidProposals, aliases);
        var detectedGlobalConflicts = CanonicalSemanticGlobalConflictDetector.Detect(
            primaryValidation.ValidProposals, aliases, normalization.Conflicts);
        var globalConflicts = input.GlobalConflicts
            .Concat(detectedGlobalConflicts)
            .GroupBy(item => item.ConflictId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var adjudication = await ResolveConflictsAsync(
            normalization, aliases, input, context, adjudicationModel, requestId, cancellationToken);
        var globalReopen = await CanonicalSemanticGlobalReopenCoordinator.ResolveAsync(
            globalConflicts, aliases, globalReopenModel, requestId, cancellationToken: cancellationToken);
        var globalValidation = CanonicalSemanticContractValidator.ValidateProposals(
            globalReopen.AcceptedAlternatives,
            aliases.ToDictionary(item => item.Alias, StringComparer.Ordinal),
            input.OwnedAliases);
        allContractIssues = allContractIssues.Concat(globalValidation.Issues).ToArray();
        var bindingReady = adjudication.BindingReadyProposals.ToList();
        foreach (var accepted in globalValidation.ValidProposals)
        {
            var acceptedIdentity = CanonicalSemanticGlobalConflictDetector.PhysicalIdentity(accepted, aliases.ToDictionary(item => item.Alias, StringComparer.Ordinal));
            bindingReady.RemoveAll(existing => string.Equals(
                CanonicalSemanticGlobalConflictDetector.PhysicalIdentity(existing, aliases.ToDictionary(item => item.Alias, StringComparer.Ordinal)),
                acceptedIdentity, StringComparison.Ordinal));
            bindingReady.Add(accepted);
        }
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
        var visualInput = input with { GlobalConflicts = globalConflicts };
        if (visualInference.Blocks.Count > 0)
            visualInput = visualInput with { VisualBlocks = visualInference.Blocks };
        var result = RunPostInference(visualInput with { SemanticProposals = bindingReady },
            bindingReady, textInference.Proposals, visualInference.Proposals, normalization,
            textInference.Telemetry, visualInference.Telemetry, 1,
            profile.Pages.Any(page => page.UseVisualRecovery) ? 1 : 0,
            primaryValidation, adjudication.AdjudicationCalls,
            adjudication.ResolvedCount, adjudication.UnresolvedCount, adjudication.InvalidCount,
            globalReopen.ReopenCalls);
        return result with
        {
            ContractIssues = allContractIssues,
            ContractValidProposalCount = primaryValidation.ValidProposals.Count,
            ContractInvalidProposalCount = textInference.Proposals.Count - primaryValidation.ValidProposals.Count,
        };
    }

    private static CanonicalSemanticProductionResult RunPostInference(
        CanonicalSemanticProductionInput input,
        IReadOnlyList<CanonicalSemanticProposal> semanticProposals,
        IReadOnlyList<CanonicalSemanticProposal> rawModelProposals,
        IReadOnlyList<CanonicalSemanticVisualProposal> visualProposals,
        SemanticConflictNormalizationResult normalization,
        CanonicalSemanticInferenceTelemetry textTelemetry,
        CanonicalSemanticInferenceTelemetry visualTelemetry,
        int textModelCalls,
        int visualModelCalls,
        SemanticProposalValidationSummary validation,
        int adjudicationCalls,
        int resolvedConflictCount,
        int unresolvedConflictCount,
        int invalidAdjudicationCount,
        int globalReopenCalls)
    {
        var pages = input.Pages ?? throw new ArgumentNullException(nameof(input.Pages));
        var profile = ModalityProfiler.Profile(pages);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(input.SourceCatalog);
        foreach (var alias in aliases)
            _ = SemanticCandidatePolicy.CanAcceptOwnedOccurrence(alias.Alias, input.CandidateHints);
        var context = SemanticContextPacker.Pack(input.TargetEvidence, input.LocalContext, input.GlobalContext);
        var text = CanonicalSemanticPipeline.Run(input.SourceCatalog, semanticProposals,
            input.SourceSha256, input.ExpectedSourceSha256, input.OwnedAliases);

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
            new SemanticTransitionLedgerEntry("SOURCE_IDENTITY", "COMPLETED", aliases.Count, aliases.Count),
            new SemanticTransitionLedgerEntry("MODALITY_PROFILE", "COMPLETED", pages.Count, profile.Pages.Count),
            new SemanticTransitionLedgerEntry("SOURCE_EVIDENCE", "COMPLETED", aliases.Count, aliases.Count),
            new SemanticTransitionLedgerEntry("CANDIDATE_ATTENTION", "ATTENTION_ONLY", aliases.Count, aliases.Count),
            new SemanticTransitionLedgerEntry("CONTEXT_PACKING", "COMPLETED", context.VisibleEvidence.Count, context.VisibleEvidence.Count),
            new SemanticTransitionLedgerEntry("PRIMARY_SEMANTIC_INFERENCE", textModelCalls > 0 ? "COMPLETED" : "SKIPPED_PRECOMPUTED", 0, rawModelProposals.Count),
            new SemanticTransitionLedgerEntry("SEMANTIC_CONTRACT_VALIDATION", validation.Issues.Count == 0 ? "COMPLETED" : "PARTIAL_INVALID_PROPOSALS", rawModelProposals.Count, validation.ValidProposals.Count),
            new SemanticTransitionLedgerEntry("SEMANTIC_CONFLICT_NORMALIZATION", "COMPLETED", normalization.SemanticProposalInputCount, normalization.NormalizedProposals.Count),
            new SemanticTransitionLedgerEntry("SEMANTIC_CONFLICT_CHECK",
                normalization.Conflicts.Count > 0
                    ? "CONFLICTS_WITHHELD"
                    : normalization.AttributeConflicts.Count > 0
                        ? "ATTRIBUTE_CONFLICTS_WITHHELD"
                        : "NO_CONFLICT",
                normalization.SemanticProposalInputCount,
                normalization.BindingReadyProposals.Count,
                normalization.Conflicts.Count > 0 ? "OCCURRENCE_OR_BINDING_CONFLICT" : null),
            new SemanticTransitionLedgerEntry("SEMANTIC_ADJUDICATION", adjudicationCalls > 0 ? "COMPLETED" : normalization.Conflicts.Count + normalization.AttributeConflicts.Count > 0 ? "SKIPPED_NO_ADJUDICATOR" : "SKIPPED_NO_CONFLICT", normalization.Conflicts.Count + normalization.AttributeConflicts.Count, resolvedConflictCount),
            new SemanticTransitionLedgerEntry("TEXT_EXACT_BINDING", "COMPLETED", semanticProposals.Count, text.BoundHeadings.Count),
            new SemanticTransitionLedgerEntry("HARD_BINDING_VALIDATION", text.BindingFailureCount == 0 ? "COMPLETED" : "COMPLETED_WITH_REJECTIONS", text.BindingObservations.Count, text.BoundHeadings.Count),
            new SemanticTransitionLedgerEntry("VISUAL_RECOVERY", input.VisualBlocks?.Count > 0 ? "COMPLETED" : "SKIPPED_NO_VISUAL_RECOVERY", input.VisualBlocks?.Count ?? 0, visualOccurrences.Count),
            new SemanticTransitionLedgerEntry("VISUAL_SEMANTIC_INFERENCE", visualModelCalls > 0 ? "COMPLETED" : "SKIPPED_NO_VISUAL_RECOVERY", visualProposals.Count, visualProposals.Count),
            new SemanticTransitionLedgerEntry("VISUAL_REGION_BINDING", visualProposals.Count > 0 ? "COMPLETED" : "SKIPPED_NO_VISUAL_RECOVERY", visualProposals.Count, visualHeadings.Count),
            new SemanticTransitionLedgerEntry("CROSS_MODAL_RECONCILIATION", "COMPLETED", textEvidence.Length + visualEvidence.Length, unified.Count),
            new SemanticTransitionLedgerEntry("GLOBAL_RESOLUTION", "COMPLETED", text.BoundHeadings.Count + visualHeadings.Count, canonicalGraph.Occurrences.Count),
            new SemanticTransitionLedgerEntry("GLOBAL_SEMANTIC_REOPEN", globalReopenCalls > 0 ? "COMPLETED" : "SKIPPED_NO_CONFLICT", input.GlobalConflicts.Count, globalReopenCalls),
            new SemanticTransitionLedgerEntry("CANONICAL_GRAPH", "COMPLETED", canonicalGraph.Occurrences.Count, canonicalGraph.Occurrences.Count),
            new SemanticTransitionLedgerEntry("SEMANTIC_BOUNDARY", "COMPLETED", canonicalGraph.Occurrences.Count, canonicalGraph.Occurrences.Count),
            new SemanticTransitionLedgerEntry("TASK_PROJECTION", "COMPLETED", canonicalGraph.Occurrences.Count, canonicalGraph.OutlineProjection.Count),
        };

        return new(profile, context, input.CandidateHints, text,
            visualOccurrences, visualHeadings, unified, ledger, rawModelProposals,
            textTelemetry, visualTelemetry, textModelCalls, visualModelCalls)
        { CanonicalGraph = canonicalGraph, ConflictNormalization = normalization,
          SemanticAdjudicationCalls = adjudicationCalls,
          ResolvedConflictCount = resolvedConflictCount,
          UnresolvedConflictCount = unresolvedConflictCount,
          InvalidAdjudicationCount = invalidAdjudicationCount,
          GlobalReopenCalls = globalReopenCalls,
          ContractValidProposalCount = validation.ValidProposals.Count,
          ContractInvalidProposalCount = validation.Issues.Count == 0 ? 0 : semanticProposals.Count - validation.ValidProposals.Count };
    }

    private sealed record AdjudicationResolution(
        IReadOnlyList<CanonicalSemanticProposal> BindingReadyProposals,
        int AdjudicationCalls,
        int ResolvedCount,
        int UnresolvedCount,
        int InvalidCount);

    /// <summary>
    /// Production adapter over the one adjudication owner.
    /// <para>
    /// It prepares this path's context and maps the result onto the production shape. The algorithm
    /// - which conflicts to open, what to accept, what to withhold - belongs to
    /// <see cref="CanonicalSemanticClosedLoopControlPlane"/>, and there is no second copy of it
    /// here. If this method ever loops over conflicts again, the duplicate owner has come back.
    /// </para>
    /// <para>
    /// Normalization has already happened once, before contract validation filtered the proposals,
    /// and the result is passed through rather than recomputed.
    /// </para>
    /// </summary>
    private static async Task<AdjudicationResolution> ResolveConflictsAsync(
        SemanticConflictNormalizationResult normalization,
        IReadOnlyList<SemanticSourceAlias> aliases,
        CanonicalSemanticProductionInput input,
        SemanticContextPacket context,
        ICanonicalSemanticAdjudicationModel? adjudicationModel,
        string requestId,
        CancellationToken cancellationToken)
    {
        var conflictCount = normalization.Conflicts.Count + normalization.AttributeConflicts.Count;

        // No adjudicator, or nothing to adjudicate: no provider call is spent, and every conflict
        // stays unresolved rather than being quietly treated as settled.
        if (adjudicationModel is null || conflictCount == 0)
            return new(normalization.BindingReadyProposals.ToList(), 0, 0, conflictCount, 0);

        var adjudicated = await CanonicalSemanticClosedLoopControlPlane.AdjudicateAsync(
            normalization,
            aliases,
            adjudicationModel,
            context.LocalContext,
            input.GlobalContext,
            requestId,
            cancellationToken);

        return new(
            adjudicated.BindingReadyProposals,
            adjudicated.ModelCalls,
            adjudicated.BindingReadyProposals.Count - normalization.BindingReadyProposals.Count,
            adjudicated.UnresolvedCaseIds.Count,
            adjudicated.InvalidCaseIds.Count);
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
