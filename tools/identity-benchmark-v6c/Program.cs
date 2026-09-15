using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Eval.ReasoningRetention;
using DocxHeaderExtractor.Infrastructure.AI;

namespace IdentityBenchmarkV6C;

internal static class Program
{
    private const string V6BRelative = "artifacts/identity-benchmark/v6/owner-evidence/source-only-freeze-v1";
    private const string OutputRelative = "artifacts/identity-benchmark/v6/owner-induction/preflight-v1";
    private const string ExecutionRelative = "artifacts/identity-benchmark/v6/owner-induction/execution-v1";
    private const string Provider = "OpenRouter";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const int ExpectedRequests = 3;
    private const int ExpectedOccurrences = 226;
    private static readonly string[] ExpectedDocuments = ["DOC-0123", "DOC-0133", "DOC-0252"];
    private static readonly string[] ExpectedRequestHashes =
    [
        "53caf4c563485efb5e084a7efb062883cad6611a9d62a416b5e6a2edc09d7a08",
        "a78c2139f0f82c1d369e0e933511f0ef38539c25faa00357545eaed9664f2880",
        "3a4e3f7ad2e16de36f4582bf04fa7a361a0732290877e6c03b1c84000cd968cf",
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            if (args.Any(x => string.Equals(x, "--execute-primary", StringComparison.Ordinal))) return await ExecutePrimaryAsync(root);
            if (args.Any(x => string.Equals(x, "--finalize-primary", StringComparison.Ordinal))) return await FinalizePrimaryAsync(root);
            await RunPreflightAsync(root);
            Console.WriteLine("V6C_STATUS=OWNER_INDUCTION_PREFLIGHT_FROZEN REQUESTS=3 MODEL_CALLS=0 PROVIDER_CALLS=0 GOLD_READ_COUNT=0 V5C_READ_COUNT=0 V6A_READ_COUNT=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V6C_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task<int> ExecutePrimaryAsync(string root)
    {
        var preflight = Full(root, OutputRelative);
        var execution = Full(root, ExecutionRelative);
        Require(File.Exists(Path.Combine(preflight, "manifest.json")), "V6C_PREFLIGHT_MISSING");
        Require(File.Exists(Path.Combine(preflight, "requests.json")), "V6C_REQUEST_SET_MISSING");
        Require(!Directory.Exists(execution) || !Directory.EnumerateFiles(execution, "*", SearchOption.AllDirectories).Any(), "V6C_EXECUTION_ALREADY_STARTED_NO_RESUME");
        var frozen = LoadFrozenRequests(preflight);
        Directory.CreateDirectory(execution);
        var attemptsDir = Path.Combine(execution, "attempts");
        var rawDir = Path.Combine(execution, "raw-responses");
        var parsedDir = Path.Combine(execution, "parsed");
        Directory.CreateDirectory(attemptsDir);
        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(parsedDir);
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await WriteAsync(Path.Combine(execution, "execution-manifest.json"), new { schemaVersion = "a99-v6c-execution-manifest-v1", status = "BLOCKED_ON_PROVIDER_API_KEY", scheduledCalls = ExpectedRequests, modelCalls = 0, providerCalls = 0, retryCount = 0, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0 });
            return 1;
        }
        await WriteAsync(Path.Combine(execution, "execution-manifest.json"), new
        {
            schemaVersion = "a99-v6c-execution-manifest-v1", status = "EXECUTING", provider = Provider, model = Model, endpoint = Endpoint,
            preflightManifestSha256 = Sha256File(Path.Combine(preflight, "manifest.json")), requestSetSha256 = Sha256File(Path.Combine(preflight, "requests.json")),
            scheduledCalls = ExpectedRequests, transientRequestRetries = 0, maxParallelRequests = 1, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, startedUtc = DateTimeOffset.UtcNow,
        });

        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), ApiKey = apiKey, Model = Model, ContextSize = 1_000_000, MaxOutputTokens = 48_000,
            RequestTimeoutSeconds = 600, TransientRequestRetries = 0, MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = new OpenRouterModelCapability
        {
            ModelId = Model, ContextLength = 1_000_000, ReasoningSupported = true, StructuredOutputSupported = true, SelectedReasoningEffort = "enabled",
            ReasoningEnabled = true, EffortListReported = false, MaxCompletionTokens = 65_536,
        };
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(root, "identity-benchmark-v6c", "3-document-global-structural-owner-induction");
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var completedAttempts = new List<object>();
        for (var i = 0; i < frozen.Count; i++)
        {
            var item = frozen[i];
            var sequence = i + 1;
            var attemptId = $"primary-{sequence:D3}";
            await WriteAsync(Path.Combine(attemptsDir, $"{sequence:D3}.started.json"), new
            {
                schemaVersion = "a99-v6c-attempt-start-v1", attemptId, sequence, documentId = item.DocumentId, requestHash = item.RequestHash,
                requestHashVerified = true, retry = false, goldReadBeforeAttempt = false, v5cReadBeforeAttempt = false, v6aReadBeforeAttempt = false, startedUtc = DateTimeOffset.UtcNow,
            });
            var status = "PROVIDER_ERROR";
            string? error = null;
            string? rawHash = null;
            string? parsedHash = null;
            int? httpStatus = null;
            string? providerCallId = null;
            RequestPacketTelemetry? telemetry = null;
            object? parsed = null;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var requestJson = item.Request.GetRawText();
                var result = await model.CompleteRawStructuredSemanticAsync(
                    item.DocumentId, "V6C_DOCUMENT_GLOBAL_STRUCTURAL_OWNER_INDUCTION", $"v6c:{item.DocumentId}:{item.RequestHash}", requestJson,
                    item.OccurrenceCount, item.OccurrenceCount, item.OccurrenceCount, SystemPrompt,
                    $"TASK=V6C_DOCUMENT_GLOBAL_STRUCTURAL_OWNER_INDUCTION\n{requestJson}\nReturn exactly the requested JSON object.", ResponseSchema(), "a99_v6c_structural_owner_induction_v1");
                telemetry = result.Telemetry;
                httpStatus = telemetry.HttpStatus;
                providerCallId = telemetry.ProviderCallId;
                rawHash = Sha256Text(result.Content);
                await File.WriteAllTextAsync(Path.Combine(rawDir, $"{sequence:D3}.json"), result.Content, new UTF8Encoding(false));
                try
                {
                    using var response = JsonDocument.Parse(result.Content);
                    try
                    {
                        var validation = ValidateOwnerResponse(item.Request, response.RootElement);
                        parsed = new { response = response.RootElement.Clone(), validation };
                        parsedHash = Sha256Text(JsonSerializer.Serialize(parsed, JsonOptions));
                        await WriteAsync(Path.Combine(parsedDir, $"{sequence:D3}.json"), parsed);
                        status = validation.Accepted ? "VALID" : "INVALID_VALIDATION";
                    }
                    catch (InvalidDataException ex)
                    {
                        parsed = new { response = response.RootElement.Clone(), validation = new { accepted = false, error = ex.Message } };
                        parsedHash = Sha256Text(JsonSerializer.Serialize(parsed, JsonOptions));
                        await WriteAsync(Path.Combine(parsedDir, $"{sequence:D3}.json"), parsed);
                        status = "INVALID_VALIDATION";
                        error = ex.Message;
                    }
                }
                catch (JsonException ex)
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
                schemaVersion = "a99-v6c-attempt-v1", attemptId, sequence, documentId = item.DocumentId, requestHash = item.RequestHash, status,
                httpStatus, providerCallId, rawResponseSha256 = rawHash, parsedResponseSha256 = parsedHash, provider = Provider, model = Model,
                latencyMs = stopwatch.ElapsedMilliseconds, reportedInputTokens = telemetry?.ReportedInputTokens, reportedReasoningTokens = telemetry?.ReportedReasoningTokens,
                reportedOutputTokens = telemetry?.ReportedOutputTokens, finishReason = telemetry?.FinishReason, error, retryCount = 0,
                goldReadBeforeFreeze = false, v5cReadBeforeFreeze = false, v6aReadBeforeFreeze = false, response = parsed,
            };
            await WriteAsync(Path.Combine(attemptsDir, $"{sequence:D3}.completed.json"), completed);
            completedAttempts.Add(completed);
            await WriteAsync(Path.Combine(execution, "attempt-manifest.json"), new
            {
                schemaVersion = "a99-v6c-attempt-manifest-v1", immutableAttemptRecords = true, expectedAttemptCount = ExpectedRequests,
                actualAttemptCount = completedAttempts.Count, actualModelCalls = model.ProviderCalls, actualProviderCalls = model.ProviderCalls,
                retryCount = 0, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, attempts = completedAttempts,
            });
            Console.WriteLine($"V6C_ATTEMPT={sequence}/{ExpectedRequests} DOCUMENT={item.DocumentId} STATUS={status} PROVIDER_CALLS={model.ProviderCalls}");
        }
        Require(completedAttempts.Count == ExpectedRequests, "V6C_ATTEMPT_COUNT");
        Require(model.ProviderCalls == ExpectedRequests, "V6C_PROVIDER_CALL_COUNT");
        await WriteAsync(Path.Combine(execution, "prediction-freeze.json"), new
        {
            schemaVersion = "a99-v6c-prediction-freeze-v1", status = "OWNER_PREDICTIONS_FROZEN_BEFORE_EVALUATION", scheduledRequests = ExpectedRequests,
            completedAttempts = completedAttempts.Count, modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0,
            goldReadBeforeFreeze = false, rawResponsesPersistedBeforeParsing = true, requestMutation = false, retryCount = 0, attempts = completedAttempts,
        });
        await WriteAsync(Path.Combine(execution, "firewall.json"), new
        {
            schemaVersion = "a99-v6c-execution-firewall-v1", status = "COMPLETE_OWNER_PREDICTION_FREEZE", scheduledRequests = ExpectedRequests, completedAttempts = completedAttempts.Count,
            modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, pairLabelsRead = false, hierarchyRead = false,
            semanticNodeInduction = false, retries = 0, overwrite = false,
        });
        Console.WriteLine($"V6C_STATUS=OWNER_PREDICTIONS_FROZEN REQUESTS={ExpectedRequests} MODEL_CALLS={model.ProviderCalls} PROVIDER_CALLS={model.ProviderCalls} GOLD_READ_COUNT=0 V5C_READ_COUNT=0 V6A_READ_COUNT=0");
        return 0;
    }

    private static async Task<int> FinalizePrimaryAsync(string root)
    {
        var preflight = Full(root, OutputRelative);
        var execution = Full(root, ExecutionRelative);
        var frozen = LoadFrozenRequests(preflight);
        var attemptManifestPath = Path.Combine(execution, "attempt-manifest.json");
        Require(File.Exists(attemptManifestPath), "V6C_ATTEMPT_MANIFEST_MISSING");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(attemptManifestPath));
        var attempts = manifest.RootElement.GetProperty("attempts").EnumerateArray().ToArray();
        Require(attempts.Length == ExpectedRequests && manifest.RootElement.GetProperty("retryCount").GetInt32() == 0 && manifest.RootElement.GetProperty("goldReadCount").GetInt32() == 0, "V6C_FINALIZE_FIREWALL");
        var byDocument = frozen.ToDictionary(x => x.DocumentId, StringComparer.Ordinal);
        var revalidated = await RevalidateFrozenResponsesAsync(execution, frozen, attempts);
        var effectiveStatuses = attempts.ToDictionary(x => x.GetProperty("sequence").GetInt32(), x => revalidated.TryGetValue(x.GetProperty("sequence").GetInt32(), out var status) ? status : x.GetProperty("status").GetString()!, EqualityComparer<int>.Default);
        var valid = attempts.Where(x => effectiveStatuses[x.GetProperty("sequence").GetInt32()] == "VALID").ToArray();
        var assigned = 0;
        var unresolved = 0;
        var ownerCount = 0;
        var crossScopeOwners = 0;
        var splitScopeGroups = 0;
        var ownerSizes = new List<int>();
        foreach (var attempt in valid)
        {
            var documentId = attempt.GetProperty("documentId").GetString()!;
            var response = attempt.GetProperty("response").GetProperty("response");
            var ownerMembers = response.GetProperty("owners").EnumerateArray().ToDictionary(x => x.GetProperty("ownerLocalId").GetString()!, x => x.GetProperty("memberOccurrenceIds").EnumerateArray().Select(y => y.GetString()!).ToArray(), StringComparer.Ordinal);
            ownerCount += ownerMembers.Count;
            assigned += response.GetProperty("assignments").GetArrayLength();
            unresolved += response.GetProperty("unresolvedOccurrenceIds").GetArrayLength();
            var request = byDocument[documentId].Request;
            var occurrenceScopes = request.GetProperty("occurrences").EnumerateArray().ToDictionary(x => x.GetProperty("occurrenceId").GetString()!, x => x.GetProperty("sourceContainerIdentity").GetString()!, StringComparer.Ordinal);
            foreach (var members in ownerMembers.Values)
            {
                ownerSizes.Add(members.Length);
                if (members.Select(id => occurrenceScopes[id]).Distinct(StringComparer.Ordinal).Count() > 1) crossScopeOwners++;
            }
            foreach (var group in request.GetProperty("parserOwnedScopeGroups").EnumerateArray())
            {
                var groupOccurrences = group.GetProperty("occurrenceIds").EnumerateArray().Select(x => x.GetString()!).ToArray();
                var ownerIds = response.GetProperty("assignments").EnumerateArray().Where(x => groupOccurrences.Contains(x.GetProperty("occurrenceId").GetString()!, StringComparer.Ordinal)).Select(x => x.GetProperty("ownerLocalId").GetString()!).Distinct(StringComparer.Ordinal).ToArray();
                if (ownerIds.Length > 1) splitScopeGroups++;
            }
        }
        var invalid = attempts.Count(x => effectiveStatuses[x.GetProperty("sequence").GetInt32()] is "INVALID_VALIDATION" or "INVALID_SCHEMA");
        var providerErrors = attempts.Count(x => effectiveStatuses[x.GetProperty("sequence").GetInt32()] == "PROVIDER_ERROR");
        var modelCalls = manifest.RootElement.GetProperty("actualModelCalls").GetInt32();
        var providerCalls = manifest.RootElement.GetProperty("actualProviderCalls").GetInt32();
        var summary = new
        {
            schemaVersion = "a99-v6c-prediction-summary-v1", status = "OWNER_PREDICTIONS_FROZEN_BEFORE_EVALUATION", scheduledRequests = ExpectedRequests, completedAttempts = attempts.Length,
            validOwnerPredictions = valid.Length, invalidValidationPredictions = invalid, providerErrors, sourceOccurrenceDenominator = ExpectedOccurrences, assignedOccurrences = assigned,
            unresolvedOccurrences = unresolved, inducedOwnerCount = ownerCount, ownerSizeDistribution = ownerSizes.OrderBy(x => x).ToArray(), crossParserScopeOwners = crossScopeOwners,
            splitParserScopeGroups = splitScopeGroups, modelCalls, providerCalls, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, goldEvaluationOpened = false,
            semanticNodeInduction = false, hierarchyExecuted = false, rawResponsesPersistedBeforeParsing = true, retryCount = 0,
            effectiveAttemptStatuses = effectiveStatuses.OrderBy(x => x.Key).ToDictionary(x => x.Key.ToString("D3"), x => x.Value, StringComparer.Ordinal),
            note = "Source-independent owner induction aggregation only. Raw responses were revalidated offline to distinguish schema parsing from validator rejection; no Gold, V5C, or V6A artifacts were read and no semantic repair was applied.",
        };
        await WriteAsync(Path.Combine(execution, "prediction-summary.json"), summary);
        await WriteAsync(Path.Combine(execution, "execution-final.json"), new
        {
            schemaVersion = "a99-v6c-execution-final-v1", status = "OWNER_PREDICTION_FREEZE_COMPLETE", scheduledCalls = ExpectedRequests, completedAttempts = attempts.Length,
            validOwnerPredictions = valid.Length, invalidValidationPredictions = invalid, providerErrors, modelCalls, providerCalls, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0,
            frozenBeforeEvaluation = true, recoveryCohort = false, retries = 0, completedUtc = DateTimeOffset.UtcNow,
        });
        Console.WriteLine($"V6C_OFFLINE_SUMMARY_COMPLETE VALID={valid.Length} INVALID={invalid} PROVIDER_ERRORS={providerErrors} MODEL_CALLS={modelCalls} PROVIDER_CALLS={providerCalls} GOLD_READ_COUNT=0");
        return 0;
    }

    private static async Task<Dictionary<int, string>> RevalidateFrozenResponsesAsync(string execution, IReadOnlyList<FrozenRequest> frozen, JsonElement[] attempts)
    {
        var byDocument = frozen.ToDictionary(x => x.DocumentId, StringComparer.Ordinal);
        var parsedDir = Path.Combine(execution, "parsed");
        var revalidationDir = Path.Combine(execution, "revalidation-v1");
        Directory.CreateDirectory(parsedDir);
        Directory.CreateDirectory(revalidationDir);
        var statuses = new Dictionary<int, string>();
        var records = new List<object>();
        foreach (var attempt in attempts)
        {
            var sequence = attempt.GetProperty("sequence").GetInt32();
            var documentId = attempt.GetProperty("documentId").GetString()!;
            var rawPath = Path.Combine(execution, "raw-responses", $"{sequence:D3}.json");
            if (!File.Exists(rawPath))
            {
                statuses[sequence] = attempt.GetProperty("status").GetString()!;
                continue;
            }
            var raw = await File.ReadAllTextAsync(rawPath);
            try
            {
                using var response = JsonDocument.Parse(raw);
                try
                {
                    var validation = ValidateOwnerResponse(byDocument[documentId].Request, response.RootElement);
                    var parsed = new { response = response.RootElement.Clone(), validation };
                    await WriteAsync(Path.Combine(parsedDir, $"{sequence:D3}.json"), parsed);
                    statuses[sequence] = "VALID";
                    records.Add(new { sequence, documentId, status = "VALID", validationError = (string?)null, rawResponseSha256 = Sha256Text(raw), parsedResponseSha256 = Sha256Text(JsonSerializer.Serialize(parsed, JsonOptions)) });
                }
                catch (InvalidDataException ex)
                {
                    var parsed = new { response = response.RootElement.Clone(), validation = new { accepted = false, error = ex.Message } };
                    await WriteAsync(Path.Combine(parsedDir, $"{sequence:D3}.json"), parsed);
                    statuses[sequence] = "INVALID_VALIDATION";
                    records.Add(new { sequence, documentId, status = "INVALID_VALIDATION", validationError = ex.Message, rawResponseSha256 = Sha256Text(raw), parsedResponseSha256 = Sha256Text(JsonSerializer.Serialize(parsed, JsonOptions)) });
                }
            }
            catch (JsonException ex)
            {
                statuses[sequence] = "INVALID_SCHEMA";
                records.Add(new { sequence, documentId, status = "INVALID_SCHEMA", validationError = ex.Message, rawResponseSha256 = Sha256Text(raw), parsedResponseSha256 = (string?)null });
            }
        }
        await WriteAsync(Path.Combine(revalidationDir, "manifest.json"), new
        {
            schemaVersion = "a99-v6c-v1-revalidation-manifest",
            status = "RAW_RESPONSES_REVALIDATED_OFFLINE",
            providerCalls = 0,
            goldReadCount = 0,
            v5cReadCount = 0,
            v6aReadCount = 0,
            predictionMutation = false,
            records,
        });
        return statuses;
    }

    private static IReadOnlyList<FrozenRequest> LoadFrozenRequests(string preflight)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(preflight, "manifest.json")));
        var root = manifest.RootElement;
        Require(root.GetProperty("status").GetString() == "READY_FOR_PROVIDER_EXECUTION", "V6C_PREFLIGHT_STATUS");
        Require(root.GetProperty("requestCount").GetInt32() == ExpectedRequests && root.GetProperty("documentCount").GetInt32() == ExpectedRequests && root.GetProperty("occurrenceCount").GetInt32() == ExpectedOccurrences, "V6C_PREFLIGHT_COUNTS");
        Require(root.GetProperty("goldReadCount").GetInt32() == 0 && root.GetProperty("v5cReadCount").GetInt32() == 0 && root.GetProperty("v6aReadCount").GetInt32() == 0, "V6C_PREFLIGHT_FIREWALL");
        Require(root.GetProperty("requestHashes").EnumerateArray().Select(x => x.GetString()).SequenceEqual(ExpectedRequestHashes, StringComparer.Ordinal), "V6C_FINGERPRINT_GUARD");
        using var requestsDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(preflight, "requests.json")));
        var requests = requestsDocument.RootElement.GetProperty("requests").EnumerateArray().Select(x =>
        {
            var request = x.GetProperty("request").Clone();
            var hash = x.GetProperty("requestHash").GetString() ?? throw new InvalidDataException("V6C_REQUEST_HASH_MISSING");
            Require(hash == Sha256Text(JsonSerializer.Serialize(request, JsonOptions)), "V6C_REQUEST_HASH_MISMATCH");
            Require(!request.GetProperty("goldDerivedInput").GetBoolean() && !request.GetProperty("v5cEvaluationIncluded").GetBoolean() && !request.GetProperty("v6aDiagnosisIncluded").GetBoolean() && !request.GetProperty("semanticNodeRequested").GetBoolean() && !request.GetProperty("hierarchyRequested").GetBoolean(), "V6C_REQUEST_FIREWALL");
            return new FrozenRequest(request.GetProperty("documentId").GetString()!, hash, request, request.GetProperty("occurrences").GetArrayLength());
        }).OrderBy(x => x.DocumentId, StringComparer.Ordinal).ToArray();
        Require(requests.Length == ExpectedRequests && requests.Select(x => x.DocumentId).SequenceEqual(ExpectedDocuments, StringComparer.Ordinal), "V6C_DOCUMENT_ORDER");
        Require(requests.Select(x => x.RequestHash).SequenceEqual(ExpectedRequestHashes, StringComparer.Ordinal), "V6C_REQUEST_HASH_ORDER");
        Require(requests.Sum(x => x.OccurrenceCount) == ExpectedOccurrences, "V6C_OCCURRENCE_COUNT");
        return requests;
    }

    private static OwnerValidation ValidateOwnerResponse(JsonElement request, JsonElement response)
    {
        Require(response.ValueKind == JsonValueKind.Object, "V6C_RESPONSE_OBJECT_REQUIRED");
        var allowed = new HashSet<string>(request.GetProperty("occurrences").EnumerateArray().Select(x => x.GetProperty("occurrenceId").GetString()!), StringComparer.Ordinal);
        var validEvidence = new HashSet<string>(allowed, StringComparer.Ordinal);
        validEvidence.UnionWith(request.GetProperty("occurrences").EnumerateArray().Select(x => x.GetProperty("sourceContainerIdentity").GetString()!));
        validEvidence.UnionWith(request.GetProperty("parserOwnedScopeGroups").EnumerateArray().Select(x => x.GetProperty("sourceContainerIdentity").GetString()!));
        Require(response.TryGetProperty("owners", out var owners) && owners.ValueKind == JsonValueKind.Array, "V6C_OWNERS_REQUIRED");
        Require(response.TryGetProperty("assignments", out var assignments) && assignments.ValueKind == JsonValueKind.Array, "V6C_ASSIGNMENTS_REQUIRED");
        Require(response.TryGetProperty("unresolvedOccurrenceIds", out var unresolved) && unresolved.ValueKind == JsonValueKind.Array, "V6C_UNRESOLVED_REQUIRED");
        var ownerMembers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var owner in owners.EnumerateArray())
        {
            var id = Required(owner, "ownerLocalId");
            Require(Required(owner, "ownerDescription").Length > 0, "V6C_OWNER_DESCRIPTION");
            Require(owner.TryGetProperty("memberOccurrenceIds", out var members) && members.ValueKind == JsonValueKind.Array && members.GetArrayLength() > 0, "V6C_OWNER_MEMBERS");
            Require(owner.TryGetProperty("autonomous", out var autonomous) && (autonomous.ValueKind is JsonValueKind.True or JsonValueKind.False), "V6C_OWNER_AUTONOMOUS");
            Require(owner.TryGetProperty("evidenceRefs", out var refs) && refs.ValueKind == JsonValueKind.Array, "V6C_OWNER_EVIDENCE");
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in members.EnumerateArray()) { var memberId = member.GetString() ?? throw new InvalidDataException("V6C_MEMBER_ID"); Require(allowed.Contains(memberId) && set.Add(memberId), "V6C_UNKNOWN_OR_DUPLICATE_MEMBER"); }
            foreach (var evidence in refs.EnumerateArray()) Require(evidence.ValueKind == JsonValueKind.String && validEvidence.Contains(evidence.GetString()!), "V6C_FABRICATED_EVIDENCE_REFERENCE");
            Require(ownerMembers.TryAdd(id, set), "V6C_DUPLICATE_OWNER_ID");
        }
        var assignmentCount = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assignment in assignments.EnumerateArray())
        {
            var occurrenceId = Required(assignment, "occurrenceId");
            var ownerId = Required(assignment, "ownerLocalId");
            Require(allowed.Contains(occurrenceId) && ownerMembers.ContainsKey(ownerId), "V6C_ASSIGNMENT_REFERENCE");
            Require(assignmentCount.TryAdd(occurrenceId, ownerId), "V6C_MULTIPLE_OWNER_ASSIGNMENT");
            Require(ownerMembers[ownerId].Contains(occurrenceId), "V6C_OWNER_ASSIGNMENT_MISMATCH");
        }
        foreach (var owner in ownerMembers) foreach (var member in owner.Value) Require(assignmentCount.TryGetValue(member, out var assignedOwner) && assignedOwner == owner.Key, "V6C_MISSING_ASSIGNMENT");
        var unresolvedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in unresolved.EnumerateArray()) { var id = item.GetString() ?? throw new InvalidDataException("V6C_UNRESOLVED_ID"); Require(allowed.Contains(id) && unresolvedIds.Add(id), "V6C_UNKNOWN_OR_DUPLICATE_UNRESOLVED"); }
        Require(assignmentCount.Keys.Concat(unresolvedIds).Distinct(StringComparer.Ordinal).Count() == allowed.Count, "V6C_INCOMPLETE_ASSIGNMENT_COVERAGE");
        Require(!assignmentCount.Keys.Intersect(unresolvedIds, StringComparer.Ordinal).Any(), "V6C_ASSIGNED_AND_UNRESOLVED");
        return new OwnerValidation(true, assignmentCount.Count, unresolvedIds.Count, ownerMembers.Count);
    }

    private static object ResponseSchema() => new
    {
        type = "object", additionalProperties = false, required = new[] { "owners", "assignments", "unresolvedOccurrenceIds" }, properties = new
        {
            owners = new { type = "array", items = new { type = "object", additionalProperties = false, required = new[] { "ownerLocalId", "memberOccurrenceIds", "autonomous", "ownerDescription", "evidenceRefs" }, properties = new { ownerLocalId = new { type = "string", minLength = 1 }, memberOccurrenceIds = new { type = "array", items = new { type = "string", minLength = 1 }, minItems = 1 }, autonomous = new { type = "boolean" }, ownerDescription = new { type = "string", minLength = 1 }, evidenceRefs = new { type = "array", items = new { type = "string", minLength = 1 } } } } },
            assignments = new { type = "array", items = new { type = "object", additionalProperties = false, required = new[] { "occurrenceId", "ownerLocalId" }, properties = new { occurrenceId = new { type = "string", minLength = 1 }, ownerLocalId = new { type = "string", minLength = 1 } } } },
            unresolvedOccurrenceIds = new { type = "array", items = new { type = "string", minLength = 1 } },
        },
    };

    private const string SystemPrompt = """
You are the A99 V6C document-global structural-owner induction reasoner. Infer autonomous
organizational ownership from the supplied source-backed document evidence. An owner is a
coherent document unit that may contain source occurrences; it is not a semantic node and it
is not a pair label. Parser-owned scope groups are evidence, not mandatory owner boundaries.
You may merge parser groups or split them when source evidence supports that conclusion.

Return every occurrence assignment exactly once, or list it in unresolvedOccurrenceIds. Use only
the supplied occurrence IDs and evidence references. Keep ownerLocalId local to this document.
The autonomous boolean is descriptive only; it is not a merge veto. Do not return semanticNodeId,
pair labels, SAME, DISTINCT, CONTINUATION, parent, ROOT, level, hierarchy, shouldMerge, shouldSplit,
Gold labels, benchmark labels, or model confidence. Do not infer missing assignments or repair an
incomplete response. Return exactly the requested JSON object.
""";

    private static async Task RunPreflightAsync(string root)
    {
        var v6b = Full(root, V6BRelative);
        using var manifest = Read(Path.Combine(v6b, "manifest.json"));
        using var packets = Read(Path.Combine(v6b, "packets.json"));
        using var groups = Read(Path.Combine(v6b, "scope-groups.json"));
        ValidateV6B(manifest.RootElement, packets.RootElement, groups.RootElement);
        var packetRows = packets.RootElement.EnumerateArray().ToArray();
        var groupRows = groups.RootElement.EnumerateArray().ToArray();
        var requestObjects = packetRows.GroupBy(x => x.GetProperty("documentId").GetString()!, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g => BuildRequest(g.Key, g.ToArray(), groupRows)).ToArray();
        var requestJson = requestObjects.Select(x => JsonSerializer.Serialize(x, JsonOptions)).ToArray();
        var requestHashes = requestJson.Select(Sha256Text).ToArray();
        var output = Full(root, OutputRelative);
        Directory.CreateDirectory(output);
        var sourceFingerprint = new { v6bManifestSha256 = Sha256File(Path.Combine(v6b, "manifest.json")), packetsSha256 = Sha256File(Path.Combine(v6b, "packets.json")), groupsSha256 = Sha256File(Path.Combine(v6b, "scope-groups.json")) };
        await WriteAsync(Path.Combine(output, "request-contract.json"), new { schemaVersion = "a99-v6c-structural-owner-induction-v1", input = new[] { "documentId", "frozenOccurrencePackets", "sourceContainerIdentity", "parserOwnedScopeGroups", "documentOrder", "localContext", "containerAncestry", "clusterProvenance" }, forbidden = new[] { "V4H_Gold", "V5C_evaluation", "V6A_false_merge_cases", "pairLabels", "semanticNodeId", "parent", "ROOT", "level", "shouldMerge", "shouldSplit" }, output = new { owners = new[] { "ownerLocalId", "memberOccurrenceIds", "autonomous", "ownerDescription", "evidenceRefs" }, assignments = new[] { "occurrenceId", "ownerLocalId" }, unresolvedOccurrenceIds = "allowed" }, validation = new[] { "unknown occurrence => INVALID", "missing assignment => INVALID", "multiple owner assignment => INVALID", "unknown member => INVALID", "empty owner => INVALID", "duplicate ownerLocalId conflict => INVALID", "fabricated evidence reference => INVALID", "malformed schema => INVALID" }, note = "autonomous is descriptive only; parser scope groups are evidence, not owner truth." });
        await WriteAsync(Path.Combine(output, "source-fingerprint.json"), sourceFingerprint);
        await WriteAsync(Path.Combine(output, "requests.json"), new { schemaVersion = "a99-v6c-owner-induction-requests-v1", status = "FROZEN_SOURCE_ONLY_OWNER_INDUCTION_REQUESTS", requestCount = requestObjects.Length, sourceFingerprint, goldDerivedInput = false, v5cEvaluationIncluded = false, v6aDiagnosisIncluded = false, semanticNodeRequested = false, hierarchyRequested = false, requests = requestObjects.Select((x, i) => new { request = x, requestHash = requestHashes[i] }).ToArray() });
        await WriteAsync(Path.Combine(output, "manifest.json"), new { schemaVersion = "a99-v6c-preflight-manifest-v1", status = "READY_FOR_PROVIDER_EXECUTION", documentCount = requestObjects.Length, occurrenceCount = packetRows.Length, parserScopeGroupCount = groupRows.Length, requestCount = requestObjects.Length, requestHashes, sourceFingerprint, modelCalls = 0, providerCalls = 0, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, semanticOwnerAssignments = 0, semanticNodeAssignments = 0, hierarchyRequested = false, note = "Document-global owner induction boundary. No provider execution has occurred." });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), "# A99 V6C — structural owner induction preflight\n\n" + $"Frozen document-global requests: **{requestObjects.Length}**. Source occurrence packets: **{packetRows.Length}**. Parser-owned scope groups: **{groupRows.Length}**.\n\nGold, V5C evaluation, V6A diagnosis, semantic-node labels, pair labels, parent/ROOT/level hints are excluded. This is ready for a separately authorized provider execution.\n", new UTF8Encoding(false));
    }

    private static object BuildRequest(string documentId, JsonElement[] packets, JsonElement[] groups)
    {
        var groupKeys = packets.Select(x => x.GetProperty("sourceContainerIdentity").GetString()!).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var visibleGroups = groups.Where(x => groupKeys.Contains(x.GetProperty("sourceContainerIdentity").GetString()!, StringComparer.Ordinal)).Select(x => new { sourceContainerIdentity = x.GetProperty("sourceContainerIdentity").GetString(), sourceUnitKind = x.GetProperty("sourceUnitKind").GetString(), occurrenceCount = x.GetProperty("occurrenceCount").GetInt32(), occurrenceIds = x.GetProperty("occurrenceIds").EnumerateArray().Select(y => y.GetString()!).ToArray() }).ToArray();
        return new { schemaVersion = "a99-v6c-structural-owner-induction-v1", documentId, occurrences = packets.OrderBy(x => x.GetProperty("documentOrder").GetInt32()).ThenBy(x => x.GetProperty("occurrenceId").GetString(), StringComparer.Ordinal).Select(x => new { occurrenceId = x.GetProperty("occurrenceId").GetString(), text = x.GetProperty("text").GetString(), documentOrder = x.GetProperty("documentOrder").GetInt32(), sourceContainerIdentity = x.GetProperty("sourceContainerIdentity").GetString(), sourceUnitKind = x.GetProperty("sourceUnitKind").GetString(), previousSourceOccurrences = x.GetProperty("previousSourceOccurrences"), nextSourceOccurrences = x.GetProperty("nextSourceOccurrences"), clusterIds = x.GetProperty("clusterIds"), candidateIds = x.GetProperty("candidateIds"), evidenceReasons = x.GetProperty("evidenceReasons"), packetClasses = x.GetProperty("packetClasses") }).ToArray(), parserOwnedScopeGroups = visibleGroups, goldDerivedInput = false, v5cEvaluationIncluded = false, v6aDiagnosisIncluded = false, semanticNodeRequested = false, hierarchyRequested = false };
    }

    private static void ValidateV6B(JsonElement manifest, JsonElement packets, JsonElement groups)
    {
        Require(manifest.GetProperty("status").GetString() == "SOURCE_ONLY_OWNER_EVIDENCE_FROZEN", "V6C_V6B_STATUS");
        Require(manifest.GetProperty("goldReadCount").GetInt32() == 0 && manifest.GetProperty("modelCalls").GetInt32() == 0 && manifest.GetProperty("providerCalls").GetInt32() == 0, "V6C_V6B_FIREWALL");
        Require(manifest.GetProperty("semanticOwnerAssignments").GetInt32() == 0 && manifest.GetProperty("semanticNodeAssignments").GetInt32() == 0, "V6C_V6B_SEMANTIC_CONTAMINATION");
        Require(packets.GetArrayLength() == ExpectedOccurrences && groups.GetArrayLength() == 22, "V6C_V6B_COUNTS");
        Require(packets.EnumerateArray().All(x => !x.GetProperty("semanticOwnerAssigned").GetBoolean()), "V6C_V6B_OWNER_ASSIGNMENT");
    }

    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Required(JsonElement element, string name) => element.GetProperty(name).GetString() ?? throw new InvalidDataException("V6C_REQUIRED_" + name.ToUpperInvariant());
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed record FrozenRequest(string DocumentId, string RequestHash, JsonElement Request, int OccurrenceCount);
    private sealed record OwnerValidation(bool Accepted, int AssignedOccurrences, int UnresolvedOccurrences, int OwnerCount);
}
