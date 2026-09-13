using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Oracle-selected, diagnostic-only pair verification. Structural Gold selects the pair but is
/// never sent as evidence and never used for production scoring or tuning. Exactly one verifier
/// request is issued; this runner must never be promoted to a production benchmark.
/// </summary>
public static class HdsaGlobalIdentityOraclePairDiagnosticRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string DocumentId = "DOC-0205";
    private const string CatalogPath = "eval/a99-closed-loop/hdsa-semantic-node-production-live/DOC-0205/semantic-catalog.v1.json";
    private const string FrozenA1Path = "eval/a99-closed-loop/hdsa-global-role-identity-v2-live/DOC-0205/attempt-2-keyed-a1/a1-role/prediction.v1.json";
    private const string StructuralGoldPath = "eval/a99-closed-loop/hdsa-parent-relation-live/DOC-0205/structural-gold.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-global-identity-oracle-pair-diagnostic/DOC-0205/S0014-S0015/attempt-2-authoritative";
    private const string ExpectedCatalogFingerprint = "5948fb130cdf730660991a7a83ceb5aab461374a1eb86ec5f40f112b7f64480e";
    private const string Prompt = """
You are an isolated A99 semantic-identity pair verifier. The target pair was selected by an
external diagnostic selector. Verify only this pair using the complete source-backed outline,
frozen structural roles, exact text, document order, and parser-owned context.

CONTINUATION_OF means the right occurrence continues the logical heading begun at the left
occurrence; use RIGHT_TO_LEFT. SAME_SEMANTIC_REPEAT means the same logical heading is repeated
without continuation; use NONE. DISTINCT means separate identities; UNRESOLVED means evidence
is insufficient. Related, nearby, or sequential headings are not continuation merely because
they are related. Return exactly the target pair and no explanation, groups, merge instruction,
parent, hierarchy, level, offsets, Gold, or legacy fields.
""";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Full(repoRoot, OutputRoot);
        Directory.CreateDirectory(output);
        var catalog = ReadCatalog(Full(repoRoot, CatalogPath));
        if (catalog.CatalogFingerprint != ExpectedCatalogFingerprint)
            return await BlockAsync(output, "CATALOG_FINGERPRINT_MISMATCH", 0, 0, ct);
        var oracle = ReadOraclePair(Full(repoRoot, StructuralGoldPath), catalog.SourceSha256);
        if (!oracle.Valid)
            return await BlockAsync(output, oracle.Reason!, 0, 0, ct);
        var a1 = ReadFrozenA1(Full(repoRoot, FrozenA1Path), catalog);
        if (!a1.Accepted)
            return await BlockAsync(output, "FROZEN_A1_INVALID:" + a1.Reason, 0, 0, ct);
        var roles = a1.Nodes.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        var nodes = Ordered(catalog).Select(item => new HdsaIdentityRoleNodeInput(
            item.SemanticNodeId, item.MemberOccurrenceIds, item.CanonicalText, item.SourceOrder,
            roles[item.SemanticNodeId].StructuralRole, roles[item.SemanticNodeId].OutlineBearing)).ToArray();
        var leftNodeId = catalog.Entries.SingleOrDefault(item =>
            item.MemberOccurrenceIds.Contains(oracle.LeftAlias, StringComparer.Ordinal))?.SemanticNodeId;
        var rightNodeId = catalog.Entries.SingleOrDefault(item =>
            item.MemberOccurrenceIds.Contains(oracle.RightAlias, StringComparer.Ordinal))?.SemanticNodeId;
        var target = leftNodeId is null || rightNodeId is null ? null : BuildPairs(nodes).SingleOrDefault(item =>
            (item.Left == leftNodeId && item.Right == rightNodeId) ||
            (item.Left == rightNodeId && item.Right == leftNodeId));
        if (target is null)
            return await BlockAsync(output, "ORACLE_PAIR_NOT_IN_CATALOG", 0, 0, ct);

        var request = new HdsaIdentityPairVerificationRequest(catalog.CatalogFingerprint, nodes, target, true);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var requestHash = Sha256Text(requestJson);
        await WriteJsonAsync(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-oracle-pair-diagnostic-v1",
            documentId = DocumentId, model = Model, endpoint = Endpoint,
            sourceSha256 = catalog.SourceSha256, catalogFingerprint = catalog.CatalogFingerprint,
            frozenA1Path = FrozenA1Path, structuralGoldPath = StructuralGoldPath,
            oracleSelection = new { left = oracle.LeftAlias, right = oracle.RightAlias,
                leftNodeId, rightNodeId,
                source = "STRUCTURAL_GOLD_DIAGNOSTIC_ONLY" },
            requestSha256 = requestHash, verificationSchemaSha256 = Sha256Text(JsonSerializer.Serialize(
                HdsaGlobalIdentityRetrieveVerifyContract.VerificationSchema())),
            goldDerivedInput = true, oracleDiagnostic = true, productionClaim = false,
            modelTuningAllowed = false, exactlyOneProviderCall = true, stageBExecuted = false,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "request.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-oracle-pair-request-v1",
            documentId = DocumentId, request, requestSha256 = requestHash,
            goldDerivedInput = true, oracleDiagnostic = true, productionClaim = false,
        }, ct);

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return await BlockAsync(output, "OPENROUTER_API_KEY_MISSING", 0, 0, ct);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot,
            "hdsa-global-identity-oracle-pair-diagnostic", DocumentId, ct);
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = key,
            ContextSize = 1_000_000, MaxOutputTokens = 16_000, RequestTimeoutSeconds = 600,
            TransientRequestRetries = 0, MaxParallelRequests = 1, SendChatTemplateKwargs = false,
            OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || capability.ModelId != Model || !capability.ReasoningSupported || !capability.StructuredOutputSupported)
            return await BlockAsync(output, "MODEL_CAPABILITY_MISMATCH", 0, 0, ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        (string Content, RequestPacketTelemetry Telemetry, long ElapsedMs) result;
        try
        {
            result = await CompleteAsync(model, requestJson, Prompt, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ReasoningCompletionException)
        {
            return await BlockAsync(output, "PAIR_VERIFIER_PROVIDER_FAILURE:" + ex.GetType().Name,
                model.ProviderCalls, model.ProviderCalls, ct);
        }
        var responseHash = Sha256Text(result.Content);
        await WriteJsonAsync(Path.Combine(output, "response.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-oracle-pair-response-v1",
            documentId = DocumentId, requestSha256 = requestHash, responseSha256 = responseHash,
            rawResponse = result.Content, provider = result.Telemetry.ProviderRoute,
            finishReason = result.Telemetry.FinishReason,
            inputTokens = result.Telemetry.ReportedInputTokens,
            reasoningTokens = result.Telemetry.ReportedReasoningTokens,
            outputTokens = result.Telemetry.ReportedOutputTokens, elapsedMs = result.ElapsedMs,
            modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
            goldReadBeforeFreeze = false, oracleDiagnostic = true,
        }, ct);
        HdsaIdentityPairVerificationResponse? response = null;
        string? parseError = null;
        try { response = HdsaGlobalIdentityRetrieveVerifyContract.ParseVerification(result.Content); }
        catch (Exception ex) when (ex is FormatException or JsonException) { parseError = ex.Message; }
        var validation = response is null
            ? new HdsaIdentityPairVerificationValidation(false, parseError, null)
            : HdsaGlobalIdentityRetrieveVerifyContract.ValidateOracleVerification(request, response);
        await WriteJsonAsync(Path.Combine(output, "prediction.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-oracle-pair-prediction-v1",
            documentId = DocumentId, requestSha256 = requestHash, responseSha256 = responseHash,
            response, parseError, validation, oracleDiagnostic = true, productionClaim = false,
            goldReadBeforeFreeze = false, frozenBeforeGold = true,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "freeze.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-oracle-pair-freeze-v1",
            documentId = DocumentId, sourceSha256 = catalog.SourceSha256,
            catalogFingerprint = catalog.CatalogFingerprint, requestSha256 = requestHash,
            responseSha256 = responseHash, predictionSha256 = Sha256File(Path.Combine(output, "prediction.v1.json")),
            pair = new { left = oracle.LeftAlias, right = oracle.RightAlias, leftNodeId, rightNodeId },
            oracleDiagnostic = true, productionClaim = false, modelTuningAllowed = false,
            provider = result.Telemetry.ProviderRoute, model = Model,
            finishReason = result.Telemetry.FinishReason,
            inputTokens = result.Telemetry.ReportedInputTokens,
            reasoningTokens = result.Telemetry.ReportedReasoningTokens,
            outputTokens = result.Telemetry.ReportedOutputTokens, elapsedMs = result.ElapsedMs,
            modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
            goldReadBeforeFreeze = false, frozenBeforeGold = true,
        }, ct);
        var classification = validation.Accepted && response is not null
            ? response.Relation switch
            {
                "CONTINUATION_OF" or "SAME_SEMANTIC_REPEAT" => "VERIFIER_CAPABILITY_OBSERVED_POSITIVE",
                "DISTINCT" or "UNRESOLVED" => "VERIFIER_CAPABILITY_NOT_OBSERVED",
                _ => "MALFORMED_OR_OUT_OF_UNIVERSE",
            }
            : "MALFORMED_OR_OUT_OF_UNIVERSE";
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-oracle-pair-diagnostic-summary-v1",
            status = "DIAGNOSTIC_COMPLETE", classification, documentId = DocumentId,
            pair = new { left = oracle.LeftAlias, right = oracle.RightAlias },
            validation, model = Model, provider = result.Telemetry.ProviderRoute,
            modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
            goldDerivedInput = true, oracleDiagnostic = true, productionClaim = false,
            modelTuningAllowed = false, goldReadBeforeFreeze = false, predictionFrozenBeforeGold = true,
        }, ct);
        Console.WriteLine("HDSA_GLOBAL_IDENTITY_ORACLE_PAIR_STATUS=DIAGNOSTIC_COMPLETE");
        Console.WriteLine("PAIR=S0014,S0015");
        Console.WriteLine("CLASSIFICATION=" + classification);
        Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
        Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
        Console.WriteLine("GOLD_DERIVED_INPUT=1");
        Console.WriteLine("PRODUCTION_CLAIM=0");
        return 0;
    }

    private static async Task<(string Content, RequestPacketTelemetry Telemetry, long ElapsedMs)> CompleteAsync(
        OpenRouterCeilingReasoningModel model, string requestJson, string prompt, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await model.CompleteRawStructuredSemanticAsync(DocumentId,
            "HDSA_GLOBAL_IDENTITY_ORACLE_PAIR", "HDSA_GLOBAL_IDENTITY_ORACLE_PAIR",
            requestJson, requestJson.Length, 1, 1, prompt,
            $"TASK=HDSA_GLOBAL_IDENTITY_ORACLE_PAIR\n{requestJson}\nReturn exactly the requested JSON object.",
            HdsaGlobalIdentityRetrieveVerifyContract.VerificationSchema(),
            "hdsa_global_identity_oracle_pair_v1", ct);
        stopwatch.Stop();
        return (result.Content, result.Telemetry, stopwatch.ElapsedMilliseconds);
    }

    private static IReadOnlyList<HdsaIdentityCandidatePair> BuildPairs(IReadOnlyList<HdsaIdentityRoleNodeInput> nodes)
    {
        var pairs = new List<HdsaIdentityCandidatePair>();
        for (var i = 0; i < nodes.Count; i++)
            for (var j = i + 1; j < nodes.Count; j++)
                pairs.Add(new($"P{i + 1:D2}-{j + 1:D2}", nodes[i].NodeId, nodes[j].NodeId));
        return pairs;
    }

    private static OraclePair ReadOraclePair(string path, string sourceSha256)
    {
        if (!File.Exists(path)) return new(false, "STRUCTURAL_GOLD_MISSING", "", "", "", "");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("sourceSha256").GetString() != sourceSha256)
            return new(false, "STRUCTURAL_GOLD_SOURCE_MISMATCH", "", "", "", "");
        var rows = root.GetProperty("occurrences").EnumerateArray()
            .Where(item => item.GetProperty("sourceAlias").GetString() is "S0014" or "S0015")
            .ToArray();
        if (rows.Length != 2 || rows.Any(item => item.GetProperty("reviewStatus").GetString() != "RESOLVED"))
            return new(false, "ORACLE_PAIR_NOT_RESOLVED_IN_GOLD", "", "", "", "");
        var left = rows.Single(item => item.GetProperty("sourceAlias").GetString() == "S0014");
        var right = rows.Single(item => item.GetProperty("sourceAlias").GetString() == "S0015");
        var leftNode = left.GetProperty("semanticNodeId").GetString();
        var rightNode = right.GetProperty("semanticNodeId").GetString();
        if (leftNode is null || rightNode is null || leftNode != rightNode ||
            right.GetProperty("occurrenceRelation").GetString() != "CONTINUATION")
            return new(false, "ORACLE_PAIR_GOLD_RELATION_MISMATCH", "", "", "", "");
        return new(true, null, "S0014", "S0015", "", "");
    }

    private static FrozenA1 ReadFrozenA1(string path, HdsaFrozenSemanticNodeCatalog catalog)
    {
        if (!File.Exists(path)) return new(false, "FROZEN_A1_MISSING", []);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("goldReadBeforeFreeze").GetBoolean() || root.GetProperty("goldDerivedInput").GetBoolean())
            return new(false, "FROZEN_A1_GOLD_CONTAMINATION", []);
        var proposal = HdsaGlobalRoleClassificationContract.Parse(root.GetProperty("proposal").GetRawText());
        var validation = HdsaGlobalRoleClassificationContract.Validate(catalog.SemanticNodeIds, proposal);
        return validation.Accepted ? new(true, null, validation.Nodes) : new(false, validation.RejectionReason, []);
    }

    private static HdsaFrozenSemanticNodeCatalog ReadCatalog(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("ORACLE_CATALOG_MISSING", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var entries = root.GetProperty("entries").EnumerateArray().Select(item => new
        {
            Id = item.GetProperty("semanticNodeId").GetString()!,
            Aliases = item.GetProperty("memberOccurrenceIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            Text = item.GetProperty("canonicalText").GetString()!, Order = item.GetProperty("sourceOrder").GetInt32(),
        }).ToArray();
        var input = new HdsaSemanticNodeResolutionInput(root.GetProperty("sourceSha256").GetString()!,
            root.GetProperty("preprocessingSnapshotHash").GetString()!,
            entries.SelectMany(item => item.Aliases.Select(alias => new HdsaSemanticNodeSourceOccurrence(alias, item.Order, item.Text))).ToArray(), false);
        var version = root.GetProperty("resolverVersion").GetString()!;
        var predictions = entries.Select(item => new HdsaSemanticNodePrediction(item.Id, item.Aliases, item.Text, "FROZEN_SAFE_CATALOG", version, false)).ToArray();
        var result = new HdsaSemanticNodeResolutionV3Result(input.SourceSha256, input.PreprocessingSnapshotHash,
            predictions, [], [], [], version, false);
        var catalog = HdsaSemanticNodeCatalogBuilder.Build(input, result);
        if (catalog.CatalogFingerprint != root.GetProperty("catalogFingerprint").GetString())
            throw new InvalidDataException("ORACLE_CATALOG_RECONSTRUCTION_MISMATCH");
        return catalog;
    }

    private static IEnumerable<HdsaSemanticNodeCatalogEntry> Ordered(HdsaFrozenSemanticNodeCatalog catalog) =>
        catalog.Entries.OrderBy(item => item.SourceOrder).ThenBy(item => item.SemanticNodeId, StringComparer.Ordinal);

    private static async Task<int> BlockAsync(string output, string reason, int modelCalls, int providerCalls, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-oracle-pair-diagnostic-summary-v1",
            status = "BLOCKED", reason, modelCalls, providerCalls, oracleDiagnostic = true,
            productionClaim = false, goldReadBeforeFreeze = false,
        }, ct);
        Console.WriteLine("HDSA_GLOBAL_IDENTITY_ORACLE_PAIR_STATUS=BLOCKED");
        Console.WriteLine("REASON=" + reason);
        Console.WriteLine($"MODEL_CALLS={modelCalls}");
        Console.WriteLine($"PROVIDER_CALLS={providerCalls}");
        return 1;
    }

    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Sha256File(string path) => Sha256Text(File.ReadAllText(path));
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record FrozenA1(bool Accepted, string? Reason, IReadOnlyList<HdsaGlobalRoleNodeProposal> Nodes);
    private sealed record OraclePair(bool Valid, string? Reason, string LeftAlias, string RightAlias, string LeftNodeId, string RightNodeId);
}
