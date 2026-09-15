using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Eval.ReasoningRetention;
using DocxHeaderExtractor.Infrastructure.AI;

namespace IdentityBenchmarkV5B;

internal static class Program
{
    private const string ClusterRelative = "artifacts/identity-benchmark/v5/source-only-clusters/freeze/clusters.json";
    private const string ClusterManifestRelative = "artifacts/identity-benchmark/v5/source-only-clusters/freeze/manifest.json";
    private const string SourceCatalogRelative = "artifacts/identity-benchmark/v2/source-catalog.json";
    private const string OutputRelative = "artifacts/identity-benchmark/v5/semantic-node-induction/preflight";
    private const string ExecutionRelative = "artifacts/identity-benchmark/v5/semantic-node-induction/execution";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Provider = "OpenRouter";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const int ExpectedClusters = 102;
    private const string LocalContextRadius = "2_source_occurrences_each_side";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            if (args.Any(x => string.Equals(x, "--execute-primary", StringComparison.Ordinal)))
            {
                return await RunPrimaryAsync(root);
            }
            await RunAsync(root);
            Console.WriteLine("V5B_STATUS=SEMANTIC_NODE_INDUCTION_PREFLIGHT_COMPLETE REQUESTS_FROZEN=true MODEL_CALLS=0 PROVIDER_CALLS=0 GOLD_READ_COUNT=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V5B_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task<int> RunPrimaryAsync(string root)
    {
        var preflight = Full(root, OutputRelative);
        var execution = Full(root, ExecutionRelative);
        Require(File.Exists(Path.Combine(preflight, "manifest.json")), "V5B_PREFLIGHT_MISSING");
        Require(File.Exists(Path.Combine(preflight, "requests.json")), "V5B_REQUEST_SET_MISSING");
        Require(!Directory.Exists(execution) || !Directory.EnumerateFiles(execution, "*", SearchOption.AllDirectories).Any(), "V5B_EXECUTION_ALREADY_STARTED_NO_RESUME");

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(preflight, "manifest.json")));
        Require(manifest.RootElement.GetProperty("status").GetString() == "READY_FOR_PROVIDER_EXECUTION", "V5B_PREFLIGHT_STATUS");
        Require(manifest.RootElement.GetProperty("requestCount").GetInt32() == ExpectedClusters, "V5B_REQUEST_COUNT");
        using var requestsDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(preflight, "requests.json")));
        var requests = requestsDocument.RootElement.GetProperty("requests").EnumerateArray()
            .Select(x => JsonSerializer.Deserialize<HdsaSemanticClusterInductionRequest>(x.GetRawText(), JsonOptions) ?? throw new InvalidDataException("V5B_REQUEST_PARSE"))
            .OrderBy(x => x.ClusterId, StringComparer.Ordinal).ToArray();
        Require(requests.Length == ExpectedClusters, "V5B_REQUEST_COUNT");
        Require(requests.All(x => !x.GoldDerivedInput && !x.KnownPairLabelsIncluded && !x.HierarchyRequested), "V5B_REQUEST_FIREWALL");
        var requestHashes = requests.ToDictionary(x => x.ClusterId, HdsaSemanticClusterInductionContract.RequestHash, StringComparer.Ordinal);
        Require(requestHashes.Values.Distinct(StringComparer.Ordinal).Count() == requests.Length, "V5B_DUPLICATE_REQUEST_HASH");

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Directory.CreateDirectory(execution);
            await WriteAsync(Path.Combine(execution, "execution-manifest.json"), new { status = "BLOCKED_ON_PROVIDER_API_KEY", model = Model, provider = Provider, scheduledCalls = ExpectedClusters, modelCalls = 0, providerCalls = 0, goldReadCount = 0 });
            return 1;
        }

        Directory.CreateDirectory(execution);
        var attemptsDir = Path.Combine(execution, "attempts");
        var rawDir = Path.Combine(execution, "raw-responses");
        var parsedDir = Path.Combine(execution, "parsed");
        Directory.CreateDirectory(attemptsDir);
        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(parsedDir);
        await WriteAsync(Path.Combine(execution, "execution-manifest.json"), new
        {
            schemaVersion = "a99-v5b-execution-manifest-v1",
            status = "EXECUTING",
            provider = Provider,
            model = Model,
            endpoint = Endpoint,
            preflightManifestSha256 = Sha256File(Path.Combine(preflight, "manifest.json")),
            requestSetSha256 = Sha256File(Path.Combine(preflight, "requests.json")),
            scheduledCalls = ExpectedClusters,
            transientRequestRetries = 0,
            maxParallelRequests = 1,
            goldReadCount = 0,
            goldReadBeforeFreeze = false,
            startedUtc = DateTimeOffset.UtcNow,
        });

        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint),
            ApiKey = key,
            Model = Model,
            ContextSize = 1_000_000,
            MaxOutputTokens = 48_000,
            RequestTimeoutSeconds = 600,
            TransientRequestRetries = 0,
            MaxParallelRequests = 1,
            SendChatTemplateKwargs = false,
            OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = new OpenRouterModelCapability
        {
            ModelId = Model,
            ContextLength = 1_000_000,
            ReasoningSupported = true,
            StructuredOutputSupported = true,
            SelectedReasoningEffort = "enabled",
            ReasoningEnabled = true,
            EffortListReported = false,
            MaxCompletionTokens = 65_536,
        };
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(root, "identity-benchmark-v5b", "102-cluster-semantic-node-induction");
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var attempts = new List<object>();
        var results = new List<ExecutionResult>();
        var sequence = 0;
        foreach (var request in requests)
        {
            sequence++;
            var clusterId = request.ClusterId;
            var requestHash = requestHashes[clusterId];
            var started = DateTimeOffset.UtcNow;
            var attemptId = $"primary-{sequence:D3}";
            var attemptStartedPath = Path.Combine(attemptsDir, $"{sequence:D3}.started.json");
            await WriteAsync(attemptStartedPath, new
            {
                schemaVersion = "a99-v5b-attempt-start-v1",
                attemptId,
                sequence,
                clusterId,
                requestHash,
                startedUtc = started,
                retry = false,
                goldReadBeforeAttempt = false,
            });

            string status = "PROVIDER_ERROR";
            string? rawHash = null;
            string? parsedHash = null;
            string? error = null;
            int? httpStatus = null;
            string? providerCallId = null;
            RequestPacketTelemetry? telemetry = null;
            object? parsed = null;
            var requestJson = JsonSerializer.Serialize(request, JsonOptions);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await model.CompleteRawStructuredSemanticAsync(
                    request.DocumentId,
                    "V5B_GLOBAL_CLUSTER_SEMANTIC_NODE_INDUCTION",
                    $"v5b:{clusterId}:{requestHash}",
                    requestJson,
                    request.Occurrences.Sum(x => x.Text.Length),
                    request.Occurrences.Count,
                    request.Occurrences.Count,
                    SystemPrompt,
                    $"TASK=V5B_GLOBAL_CLUSTER_SEMANTIC_NODE_INDUCTION\n{requestJson}\nReturn exactly the requested JSON object.",
                    ResponseSchema(),
                    "a99_v5b_cluster_semantic_node_induction_v1");
                telemetry = result.Telemetry;
                httpStatus = telemetry.HttpStatus;
                providerCallId = telemetry.ProviderCallId;
                rawHash = Sha256Text(result.Content);
                await File.WriteAllTextAsync(Path.Combine(rawDir, $"{sequence:D3}.json"), result.Content, new UTF8Encoding(false));
                try
                {
                    var response = JsonSerializer.Deserialize<HdsaSemanticClusterInductionResponse>(result.Content, JsonOptions);
                    if (response is null) throw new JsonException("V5B_EMPTY_RESPONSE");
                    var validation = HdsaSemanticClusterInductionContract.Validate(request, response);
                    parsed = new { response, validation };
                    parsedHash = Sha256Text(JsonSerializer.Serialize(parsed, JsonOptions));
                    await WriteAsync(Path.Combine(parsedDir, $"{sequence:D3}.json"), parsed);
                    status = validation.Accepted ? "VALID" : "INVALID_VALIDATION";
                }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException)
                {
                    status = "INVALID_SCHEMA";
                    error = ex.Message;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or ReasoningCompletionException or FormatException)
            {
                status = "PROVIDER_ERROR";
                error = ex.GetType().Name + ":" + ex.Message;
            }
            stopwatch.Stop();
            var completed = new
            {
                schemaVersion = "a99-v5b-attempt-v1",
                attemptId,
                sequence,
                clusterId,
                requestHash,
                status,
                httpStatus,
                providerCallId,
                rawResponseSha256 = rawHash,
                parsedResponseSha256 = parsedHash,
                provider = Provider,
                model = Model,
                latencyMs = stopwatch.ElapsedMilliseconds,
                reportedInputTokens = telemetry?.ReportedInputTokens,
                reportedReasoningTokens = telemetry?.ReportedReasoningTokens,
                reportedOutputTokens = telemetry?.ReportedOutputTokens,
                finishReason = telemetry?.FinishReason,
                error,
                goldReadBeforeFreeze = false,
                response = parsed,
            };
            await WriteAsync(Path.Combine(attemptsDir, $"{sequence:D3}.completed.json"), completed);
            attempts.Add(completed);
            results.Add(new ExecutionResult(clusterId, requestHash, status, parsed, rawHash, parsedHash));
            await WriteAsync(Path.Combine(execution, "attempt-manifest.json"), new
            {
                schemaVersion = "a99-v5b-attempt-manifest-v1",
                immutableAttemptRecords = true,
                expectedAttemptCount = ExpectedClusters,
                actualAttemptCount = attempts.Count,
                actualModelCalls = model.ProviderCalls,
                actualProviderCalls = model.ProviderCalls,
                transientRequestRetries = 0,
                goldReadCount = 0,
                attempts,
            });
            Console.WriteLine($"V5B_ATTEMPT={sequence}/{ExpectedClusters} CLUSTER={clusterId} STATUS={status} PROVIDER_CALLS={model.ProviderCalls}");
        }

        await WriteAsync(Path.Combine(execution, "prediction-freeze.json"), new
        {
            schemaVersion = "a99-v5b-prediction-freeze-v1",
            status = "PREDICTIONS_FROZEN_BEFORE_GOLD",
            scheduledClusters = ExpectedClusters,
            completedAttempts = attempts.Count,
            validClusters = results.Count(x => x.Status == "VALID"),
            invalidClusters = results.Count(x => x.Status != "VALID"),
            assignedOccurrences = "NOT_OPENED_UNTIL_PARSE_SUMMARY",
            modelCalls = model.ProviderCalls,
            providerCalls = model.ProviderCalls,
            goldReadCount = 0,
            goldReadBeforeFreeze = false,
            rawResponsesPersistedBeforeParsing = true,
            retryCount = 0,
            attempts,
        });
        await WriteAsync(Path.Combine(execution, "firewall.json"), new
        {
            schemaVersion = "a99-v5b-execution-firewall-v1",
            status = "COMPLETE_PREDICTION_FREEZE",
            scheduledClusters = ExpectedClusters,
            completedAttempts = attempts.Count,
            modelCalls = model.ProviderCalls,
            providerCalls = model.ProviderCalls,
            goldReadCount = 0,
            goldReadBeforeFreeze = false,
            pairLabelsRead = false,
            hierarchyHintsRead = false,
            promotionDecision = false,
            retries = 0,
            overwrite = false,
        });
        return attempts.Count == ExpectedClusters && model.ProviderCalls == ExpectedClusters ? 0 : 1;
    }

    private const string SystemPrompt = """
You are the A99 V5B global semantic-node induction reasoner. Inspect only the supplied frozen
source-backed ambiguity cluster and its parser-owned context. Decide which occurrences represent
the same semantic object, and assign each occurrence to a cluster-local semanticNodeLocalId.
Explain the organizational function of each induced node in the required fields. Use PRIMARY,
REPEAT, CONTINUATION, or ALIAS_REPRESENTATION for occurrenceRole. Use continuationEdges only
when a later occurrence explicitly continues the logical heading begun by an earlier occurrence.
Every occurrence must be assigned exactly once or listed in unresolvedOccurrenceIds.

Do not return pair labels, Gold labels, known benchmark IDs, parent, hierarchy, level, promotion
decisions, offsets, or global semantic node IDs. semanticNodeLocalId is meaningful only inside
this cluster. Do not infer missing assignments or repair incomplete output.
""";

    private static object ResponseSchema() => new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "clusterId", "occurrenceAssignments", "semanticNodes", "continuationEdges", "unresolvedOccurrenceIds" },
        properties = new
        {
            clusterId = new { type = "string", minLength = 1 },
            occurrenceAssignments = new
            {
                type = "array",
                items = new
                {
                    type = "object", additionalProperties = false,
                    required = new[] { "occurrenceId", "semanticNodeLocalId", "occurrenceRole", "confidence" },
                    properties = new
                    {
                        occurrenceId = new { type = "string", minLength = 1 },
                        semanticNodeLocalId = new { type = "string", minLength = 1 },
                        occurrenceRole = new { type = "string", @enum = new[] { "PRIMARY", "REPEAT", "CONTINUATION", "ALIAS_REPRESENTATION" } },
                        confidence = new { type = "string", @enum = new[] { "HIGH", "MEDIUM", "LOW" } },
                    },
                },
            },
            semanticNodes = new
            {
                type = "array",
                items = new
                {
                    type = "object", additionalProperties = false,
                    required = new[] { "semanticNodeLocalId", "canonicalMeaning", "structuralScopeDescription", "organizationalFunction" },
                    properties = new
                    {
                        semanticNodeLocalId = new { type = "string", minLength = 1 },
                        canonicalMeaning = new { type = "string", minLength = 1 },
                        structuralScopeDescription = new { type = "string", minLength = 1 },
                        organizationalFunction = new { type = "string", minLength = 1 },
                    },
                },
            },
            continuationEdges = new
            {
                type = "array",
                items = new
                {
                    type = "object", additionalProperties = false,
                    required = new[] { "fromOccurrenceId", "toOccurrenceId" },
                    properties = new
                    {
                        fromOccurrenceId = new { type = "string", minLength = 1 },
                        toOccurrenceId = new { type = "string", minLength = 1 },
                    },
                },
            },
            unresolvedOccurrenceIds = new { type = "array", items = new { type = "string", minLength = 1 } },
        },
    };

    private sealed record ExecutionResult(string ClusterId, string RequestHash, string Status, object? Parsed, string? RawResponseSha256, string? ParsedResponseSha256);

    private static async Task RunAsync(string root)
    {
        var clusterPath = Full(root, ClusterRelative);
        var clusterManifestPath = Full(root, ClusterManifestRelative);
        var sourceCatalogPath = Full(root, SourceCatalogRelative);
        var output = Full(root, OutputRelative);
        Directory.CreateDirectory(output);

        using var clusterDoc = JsonDocument.Parse(await File.ReadAllTextAsync(clusterPath));
        using var clusterManifest = JsonDocument.Parse(await File.ReadAllTextAsync(clusterManifestPath));
        using var sourceCatalogDoc = JsonDocument.Parse(await File.ReadAllTextAsync(sourceCatalogPath));
        ValidateClusterFreeze(clusterDoc.RootElement, clusterManifest.RootElement);
        var sourceOccurrences = ReadSourceOccurrences(sourceCatalogDoc.RootElement);
        var requests = BuildRequests(clusterDoc.RootElement, sourceOccurrences, Sha256File(sourceCatalogPath));
        if (requests.Count != ExpectedClusters) throw new InvalidDataException("V5B_CLUSTER_COUNT");

        var requestRecords = requests.Select(request => new
        {
            clusterId = request.ClusterId,
            documentId = request.DocumentId,
            requestHash = HdsaSemanticClusterInductionContract.RequestHash(request),
            occurrenceCount = request.Occurrences.Count,
            occurrenceIds = request.Occurrences.Select(x => x.OccurrenceId).ToArray(),
        }).ToArray();

        var manifest = new
        {
            schemaVersion = "a99-v5b-semantic-node-induction-preflight-v1",
            phase = "V5B_GLOBAL_CLUSTER_SEMANTIC_NODE_INDUCTION",
            status = "READY_FOR_PROVIDER_EXECUTION",
            clusterFreeze = Relative(root, clusterPath),
            clusterFreezeSha256 = Sha256File(clusterPath),
            clusterManifestSha256 = Sha256File(clusterManifestPath),
            sourceCatalog = Relative(root, sourceCatalogPath),
            sourceCatalogSha256 = Sha256File(sourceCatalogPath),
            requestSchemaVersion = HdsaSemanticClusterInductionContract.Version,
            clusterCount = requests.Count,
            occurrenceCount = requests.Sum(x => x.Occurrences.Count),
            requestCount = requests.Count,
            localContext = LocalContextRadius,
            modelCalls = 0,
            providerCalls = 0,
            goldReadCount = 0,
            goldDerivedInput = false,
            knownPairLabelsRead = false,
            knownPairLabelsIncluded = false,
            hierarchyRequested = false,
            rawResponsesPersisted = false,
            parsedResponsesPersisted = false,
            predictionsFrozen = false,
            note = "Offline V5B request preflight only. Requests contain source-owned cluster evidence and context; they contain no pair labels, Gold, hierarchy, or promotion decisions.",
        };

        await WriteAsync(Path.Combine(output, "manifest.json"), manifest);
        await WriteAsync(Path.Combine(output, "requests.json"), new
        {
            schemaVersion = "a99-v5b-semantic-node-induction-requests-v1",
            sourceOnly = true,
            requests,
        });
        await WriteAsync(Path.Combine(output, "request-index.json"), new
        {
            schemaVersion = "a99-v5b-request-index-v1",
            sourceOnly = true,
            requests = requestRecords,
        });
        await WriteAsync(Path.Combine(output, "firewall.json"), new
        {
            schemaVersion = "a99-v5b-firewall-v1",
            modelCalls = 0,
            providerCalls = 0,
            goldReadCount = 0,
            goldDerivedInput = false,
            knownPairLabelsRead = false,
            knownPairLabelsIncluded = false,
            hierarchyRequested = false,
            promotionDecision = false,
            pairLabel = false,
            semanticNodeAssignment = false,
            predictionFreeze = false,
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(manifest, requestRecords), new UTF8Encoding(false));
    }

    private static IReadOnlyList<HdsaSemanticClusterInductionRequest> BuildRequests(JsonElement root, IReadOnlyList<SourceOccurrence> sourceOccurrences, string sourceCatalogSha256)
    {
        var byId = sourceOccurrences.ToDictionary(x => x.OccurrenceId, StringComparer.Ordinal);
        var byDocument = sourceOccurrences.GroupBy(x => x.OccurrenceId.Split(':', 2)[0], StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.DocumentOrder).ToArray(), StringComparer.Ordinal);
        var requests = new List<HdsaSemanticClusterInductionRequest>();
        foreach (var cluster in root.GetProperty("clusters").EnumerateArray())
        {
            var clusterId = Required(cluster, "clusterId");
            var documentId = Required(cluster, "documentId");
            var occurrenceIds = cluster.GetProperty("occurrenceIds").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (occurrenceIds.Length == 0) throw new InvalidDataException("V5B_EMPTY_CLUSTER:" + clusterId);
            if (!byDocument.TryGetValue(documentId, out var ordered)) throw new InvalidDataException("V5B_UNKNOWN_DOCUMENT:" + documentId);
            var occurrences = occurrenceIds.Select(id =>
            {
                if (!byId.TryGetValue(id, out var source)) throw new InvalidDataException("V5B_UNKNOWN_OCCURRENCE:" + id);
                if (!id.StartsWith(documentId + ":", StringComparison.Ordinal)) throw new InvalidDataException("V5B_CROSS_DOCUMENT_OCCURRENCE:" + id);
                var index = Array.FindIndex(ordered, x => string.Equals(x.OccurrenceId, id, StringComparison.Ordinal));
                return new HdsaSemanticClusterOccurrence(
                    source.OccurrenceId,
                    source.Text,
                    source.DocumentOrder,
                    ordered.Skip(Math.Max(0, index - 2)).Take(Math.Min(2, index)).Where(x => x.DocumentOrder < source.DocumentOrder).Select(ToContext).ToArray(),
                    ordered.Skip(index + 1).Take(2).Select(ToContext).ToArray());
            }).OrderBy(x => x.DocumentOrder).ThenBy(x => x.OccurrenceId, StringComparer.Ordinal).ToArray();

            var evidence = new HdsaSemanticClusterEvidence(
                Strings(cluster, "evidenceReasons"),
                Strings(cluster, "evidenceConfigurations"),
                Strings(cluster, "sourceOrderDistanceBuckets"),
                Strings(cluster, "packetClasses"),
                Strings(cluster, "candidateIds"));
            requests.Add(new HdsaSemanticClusterInductionRequest(
                HdsaSemanticClusterInductionContract.Version,
                clusterId,
                documentId,
                sourceCatalogSha256,
                $"V5:{documentId}:{clusterId}",
                occurrences,
                evidence));
        }
        return requests.OrderBy(x => x.ClusterId, StringComparer.Ordinal).ToArray();
    }

    private static HdsaSemanticClusterContextOccurrence ToContext(SourceOccurrence source) => new(source.OccurrenceId, source.Text, source.DocumentOrder);

    private static IReadOnlyList<SourceOccurrence> ReadSourceOccurrences(JsonElement root) => root.GetProperty("sourceOccurrences").EnumerateArray()
        .Select(x => new SourceOccurrence(Required(x, "nodeId"), x.GetProperty("text").GetString() ?? string.Empty, x.GetProperty("documentOrder").GetInt32()))
        .ToArray();

    private static void ValidateClusterFreeze(JsonElement clusters, JsonElement manifest)
    {
        if (clusters.GetProperty("schemaVersion").GetString() != "a99-v5a-source-only-clusters-v1") throw new InvalidDataException("V5B_CLUSTER_SCHEMA_DRIFT");
        if (manifest.GetProperty("status").GetString() != "FROZEN_SOURCE_ONLY_AMBIGUITY_CLUSTERS") throw new InvalidDataException("V5B_CLUSTER_NOT_FROZEN");
        if (manifest.GetProperty("goldReadCount").GetInt32() != 0 || manifest.GetProperty("providerCalls").GetInt32() != 0) throw new InvalidDataException("V5B_CLUSTER_FIREWALL");
        if (manifest.GetProperty("semanticMergePerformed").GetBoolean() || manifest.GetProperty("semanticNodeIdsAssigned").GetBoolean()) throw new InvalidDataException("V5B_CLUSTER_SEMANTIC_CONTAMINATION");
        if (clusters.GetProperty("clusters").GetArrayLength() != ExpectedClusters) throw new InvalidDataException("V5B_CLUSTER_COUNT");
    }

    private static string[] Strings(JsonElement element, string name) => element.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    private static string Required(JsonElement element, string name) => element.GetProperty(name).GetString() ?? throw new InvalidDataException("V5B_EMPTY_" + name.ToUpperInvariant());
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
    private static string BuildReport(object manifest, IReadOnlyList<object> requests) => "# A99 V5B — semantic node induction preflight\n\n" +
        "Status: **READY_FOR_PROVIDER_EXECUTION**. This checkpoint prepares requests only; it does not execute a model/provider or open Gold.\n\n" +
        "## Contract boundary\n\n" +
        "The model is asked to jointly assign occurrences to cluster-local semantic nodes and occurrence roles. It is not asked for pair labels, hierarchy, levels, promotion decisions, or global node IDs.\n\n" +
        "## Firewall\n\n" +
        "- Model/provider calls: **0**.\n- Gold reads: **0**.\n- Known pair labels included: **false**.\n- Hierarchy requested: **false**.\n- Prediction freeze: **false**.\n\n" +
        "## Manifest\n\n```json\n" + JsonSerializer.Serialize(manifest, JsonOptions) + "\n```\n\n" +
        $"Prepared requests: **{requests.Count}**. Each request hash is indexed in `request-index.json`; raw and parsed response artifacts are intentionally absent until an authorized provider campaign.\n";

    private sealed record SourceOccurrence(string OccurrenceId, string Text, int DocumentOrder);
}
