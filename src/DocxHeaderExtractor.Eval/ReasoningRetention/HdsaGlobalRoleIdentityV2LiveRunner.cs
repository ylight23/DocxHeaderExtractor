using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Stage-A-only challenger: classify structural role first, then reason explicit identity
/// relations over the whole catalog. It deliberately stops before hierarchy until both H1 and H2
/// are observed, so parent quality cannot confound identity quality.
/// </summary>
public static class HdsaGlobalRoleIdentityV2LiveRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string DocumentId = "DOC-0205";
    private const string CatalogPath = "eval/a99-closed-loop/hdsa-semantic-node-production-live/DOC-0205/semantic-catalog.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-global-role-identity-v2-live/DOC-0205/attempt-2-keyed-a1";
    private const string ExpectedCatalogFingerprint = "5948fb130cdf730660991a7a83ceb5aab461374a1eb86ec5f40f112b7f64480e";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string RolePrompt = """
You are the A99 structural-role classifier. The request contains every source-backed semantic
candidate in deterministic document order. Classify what each node does in the document and
whether it bears the semantic outline. Do not decide parent, identity grouping, continuation,
repeat, or level.

DOCUMENT_FRAMING includes issuer mastheads, banners, and document framing that is not a semantic
outline root. OUTLINE_ROOT is the root of the semantic outline. OUTLINE_HEADING and
SECTION_HEADING are outline-bearing descendants. CONTENT_LABEL and NON_OUTLINE_LABEL are labels
that do not by themselves establish hierarchy. REPEAT and CONTINUATION are permitted descriptive
roles when that is the best local description. Return only the compact keyed object:
{"expectedNodeCount":12,"classifiedNodeCount":12,"roles":{"NODE_ID":"ROLE"}}.
Use every supplied node ID exactly once as a key. The two counts must describe the supplied
catalog. Do not return an array, per-node explanations, confidence, offsets, Gold, or legacy fields.
""";

    private const string IdentityPrompt = """
You are the A99 whole-document semantic identity relation reasoner. Roles are already frozen by
an earlier stage and are evidence only. For every supplied pair, decide whether the two
source-backed nodes are one logical semantic identity. CONTINUATION_OF is directional: use
RIGHT_TO_LEFT when the right occurrence continues the logical heading begun by the left
occurrence. SAME_SEMANTIC_REPEAT is for the same heading repeated without continuation. DISTINCT
means separate identities. UNRESOLVED means insufficient evidence.

Use exact texts, roles, outline-bearing status, document order, and surrounding source context.
Do not return groups, parent, hierarchy, level, offsets, Gold, or legacy fields. Return every
pair exactly once in the supplied order. Keep reasons concise.
""";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var roleDir = Path.Combine(output, "a1-role");
        var identityDir = Path.Combine(output, "a2-identity");
        Directory.CreateDirectory(roleDir);
        Directory.CreateDirectory(identityDir);
        var catalog = ReadCatalog(Path.Combine(repoRoot, CatalogPath.Replace('/', Path.DirectorySeparatorChar)));
        if (catalog.CatalogFingerprint != ExpectedCatalogFingerprint)
            return await BlockAsync(output, "CATALOG_FINGERPRINT_MISMATCH", 0, 0, ct);

        var roleRequest = new HdsaGlobalRoleClassificationRequest(
            catalog.CatalogFingerprint,
            Ordered(catalog).Select(item => new HdsaGlobalRoleNodeInput(
                item.SemanticNodeId, item.MemberOccurrenceIds, item.CanonicalText, item.SourceOrder)).ToArray(), false);
        var roleJson = JsonSerializer.Serialize(roleRequest, JsonOptions);
        var roleHash = Sha256Text(roleJson);
        await WriteJsonAsync(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-role-identity-v2-keyed-a1-manifest-v1",
            documentId = DocumentId,
            model = Model,
            endpoint = Endpoint,
            sourceSha256 = catalog.SourceSha256,
            catalogFingerprint = catalog.CatalogFingerprint,
            catalogNodeCount = catalog.Entries.Count,
            a1RoleRequestSha256 = roleHash,
            a1RoleSchemaSha256 = Sha256Text(JsonSerializer.Serialize(HdsaGlobalRoleClassificationContract.Schema())),
            a2IdentitySchemaSha256 = Sha256Text(JsonSerializer.Serialize(HdsaGlobalIdentityRelationContract.Schema())),
            a2PairCount = catalog.Entries.Count * (catalog.Entries.Count - 1) / 2,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            globalDecoderUsed = false,
            transientRequestRetries = 0,
            stageBExecuted = false,
        }, ct);
        await WriteJsonAsync(Path.Combine(roleDir, "request.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-role-v2-keyed-request-v1",
            documentId = DocumentId,
            request = roleRequest,
            requestSha256 = roleHash,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
        }, ct);

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return await BlockAsync(output, "OPENROUTER_API_KEY_MISSING", 0, 0, ct);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "hdsa-global-role-identity-v2", DocumentId, ct);
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

        (string Content, RequestPacketTelemetry Telemetry, long ElapsedMs) roleResult;
        try
        {
            roleResult = await CompleteAsync(model, roleJson, roleRequest.Nodes.Count, RolePrompt,
                HdsaGlobalRoleClassificationContract.Schema(), "hdsa_global_role_classification_v2",
                "HDSA_GLOBAL_ROLE_CLASSIFICATION", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ReasoningCompletionException)
        {
            return await BlockAsync(output, "A1_PROVIDER_FAILURE:" + ex.GetType().Name, model.ProviderCalls, model.ProviderCalls, ct);
        }
        var roleResponseHash = Sha256Text(roleResult.Content);
        await WriteJsonAsync(Path.Combine(roleDir, "response.v1.json"), ResponseArtifact(
            "a99-hdsa-global-role-v2-keyed-response-v1", roleHash, roleResponseHash, roleResult), ct);
        HdsaGlobalRoleClassificationProposal? roleProposal = null;
        string? roleParseError = null;
        try { roleProposal = HdsaGlobalRoleClassificationContract.Parse(roleResult.Content); }
        catch (Exception ex) when (ex is FormatException or JsonException) { roleParseError = ex.Message; }
        var known = catalog.Entries.Select(item => item.SemanticNodeId).ToHashSet(StringComparer.Ordinal);
        var roleValidation = roleProposal is null
            ? new HdsaGlobalRoleClassificationValidation(false, roleParseError, [])
            : HdsaGlobalRoleClassificationContract.Validate(known, roleProposal);
        await WriteJsonAsync(Path.Combine(roleDir, "prediction.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-role-v2-keyed-prediction-v1",
            documentId = DocumentId, requestSha256 = roleHash, responseSha256 = roleResponseHash,
            proposal = roleProposal, parseError = roleParseError, validation = roleValidation,
            goldReadBeforeFreeze = false, goldDerivedInput = false,
        }, ct);
        var rolePredictionPath = Path.Combine(roleDir, "prediction.v1.json");
        await WriteJsonAsync(Path.Combine(roleDir, "freeze.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-role-v2-keyed-freeze-v1",
            documentId = DocumentId, sourceSha256 = catalog.SourceSha256,
            catalogFingerprint = catalog.CatalogFingerprint, requestSha256 = roleHash,
            responseSha256 = roleResponseHash, predictionSha256 = Sha256File(rolePredictionPath),
            provider = roleResult.Telemetry.ProviderRoute, model = Model,
            finishReason = roleResult.Telemetry.FinishReason,
            inputTokens = roleResult.Telemetry.ReportedInputTokens,
            reasoningTokens = roleResult.Telemetry.ReportedReasoningTokens,
            outputTokens = roleResult.Telemetry.ReportedOutputTokens,
            elapsedMs = roleResult.ElapsedMs, modelCalls = 1, providerCalls = 1,
            goldReadBeforeFreeze = false, frozenBeforeGold = true,
        }, ct);
        if (!roleValidation.Accepted)
            return await BlockAsync(output, "A1_ROLE_INVALID:" + roleValidation.RejectionReason, model.ProviderCalls, model.ProviderCalls, ct);

        var roleById = roleValidation.Nodes.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        var pairs = BuildPairs(catalog, roleById);
        var identityRequest = new HdsaGlobalIdentityRelationRequest(catalog.CatalogFingerprint, pairs, false);
        var identityJson = JsonSerializer.Serialize(identityRequest, JsonOptions);
        var identityHash = Sha256Text(identityJson);
        await WriteJsonAsync(Path.Combine(identityDir, "request.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-v2-request-v1",
            documentId = DocumentId, request = identityRequest, requestSha256 = identityHash,
            roleFreeze = "a1-role/freeze.v1.json", goldReadBeforeFreeze = false, goldDerivedInput = false,
        }, ct);
        (string Content, RequestPacketTelemetry Telemetry, long ElapsedMs) identityResult;
        try
        {
            identityResult = await CompleteAsync(model, identityJson, pairs.Count, IdentityPrompt,
                HdsaGlobalIdentityRelationContract.Schema(), "hdsa_global_identity_relation_v2",
                "HDSA_GLOBAL_IDENTITY_RELATION", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ReasoningCompletionException)
        {
            return await BlockAsync(output, "A2_PROVIDER_FAILURE:" + ex.GetType().Name, model.ProviderCalls, model.ProviderCalls, ct);
        }
        var identityResponseHash = Sha256Text(identityResult.Content);
        await WriteJsonAsync(Path.Combine(identityDir, "response.v1.json"), ResponseArtifact(
            "a99-hdsa-global-identity-v2-response-v1", identityHash, identityResponseHash, identityResult), ct);
        HdsaGlobalIdentityRelationResponse? identityResponse = null;
        string? identityParseError = null;
        try { identityResponse = HdsaGlobalIdentityRelationContract.Parse(identityResult.Content); }
        catch (Exception ex) when (ex is FormatException or JsonException) { identityParseError = ex.Message; }
        var identityValidation = identityResponse is null
            ? new HdsaGlobalIdentityRelationValidation(false, identityParseError, [], [])
            : HdsaGlobalIdentityRelationContract.Validate(identityRequest, identityResponse);
        await WriteJsonAsync(Path.Combine(identityDir, "prediction.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-v2-prediction-v1",
            documentId = DocumentId, requestSha256 = identityHash, responseSha256 = identityResponseHash,
            response = identityResponse, parseError = identityParseError, validation = identityValidation,
            goldReadBeforeFreeze = false, goldDerivedInput = false,
        }, ct);
        var identityPredictionPath = Path.Combine(identityDir, "prediction.v1.json");
        await WriteJsonAsync(Path.Combine(identityDir, "freeze.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-v2-freeze-v1",
            documentId = DocumentId, sourceSha256 = catalog.SourceSha256,
            catalogFingerprint = catalog.CatalogFingerprint, requestSha256 = identityHash,
            responseSha256 = identityResponseHash, predictionSha256 = Sha256File(identityPredictionPath),
            provider = identityResult.Telemetry.ProviderRoute, model = Model,
            finishReason = identityResult.Telemetry.FinishReason,
            inputTokens = identityResult.Telemetry.ReportedInputTokens,
            reasoningTokens = identityResult.Telemetry.ReportedReasoningTokens,
            outputTokens = identityResult.Telemetry.ReportedOutputTokens,
            elapsedMs = identityResult.ElapsedMs, modelCalls = 1, providerCalls = 1,
            goldReadBeforeFreeze = false, frozenBeforeGold = true,
        }, ct);
        if (!identityValidation.Accepted)
            return await BlockAsync(output, "A2_IDENTITY_INVALID:" + identityValidation.RejectionReason, model.ProviderCalls, model.ProviderCalls, ct);

        var flags = ComputeFlags(catalog, roleValidation.Nodes, identityValidation.Relations);
        var normalized = BuildComponents(catalog, roleValidation.Nodes, identityValidation.AcceptedComponents);
        await WriteJsonAsync(Path.Combine(output, "normalized-catalog.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-role-identity-v2-normalized-catalog-v1",
            documentId = DocumentId, sourceSha256 = catalog.SourceSha256,
            sourceCatalogFingerprint = catalog.CatalogFingerprint,
            normalizedCatalogFingerprint = normalized.Fingerprint,
            entries = normalized.Entries, goldUsed = false,
        }, ct);
        var stageBAuthorized = flags.S0001Framing && flags.S0005OutlineRoot && flags.S0014S0015Continuation;
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-role-identity-v2-live-summary-v1",
            status = stageBAuthorized ? "STAGE_A_HYPOTHESES_CONFIRMED_STAGE_B_AUTHORIZED" : "STAGE_A_HYPOTHESES_NOT_CONFIRMED",
            documentId = DocumentId, model = Model,
            provider = identityResult.Telemetry.ProviderRoute,
            sourceCatalogFingerprint = catalog.CatalogFingerprint,
            normalizedCatalogFingerprint = normalized.Fingerprint,
            sourceNodeCount = catalog.Entries.Count, normalizedNodeCount = normalized.Entries.Count,
            stageA1 = new { requestSha256 = roleHash, responseSha256 = roleResponseHash, validation = roleValidation },
            stageA2 = new
            {
                requestSha256 = identityHash, responseSha256 = identityResponseHash,
                pairCount = pairs.Count, validation = identityValidation,
            },
            hypotheses = new
            {
                H1 = flags.S0001Framing && flags.S0005OutlineRoot ? "PASS" : "FAIL",
                H2 = flags.S0014S0015Continuation ? "PASS" : "FAIL",
                flags,
            },
            stageB = new { authorized = stageBAuthorized, executed = false },
            modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
            goldReadBeforeFreeze = false, predictionFrozenBeforeGold = true, globalDecoderUsed = false,
        }, ct);
        Console.WriteLine("HDSA_GLOBAL_ROLE_IDENTITY_V2_STATUS=" + (stageBAuthorized ? "STAGE_A_HYPOTHESES_CONFIRMED_STAGE_B_AUTHORIZED" : "STAGE_A_HYPOTHESES_NOT_CONFIRMED"));
        Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
        Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
        Console.WriteLine("GOLD_READ_BEFORE_FREEZE=0");
        Console.WriteLine($"H1_ROLE={flags.S0001Framing && flags.S0005OutlineRoot}");
        Console.WriteLine($"H2_CONTINUATION={flags.S0014S0015Continuation}");
        Console.WriteLine($"IDENTITY_GROUPS={normalized.Entries.Count}");
        return 0;
    }

    private static IReadOnlyList<HdsaGlobalIdentityPairInput> BuildPairs(HdsaFrozenSemanticNodeCatalog catalog,
        IReadOnlyDictionary<string, HdsaGlobalRoleNodeProposal> roles)
    {
        var ordered = Ordered(catalog).ToArray();
        var pairs = new List<HdsaGlobalIdentityPairInput>();
        for (var i = 0; i < ordered.Length; i++)
        for (var j = i + 1; j < ordered.Length; j++)
        {
            var left = ordered[i]; var right = ordered[j];
            var pairId = $"P{i + 1:D2}-{j + 1:D2}";
            pairs.Add(new(pairId,
                new HdsaGlobalRoleNodeInput(left.SemanticNodeId, left.MemberOccurrenceIds, left.CanonicalText, left.SourceOrder),
                new HdsaGlobalRoleNodeInput(right.SemanticNodeId, right.MemberOccurrenceIds, right.CanonicalText, right.SourceOrder),
                roles[left.SemanticNodeId].StructuralRole, roles[right.SemanticNodeId].StructuralRole,
                roles[left.SemanticNodeId].OutlineBearing, roles[right.SemanticNodeId].OutlineBearing));
        }
        return pairs;
    }

    private static StageAFlags ComputeFlags(HdsaFrozenSemanticNodeCatalog catalog,
        IReadOnlyList<HdsaGlobalRoleNodeProposal> roles,
        IReadOnlyList<HdsaGlobalIdentityRelationProposal> relations)
    {
        var byOccurrence = catalog.Entries.ToDictionary(item => item.MemberOccurrenceIds.First(), item => item.SemanticNodeId, StringComparer.Ordinal);
        var roleById = roles.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        var framing = byOccurrence.TryGetValue("S0001", out var s1) && roleById[s1].StructuralRole == "DOCUMENT_FRAMING";
        var root = byOccurrence.TryGetValue("S0005", out var s5) && roleById[s5].StructuralRole == "OUTLINE_ROOT";
        var s14 = byOccurrence["S0014"]; var s15 = byOccurrence["S0015"];
        var continuation = relations.Any(item => item.Left == s14 && item.Right == s15 &&
            item.Relation == "CONTINUATION_OF" && item.Direction == "RIGHT_TO_LEFT");
        return new(framing, root, continuation);
    }

    private static NormalizedCatalog BuildComponents(HdsaFrozenSemanticNodeCatalog source,
        IReadOnlyList<HdsaGlobalRoleNodeProposal> roles, IReadOnlyList<IReadOnlyList<string>> components)
    {
        var sourceById = source.Entries.ToDictionary(item => item.SemanticNodeId, StringComparer.Ordinal);
        var roleById = roles.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        var entries = components.Select(component =>
        {
            var members = component.Select(id => sourceById[id]).OrderBy(item => item.SourceOrder).ToArray();
            var id = "GN-" + Sha256Text(string.Join("|", component.Order(StringComparer.Ordinal))).Substring(0, 16);
            var rolesInComponent = component.Select(id => roleById[id].StructuralRole)
                .Distinct(StringComparer.Ordinal).ToArray();
            var role = rolesInComponent.Length == 1 ? rolesInComponent[0] : "UNRESOLVED";
            return new NormalizedEntry(id, component, members.SelectMany(item => item.MemberOccurrenceIds).ToArray(),
                string.Join(" / ", members.Select(item => item.CanonicalText)), members.Min(item => item.SourceOrder),
                role, component.All(id => roleById[id].OutlineBearing));
        }).OrderBy(item => item.SourceOrder).ThenBy(item => item.NormalizedNodeId, StringComparer.Ordinal).ToArray();
        return new(entries, Sha256Text(JsonSerializer.Serialize(entries)));
    }

    private static IEnumerable<HdsaSemanticNodeCatalogEntry> Ordered(HdsaFrozenSemanticNodeCatalog catalog) =>
        catalog.Entries.OrderBy(item => item.SourceOrder).ThenBy(item => item.SemanticNodeId, StringComparer.Ordinal);

    private static async Task<(string Content, RequestPacketTelemetry Telemetry, long ElapsedMs)> CompleteAsync(
        OpenRouterCeilingReasoningModel model, string requestJson, int itemCount, string systemPrompt,
        object schema, string schemaName, string route, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await model.CompleteRawStructuredSemanticAsync(
            DocumentId, route, route, requestJson, requestJson.Length, itemCount, itemCount,
            systemPrompt, $"TASK={route}\n{requestJson}\nReturn exactly the requested JSON object.",
            schema, schemaName, ct);
        stopwatch.Stop();
        return (result.Content, result.Telemetry, stopwatch.ElapsedMilliseconds);
    }

    private static object ResponseArtifact(string schema, string requestHash, string responseHash,
        (string Content, RequestPacketTelemetry Telemetry, long ElapsedMs) result) => new
        {
            schemaVersion = schema, documentId = DocumentId, requestSha256 = requestHash,
            responseSha256 = responseHash, rawResponse = result.Content,
            provider = result.Telemetry.ProviderRoute, finishReason = result.Telemetry.FinishReason,
            inputTokens = result.Telemetry.ReportedInputTokens,
            reasoningTokens = result.Telemetry.ReportedReasoningTokens,
            outputTokens = result.Telemetry.ReportedOutputTokens, elapsedMs = result.ElapsedMs,
            modelCalls = 1, providerCalls = 1, goldReadBeforeFreeze = false,
        };

    private static HdsaFrozenSemanticNodeCatalog ReadCatalog(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("GLOBAL_ROLE_IDENTITY_V2_CATALOG_MISSING", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var entries = root.GetProperty("entries").EnumerateArray().Select(item => new
        {
            Id = item.GetProperty("semanticNodeId").GetString()!,
            Aliases = item.GetProperty("memberOccurrenceIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            Text = item.GetProperty("canonicalText").GetString()!,
            Order = item.GetProperty("sourceOrder").GetInt32(),
        }).ToArray();
        var input = new HdsaSemanticNodeResolutionInput(root.GetProperty("sourceSha256").GetString()!,
            root.GetProperty("preprocessingSnapshotHash").GetString()!,
            entries.SelectMany(item => item.Aliases.Select(alias => new HdsaSemanticNodeSourceOccurrence(alias, item.Order, item.Text))).ToArray(), false);
        var version = root.GetProperty("resolverVersion").GetString()!;
        var predictions = entries.Select(item => new HdsaSemanticNodePrediction(item.Id, item.Aliases, item.Text, "FROZEN_SAFE_CATALOG", version, false)).ToArray();
        var result = new HdsaSemanticNodeResolutionV3Result(input.SourceSha256, input.PreprocessingSnapshotHash, predictions, [], [], [], version, false);
        var catalog = HdsaSemanticNodeCatalogBuilder.Build(input, result);
        if (catalog.CatalogFingerprint != root.GetProperty("catalogFingerprint").GetString())
            throw new InvalidDataException("GLOBAL_ROLE_IDENTITY_V2_CATALOG_RECONSTRUCTION_MISMATCH");
        return catalog;
    }

    private static async Task<int> BlockAsync(string output, string reason, int modelCalls, int providerCalls, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-role-identity-v2-live-summary-v1",
            status = "BLOCKED", reason, modelCalls, providerCalls, goldReadBeforeFreeze = false,
        }, ct);
        Console.WriteLine("HDSA_GLOBAL_ROLE_IDENTITY_V2_STATUS=BLOCKED");
        Console.WriteLine("REASON=" + reason);
        Console.WriteLine($"MODEL_CALLS={modelCalls}");
        Console.WriteLine($"PROVIDER_CALLS={providerCalls}");
        return 1;
    }

    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Sha256File(string path) => Sha256Text(File.ReadAllText(path));
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);

    private sealed record StageAFlags(bool S0001Framing, bool S0005OutlineRoot, bool S0014S0015Continuation);
    private sealed record NormalizedCatalog(IReadOnlyList<NormalizedEntry> Entries, string Fingerprint);
    private sealed record NormalizedEntry(string NormalizedNodeId, IReadOnlyList<string> MemberSourceNodeIds,
        IReadOnlyList<string> MemberOccurrenceIds, string CanonicalText, int SourceOrder, string StructuralRole, bool OutlineBearing);
}
