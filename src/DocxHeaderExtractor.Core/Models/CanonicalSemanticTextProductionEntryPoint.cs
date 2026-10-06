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

/// <summary>Model-visible source authority at the text-inference boundary.</summary>
public sealed record CanonicalSemanticTextInferenceInput(
    IReadOnlyList<CanonicalSemanticSourceEvidence> SourceEvidence,
    IReadOnlyDictionary<string, string>? LayoutBlockBySourceId = null);

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

/// <summary>Independent text-only production orchestration for the live DOCX route.</summary>
public static class CanonicalSemanticTextProductionEntryPoint
{
    public static CanonicalSemanticTextProductionResult Run(CanonicalSemanticTextProductionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.SemanticProposals is null)
            throw new InvalidOperationException("LIVE_INFERENCE_REQUIRES_RUN_ASYNC");
        return Bind(input, input.SemanticProposals, input.SemanticProposals, new(), [], 0);
    }

    public static async Task<CanonicalSemanticTextProductionResult> RunAsync(
        CanonicalSemanticTextProductionInput input,
        ICanonicalSemanticTextModel textModel,
        string requestId = "canonical-semantic-production",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(textModel);
        var context = SemanticContextPacker.Pack(input.TargetEvidence, input.LocalContext, input.GlobalContext);
        var inference = await textModel.InferAsync(
            new CanonicalSemanticTextInferenceInput(input.SourceEvidence ?? []), context, requestId, cancellationToken);
        return Bind(input, inference.Proposals, inference.Proposals,
            inference.Telemetry, inference.ContractIssues ?? [], 1);
    }

    private static CanonicalSemanticTextProductionResult Bind(
        CanonicalSemanticTextProductionInput input,
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        IReadOnlyList<CanonicalSemanticProposal> modelProposals,
        CanonicalSemanticInferenceTelemetry telemetry,
        IReadOnlyList<SemanticContractIssue> parserIssues,
        int textModelCalls)
    {
        var aliases = SemanticSourceAliasCatalog.FromCatalog(input.SourceCatalog);
        var validation = SemanticCoordinateBinding.AliasSpan.ValidateProposals(
            proposals, aliases.ToDictionary(x => x.Alias, StringComparer.Ordinal), input.OwnedAliases);
        var normalization = SemanticConflictNormalizer.Normalize(validation.ValidProposals, aliases);
        var pipeline = CanonicalSemanticPipeline.RunAliases(aliases, normalization.BindingReadyProposals,
            input.SourceSha256, input.ExpectedSourceSha256, input.OwnedAliases, SemanticCoordinateBinding.AliasSpan, null);
        return new(pipeline, normalization, modelProposals, telemetry, textModelCalls)
        {
            ContractIssues = parserIssues.Concat(validation.Issues).ToArray(),
            ContractValidProposalCount = validation.ValidProposals.Count,
            ContractInvalidProposalCount = proposals.Count - validation.ValidProposals.Count,
        };
    }
}
