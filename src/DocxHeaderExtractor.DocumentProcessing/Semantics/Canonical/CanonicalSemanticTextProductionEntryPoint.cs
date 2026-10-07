using DocxHeaderExtractor.Core.Semantics.Binding;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Semantics.Canonical;

namespace DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;

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
