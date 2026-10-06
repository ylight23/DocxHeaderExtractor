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
    public SemanticAuthorityReplayBundle? ReplayBundle { get; init; }
    public IReadOnlyList<SemanticAuthorityTransportCall> TransportCalls { get; init; } = [];
}

/// <summary>Independent text-only production orchestration for the live DOCX route.</summary>
public static class CanonicalSemanticTextProductionEntryPoint
{
    public static CanonicalSemanticTextProductionResult Run(CanonicalSemanticTextProductionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.ReplayCapture is not null)
            throw new InvalidOperationException("REPLAY_CAPTURE_REQUIRES_ASYNC_INFERENCE");
        if (input.SemanticProposals is null)
            throw new InvalidOperationException("LIVE_INFERENCE_REQUIRES_RUN_ASYNC");
        return Bind(input, input.SemanticProposals, input.SemanticProposals, new(), [], null, 0);
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
        var capture = input.ReplayCapture is null ? null : CreateReplay(input, inference);
        return Bind(input, inference.Proposals, inference.Proposals,
            inference.Telemetry, inference.ContractIssues ?? [], capture, 1, inference.TransportCalls);
    }

    private static CanonicalSemanticTextProductionResult Bind(
        CanonicalSemanticTextProductionInput input,
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        IReadOnlyList<CanonicalSemanticProposal> modelProposals,
        CanonicalSemanticInferenceTelemetry telemetry,
        IReadOnlyList<SemanticContractIssue> parserIssues,
        SemanticAuthorityReplayBundle? replay,
        int textModelCalls,
        IReadOnlyList<SemanticAuthorityTransportCall>? transport = null)
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
            ReplayBundle = replay,
            TransportCalls = transport ?? [],
        };
    }

    private static SemanticAuthorityReplayBundle CreateReplay(
        CanonicalSemanticTextProductionInput input, CanonicalSemanticTextInferenceResult inference)
    {
        if (string.IsNullOrWhiteSpace(inference.RawModelResponseHash))
            throw new InvalidOperationException("REPLAY_CAPTURE_RAW_RESPONSE_HASH_MISSING");
        var capture = input.ReplayCapture!;
        return SemanticAuthorityReplayBundleFactory.Create(
            input.DocumentId ?? throw new InvalidOperationException("REPLAY_CAPTURE_DOCUMENT_ID_MISSING"),
            capture.SourceType, input.SourceSha256, capture.SourceUniverseHash,
            SemanticSourceAliasCatalog.FromCatalog(input.SourceCatalog), capture.ModelIdentity, capture.ModelRoute,
            capture.PromptHash, inference.RawModelResponseHash, inference.ParsedProposals ?? inference.Proposals,
            capture.GoldId, capture.GoldHash, capture.EvaluatorIdentity, capture.ManifestHash, capture.RunId,
            capture.Commit, capture.CreatedAt) with { RequestVersion = capture.RequestVersion };
    }
}
