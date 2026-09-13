using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Production-valid verifier campaign over the already frozen, Gold-independent candidate set.
/// Every candidate is verified in its own whole-outline request; this runner never asks the model
/// to retrieve pairs and never selects a pair from Structural Gold.
/// </summary>
public static class HdsaDeterministicCandidatePairVerificationLiveRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string DocumentId = "DOC-0205";
    private const string CatalogPath = "eval/a99-closed-loop/hdsa-semantic-node-production-live/DOC-0205/semantic-catalog.v1.json";
    private const string CandidateSetPath = "eval/a99-closed-loop/hdsa-global-identity-deterministic-candidate-generator-poc/DOC-0205/candidate-set.v1.json";
    private const string CandidateFreezePath = "eval/a99-closed-loop/hdsa-global-identity-deterministic-candidate-generator-poc/DOC-0205/freeze.v1.json";
    private const string FrozenA1Path = "eval/a99-closed-loop/hdsa-global-role-identity-v2-live/DOC-0205/attempt-2-keyed-a1/a1-role/prediction.v1.json";
    private const string GoldPath = "eval/a99-closed-loop/hdsa-parent-relation-live/DOC-0205/structural-gold.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-global-identity-deterministic-pair-verification-live/DOC-0205";
    private const string ExpectedCatalogFingerprint = "5948fb130cdf730660991a7a83ceb5aab461374a1eb86ec5f40f112b7f64480e";
    private const string ExpectedCandidateSetSha256 = "bf4f934f56ff6da2c5352128af45e23ac18709648dbda8721c87159358181a36";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string Prompt = """
You are an A99 semantic-identity pair verifier. The complete source-backed outline is supplied
with frozen structural-role evidence. Verify only the target pair. CONTINUATION_OF means the
right occurrence continues the logical heading begun at the left occurrence; use RIGHT_TO_LEFT.
SAME_SEMANTIC_REPEAT means the same logical heading is repeated without continuation; use NONE.
DISTINCT means separate logical headings, even if nearby or semantically related. UNRESOLVED means
the evidence is insufficient. Related, nearby, or sequential headings are not continuation merely
because they are related. Return exactly the target pair and no explanation, groups, merge
instruction, parent, hierarchy, level, offsets, Gold, or legacy fields.
""";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Full(repoRoot, OutputRoot);
        Directory.CreateDirectory(output);
        var catalog = ReadCatalog(Full(repoRoot, CatalogPath));
        if (catalog.CatalogFingerprint != ExpectedCatalogFingerprint)
            return await BlockAsync(output, "CATALOG_FINGERPRINT_MISMATCH", 0, ct);
        var candidateInput = ReadCandidateSet(Full(repoRoot, CandidateSetPath), Full(repoRoot, CandidateFreezePath), catalog);
        if (!candidateInput.Valid)
            return await BlockAsync(output, candidateInput.Reason!, 0, ct);
        var a1 = ReadFrozenA1(Full(repoRoot, FrozenA1Path), catalog);
        if (!a1.Accepted)
            return await BlockAsync(output, "FROZEN_A1_INVALID:" + a1.Reason, 0, ct);

        var roles = a1.Nodes.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        var nodes = Ordered(catalog).Select(item => new HdsaIdentityRoleNodeInput(
            item.SemanticNodeId, item.MemberOccurrenceIds, item.CanonicalText, item.SourceOrder,
            roles[item.SemanticNodeId].StructuralRole, roles[item.SemanticNodeId].OutlineBearing)).ToArray();
        var nodeIds = nodes.Select(item => item.NodeId).ToHashSet(StringComparer.Ordinal);
        if (candidateInput.Candidates.Any(item => !nodeIds.Contains(item.Left) || !nodeIds.Contains(item.Right)))
            return await BlockAsync(output, "CANDIDATE_ENDPOINT_NOT_IN_CATALOG", 0, ct);

        await WriteJsonAsync(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-deterministic-pair-verification-live-v1",
            documentId = DocumentId, model = Model, endpoint = Endpoint,
            sourceSha256 = catalog.SourceSha256, catalogFingerprint = catalog.CatalogFingerprint,
            candidateSetPath = CandidateSetPath, candidateSetSha256 = candidateInput.Hash,
            candidateFreezePath = CandidateFreezePath, candidateCount = candidateInput.Candidates.Count,
            frozenA1Path = FrozenA1Path, verifierVersion = HdsaGlobalIdentityRetrieveVerifyContract.Version,
            oraclePairSelection = false, goldDerivedInput = false,
            goldReadBeforePredictionFreeze = false, transientRequestRetries = 0,
            onePairPerRequest = true, stageBExecuted = false,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "candidate-input-freeze.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-deterministic-pair-verification-input-freeze-v1",
            documentId = DocumentId, sourceSha256 = catalog.SourceSha256,
            catalogFingerprint = catalog.CatalogFingerprint, candidateSetSha256 = candidateInput.Hash,
            candidateCount = candidateInput.Candidates.Count, candidates = candidateInput.Candidates,
            oraclePairSelection = false, goldDerivedInput = false,
            frozenBeforeProviderCalls = true, goldReadBeforeFreeze = false,
        }, ct);

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return await BlockAsync(output, "OPENROUTER_API_KEY_MISSING", 0, ct);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot,
            "hdsa-deterministic-pair-verification", DocumentId, ct);
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = key,
            ContextSize = 1_000_000, MaxOutputTokens = 16_000, RequestTimeoutSeconds = 600,
            TransientRequestRetries = 0, MaxParallelRequests = 1, SendChatTemplateKwargs = false,
            OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || capability.ModelId != Model || !capability.ReasoningSupported || !capability.StructuredOutputSupported)
            return await BlockAsync(output, "MODEL_CAPABILITY_MISMATCH", 0, ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var allPairs = BuildIdentityPairs(nodes);
        var fullRelationRequest = new HdsaGlobalIdentityRelationRequest(catalog.CatalogFingerprint, allPairs, false);
        var validations = new List<HdsaIdentityPairVerificationResponse>();
        var validationStatuses = new List<object>();
        foreach (var candidate in candidateInput.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            var target = new HdsaIdentityCandidatePair(candidate.PairId, candidate.Left, candidate.Right);
            var request = new HdsaIdentityPairVerificationRequest(catalog.CatalogFingerprint, nodes, target, false);
            var requestJson = JsonSerializer.Serialize(request, JsonOptions);
            var requestHash = Sha256Text(requestJson);
            var pairDir = Path.Combine(output, "pairs", candidate.PairId);
            Directory.CreateDirectory(pairDir);
            await WriteJsonAsync(Path.Combine(pairDir, "request.v1.json"), new
            {
                schemaVersion = "a99-hdsa-global-identity-deterministic-pair-request-v1",
                documentId = DocumentId, request, requestSha256 = requestHash,
                candidateReasons = candidate.Reasons, candidateSetSha256 = candidateInput.Hash,
                oraclePairSelection = false, goldDerivedInput = false, goldReadBeforeFreeze = false,
            }, ct);
            (string Content, RequestPacketTelemetry Telemetry, long ElapsedMs) result;
            try
            {
                result = await CompleteAsync(model, requestJson, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ReasoningCompletionException)
            {
                await WriteJsonAsync(Path.Combine(pairDir, "blocked.v1.json"), new
                {
                    schemaVersion = "a99-hdsa-global-identity-deterministic-pair-blocked-v1",
                    documentId = DocumentId, pairId = candidate.PairId,
                    status = "TRANSPORT_FAILURE_CAMPAIGN_STOPPED", reason = ex.GetType().Name,
                    completedPairCount = validations.Count, expectedPairCount = candidateInput.Candidates.Count,
                    modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
                    goldReadBeforeFreeze = false,
                }, ct);
                return await BlockAsync(output, "TRANSPORT_FAILURE_CAMPAIGN_STOPPED:" + ex.GetType().Name,
                    model.ProviderCalls, ct);
            }
            var responseHash = Sha256Text(result.Content);
            await WriteJsonAsync(Path.Combine(pairDir, "response.v1.json"), new
            {
                schemaVersion = "a99-hdsa-global-identity-deterministic-pair-response-v1",
                documentId = DocumentId, pairId = candidate.PairId, requestSha256 = requestHash,
                responseSha256 = responseHash, rawResponse = result.Content,
                provider = result.Telemetry.ProviderRoute, finishReason = result.Telemetry.FinishReason,
                inputTokens = result.Telemetry.ReportedInputTokens,
                reasoningTokens = result.Telemetry.ReportedReasoningTokens,
                outputTokens = result.Telemetry.ReportedOutputTokens, elapsedMs = result.ElapsedMs,
                modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
                goldReadBeforeFreeze = false,
            }, ct);
            HdsaIdentityPairVerificationResponse? response = null;
            string? parseError = null;
            try { response = HdsaGlobalIdentityRetrieveVerifyContract.ParseVerification(result.Content); }
            catch (Exception ex) when (ex is FormatException or JsonException) { parseError = ex.Message; }
            var validation = response is null
                ? new HdsaIdentityPairVerificationValidation(false, parseError, null)
                : HdsaGlobalIdentityRetrieveVerifyContract.ValidateVerification(request, response);
            await WriteJsonAsync(Path.Combine(pairDir, "prediction.v1.json"), new
            {
                schemaVersion = "a99-hdsa-global-identity-deterministic-pair-prediction-v1",
                documentId = DocumentId, pairId = candidate.PairId, requestSha256 = requestHash,
                responseSha256 = responseHash, response, parseError, validation,
                candidateReasons = candidate.Reasons, oraclePairSelection = false,
                goldDerivedInput = false, goldReadBeforeFreeze = false, frozenBeforeGold = true,
            }, ct);
            var predictionPath = Path.Combine(pairDir, "prediction.v1.json");
            await WriteJsonAsync(Path.Combine(pairDir, "freeze.v1.json"), new
            {
                schemaVersion = "a99-hdsa-global-identity-deterministic-pair-freeze-v1",
                documentId = DocumentId, pairId = candidate.PairId,
                sourceSha256 = catalog.SourceSha256, catalogFingerprint = catalog.CatalogFingerprint,
                candidateSetSha256 = candidateInput.Hash, requestSha256 = requestHash,
                responseSha256 = responseHash, predictionSha256 = Sha256File(predictionPath),
                pair = candidate, validation, provider = result.Telemetry.ProviderRoute,
                model = Model, finishReason = result.Telemetry.FinishReason,
                inputTokens = result.Telemetry.ReportedInputTokens,
                reasoningTokens = result.Telemetry.ReportedReasoningTokens,
                outputTokens = result.Telemetry.ReportedOutputTokens, elapsedMs = result.ElapsedMs,
                modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
                oraclePairSelection = false, goldReadBeforeFreeze = false, frozenBeforeGold = true,
            }, ct);
            if (validation.Accepted && response is not null)
            {
                validations.Add(response);
                validationStatuses.Add(new { pairId = candidate.PairId, status = "VALID", relation = response.Relation, direction = response.Direction });
            }
            else
                validationStatuses.Add(new { pairId = candidate.PairId, status = "INVALID_KEEP_SPLIT", reason = validation.RejectionReason });
        }

        var sparseResponse = new HdsaSparsePositiveIdentityResponse(validations
            .Where(item => item.Relation is "CONTINUATION_OF" or "SAME_SEMANTIC_REPEAT")
            .Select(item => new HdsaSparsePositiveIdentityRelation(item.Left, item.Right, item.Relation, item.Direction)).ToArray());
        var sparseValidation = HdsaGlobalIdentitySparsePositiveContract.Validate(fullRelationRequest, sparseResponse);
        await WriteJsonAsync(Path.Combine(output, "normalized-catalog.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-deterministic-pair-normalized-catalog-v1",
            documentId = DocumentId, sourceSha256 = catalog.SourceSha256,
            catalogFingerprint = catalog.CatalogFingerprint, sparseValidation,
            goldUsed = false, goldReadBeforeFreeze = false,
        }, ct);
        var campaignPredictionHash = Sha256Text(string.Join("|", candidateInput.Candidates.Select(candidate =>
            Sha256File(Path.Combine(output, "pairs", candidate.PairId, "prediction.v1.json")))));
        await WriteJsonAsync(Path.Combine(output, "campaign-freeze.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-deterministic-pair-campaign-freeze-v1",
            documentId = DocumentId, sourceSha256 = catalog.SourceSha256,
            catalogFingerprint = catalog.CatalogFingerprint, candidateSetSha256 = candidateInput.Hash,
            candidateCount = candidateInput.Candidates.Count,
            predictionCount = candidateInput.Candidates.Count, campaignPredictionHash,
            validationStatuses, sparseValidation, normalizedComponents = sparseValidation.PositiveComponents,
            modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
            oraclePairSelection = false, goldDerivedInput = false,
            goldReadBeforePredictionFreeze = false, predictionsFrozen = true,
        }, ct);

        // Structural Gold is opened only after all 11 pair predictions and the campaign freeze exist.
        var gold = EvaluateGold(Full(repoRoot, GoldPath), catalog, candidateInput.Candidates, validations);
        await WriteJsonAsync(Path.Combine(output, "gold-evaluation.v1.json"), gold, ct);
        var h2Target = gold.S0014S0015Covered && sparseValidation.Accepted;
        var h2 = h2Target && gold.NoFalseMerge;
        var status = h2
            ? "H2_PASS_STAGE_B_AUTHORIZED"
            : h2Target && !gold.NoFalseMerge
                ? "H2_TARGET_PASS_FALSE_MERGE_STAGE_B_BLOCKED"
                : "H2_FAIL_STAGE_B_BLOCKED";
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-deterministic-pair-verification-summary-v1",
            status,
            documentId = DocumentId, model = Model, provider = validations.Count > 0 ? "Alibaba" : null,
            candidateSet = new { count = candidateInput.Candidates.Count, sha256 = candidateInput.Hash },
            verification = new
            {
                requested = candidateInput.Candidates.Count, completed = validations.Count,
                statuses = validationStatuses,
                continuationCount = validations.Count(item => item.Relation == "CONTINUATION_OF"),
                repeatCount = validations.Count(item => item.Relation == "SAME_SEMANTIC_REPEAT"),
                distinctCount = validations.Count(item => item.Relation == "DISTINCT"),
                unresolvedCount = validations.Count(item => item.Relation == "UNRESOLVED"),
                invalidCount = validationStatuses.Count(item => item.ToString()!.Contains("INVALID", StringComparison.Ordinal)),
            },
            sparsePromotion = new { accepted = sparseValidation.Accepted, reason = sparseValidation.RejectionReason,
                positiveRelations = sparseResponse.PositiveRelations, components = sparseValidation.PositiveComponents },
            goldEvaluation = gold, h1FrozenA1 = true, h2Target, h2,
            stageB = new { authorized = h2, executed = false,
                reason = h2 ? null : status },
            modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
            goldReadBeforePredictionFreeze = false, predictionsFrozen = true, oraclePairSelection = false,
        }, ct);
        Console.WriteLine("HDSA_DETERMINISTIC_PAIR_VERIFICATION_STATUS=" + status);
        Console.WriteLine($"VERIFIED_PAIRS={validations.Count}/{candidateInput.Candidates.Count}");
        Console.WriteLine($"S0014_S0015_COVERED={gold.S0014S0015Covered}");
        Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
        Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
        Console.WriteLine("GOLD_READ_BEFORE_FREEZE=0");
        return h2 ? 0 : 1;
    }

    private static GoldResult EvaluateGold(string path, HdsaFrozenSemanticNodeCatalog catalog,
        IReadOnlyList<HdsaDeterministicIdentityCandidate> candidates,
        IReadOnlyList<HdsaIdentityPairVerificationResponse> responses)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("PAIR_VERIFICATION_GOLD_MISSING", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("sourceSha256").GetString() != catalog.SourceSha256)
            throw new InvalidDataException("PAIR_VERIFICATION_GOLD_SOURCE_MISMATCH");
        var aliasToNode = catalog.Entries.SelectMany(entry => entry.MemberOccurrenceIds
            .Select(alias => (alias, node: entry.SemanticNodeId)))
            .ToDictionary(item => item.alias, item => item.node, StringComparer.Ordinal);
        var goldRows = root.GetProperty("occurrences").EnumerateArray()
            .Where(item => item.GetProperty("reviewStatus").GetString() == "RESOLVED")
            .ToArray();
        var goldNodeByAlias = goldRows.ToDictionary(
            item => item.GetProperty("sourceAlias").GetString()!,
            item => item.GetProperty("semanticNodeId").GetString()!,
            StringComparer.Ordinal);
        var s14 = goldRows.Single(item => item.GetProperty("sourceAlias").GetString() == "S0014");
        var s15 = goldRows.Single(item => item.GetProperty("sourceAlias").GetString() == "S0015");
        var s14Node = aliasToNode["S0014"];
        var s15Node = aliasToNode["S0015"];
        var target = responses.FirstOrDefault(item =>
            item.Left == s14Node && item.Right == s15Node &&
            item.Relation is "CONTINUATION_OF" or "SAME_SEMANTIC_REPEAT");
        var positiveResponses = responses
            .Where(item => item.Relation is "CONTINUATION_OF" or "SAME_SEMANTIC_REPEAT")
            .ToArray();
        var falsePositivePairIds = positiveResponses
            .Select(response =>
            {
                var candidate = candidates.FirstOrDefault(item =>
                    item.Left == response.Left && item.Right == response.Right);
                var leftGoldNodes = catalog.Entries
                    .Where(entry => entry.SemanticNodeId == response.Left)
                    .SelectMany(entry => entry.MemberOccurrenceIds)
                    .Where(goldNodeByAlias.ContainsKey)
                    .Select(alias => goldNodeByAlias[alias])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var rightGoldNodes = catalog.Entries
                    .Where(entry => entry.SemanticNodeId == response.Right)
                    .SelectMany(entry => entry.MemberOccurrenceIds)
                    .Where(goldNodeByAlias.ContainsKey)
                    .Select(alias => goldNodeByAlias[alias])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                return new
                {
                    PairId = candidate?.PairId,
                    IsFalsePositive = leftGoldNodes.Length == 1 && rightGoldNodes.Length == 1 &&
                        !string.Equals(leftGoldNodes[0], rightGoldNodes[0], StringComparison.Ordinal),
                };
            })
            .Where(item => item.IsFalsePositive && item.PairId is not null)
            .Select(item => item.PairId!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new(
            GoldSourceSha256: catalog.SourceSha256,
            GoldSemanticNodeForS0014: s14.GetProperty("semanticNodeId").GetString()!,
            GoldSemanticNodeForS0015: s15.GetProperty("semanticNodeId").GetString()!,
            GoldPositivePairCount: 1,
            RetrievedCandidateCount: candidates.Count,
            S0014S0015Generated: candidates.Any(item => item.Left == s14Node && item.Right == s15Node),
            S0014S0015Covered: target is not null,
            VerifiedRelation: target?.Relation,
            GoldOpenedAfterPredictionFreeze: true,
            ModelCallsDuringEvaluation: 0,
            ProviderCallsDuringEvaluation: 0,
            PositiveRelationCount: positiveResponses.Length,
            FalsePositivePositiveRelationCount: falsePositivePairIds.Length,
            FalsePositivePairIds: falsePositivePairIds,
            NoFalseMerge: falsePositivePairIds.Length == 0);
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

    private static CandidateInput ReadCandidateSet(string path, string freezePath, HdsaFrozenSemanticNodeCatalog catalog)
    {
        if (!File.Exists(path) || !File.Exists(freezePath)) return new(false, "FROZEN_CANDIDATE_SET_MISSING", "", []);
        var hash = Sha256File(path);
        if (!string.Equals(hash, ExpectedCandidateSetSha256, StringComparison.Ordinal))
            return new(false, "FROZEN_CANDIDATE_SET_HASH_MISMATCH", hash, []);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("goldUsed").GetBoolean() || root.GetProperty("goldReadBeforeFreeze").GetBoolean() ||
            root.GetProperty("catalogFingerprint").GetString() != catalog.CatalogFingerprint)
            return new(false, "FROZEN_CANDIDATE_SET_PROVENANCE_INVALID", hash, []);
        var candidates = root.GetProperty("candidates").EnumerateArray().Select(item =>
            new HdsaDeterministicIdentityCandidate(item.GetProperty("pairId").GetString()!,
                item.GetProperty("left").GetString()!, item.GetProperty("right").GetString()!,
                item.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString()!).ToArray())).ToArray();
        return candidates.Length == 11 ? new(true, null, hash, candidates) :
            new(false, "FROZEN_CANDIDATE_COUNT_MISMATCH", hash, candidates);
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
        if (!File.Exists(path)) throw new FileNotFoundException("PAIR_VERIFICATION_CATALOG_MISSING", path);
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
        var predictions = entries.Select(item => new HdsaSemanticNodePrediction(item.Id, item.Aliases, item.Text,
            "FROZEN_SAFE_CATALOG", version, false)).ToArray();
        var result = new HdsaSemanticNodeResolutionV3Result(input.SourceSha256, input.PreprocessingSnapshotHash,
            predictions, [], [], [], version, false);
        var catalog = HdsaSemanticNodeCatalogBuilder.Build(input, result);
        if (catalog.CatalogFingerprint != root.GetProperty("catalogFingerprint").GetString())
            throw new InvalidDataException("PAIR_VERIFICATION_CATALOG_RECONSTRUCTION_MISMATCH");
        return catalog;
    }

    private static async Task<(string Content, RequestPacketTelemetry Telemetry, long ElapsedMs)> CompleteAsync(
        OpenRouterCeilingReasoningModel model, string requestJson, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await model.CompleteRawStructuredSemanticAsync(DocumentId,
            "HDSA_DETERMINISTIC_PAIR_VERIFICATION", "HDSA_DETERMINISTIC_PAIR_VERIFICATION",
            requestJson, requestJson.Length, 1, 1, Prompt,
            $"TASK=HDSA_DETERMINISTIC_PAIR_VERIFICATION\n{requestJson}\nReturn exactly the requested JSON object.",
            HdsaGlobalIdentityRetrieveVerifyContract.VerificationSchema(),
            "hdsa_global_identity_deterministic_pair_verification_v1", ct);
        stopwatch.Stop();
        return (result.Content, result.Telemetry, stopwatch.ElapsedMilliseconds);
    }

    private static IEnumerable<HdsaSemanticNodeCatalogEntry> Ordered(HdsaFrozenSemanticNodeCatalog catalog) =>
        catalog.Entries.OrderBy(item => item.SourceOrder).ThenBy(item => item.SemanticNodeId, StringComparer.Ordinal);

    private static async Task<int> BlockAsync(string output, string reason, int calls, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-deterministic-pair-verification-summary-v1",
            status = "BLOCKED", reason, modelCalls = calls, providerCalls = calls,
            goldReadBeforeFreeze = false, oraclePairSelection = false,
        }, ct);
        Console.WriteLine("HDSA_DETERMINISTIC_PAIR_VERIFICATION_STATUS=BLOCKED");
        Console.WriteLine("REASON=" + reason);
        Console.WriteLine($"MODEL_CALLS={calls}");
        Console.WriteLine($"PROVIDER_CALLS={calls}");
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

    private sealed record CandidateInput(bool Valid, string? Reason, string Hash, IReadOnlyList<HdsaDeterministicIdentityCandidate> Candidates);
    private sealed record FrozenA1(bool Accepted, string? Reason, IReadOnlyList<HdsaGlobalRoleNodeProposal> Nodes);
    private sealed record GoldResult(string GoldSourceSha256, string GoldSemanticNodeForS0014,
        string GoldSemanticNodeForS0015, int GoldPositivePairCount, int RetrievedCandidateCount,
        bool S0014S0015Generated, bool S0014S0015Covered, string? VerifiedRelation,
        bool GoldOpenedAfterPredictionFreeze, int ModelCallsDuringEvaluation, int ProviderCallsDuringEvaluation,
        int PositiveRelationCount, int FalsePositivePositiveRelationCount,
        IReadOnlyList<string> FalsePositivePairIds, bool NoFalseMerge);
}
