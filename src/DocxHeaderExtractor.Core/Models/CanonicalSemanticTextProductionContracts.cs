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
}
