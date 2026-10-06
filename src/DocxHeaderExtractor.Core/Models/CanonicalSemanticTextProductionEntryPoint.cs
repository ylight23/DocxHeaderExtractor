namespace DocxHeaderExtractor.Core.Models;

/// <summary>Text-only production authority used by the live DOCX route.</summary>
public sealed record CanonicalSemanticTextProductionInput(
    DocumentSourceCatalog SourceCatalog,
    IReadOnlyList<CanonicalSemanticProposal>? SemanticProposals,
    string SourceSha256,
    string? ExpectedSourceSha256,
    string? DocumentId,
    IReadOnlyList<CanonicalSemanticSourceEvidence>? SourceEvidence,
    IReadOnlyList<string> TargetEvidence,
    IReadOnlyList<string> LocalContext,
    IReadOnlyList<string> GlobalContext)
{
    public IReadOnlySet<string>? OwnedAliases { get; init; }
    public SemanticAuthorityCaptureMetadata? ReplayCapture { get; init; }

    internal CanonicalSemanticProductionInput ToLegacyInput() => new(
        SourceCatalog, SemanticProposals, SourceSha256,
        [new CanonicalSemanticPageEvidence("DOCX", true, 0, "docx-source")],
        TargetEvidence, LocalContext, GlobalContext,
        ExpectedSourceSha256: ExpectedSourceSha256,
        DocumentId: DocumentId,
        SourceEvidence: SourceEvidence)
    {
        OwnedAliases = OwnedAliases,
        ReplayCapture = ReplayCapture,
    };
}

/// <summary>Text authority and provenance; no visual or cross-modal result surface.</summary>
public sealed record CanonicalSemanticTextProductionResult(
    CanonicalSemanticPipelineResult TextPipeline,
    SemanticConflictNormalizationResult ConflictNormalization,
    IReadOnlyList<CanonicalSemanticProposal> ModelProposals,
    CanonicalSemanticInferenceTelemetry TextModelTelemetry,
    int TextModelCalls)
{
    public IReadOnlyList<SemanticContractIssue> ContractIssues { get; init; } = [];
    public int ContractValidProposalCount { get; init; }
    public int ContractInvalidProposalCount { get; init; }
    public SemanticAuthorityReplayBundle? ReplayBundle { get; init; }
    public IReadOnlyList<SemanticAuthorityTransportCall> TransportCalls { get; init; } = [];
    public int TotalModelCalls => TextModelCalls;
    public int PrimaryTextModelCalls => TextModelCalls;
    public IReadOnlyList<CanonicalSemanticProposal> NormalizedModelProposals => ConflictNormalization.NormalizedProposals;
    public IReadOnlyList<SemanticProposalConflict> SemanticConflicts => ConflictNormalization.Conflicts;
    public int SemanticAdjudicationCalls { get; init; }
    public int GlobalReopenCalls { get; init; }

    internal static CanonicalSemanticTextProductionResult FromLegacy(CanonicalSemanticProductionResult result) =>
        new(result.TextPipeline, result.ConflictNormalization, result.ModelProposals,
            result.TextModelTelemetry, result.TextModelCalls)
        {
            ContractIssues = result.ContractIssues,
            ContractValidProposalCount = result.ContractValidProposalCount,
            ContractInvalidProposalCount = result.ContractInvalidProposalCount,
            ReplayBundle = result.ReplayBundle,
            TransportCalls = result.TransportCalls,
            SemanticAdjudicationCalls = result.SemanticAdjudicationCalls,
            GlobalReopenCalls = result.GlobalReopenCalls,
        };
}

/// <summary>
/// Compatibility seam for the text-only production route. B2 removes the inactive legacy
/// multimodal/control-plane implementation after no production caller remains on it.
/// </summary>
public static class CanonicalSemanticTextProductionEntryPoint
{
    public static CanonicalSemanticTextProductionResult Run(CanonicalSemanticTextProductionInput input) =>
        CanonicalSemanticTextProductionResult.FromLegacy(
            CanonicalSemanticProductionEntryPoint.Run(input.ToLegacyInput()));

    public static async Task<CanonicalSemanticTextProductionResult> RunAsync(
        CanonicalSemanticTextProductionInput input,
        ICanonicalSemanticTextModel textModel,
        string requestId = "canonical-semantic-production",
        CancellationToken cancellationToken = default) =>
        CanonicalSemanticTextProductionResult.FromLegacy(
            await CanonicalSemanticProductionEntryPoint.RunAsync(
                input.ToLegacyInput(), textModel, requestId: requestId,
                cancellationToken: cancellationToken));
}
