using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Retrieve-then-verify identity challenger. A frozen A1 role result is reused; A2a has only
/// retrieval authority and A2b verifies each retrieved pair independently. No Gold or Stage B
/// is read or executed by this runner.
/// </summary>
public static class HdsaGlobalIdentityRetrieveVerifyLiveRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string DocumentId = "DOC-0205";
    private const string CatalogPath = "eval/a99-closed-loop/hdsa-semantic-node-production-live/DOC-0205/semantic-catalog.v1.json";
    private const string FrozenA1Path = "eval/a99-closed-loop/hdsa-global-role-identity-v2-live/DOC-0205/attempt-2-keyed-a1/a1-role/prediction.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-global-identity-retrieve-verify-live/DOC-0205";
    private const string ExpectedCatalogFingerprint = "5948fb130cdf730660991a7a83ceb5aab461374a1eb86ec5f40f112b7f64480e";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string DiscoveryPrompt = """
You are the A99 semantic-identity candidate retriever. The request contains the complete
source-backed semantic catalog in document order, with roles and parser-owned evidence. Find
only a small number of pairs that genuinely deserve pair-specific identity verification.
This is retrieval, not classification: do not decide CONTINUATION_OF, SAME_SEMANTIC_REPEAT,
DISTINCT, or any merge. Return only pair IDs from the supplied pairUniverse.

A candidate is a pair that may represent one logical heading identity (a repeat or a continuation),
not merely two related, nearby, sequential, or topically similar headings. Prefer an empty list
when no pair has strong identity evidence. Do not return explanations, endpoints, relations,
groups, parent, hierarchy, level, offsets, Gold, or legacy fields. Keep the list sparse.
""";

    private const string VerificationPrompt = """
You are the A99 semantic-identity pair verifier. The complete outline remains visible, but reason
only about the supplied target pair. Decide whether the two source-backed occurrences represent
one logical heading identity. CONTINUATION_OF means the right occurrence continues the logical
heading begun at the left occurrence; use RIGHT_TO_LEFT. SAME_SEMANTIC_REPEAT means the same
heading identity is repeated without continuation; use NONE. DISTINCT means separate identities;
UNRESOLVED means evidence is insufficient. Related, nearby, or sequential headings are DISTINCT
unless they are genuinely one logical heading. Return exactly the target pair IDs and no other
fields. Do not return groups, merge instructions, parent, hierarchy, level, offsets, Gold, or
legacy fields.
""";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Full(repoRoot, OutputRoot);
        var discoveryDir = Path.Combine(output, "a2a-discovery");
        var verificationDir = Path.Combine(output, "a2b-verification");
        Directory.CreateDirectory(discoveryDir);
        Directory.CreateDirectory(verificationDir);
        var catalog = ReadCatalog(Full(repoRoot, CatalogPath));
        if (catalog.CatalogFingerprint != ExpectedCatalogFingerprint)
            return await BlockAsync(output, "CATALOG_FINGERPRINT_MISMATCH", 0, 0, ct);

        var a1 = ReadFrozenA1(Full(repoRoot, FrozenA1Path), catalog);
        if (!a1.Accepted)
            return await BlockAsync(output, "FROZEN_A1_INVALID:" + a1.Reason, 0, 0, ct);

        var roleById = a1.Nodes.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        var nodes = Ordered(catalog).Select(item => new HdsaIdentityRoleNodeInput(
            item.SemanticNodeId, item.MemberOccurrenceIds, item.CanonicalText, item.SourceOrder,
            roleById[item.SemanticNodeId].StructuralRole, roleById[item.SemanticNodeId].OutlineBearing)).ToArray();
        var pairUniverse = BuildPairUniverse(nodes);
        var discoveryRequest = new HdsaIdentityCandidateDiscoveryRequest(
            catalog.CatalogFingerprint, nodes, pairUniverse, false);
        var discoveryJson = JsonSerializer.Serialize(discoveryRequest, JsonOptions);
        var discoveryHash = Sha256Text(discoveryJson);
        await WriteJsonAsync(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = HdsaGlobalIdentityRetrieveVerifyContract.Version + "-manifest-v1",
            documentId = DocumentId, model = Model, endpoint = Endpoint,
            sourceSha256 = catalog.SourceSha256, catalogFingerprint = catalog.CatalogFingerprint,
            catalogNodeCount = nodes.Length, pairUniverseCount = pairUniverse.Count,
            frozenA1PredictionPath = FrozenA1Path,
            frozenA1PredictionSha256 = Sha256File(Full(repoRoot, FrozenA1Path)),
            discoveryRequestSha256 = discoveryHash,
            discoverySchemaSha256 = Sha256Text(JsonSerializer.Serialize(HdsaGlobalIdentityRetrieveVerifyContract.DiscoverySchema())),
            verificationSchemaSha256 = Sha256Text(JsonSerializer.Serialize(HdsaGlobalIdentityRetrieveVerifyContract.VerificationSchema())),
            goldReadBeforeFreeze = false, goldDerivedInput = false, stageBExecuted = false,
            candidateGenerationIsRecallGate = false, omittedPairPolicy = "NO_CLAIM_KEEP_SPLIT",
            transientRequestRetries = 0,
        }, ct);
        await WriteJsonAsync(Path.Combine(discoveryDir, "request.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-a2a-discovery-request-v1",
            documentId = DocumentId, request = discoveryRequest, requestSha256 = discoveryHash,
            goldReadBeforeFreeze = false, goldDerivedInput = false,
        }, ct);

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return await BlockAsync(output, "OPENROUTER_API_KEY_MISSING", 0, 0, ct);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot,
            "hdsa-global-identity-retrieve-verify", DocumentId, ct);
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

        (string Content, RequestPacketTelemetry Telemetry, long ElapsedMs) discoveryResult;
        try
        {
            discoveryResult = await CompleteAsync(model, discoveryJson, pairUniverse.Count,
                DiscoveryPrompt, HdsaGlobalIdentityRetrieveVerifyContract.DiscoverySchema(),
                "hdsa_global_identity_candidate_discovery_v1", "HDSA_GLOBAL_IDENTITY_A2A", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ReasoningCompletionException)
        {
            return await BlockAsync(output, "A2A_PROVIDER_FAILURE:" + ex.GetType().Name,
                model.ProviderCalls, model.ProviderCalls, ct);
        }
        var discoveryResponseHash = Sha256Text(discoveryResult.Content);
        await WriteJsonAsync(Path.Combine(discoveryDir, "response.v1.json"), ResponseArtifact(
            "a99-hdsa-global-identity-a2a-discovery-response-v1", discoveryHash, discoveryResponseHash,
            discoveryResult, model.ProviderCalls), ct);
        HdsaIdentityCandidateDiscoveryResponse? discoveryResponse = null;
        string? discoveryParseError = null;
        try { discoveryResponse = HdsaGlobalIdentityRetrieveVerifyContract.ParseDiscovery(discoveryResult.Content); }
        catch (Exception ex) when (ex is FormatException or JsonException) { discoveryParseError = ex.Message; }
        var discoveryValidation = discoveryResponse is null
            ? new HdsaIdentityCandidateDiscoveryValidation(false, discoveryParseError, [], 0)
            : HdsaGlobalIdentityRetrieveVerifyContract.ValidateDiscovery(discoveryRequest, discoveryResponse);
        await WriteJsonAsync(Path.Combine(discoveryDir, "prediction.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-a2a-discovery-prediction-v1",
            documentId = DocumentId, requestSha256 = discoveryHash, responseSha256 = discoveryResponseHash,
            response = discoveryResponse, parseError = discoveryParseError, validation = discoveryValidation,
            frozenBeforeGold = true, goldReadBeforeFreeze = false, goldDerivedInput = false,
        }, ct);
        var discoveryPredictionPath = Path.Combine(discoveryDir, "prediction.v1.json");
        await WriteJsonAsync(Path.Combine(discoveryDir, "freeze.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-a2a-discovery-freeze-v1",
            documentId = DocumentId, sourceSha256 = catalog.SourceSha256,
            catalogFingerprint = catalog.CatalogFingerprint, requestSha256 = discoveryHash,
            responseSha256 = discoveryResponseHash, predictionSha256 = Sha256File(discoveryPredictionPath),
            candidatePairCount = discoveryValidation.CandidatePairIds.Count,
            maxCandidatePairCount = discoveryValidation.MaxCandidatePairCount,
            provider = discoveryResult.Telemetry.ProviderRoute, model = Model,
            finishReason = discoveryResult.Telemetry.FinishReason,
            inputTokens = discoveryResult.Telemetry.ReportedInputTokens,
            reasoningTokens = discoveryResult.Telemetry.ReportedReasoningTokens,
            outputTokens = discoveryResult.Telemetry.ReportedOutputTokens, elapsedMs = discoveryResult.ElapsedMs,
            modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
            goldReadBeforeFreeze = false, frozenBeforeGold = true,
        }, ct);
        if (!discoveryValidation.Accepted)
            return await BlockAsync(output, "A2A_INVALID:" + discoveryValidation.RejectionReason,
                model.ProviderCalls, model.ProviderCalls, ct);

        var pairById = pairUniverse.ToDictionary(item => item.PairId, StringComparer.Ordinal);
        var fullPairs = BuildIdentityPairs(nodes);
        var fullRelationRequest = new HdsaGlobalIdentityRelationRequest(catalog.CatalogFingerprint, fullPairs, false);
        var verificationResults = new List<object>();
        var positiveRelations = new List<HdsaSparsePositiveIdentityRelation>();
        foreach (var pairId in discoveryValidation.CandidatePairIds)
        {
            ct.ThrowIfCancellationRequested();
            var target = pairById[pairId];
            var verificationRequest = new HdsaIdentityPairVerificationRequest(
                catalog.CatalogFingerprint, nodes, target, false);
            var canonicalRequest = HdsaCanonicalPairVerifierRequestBuilder.Build(verificationRequest);
            var verificationJson = canonicalRequest.Json;
            var verificationHash = canonicalRequest.Sha256;
            var dir = Path.Combine(verificationDir, pairId);
            Directory.CreateDirectory(dir);
            await WriteJsonAsync(Path.Combine(dir, "request.v1.json"), new
            {
                schemaVersion = "a99-hdsa-global-identity-a2b-verification-request-v1",
                documentId = DocumentId, request = verificationRequest, requestSha256 = verificationHash,
                discoveryFreeze = "../a2a-discovery/freeze.v1.json", goldReadBeforeFreeze = false,
                goldDerivedInput = false,
            }, ct);
            (string Content, RequestPacketTelemetry Telemetry, long ElapsedMs) result;
            try
            {
                result = await CompleteAsync(model, verificationJson, 1, VerificationPrompt,
                    HdsaGlobalIdentityRetrieveVerifyContract.VerificationSchema(),
                    "hdsa_global_identity_pair_verification_v1", "HDSA_GLOBAL_IDENTITY_A2B", ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ReasoningCompletionException)
            {
                await WriteJsonAsync(Path.Combine(dir, "blocked.v1.json"), new
                {
                    status = "ROLE_VERIFICATION_BLOCKED", reason = ex.GetType().Name,
                    modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
                    goldReadBeforeFreeze = false,
                }, ct);
                verificationResults.Add(new { pairId, status = "BLOCKED", reason = ex.GetType().Name });
                continue;
            }
            var responseHash = Sha256Text(result.Content);
            await WriteJsonAsync(Path.Combine(dir, "response.v1.json"), ResponseArtifact(
                "a99-hdsa-global-identity-a2b-verification-response-v1", verificationHash, responseHash,
                result, model.ProviderCalls), ct);
            HdsaIdentityPairVerificationResponse? response = null;
            string? parseError = null;
            try { response = HdsaGlobalIdentityRetrieveVerifyContract.ParseVerification(result.Content); }
            catch (Exception ex) when (ex is FormatException or JsonException) { parseError = ex.Message; }
            var validation = response is null
                ? new HdsaIdentityPairVerificationValidation(false, parseError, null)
                : HdsaGlobalIdentityRetrieveVerifyContract.ValidateVerification(verificationRequest, response);
            await WriteJsonAsync(Path.Combine(dir, "prediction.v1.json"), new
            {
                schemaVersion = "a99-hdsa-global-identity-a2b-verification-prediction-v1",
                documentId = DocumentId, pairId, requestSha256 = verificationHash,
                responseSha256 = responseHash, response, parseError, validation,
                frozenBeforeGold = true, goldReadBeforeFreeze = false, goldDerivedInput = false,
            }, ct);
            await WriteJsonAsync(Path.Combine(dir, "freeze.v1.json"), new
            {
                schemaVersion = "a99-hdsa-global-identity-a2b-verification-freeze-v1",
                documentId = DocumentId, pairId, sourceSha256 = catalog.SourceSha256,
                catalogFingerprint = catalog.CatalogFingerprint, requestSha256 = verificationHash,
                responseSha256 = responseHash, predictionSha256 = Sha256File(Path.Combine(dir, "prediction.v1.json")),
                provider = result.Telemetry.ProviderRoute, model = Model,
                finishReason = result.Telemetry.FinishReason,
                inputTokens = result.Telemetry.ReportedInputTokens,
                reasoningTokens = result.Telemetry.ReportedReasoningTokens,
                outputTokens = result.Telemetry.ReportedOutputTokens, elapsedMs = result.ElapsedMs,
                modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
                goldReadBeforeFreeze = false, frozenBeforeGold = true,
            }, ct);
            verificationResults.Add(new { pairId, status = validation.Accepted ? "VALID" : "INVALID", validation });
            if (validation.Accepted && response is not null &&
                response.Relation is "CONTINUATION_OF" or "SAME_SEMANTIC_REPEAT")
                positiveRelations.Add(new(response.Left, response.Right, response.Relation, response.Direction));
        }

        var sparseResponse = new HdsaSparsePositiveIdentityResponse(positiveRelations);
        var sparseValidation = HdsaGlobalIdentitySparsePositiveContract.Validate(fullRelationRequest, sparseResponse);
        await WriteJsonAsync(Path.Combine(output, "sparse-promotion.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-retrieve-verify-sparse-promotion-v1",
            documentId = DocumentId, source = "A2B_VALIDATED_POSITIVE_RELATIONS_ONLY",
            response = sparseResponse, validation = sparseValidation,
            noClaimPairCount = fullPairs.Count - positiveRelations.Count,
            goldUsed = false, goldReadBeforeFreeze = false,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-retrieve-verify-live-summary-v1",
            status = sparseValidation.Accepted ? "A2_RETRIEVE_VERIFY_COMPLETE_STAGE_B_BLOCKED" : "A2_RETRIEVE_VERIFY_PROMOTION_BLOCKED",
            documentId = DocumentId, model = Model, provider = discoveryResult.Telemetry.ProviderRoute,
            catalogFingerprint = catalog.CatalogFingerprint, sourceNodeCount = nodes.Length,
            pairUniverseCount = pairUniverse.Count,
            a1Reused = new { path = FrozenA1Path, providerCalls = 0, goldReadBeforeFreeze = false },
            a2a = new { requestSha256 = discoveryHash, responseSha256 = discoveryResponseHash,
                validation = discoveryValidation, providerCalls = 1 },
            a2b = new { requested = discoveryValidation.CandidatePairIds.Count,
                completed = verificationResults.Count(item => !item.ToString()!.Contains("BLOCKED", StringComparison.Ordinal)),
                results = verificationResults },
            sparsePromotion = new { positiveRelationCount = positiveRelations.Count, validation = sparseValidation },
            stageB = new { authorized = false, executed = false },
            modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
            goldReadBeforeFreeze = false, predictionFrozenBeforeGold = true,
        }, ct);
        Console.WriteLine("HDSA_GLOBAL_IDENTITY_RETRIEVE_VERIFY_STATUS=" +
            (sparseValidation.Accepted ? "COMPLETE_STAGE_B_BLOCKED" : "PROMOTION_BLOCKED"));
        Console.WriteLine($"A2A_CANDIDATE_PAIRS={discoveryValidation.CandidatePairIds.Count}");
        Console.WriteLine($"A2B_VERIFIED_POSITIVES={positiveRelations.Count}");
        Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
        Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
        Console.WriteLine("GOLD_READ_BEFORE_FREEZE=0");
        return sparseValidation.Accepted ? 0 : 1;
    }

    private static IReadOnlyList<HdsaIdentityCandidatePair> BuildPairUniverse(IReadOnlyList<HdsaIdentityRoleNodeInput> nodes)
    {
        var pairs = new List<HdsaIdentityCandidatePair>();
        for (var i = 0; i < nodes.Count; i++)
            for (var j = i + 1; j < nodes.Count; j++)
                pairs.Add(new($"P{i + 1:D2}-{j + 1:D2}", nodes[i].NodeId, nodes[j].NodeId));
        return pairs;
    }

    private static IReadOnlyList<HdsaGlobalIdentityPairInput> BuildIdentityPairs(IReadOnlyList<HdsaIdentityRoleNodeInput> nodes)
    {
        var pairs = new List<HdsaGlobalIdentityPairInput>();
        for (var i = 0; i < nodes.Count; i++)
            for (var j = i + 1; j < nodes.Count; j++)
            {
                var left = nodes[i]; var right = nodes[j];
                pairs.Add(new($"P{i + 1:D2}-{j + 1:D2}",
                    new HdsaGlobalRoleNodeInput(left.NodeId, left.MemberOccurrenceIds, left.CanonicalText, left.DocumentOrder),
                    new HdsaGlobalRoleNodeInput(right.NodeId, right.MemberOccurrenceIds, right.CanonicalText, right.DocumentOrder),
                    left.StructuralRole, right.StructuralRole, left.OutlineBearing, right.OutlineBearing));
            }
        return pairs;
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
        if (!File.Exists(path)) throw new FileNotFoundException("RETRIEVE_VERIFY_CATALOG_MISSING", path);
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
        var result = new HdsaSemanticNodeResolutionV3Result(input.SourceSha256, input.PreprocessingSnapshotHash,
            predictions, [], [], [], version, false);
        var catalog = HdsaSemanticNodeCatalogBuilder.Build(input, result);
        if (catalog.CatalogFingerprint != root.GetProperty("catalogFingerprint").GetString())
            throw new InvalidDataException("RETRIEVE_VERIFY_CATALOG_RECONSTRUCTION_MISMATCH");
        return catalog;
    }

    private static IEnumerable<HdsaSemanticNodeCatalogEntry> Ordered(HdsaFrozenSemanticNodeCatalog catalog) =>
        catalog.Entries.OrderBy(item => item.SourceOrder).ThenBy(item => item.SemanticNodeId, StringComparer.Ordinal);

    private static async Task<(string Content, RequestPacketTelemetry Telemetry, long ElapsedMs)> CompleteAsync(
        OpenRouterCeilingReasoningModel model, string requestJson, int itemCount, string systemPrompt,
        object schema, string schemaName, string route, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await model.CompleteRawStructuredSemanticAsync(DocumentId, route, route,
            requestJson, requestJson.Length, itemCount, itemCount, systemPrompt,
            $"TASK={route}\n{requestJson}\nReturn exactly the requested JSON object.", schema, schemaName, ct);
        stopwatch.Stop();
        return (result.Content, result.Telemetry, stopwatch.ElapsedMilliseconds);
    }

    private static object ResponseArtifact(string schema, string requestHash, string responseHash,
        (string Content, RequestPacketTelemetry Telemetry, long ElapsedMs) result, int calls) => new
    {
        schemaVersion = schema, documentId = DocumentId, requestSha256 = requestHash,
        responseSha256 = responseHash, rawResponse = result.Content,
        provider = result.Telemetry.ProviderRoute, finishReason = result.Telemetry.FinishReason,
        inputTokens = result.Telemetry.ReportedInputTokens,
        reasoningTokens = result.Telemetry.ReportedReasoningTokens,
        outputTokens = result.Telemetry.ReportedOutputTokens, elapsedMs = result.ElapsedMs,
        modelCalls = calls, providerCalls = calls, goldReadBeforeFreeze = false,
    };

    private static async Task<int> BlockAsync(string output, string reason, int modelCalls, int providerCalls, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-retrieve-verify-live-summary-v1",
            status = "BLOCKED", reason, modelCalls, providerCalls, goldReadBeforeFreeze = false,
        }, ct);
        Console.WriteLine("HDSA_GLOBAL_IDENTITY_RETRIEVE_VERIFY_STATUS=BLOCKED");
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

    private sealed record FrozenA1(bool Accepted, string? Reason, IReadOnlyList<HdsaGlobalRoleNodeProposal> Nodes);
}
