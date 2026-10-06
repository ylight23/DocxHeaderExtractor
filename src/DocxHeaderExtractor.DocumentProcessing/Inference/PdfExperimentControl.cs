using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Inference;

public sealed record PdfExperimentGoldIdentity(
    [property: JsonPropertyName("artifactPath")] string ArtifactPath,
    [property: JsonPropertyName("artifactSha256")] string ArtifactSha256,
    [property: JsonPropertyName("headingClaimCount")] int HeadingClaimCount,
    [property: JsonPropertyName("occurrenceEvaluable")] bool OccurrenceEvaluable);

public sealed record PdfExperimentSemanticAuthorityIdentity(
    [property: JsonPropertyName("artifactPath")] string ArtifactPath,
    [property: JsonPropertyName("artifactSha256")] string ArtifactSha256,
    [property: JsonPropertyName("semanticHeadingTotal")] int SemanticHeadingTotal);

public sealed record PdfExperimentModelIdentity(
    [property: JsonPropertyName("providerIdentifier")] string ProviderIdentifier,
    [property: JsonPropertyName("modelIdentifier")] string ModelIdentifier,
    [property: JsonPropertyName("transportProtocol")] string TransportProtocol);

public sealed record PdfExperimentPromptIdentity(
    [property: JsonPropertyName("profileVersion")] string ProfileVersion,
    [property: JsonPropertyName("promptSha256")] string PromptSha256);

public sealed record PdfExperimentPacketIdentity(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("packetSha256")] string PacketSha256);

public sealed record PdfExperimentRoutingIdentity(
    [property: JsonPropertyName("enabledStages")] IReadOnlyList<string> EnabledStages,
    [property: JsonPropertyName("retryPolicyIdentity")] string RetryPolicyIdentity,
    [property: JsonPropertyName("visualRecoveryEnabled")] bool VisualRecoveryEnabled,
    [property: JsonPropertyName("semanticAdjudicationEnabled")] bool SemanticAdjudicationEnabled,
    [property: JsonPropertyName("globalReopenEnabled")] bool GlobalReopenEnabled,
    [property: JsonPropertyName("placementRetryEnabled")] bool PlacementRetryEnabled);

public sealed record PdfExperimentBudgetIdentity(
    [property: JsonPropertyName("maximumProviderCalls")] int MaximumProviderCalls);

public sealed record PdfExperimentEvaluatorIdentity(
    [property: JsonPropertyName("occurrenceEvaluatorContractVersion")] string OccurrenceEvaluatorContractVersion,
    [property: JsonPropertyName("semanticRoleEvaluationEnabled")] bool SemanticRoleEvaluationEnabled,
    [property: JsonPropertyName("hierarchyEvaluationMode")] string HierarchyEvaluationMode);

/// <summary>
/// Immutable identity for one provider experiment. It is an execution authority, never semantic
/// authority: changing any identity-bearing field creates a different experiment.
/// </summary>
public sealed record PdfExperimentManifest(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("experimentId")] string ExperimentId,
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("sourceUniverseSha256")] string SourceUniverseSha256,
    [property: JsonPropertyName("occurrenceGold")] PdfExperimentGoldIdentity OccurrenceGold,
    [property: JsonPropertyName("semanticAuthority")] PdfExperimentSemanticAuthorityIdentity SemanticAuthority,
    [property: JsonPropertyName("model")] PdfExperimentModelIdentity Model,
    [property: JsonPropertyName("prompt")] PdfExperimentPromptIdentity Prompt,
    [property: JsonPropertyName("sourcePacket")] PdfExperimentPacketIdentity SourcePacket,
    [property: JsonPropertyName("routing")] PdfExperimentRoutingIdentity Routing,
    [property: JsonPropertyName("budget")] PdfExperimentBudgetIdentity Budget,
    [property: JsonPropertyName("evaluator")] PdfExperimentEvaluatorIdentity Evaluator,
    [property: JsonPropertyName("semanticContractHash")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SemanticContractHash = null,
    // Packing is an execution dimension. A manifest must declare it before the lane transports;
    // an omitted policy is refused, never read as a default.
    [property: JsonPropertyName("packingPolicy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PackingPolicyId = null)
{
    [JsonIgnore]
    public string ManifestHash => PdfExperimentManifestHasher.Compute(this);
}

public sealed record PdfExperimentRuntimeBinding(
    string DocumentId,
    string SourceSha256,
    string SourceUniverseSha256,
    string OccurrenceGoldSha256,
    int OccurrenceGoldHeadingClaimCount,
    bool OccurrenceEvaluable,
    string SemanticAuthoritySha256,
    int SemanticHeadingTotal,
    string PromptSha256,
    string SourcePacketSha256,
    string ProviderIdentifier,
    string ModelIdentifier,
    string TransportProtocol,
    PdfExperimentRoutingIdentity Routing,
    string OccurrenceEvaluatorContractVersion,
    bool SemanticRoleEvaluationEnabled,
    string? PackingPolicyId = null);

public sealed record PdfExperimentApproval(
    string ManifestHash,
    string ApprovalId,
    string ApprovedBy);

public static class PdfExperimentManifestHasher
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static string Compute(PdfExperimentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return HashCanonicalJson(JsonSerializer.Serialize(manifest, Json));
    }

    public static string Serialize(PdfExperimentManifest manifest) =>
        JsonSerializer.Serialize(manifest, Json);

    public static string HashCanonicalJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var canonical = json.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return Convert.ToHexStringLower(SHA256.HashData(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(canonical)));
    }
}

/// <summary>
/// Fail-closed manifest/approval validation and one total provider-call budget for an experiment.
/// </summary>
public sealed class PdfExperimentExecutionGate
{
    private readonly PdfExperimentManifest _manifest;
    private readonly PdfExperimentApproval? _approval;
    private readonly PdfExperimentRuntimeBinding _runtime;
    private readonly object _sync = new();
    private int _providerCalls;

    public PdfExperimentExecutionGate(
        PdfExperimentManifest manifest,
        PdfExperimentApproval? approval,
        PdfExperimentRuntimeBinding runtime)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _approval = approval;
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public PdfExperimentManifest Manifest => _manifest;
    public int ProviderCalls => Volatile.Read(ref _providerCalls);

    public void EnsureReady()
    {
        if (_approval is null)
            throw new InvalidOperationException("PDF_EXPERIMENT_APPROVAL_REQUIRED");
        if (!string.Equals(_approval.ManifestHash, _manifest.ManifestHash, StringComparison.Ordinal))
            throw new InvalidOperationException("PDF_EXPERIMENT_APPROVAL_MANIFEST_MISMATCH");
        if (string.IsNullOrWhiteSpace(_approval.ApprovalId) || string.IsNullOrWhiteSpace(_approval.ApprovedBy))
            throw new InvalidOperationException("PDF_EXPERIMENT_APPROVAL_IDENTITY_MISSING");

        Require(_runtime.DocumentId, _manifest.DocumentId, "DOCUMENT_ID_MISMATCH");
        Require(_runtime.SourceSha256, _manifest.SourceSha256, "SOURCE_SHA_MISMATCH");
        Require(_runtime.SourceUniverseSha256, _manifest.SourceUniverseSha256, "SOURCE_UNIVERSE_SHA_MISMATCH");
        Require(_runtime.OccurrenceGoldSha256, _manifest.OccurrenceGold.ArtifactSha256, "OCCURRENCE_GOLD_MISMATCH");
        Require(_runtime.OccurrenceGoldHeadingClaimCount, _manifest.OccurrenceGold.HeadingClaimCount, "OCCURRENCE_GOLD_COUNT_MISMATCH");
        Require(_runtime.OccurrenceEvaluable, _manifest.OccurrenceGold.OccurrenceEvaluable, "OCCURRENCE_EVALUABLE_MISMATCH");
        Require(_runtime.SemanticAuthoritySha256, _manifest.SemanticAuthority.ArtifactSha256, "SEMANTIC_AUTHORITY_MISMATCH");
        Require(_runtime.SemanticHeadingTotal, _manifest.SemanticAuthority.SemanticHeadingTotal, "SEMANTIC_TOTAL_MISMATCH");
        Require(_runtime.PromptSha256, _manifest.Prompt.PromptSha256, "PROMPT_HASH_MISMATCH");
        Require(_runtime.SourcePacketSha256, _manifest.SourcePacket.PacketSha256, "SOURCE_PACKET_HASH_MISMATCH");
        Require(_runtime.ProviderIdentifier, _manifest.Model.ProviderIdentifier, "PROVIDER_MISMATCH");
        Require(_runtime.ModelIdentifier, _manifest.Model.ModelIdentifier, "MODEL_MISMATCH");
        Require(_runtime.TransportProtocol, _manifest.Model.TransportProtocol, "TRANSPORT_MISMATCH");
        if (!RoutingEquals(_runtime.Routing, _manifest.Routing))
            throw new InvalidOperationException("PDF_EXPERIMENT_ROUTING_MISMATCH");
        Require(_runtime.OccurrenceEvaluatorContractVersion,
            _manifest.Evaluator.OccurrenceEvaluatorContractVersion,
            "EVALUATOR_CONTRACT_MISMATCH");
        Require(_runtime.SemanticRoleEvaluationEnabled,
            _manifest.Evaluator.SemanticRoleEvaluationEnabled,
            "SEMANTIC_ROLE_EVALUATION_MISMATCH");

        if (_manifest.SemanticContractHash is { } manifestSemanticContractHash)
        {
            Require(manifestSemanticContractHash,
                SemanticCoordinateContract.PdfSemanticFunctionMembershipV1.SchemaHash(),
                "SEMANTIC_CONTRACT_HASH_MISMATCH");
        }

        if (_manifest.PackingPolicyId is { } manifestPackingPolicy)
        {
            Require(_runtime.PackingPolicyId ?? SemanticEvidencePackingPolicies.ResourceBoundedSourcePackingV1Id,
                manifestPackingPolicy,
                "PACKING_POLICY_MISMATCH");
        }
    }

    /// <summary>
    /// Binds a live provider route to the semantic contract used by the parser and replay bundle.
    /// A successor manifest must carry this identity; historical manifests without it are not
    /// eligible for live transport. This runs before the lane starts and before call accounting.
    /// </summary>
    internal void EnsureLiveSemanticContract()
    {
        EnsureReady();
        if (string.IsNullOrWhiteSpace(_manifest.SemanticContractHash))
            throw new InvalidOperationException("PDF_EXPERIMENT_SEMANTIC_CONTRACT_AUTHORITY_MISSING");

        // The schema the PDF lane actually sends; there is one PDF contract.
        Require(_manifest.SemanticContractHash,
            SemanticCoordinateContract.PdfSemanticFunctionMembershipV1.SchemaHash(),
            "SEMANTIC_CONTRACT_HASH_MISMATCH");
    }

    /// <summary>
    /// Binds the execution gate to the source universe actually parsed by the live PDF route.
    /// This is deliberately checked before the lane starts: a manifest-bound runtime binding can
    /// otherwise look valid while the parser has produced a different universe identity, allowing
    /// transport to spend calls before replay capture reports the mismatch.
    /// </summary>
    internal void EnsureLiveSourceUniverse(string sourceUniverseSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUniverseSha256);
        EnsureReady();
        Require(sourceUniverseSha256, _manifest.SourceUniverseSha256,
            "LIVE_SOURCE_UNIVERSE_SHA_MISMATCH");
        Require(sourceUniverseSha256, _runtime.SourceUniverseSha256,
            "LIVE_RUNTIME_SOURCE_UNIVERSE_SHA_MISMATCH");
    }

    /// <summary>
    /// Binds the runtime request partition to the manifest. The manifest must declare it: an
    /// omitted policy is refused, never read as some historical default.
    /// </summary>
    internal void EnsureLivePackingPolicy(string runtimePackingPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePackingPolicy);
        EnsureReady();
        var declared = _manifest.PackingPolicyId
            ?? throw new InvalidOperationException("PDF_EXPERIMENT_PACKING_POLICY_DECLARATION_MISSING");
        Require(runtimePackingPolicy, declared, "PACKING_POLICY_MISMATCH");
        if (_runtime.PackingPolicyId is { } runtimeBinding)
            Require(runtimePackingPolicy, runtimeBinding, "RUNTIME_PACKING_POLICY_MISMATCH");
    }

    public void ReserveProviderCall(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
            throw new ArgumentException("A provider call stage is required.", nameof(stage));
        EnsureReady();
        lock (_sync)
        {
            if (_providerCalls >= _manifest.Budget.MaximumProviderCalls)
                throw new InvalidOperationException("PDF_EXPERIMENT_PROVIDER_CALL_BUDGET_EXCEEDED");
            _providerCalls++;
        }
    }

    private static bool RoutingEquals(PdfExperimentRoutingIdentity left, PdfExperimentRoutingIdentity right) =>
        left.VisualRecoveryEnabled == right.VisualRecoveryEnabled &&
        left.SemanticAdjudicationEnabled == right.SemanticAdjudicationEnabled &&
        left.GlobalReopenEnabled == right.GlobalReopenEnabled &&
        left.PlacementRetryEnabled == right.PlacementRetryEnabled &&
        string.Equals(left.RetryPolicyIdentity, right.RetryPolicyIdentity, StringComparison.Ordinal) &&
        left.EnabledStages.SequenceEqual(right.EnabledStages, StringComparer.Ordinal);

    private static void Require(string actual, string expected, string code)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"PDF_EXPERIMENT_{code}");
    }

    private static void Require(int actual, int expected, string code)
    {
        if (actual != expected) throw new InvalidOperationException($"PDF_EXPERIMENT_{code}");
    }

    private static void Require(bool actual, bool expected, string code)
    {
        if (actual != expected) throw new InvalidOperationException($"PDF_EXPERIMENT_{code}");
    }
}

/// <summary>Classifier decorator that makes the execution gate the provider-call boundary.</summary>
public sealed class PdfExperimentGatedHeaderClassifier : IFrozenRequestHeaderClassifier
{
    private readonly IHeaderClassifier _inner;
    private readonly PdfExperimentExecutionGate _gate;
    private readonly bool _disposeInner;

    public PdfExperimentGatedHeaderClassifier(
        IHeaderClassifier inner,
        PdfExperimentExecutionGate gate,
        bool disposeInner = true)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _disposeInner = disposeInner;
    }

    public string ModelName => _inner.ModelName;
    public int ContextSize => _inner.ContextSize;
    public string RuntimeDescription => _inner.RuntimeDescription;
    public int SharedPrefixTokens => _inner.SharedPrefixTokens;

    public Task<string> BoundaryCutAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken ct = default,
        int expectedItemCount = 0)
    {
        _gate.ReserveProviderCall("boundary-cut");
        return _inner.BoundaryCutAsync(systemPrompt, userMessage, ct, expectedItemCount);
    }

    public Task<FrozenHeaderExecutionResult> ExecuteFrozenRequestAsync(
        byte[] providerBody, int maxTokens, string systemPrompt, string userMessage,
        CancellationToken cancellationToken = default)
    {
        _gate.ReserveProviderCall("frozen-request");
        if (_inner is not IFrozenRequestHeaderClassifier frozen)
            throw new InvalidOperationException("PDF_H2C_PRODUCTION_ROUTE_REQUIRES_FROZEN_REQUEST_TRANSPORT");
        return frozen.ExecuteFrozenRequestAsync(providerBody, maxTokens, systemPrompt, userMessage, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposeInner) _inner.Dispose();
    }
}
