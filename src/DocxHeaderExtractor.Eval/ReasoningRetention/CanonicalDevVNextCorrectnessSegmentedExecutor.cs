using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Executes only the already-frozen rich-evidence segment requests. The executor never rebuilds
/// a full-input model request. It persists each raw response before parsing, rejects proposals
/// outside segment ownership, and invokes the canonical post-inference pipeline once after all
/// segments have completed.
/// </summary>
public static class CanonicalDevVNextCorrectnessSegmentedExecutor
{
    private const string Route = "ModelCapabilityCeiling";
    private const string SchemaName = "semantic_text_exact_binding_v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<CanonicalSegmentedExecutorValidation> ValidateFrozenPlanAsync(
        string preflightRoot,
        CancellationToken ct = default,
        OpenRouterCeilingReasoningModel? runtimeModel = null)
    {
        var freezePath = Path.Combine(preflightRoot, "request-freeze-manifest.v1.json");
        var planPath = Path.Combine(preflightRoot, "request-plan.v1.json");
        if (!File.Exists(freezePath) || !File.Exists(planPath))
            throw new InvalidDataException("FROZEN_SEGMENT_PLAN_MISSING");

        using var freeze = JsonDocument.Parse(await File.ReadAllTextAsync(freezePath, ct));
        using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath, ct));
        var freezeRoot = freeze.RootElement;
        var planRoot = plan.RootElement;
        var freezeStatus = freezeRoot.GetProperty("status").GetString();
        if (freezeStatus is not ("READY_FOR_DOC0116_PROVIDER_EXECUTION" or "READY_FOR_DOC0116_PROVIDER_EXECUTION_V2_1"))
            throw new InvalidDataException("FROZEN_SEGMENT_PLAN_NOT_AUTHORIZED");

        var owned = new HashSet<string>(StringComparer.Ordinal);
        var requestCount = planRoot.GetProperty("segmentCount").GetInt32();
        var requestRows = planRoot.GetProperty("requests").EnumerateArray().ToArray();
        if (requestRows.Length != requestCount)
            throw new InvalidDataException("SEGMENT_REQUEST_COUNT_MISMATCH");

        foreach (var request in requestRows)
        {
            var ordinal = request.GetProperty("requestOrdinal").GetInt32();
            var ownedAliases = request.GetProperty("ownedAliases").EnumerateArray()
                .Select(item => item.GetString()!).ToArray();
            if (!ownedAliases.All(owned.Add))
                throw new InvalidDataException("SEGMENT_OWNERSHIP_OVERLAP");

            var materializedPath = Path.Combine(preflightRoot, $"materialized-request-{ordinal:000}.v1.json");
            if (!File.Exists(materializedPath))
                throw new InvalidDataException($"MATERIALIZED_REQUEST_MISSING:{ordinal}");
            using var materialized = JsonDocument.Parse(await File.ReadAllTextAsync(materializedPath, ct));
            var root = materialized.RootElement;
            var packet = root.GetProperty("packetText").GetString()!;
            var user = root.GetProperty("userPrompt").GetString()!;
            var schema = root.GetProperty("schemaText").GetString()!;
            if (!string.Equals(Sha256(packet), request.GetProperty("serializedPayloadSha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Sha256(packet), root.GetProperty("packetSha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Sha256(user), request.GetProperty("userPromptSha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Sha256(schema), request.GetProperty("schemaSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SEGMENT_REQUEST_HASH_MISMATCH:{ordinal}");
            foreach (var property in new[] { "providerRequestBodySha256", "providerRequestBodyBytesUtf8", "providerInputUpperBoundTokens" })
            {
                if (!root.TryGetProperty(property, out _) || !request.TryGetProperty(property, out _))
                    throw new InvalidDataException($"SEGMENT_PROVIDER_RENDERING_METADATA_MISSING:{ordinal}:{property}");
                if (!JsonElementEquals(root.GetProperty(property), request.GetProperty(property)))
                    throw new InvalidDataException($"SEGMENT_PROVIDER_RENDERING_METADATA_MISMATCH:{ordinal}:{property}");
            }
        }

        var expected = freezeRoot.GetProperty("canonicalOccurrenceCount").GetInt32();
        if (owned.Count != expected || !planRoot.GetProperty("everyAliasOwnedExactlyOnce").GetBoolean())
            throw new InvalidDataException("SEGMENT_FULL_UNIVERSE_OWNERSHIP_MISMATCH");
        var effectiveInputLimit = planRoot.GetProperty("effectiveProviderInputLimit").GetInt32();
        var segmentUpperBound = planRoot.TryGetProperty("safeSegmentUpperBound", out var safeBound)
            ? safeBound.GetInt32()
            : effectiveInputLimit;
        if (segmentUpperBound > effectiveInputLimit)
            throw new InvalidDataException("SEGMENT_SAFE_UPPER_BOUND_EXCEEDS_PROVIDER_LIMIT");
        foreach (var request in requestRows)
        {
            if (request.GetProperty("providerInputUpperBoundTokens").GetInt32() > segmentUpperBound)
                throw new InvalidDataException($"SEGMENT_PROVIDER_INPUT_UPPER_BOUND_EXCEEDED:{request.GetProperty("requestOrdinal").GetInt32()}");
        }
        if (runtimeModel is not null)
            ValidateRuntimeConfiguration(freezeRoot, planRoot, runtimeModel);

        return new CanonicalSegmentedExecutorValidation(
            requestCount,
            owned.Count,
            true,
            "SEGMENTED_PLAN_HASHES_AND_OWNERSHIP_VALIDATED",
            "CanonicalDevVNextCorrectnessSegmentedExecutor",
            "ALL_SEGMENT_RESPONSES_REQUIRED_BEFORE_GLOBAL_POST_INFERENCE");
    }

    /// <summary>Provider-enabled path reserved for the separately authorized execution phase.</summary>
    public static async Task<CanonicalSemanticProductionResult> ExecuteAsync(
        string preflightRoot,
        string executionRoot,
        CanonicalSemanticProductionInput fullInput,
        OpenRouterCeilingReasoningModel model,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fullInput);
        ArgumentNullException.ThrowIfNull(model);
        var validation = await ValidateFrozenPlanAsync(preflightRoot, ct, model);
        Directory.CreateDirectory(executionRoot);
        await WriteAtomicAsync(Path.Combine(executionRoot, "executor-validation.v1.json"), validation, ct);

        using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(preflightRoot, "request-plan.v1.json"), ct));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(fullInput.SourceCatalog);
        var proposals = new List<CanonicalSemanticProposal>();

        foreach (var request in plan.RootElement.GetProperty("requests").EnumerateArray())
        {
            var ordinal = request.GetProperty("requestOrdinal").GetInt32();
            var owned = request.GetProperty("ownedAliases").EnumerateArray()
                .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
            var materializedPath = Path.Combine(preflightRoot, $"materialized-request-{ordinal:000}.v1.json");
            using var materialized = JsonDocument.Parse(await File.ReadAllTextAsync(materializedPath, ct));
            var root = materialized.RootElement;
            var packet = root.GetProperty("packetText").GetString()!;
            var system = root.GetProperty("systemPrompt").GetString()!;
            var user = root.GetProperty("userPrompt").GetString()!;
            var schemaText = root.GetProperty("schemaText").GetString()!;
            var schema = JsonDocument.Parse(schemaText).RootElement.Clone();
            var frozenWire = new FrozenWireRequestExpectation(
                root.GetProperty("providerRequestBodyBytesUtf8").GetInt32(),
                root.GetProperty("providerRequestBodySha256").GetString()!);
            var ownedSourceTextCharacters = aliases
                .Where(alias => owned.Contains(alias.Alias))
                .Sum(alias => alias.Text.Length);
            var response = await model.CompleteRawStructuredSemanticAsync(
                fullInput.DocumentId ?? "DOC-0116",
                Route,
                root.GetProperty("requestId").GetString()!,
                packet,
                ownedSourceTextCharacters,
                owned.Count,
                root.GetProperty("packet").GetProperty("sourceAliases").GetArrayLength(),
                system,
                user,
                schema,
                SchemaName,
                ct,
                frozenWire);

            // This is deliberately before Parse: a valid provider response remains forensic
            // evidence even if parsing/binding later fails.
            await WriteAtomicAsync(Path.Combine(executionRoot, $"segment-{ordinal:000}.attempt.v1.json"), new
            {
                schemaVersion = "a99-canonical-segmented-attempt-v1",
                requestOrdinal = ordinal,
                requestId = root.GetProperty("requestId").GetString(),
                requestSha256 = request.GetProperty("serializedPayloadSha256").GetString(),
                rawResponse = response.Content,
                rawResponseSha256 = Sha256(response.Content),
                telemetry = response.Telemetry,
            }, ct);

            var parsed = SemanticTextExactBindingContract.Parse(response.Content);
            var outsideOwnership = parsed.Headings
                .Where(item => !owned.Contains(item.Source))
                .Select(item => item.Source)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (outsideOwnership.Length > 0)
                throw new InvalidDataException($"SEGMENT_PROPOSAL_OUTSIDE_OWNERSHIP:{ordinal}:{string.Join(',', outsideOwnership)}");
            proposals.AddRange(parsed.Headings.Select(item => new CanonicalSemanticProposal(
                item.Source, true, item.Text, SemanticRole: item.Role, Occurrence: item.Occurrence,
                LeftExactContext: item.LeftExactContext, RightExactContext: item.RightExactContext)));
            await WriteAtomicAsync(Path.Combine(executionRoot, $"segment-{ordinal:000}.parsed.v1.json"), new
            {
                schemaVersion = "a99-canonical-segmented-parsed-v1",
                requestOrdinal = ordinal,
                proposalCount = parsed.Headings.Count,
                proposals = parsed.Headings,
            }, ct);
        }

        // The only post-segment semantic invocation is the merged whole-document post-inference
        // path. It performs normalization/binding/graph resolution without another provider call.
        var mergedInput = fullInput with { SemanticProposals = proposals };
        var production = CanonicalSemanticProductionEntryPoint.Run(mergedInput);
        await WriteAtomicAsync(Path.Combine(executionRoot, "prediction-freeze.v1.json"), new
        {
            schemaVersion = "a99-canonical-segmented-prediction-freeze-v1",
            documentId = fullInput.DocumentId,
            segmentCount = validation.SegmentCount,
            segmentOwnedOccurrenceCount = validation.OwnedOccurrenceCount,
            mergedProposalCount = proposals.Count,
            globalPostInferenceRuns = 1,
            canonicalOccurrenceCount = fullInput.SourceCatalog.Units.Count,
            canonicalGraphOccurrenceCount = production.CanonicalOccurrences.Count,
            providerCalls = model.ProviderCalls,
            goldReads = 0,
            scoring = false,
        }, ct);
        return production;
    }

    private static async Task WriteAtomicAsync(string path, object value, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);
        File.Move(temp, path, true);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool JsonElementEquals(JsonElement left, JsonElement right) =>
        left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String
            ? string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal)
            : left.ToString() == right.ToString();

    private static void ValidateRuntimeConfiguration(
        JsonElement freezeRoot,
        JsonElement planRoot,
        OpenRouterCeilingReasoningModel model)
    {
        var expectedModel = freezeRoot.GetProperty("model").GetString();
        var expectedEndpoint = freezeRoot.GetProperty("endpoint").GetString();
        var expectedRequestTimeout = freezeRoot.GetProperty("requestTimeoutSeconds").GetInt32();
        var expectedMaxOutput = planRoot.GetProperty("maxOutputTokens").GetInt32();
        var expectedAttemptDeadline = freezeRoot.GetProperty("perAttemptHardTimeoutSeconds").GetInt32();

        if (!string.Equals(model.ConfiguredModel, expectedModel, StringComparison.Ordinal))
            throw new InvalidDataException("FROZEN_RUNTIME_MODEL_MISMATCH");
        if (!string.Equals(model.ConfiguredEndpoint.ToString(), expectedEndpoint, StringComparison.Ordinal))
            throw new InvalidDataException("FROZEN_RUNTIME_ENDPOINT_MISMATCH");
        if (model.ConfiguredRequestTimeoutSeconds != expectedRequestTimeout)
            throw new InvalidDataException("FROZEN_RUNTIME_REQUEST_TIMEOUT_MISMATCH");
        if (model.SemanticMaxCompletionTokens != expectedMaxOutput)
            throw new InvalidDataException("FROZEN_RUNTIME_MAX_OUTPUT_MISMATCH");
        if (model.AttemptDeadlineSeconds != expectedAttemptDeadline)
            throw new InvalidDataException("FROZEN_RUNTIME_ATTEMPT_DEADLINE_MISMATCH");
    }
}

public sealed record CanonicalSegmentedExecutorValidation(
    int SegmentCount,
    int OwnedOccurrenceCount,
    bool HashesAndOwnershipValid,
    string ValidationStatus,
    string ExecutorType,
    string MergePolicy);
