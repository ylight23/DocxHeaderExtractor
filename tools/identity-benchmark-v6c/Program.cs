using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Eval.ReasoningRetention;
using DocxHeaderExtractor.Infrastructure.AI;

namespace IdentityBenchmarkV6C;

internal static class Program
{
    private const string V6BRelative = "artifacts/identity-benchmark/v6/owner-evidence/source-only-freeze-v1";
    private const string OutputRelative = "artifacts/identity-benchmark/v6/owner-induction/preflight-v1";
    private const string ExecutionRelative = "artifacts/identity-benchmark/v6/owner-induction/execution-v1";
    private const string V2PreflightRelative = "artifacts/identity-benchmark/v6/owner-induction/preflight-v2-addressable";
    private const string V2ExecutionRelative = "artifacts/identity-benchmark/v6/owner-induction/execution-v2-addressable";
    private const string V3PreflightRelative = "artifacts/identity-benchmark/v6/owner-induction/preflight-v3-opaque-handles";
    private const string V3ExecutionRelative = "artifacts/identity-benchmark/v6/owner-induction/execution-v3-opaque-handles";
    private const string V3RevalidationRelative = "artifacts/identity-benchmark/v6/owner-induction/revalidation-v3-target-occurrences-v2";
    private const string V3HandleMapSha256 = "0666173081122a3b031928f9feadb2e059517168281d4ea1b022bc0bdddd9210";
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
            if (args.Any(x => string.Equals(x, "--execute-v2", StringComparison.Ordinal))) return await ExecuteV2Async(root);
            if (args.Any(x => string.Equals(x, "--finalize-v2", StringComparison.Ordinal))) return await FinalizeV2Async(root);
            if (args.Any(x => string.Equals(x, "--diagnose-v2", StringComparison.Ordinal))) return await DiagnoseV2Async(root);
            if (args.Any(x => string.Equals(x, "--execute-v3", StringComparison.Ordinal))) return await ExecuteV3Async(root);
            if (args.Any(x => string.Equals(x, "--finalize-v3", StringComparison.Ordinal))) return await FinalizeV3Async(root);
            if (args.Any(x => string.Equals(x, "--revalidate-v3", StringComparison.Ordinal))) return await RevalidateV3Async(root);
            if (args.Any(x => string.Equals(x, "--diagnose-primary", StringComparison.Ordinal))) return await DiagnosePrimaryAsync(root);
            if (args.Any(x => string.Equals(x, "--prepare-v2", StringComparison.Ordinal))) return await PrepareV2Async(root);
            if (args.Any(x => string.Equals(x, "--prepare-v3", StringComparison.Ordinal))) return await PrepareV3Async(root);
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

    private static async Task<int> ExecuteV2Async(string root)
    {
        var preflight = Full(root, V2PreflightRelative);
        var execution = Full(root, V2ExecutionRelative);
        var frozen = LoadFrozenV2Requests(preflight);
        Require(!Directory.Exists(execution) || !Directory.EnumerateFiles(execution, "*", SearchOption.AllDirectories).Any(), "V6C_V2_EXECUTION_ALREADY_STARTED_NO_RESUME");
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Directory.CreateDirectory(execution);
            await WriteAsync(Path.Combine(execution, "execution-manifest.json"), new { schemaVersion = "a99-v6c-v2-execution-manifest-v1", status = "BLOCKED_ON_PROVIDER_API_KEY", scheduledCalls = ExpectedRequests, modelCalls = 0, providerCalls = 0, retryCount = 0, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, v1Mutation = false });
            return 1;
        }
        var attemptsDir = Path.Combine(execution, "attempts");
        var rawDir = Path.Combine(execution, "raw-responses");
        var parsedDir = Path.Combine(execution, "parsed");
        Directory.CreateDirectory(attemptsDir);
        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(parsedDir);
        await WriteAsync(Path.Combine(execution, "execution-manifest.json"), new
        {
            schemaVersion = "a99-v6c-v2-execution-manifest-v1", status = "EXECUTING", provider = Provider, model = Model, endpoint = Endpoint,
            preflightManifestSha256 = Sha256File(Path.Combine(preflight, "manifest.json")), requestSetSha256 = Sha256File(Path.Combine(preflight, "requests.json")),
            scheduledCalls = ExpectedRequests, transientRequestRetries = 0, maxParallelRequests = 1, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, v1Mutation = false, startedUtc = DateTimeOffset.UtcNow,
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
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(root, "identity-benchmark-v6c-v2", "3-addressable-document-global-structural-owner-induction");
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var completedAttempts = new List<object>();
        for (var i = 0; i < frozen.Count; i++)
        {
            var item = frozen[i];
            var sequence = i + 1;
            var attemptId = $"primary-{sequence:D3}";
            await WriteAsync(Path.Combine(attemptsDir, $"{sequence:D3}.started.json"), new { schemaVersion = "a99-v6c-v2-attempt-start-v1", attemptId, sequence, documentId = item.DocumentId, requestHash = item.RequestHash, requestHashVerified = true, retry = false, goldReadBeforeAttempt = false, v5cReadBeforeAttempt = false, v6aReadBeforeAttempt = false, v1Mutation = false, startedUtc = DateTimeOffset.UtcNow });
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
                var result = await model.CompleteRawStructuredSemanticAsync(item.DocumentId, "V6C_V2_DOCUMENT_GLOBAL_STRUCTURAL_OWNER_INDUCTION", $"v6c-v2:{item.DocumentId}:{item.RequestHash}", requestJson, item.OccurrenceCount, item.OccurrenceCount, item.OccurrenceCount, SystemPrompt, $"TASK=V6C_V2_DOCUMENT_GLOBAL_STRUCTURAL_OWNER_INDUCTION\n{requestJson}\nReturn exactly the requested JSON object.", ResponseSchema(), "a99_v6c_v2_addressable_owner_induction_v1");
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
                        status = "VALID";
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
                schemaVersion = "a99-v6c-v2-attempt-v1", attemptId, sequence, documentId = item.DocumentId, requestHash = item.RequestHash, status, httpStatus, providerCallId,
                rawResponseSha256 = rawHash, parsedResponseSha256 = parsedHash, provider = Provider, model = Model, latencyMs = stopwatch.ElapsedMilliseconds,
                reportedInputTokens = telemetry?.ReportedInputTokens, reportedReasoningTokens = telemetry?.ReportedReasoningTokens, reportedOutputTokens = telemetry?.ReportedOutputTokens,
                finishReason = telemetry?.FinishReason, error, retryCount = 0, goldReadBeforeFreeze = false, v5cReadBeforeFreeze = false, v6aReadBeforeFreeze = false, v1Mutation = false, response = parsed,
            };
            await WriteAsync(Path.Combine(attemptsDir, $"{sequence:D3}.completed.json"), completed);
            completedAttempts.Add(completed);
            await WriteAsync(Path.Combine(execution, "attempt-manifest.json"), new { schemaVersion = "a99-v6c-v2-attempt-manifest-v1", immutableAttemptRecords = true, expectedAttemptCount = ExpectedRequests, actualAttemptCount = completedAttempts.Count, actualModelCalls = model.ProviderCalls, actualProviderCalls = model.ProviderCalls, retryCount = 0, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, v1Mutation = false, attempts = completedAttempts });
            Console.WriteLine($"V6C_V2_ATTEMPT={sequence}/{ExpectedRequests} DOCUMENT={item.DocumentId} STATUS={status} PROVIDER_CALLS={model.ProviderCalls}");
        }
        Require(completedAttempts.Count == ExpectedRequests && model.ProviderCalls == ExpectedRequests, "V6C_V2_PROVIDER_CALL_COUNT");
        await WriteAsync(Path.Combine(execution, "prediction-freeze.json"), new { schemaVersion = "a99-v6c-v2-prediction-freeze-v1", status = "OWNER_PREDICTIONS_FROZEN_BEFORE_EVALUATION", scheduledRequests = ExpectedRequests, completedAttempts = completedAttempts.Count, modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, v1Mutation = false, rawResponsesPersistedBeforeParsing = true, requestMutation = false, retryCount = 0, attempts = completedAttempts });
        await WriteAsync(Path.Combine(execution, "firewall.json"), new { schemaVersion = "a99-v6c-v2-execution-firewall-v1", status = "COMPLETE_OWNER_PREDICTION_FREEZE", scheduledRequests = ExpectedRequests, completedAttempts = completedAttempts.Count, modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, v1Mutation = false, pairLabelsRead = false, hierarchyRead = false, semanticNodeInduction = false, retries = 0, overwrite = false });
        Console.WriteLine($"V6C_V2_STATUS=OWNER_PREDICTIONS_FROZEN REQUESTS={ExpectedRequests} MODEL_CALLS={model.ProviderCalls} PROVIDER_CALLS={model.ProviderCalls} GOLD_READ_COUNT=0 V5C_READ_COUNT=0 V6A_READ_COUNT=0 V1_MUTATION=false");
        return 0;
    }

    private static async Task<int> ExecuteV3Async(string root)
    {
        var preflight = Full(root, V3PreflightRelative);
        var execution = Full(root, V3ExecutionRelative);
        var frozen = LoadFrozenV3Requests(preflight);
        Require(!Directory.Exists(execution) || !Directory.EnumerateFiles(execution, "*", SearchOption.AllDirectories).Any(), "V6C_V3_EXECUTION_ALREADY_STARTED_NO_RESUME");
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Directory.CreateDirectory(execution);
            await WriteAsync(Path.Combine(execution, "execution-manifest.json"), new { schemaVersion = "a99-v6c-v3-execution-manifest-v1", status = "BLOCKED_ON_PROVIDER_API_KEY", scheduledCalls = ExpectedRequests, modelCalls = 0, providerCalls = 0, retryCount = 0, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, v1Mutation = false, v2Mutation = false, handleMapMutation = false });
            return 1;
        }
        var attemptsDir = Path.Combine(execution, "attempts");
        var rawDir = Path.Combine(execution, "raw-responses");
        var parsedDir = Path.Combine(execution, "parsed");
        Directory.CreateDirectory(attemptsDir);
        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(parsedDir);
        await WriteAsync(Path.Combine(execution, "execution-manifest.json"), new
        {
            schemaVersion = "a99-v6c-v3-execution-manifest-v1", status = "EXECUTING", provider = Provider, model = Model, endpoint = Endpoint,
            preflightManifestSha256 = Sha256File(Path.Combine(preflight, "manifest.json")), requestSetSha256 = Sha256File(Path.Combine(preflight, "requests.json")), handleMapSha256 = Sha256File(Path.Combine(preflight, "handle-map.json")),
            scheduledCalls = ExpectedRequests, transientRequestRetries = 0, maxParallelRequests = 1, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, v1Mutation = false, v2Mutation = false, handleMapMutation = false, startedUtc = DateTimeOffset.UtcNow,
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
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(root, "identity-benchmark-v6c-v3", "3-opaque-handle-document-global-structural-owner-induction");
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var completedAttempts = new List<object>();
        for (var i = 0; i < frozen.Count; i++)
        {
            var item = frozen[i];
            var sequence = i + 1;
            var attemptId = $"primary-{sequence:D3}";
            await WriteAsync(Path.Combine(attemptsDir, $"{sequence:D3}.started.json"), new { schemaVersion = "a99-v6c-v3-attempt-start-v1", attemptId, sequence, documentId = item.DocumentId, requestHash = item.RequestHash, handleMapSha256 = V3HandleMapSha256, requestHashVerified = true, retry = false, goldReadBeforeAttempt = false, v5cReadBeforeAttempt = false, v6aReadBeforeAttempt = false, v1Mutation = false, v2Mutation = false, handleMapMutation = false, startedUtc = DateTimeOffset.UtcNow });
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
                var result = await model.CompleteRawStructuredSemanticAsync(item.DocumentId, "V6C_V3_DOCUMENT_GLOBAL_STRUCTURAL_OWNER_INDUCTION", $"v6c-v3:{item.DocumentId}:{item.RequestHash}", requestJson, item.OccurrenceCount, item.OccurrenceCount, item.OccurrenceCount, SystemPrompt, $"TASK=V6C_V3_DOCUMENT_GLOBAL_STRUCTURAL_OWNER_INDUCTION\n{requestJson}\nReturn exactly the requested JSON object.", ResponseSchemaV3(), "a99_v6c_v3_opaque_handle_owner_induction_v1");
                telemetry = result.Telemetry;
                httpStatus = telemetry.HttpStatus;
                providerCallId = telemetry.ProviderCallId;
                rawHash = Sha256Text(result.Content);
                await File.WriteAllTextAsync(Path.Combine(rawDir, $"{sequence:D3}.json"), result.Content, new UTF8Encoding(false));
                try
                {
                    using var response = JsonDocument.Parse(result.Content);
                    var validation = InspectV3Response(item.Request, response.RootElement);
                    var deprojected = validation.Accepted ? DeprojectV3Response(item.Request, response.RootElement) : null;
                    parsed = new { response = response.RootElement.Clone(), validation, deprojected };
                    parsedHash = Sha256Text(JsonSerializer.Serialize(parsed, JsonOptions));
                    await WriteAsync(Path.Combine(parsedDir, $"{sequence:D3}.json"), parsed);
                    status = validation.Accepted ? "VALID" : "INVALID_VALIDATION";
                    error = validation.Accepted ? null : validation.Errors.FirstOrDefault();
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
                schemaVersion = "a99-v6c-v3-attempt-v1", attemptId, sequence, documentId = item.DocumentId, requestHash = item.RequestHash, handleMapSha256 = V3HandleMapSha256, status, httpStatus, providerCallId,
                rawResponseSha256 = rawHash, parsedResponseSha256 = parsedHash, provider = Provider, model = Model, latencyMs = stopwatch.ElapsedMilliseconds,
                reportedInputTokens = telemetry?.ReportedInputTokens, reportedReasoningTokens = telemetry?.ReportedReasoningTokens, reportedOutputTokens = telemetry?.ReportedOutputTokens,
                finishReason = telemetry?.FinishReason, error, retryCount = 0, goldReadBeforeFreeze = false, v5cReadBeforeFreeze = false, v6aReadBeforeFreeze = false, v1Mutation = false, v2Mutation = false, handleMapMutation = false, response = parsed,
            };
            await WriteAsync(Path.Combine(attemptsDir, $"{sequence:D3}.completed.json"), completed);
            completedAttempts.Add(completed);
            await WriteAsync(Path.Combine(execution, "attempt-manifest.json"), new { schemaVersion = "a99-v6c-v3-attempt-manifest-v1", immutableAttemptRecords = true, expectedAttemptCount = ExpectedRequests, actualAttemptCount = completedAttempts.Count, actualModelCalls = model.ProviderCalls, actualProviderCalls = model.ProviderCalls, retryCount = 0, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, v1Mutation = false, v2Mutation = false, handleMapMutation = false, handleMapSha256 = V3HandleMapSha256, attempts = completedAttempts });
            Console.WriteLine($"V6C_V3_ATTEMPT={sequence}/{ExpectedRequests} DOCUMENT={item.DocumentId} STATUS={status} PROVIDER_CALLS={model.ProviderCalls}");
        }
        Require(completedAttempts.Count == ExpectedRequests && model.ProviderCalls == ExpectedRequests, "V6C_V3_PROVIDER_CALL_COUNT");
        await WriteAsync(Path.Combine(execution, "prediction-freeze.json"), new { schemaVersion = "a99-v6c-v3-prediction-freeze-v1", status = "OWNER_PREDICTIONS_FROZEN_BEFORE_EVALUATION", scheduledRequests = ExpectedRequests, completedAttempts = completedAttempts.Count, modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, handleMapSha256 = V3HandleMapSha256, v1Mutation = false, v2Mutation = false, handleMapMutation = false, rawResponsesPersistedBeforeParsing = true, requestMutation = false, promptSemanticChanges = false, retryCount = 0, attempts = completedAttempts });
        await WriteAsync(Path.Combine(execution, "firewall.json"), new { schemaVersion = "a99-v6c-v3-execution-firewall-v1", status = "COMPLETE_OWNER_PREDICTION_FREEZE", scheduledRequests = ExpectedRequests, completedAttempts = completedAttempts.Count, modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, v1Mutation = false, v2Mutation = false, handleMapMutation = false, pairLabelsRead = false, hierarchyRead = false, semanticNodeInduction = false, retries = 0, overwrite = false });
        Console.WriteLine($"V6C_V3_STATUS=OWNER_PREDICTIONS_FROZEN REQUESTS={ExpectedRequests} MODEL_CALLS={model.ProviderCalls} PROVIDER_CALLS={model.ProviderCalls} GOLD_READ_COUNT=0 V5C_READ_COUNT=0 V6A_READ_COUNT=0 V1_MUTATION=false V2_MUTATION=false HANDLE_MAP_MUTATION=false");
        return 0;
    }

    private static async Task<int> FinalizeV3Async(string root)
    {
        var preflight = Full(root, V3PreflightRelative);
        var execution = Full(root, V3ExecutionRelative);
        var frozen = LoadFrozenV3Requests(preflight);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(execution, "attempt-manifest.json")));
        Require(manifest.RootElement.GetProperty("actualAttemptCount").GetInt32() == ExpectedRequests && manifest.RootElement.GetProperty("actualModelCalls").GetInt32() == ExpectedRequests && manifest.RootElement.GetProperty("actualProviderCalls").GetInt32() == ExpectedRequests && manifest.RootElement.GetProperty("retryCount").GetInt32() == 0 && manifest.RootElement.GetProperty("goldReadCount").GetInt32() == 0 && manifest.RootElement.GetProperty("v5cReadCount").GetInt32() == 0 && manifest.RootElement.GetProperty("v6aReadCount").GetInt32() == 0 && !manifest.RootElement.GetProperty("v1Mutation").GetBoolean() && !manifest.RootElement.GetProperty("v2Mutation").GetBoolean() && manifest.RootElement.GetProperty("handleMapMutation").GetBoolean() == false, "V6C_V3_FINALIZE_FIREWALL");
        var attempts = manifest.RootElement.GetProperty("attempts").EnumerateArray().ToArray();
        var valid = attempts.Count(x => x.GetProperty("status").GetString() == "VALID");
        var invalid = attempts.Count(x => x.GetProperty("status").GetString() is "INVALID_VALIDATION" or "INVALID_SCHEMA");
        var providerErrors = attempts.Count(x => x.GetProperty("status").GetString() == "PROVIDER_ERROR");
        var assigned = 0;
        var unresolved = 0;
        var owners = 0;
        var unknownU = 0;
        var duplicateU = 0;
        var missingU = 0;
        var unknownE = 0;
        var unknownOwner = 0;
        var emptyOwners = 0;
        var crossScopeOwners = 0;
        var splitScopeGroups = 0;
        var ownerSizes = new List<int>();
        var diagnostics = new List<object>();
        var byDocument = frozen.ToDictionary(x => x.DocumentId, StringComparer.Ordinal);
        foreach (var attempt in attempts)
        {
            var sequence = attempt.GetProperty("sequence").GetInt32();
            var documentId = attempt.GetProperty("documentId").GetString()!;
            var validation = attempt.TryGetProperty("response", out var envelope) && envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("validation", out var v) ? v : default;
            unknownU += validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("unknownOccurrenceHandleCount", out var uu) ? uu.GetInt32() : 0;
            duplicateU += validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("duplicateAssignmentCount", out var du) ? du.GetInt32() : 0;
            missingU += validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("missingAssignmentCount", out var mu) ? mu.GetInt32() : 0;
            unknownE += validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("unknownEvidenceHandleCount", out var ue) ? ue.GetInt32() : 0;
            unknownOwner += validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("unknownOwnerIdCount", out var uo) ? uo.GetInt32() : 0;
            emptyOwners += validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("emptyOwnerCount", out var eo) ? eo.GetInt32() : 0;
            var errors = validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("errors", out var es) ? es.EnumerateArray().Select(x => x.GetString()).ToArray() : Array.Empty<string?>();
            diagnostics.Add(new { sequence, documentId, status = attempt.GetProperty("status").GetString(), errors, unknownOccurrenceHandles = validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("unknownOccurrenceHandleCount", out var x1) ? x1.GetInt32() : 0, duplicateUAssignments = validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("duplicateAssignmentCount", out var x2) ? x2.GetInt32() : 0, missingUAssignments = validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("missingAssignmentCount", out var x3) ? x3.GetInt32() : 0, unknownEvidenceHandles = validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("unknownEvidenceHandleCount", out var x4) ? x4.GetInt32() : 0, unknownOwnerIds = validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("unknownOwnerIdCount", out var x5) ? x5.GetInt32() : 0, emptyOwners = validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("emptyOwnerCount", out var x6) ? x6.GetInt32() : 0 });
            if (attempt.GetProperty("status").GetString() != "VALID") continue;
            var response = attempt.GetProperty("response").GetProperty("deprojected");
            var assignmentRows = response.GetProperty("assignments").EnumerateArray().ToArray();
            assigned += assignmentRows.Length;
            unresolved += response.GetProperty("unresolvedOccurrenceIds").GetArrayLength();
            var ownerIds = response.GetProperty("owners").EnumerateArray().Select(x => x.GetProperty("owner").GetString()!).ToHashSet(StringComparer.Ordinal);
            owners += ownerIds.Count;
            var memberGroups = assignmentRows.GroupBy(x => x.GetProperty("owner").GetString()!, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Select(y => y.GetProperty("occurrenceId").GetString()!).ToArray(), StringComparer.Ordinal);
            foreach (var members in memberGroups.Values)
            {
                ownerSizes.Add(members.Length);
                var scopeMap = byDocument[documentId].Request.GetProperty("occurrences").EnumerateArray().ToDictionary(x => x.GetProperty("sourceOccurrenceId").GetString()!, x => x.GetProperty("sourceContainerIdentity").GetString()!, StringComparer.Ordinal);
                if (members.Select(id => scopeMap[id]).Distinct(StringComparer.Ordinal).Count() > 1) crossScopeOwners++;
            }
            foreach (var group in byDocument[documentId].Request.GetProperty("parserOwnedScopeGroups").EnumerateArray())
            {
                var refs = group.GetProperty("occurrenceRefs").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
                var canonical = byDocument[documentId].Request.GetProperty("occurrences").EnumerateArray().Where(x => refs.Contains(x.GetProperty("ref").GetString()!, StringComparer.Ordinal)).Select(x => x.GetProperty("sourceOccurrenceId").GetString()!).ToHashSet(StringComparer.Ordinal);
                if (assignmentRows.Where(x => canonical.Contains(x.GetProperty("occurrenceId").GetString()!)).Select(x => x.GetProperty("owner").GetString()!).Distinct(StringComparer.Ordinal).Count() > 1) splitScopeGroups++;
            }
        }
        var summary = new
        {
            schemaVersion = "a99-v6c-v3-prediction-summary-v1", status = "OWNER_PREDICTIONS_FROZEN_BEFORE_EVALUATION", scheduledRequests = ExpectedRequests, completedAttempts = attempts.Length, validOwnerPredictions = valid, invalidValidationPredictions = invalid, providerErrors,
            sourceOccurrenceDenominator = ExpectedOccurrences, assignedOccurrences = assigned, unresolvedOccurrences = unresolved, inducedOwnerCount = owners, ownerSizeDistribution = ownerSizes.OrderBy(x => x).ToArray(),
            unknownOccurrenceHandles = unknownU, duplicateUAssignments = duplicateU, missingUAssignments = missingU, unknownEvidenceHandles = unknownE, unknownOwnerIds = unknownOwner, emptyOwners, crossParserScopeOwners = crossScopeOwners, splitParserScopeGroups = splitScopeGroups,
            modelCalls = manifest.RootElement.GetProperty("actualModelCalls").GetInt32(), providerCalls = manifest.RootElement.GetProperty("actualProviderCalls").GetInt32(), goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, v1Mutation = false, v2Mutation = false, handleMapMutation = false, promptSemanticChanges = false, retryCount = 0, diagnostics,
        };
        await WriteAsync(Path.Combine(execution, "prediction-summary.json"), summary);
        await WriteAsync(Path.Combine(execution, "execution-final.json"), new { schemaVersion = "a99-v6c-v3-execution-final-v1", status = "OWNER_PREDICTION_FREEZE_COMPLETE", scheduledCalls = ExpectedRequests, completedAttempts = attempts.Length, validOwnerPredictions = valid, invalidValidationPredictions = invalid, providerErrors, modelCalls = manifest.RootElement.GetProperty("actualModelCalls").GetInt32(), providerCalls = manifest.RootElement.GetProperty("actualProviderCalls").GetInt32(), goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, v1Mutation = false, v2Mutation = false, handleMapMutation = false, frozenBeforeEvaluation = true, retries = 0, completedUtc = DateTimeOffset.UtcNow });
        Console.WriteLine($"V6C_V3_OFFLINE_SUMMARY_COMPLETE VALID={valid} INVALID={invalid} PROVIDER_ERRORS={providerErrors} MODEL_CALLS={manifest.RootElement.GetProperty("actualModelCalls").GetInt32()} PROVIDER_CALLS={manifest.RootElement.GetProperty("actualProviderCalls").GetInt32()} GOLD_READ_COUNT=0");
        return 0;
    }

    private static async Task<int> RevalidateV3Async(string root)
    {
        var preflight = Full(root, V3PreflightRelative);
        var execution = Full(root, V3ExecutionRelative);
        var output = Full(root, V3RevalidationRelative);
        Require(!Directory.Exists(output) || !Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any(), "V6C_V3_REVALIDATION_ALREADY_EXISTS");
        var frozen = LoadFrozenV3Requests(preflight).ToDictionary(x => x.DocumentId, StringComparer.Ordinal);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(execution, "attempt-manifest.json")));
        var manifestRoot = manifest.RootElement;
        Require(manifestRoot.GetProperty("actualAttemptCount").GetInt32() == ExpectedRequests && manifestRoot.GetProperty("actualModelCalls").GetInt32() == ExpectedRequests && manifestRoot.GetProperty("actualProviderCalls").GetInt32() == ExpectedRequests && manifestRoot.GetProperty("retryCount").GetInt32() == 0 && manifestRoot.GetProperty("goldReadCount").GetInt32() == 0 && manifestRoot.GetProperty("v5cReadCount").GetInt32() == 0 && manifestRoot.GetProperty("v6aReadCount").GetInt32() == 0 && !manifestRoot.GetProperty("v1Mutation").GetBoolean() && !manifestRoot.GetProperty("v2Mutation").GetBoolean() && !manifestRoot.GetProperty("handleMapMutation").GetBoolean(), "V6C_V3_REVALIDATION_FIREWALL");
        var rows = new List<object>();
        var valid = 0;
        var invalid = 0;
        var assigned = 0;
        var unresolved = 0;
        var owners = 0;
        var unknownU = 0;
        var duplicateU = 0;
        var missingU = 0;
        var unknownE = 0;
        var unknownOwner = 0;
        var emptyOwners = 0;
        var crossScopeOwners = 0;
        var splitScopeGroups = 0;
        foreach (var attempt in manifestRoot.GetProperty("attempts").EnumerateArray())
        {
            var sequence = attempt.GetProperty("sequence").GetInt32();
            var documentId = attempt.GetProperty("documentId").GetString()!;
            var rawPath = Path.Combine(execution, "raw-responses", $"{sequence:D3}.json");
            Require(File.Exists(rawPath), $"V6C_V3_REVALIDATION_RAW_MISSING_{sequence:D3}");
            var raw = await File.ReadAllTextAsync(rawPath);
            var rawHash = Sha256Text(raw);
            Require(rawHash == attempt.GetProperty("rawResponseSha256").GetString(), $"V6C_V3_REVALIDATION_RAW_HASH_{sequence:D3}");
            using var response = JsonDocument.Parse(raw);
            var validation = InspectV3Response(frozen[documentId].Request, response.RootElement);
            var deprojected = validation.Accepted ? DeprojectV3Response(frozen[documentId].Request, response.RootElement) : null;
            unknownU += validation.UnknownOccurrenceHandleCount;
            duplicateU += validation.DuplicateAssignmentCount;
            missingU += validation.MissingAssignmentCount;
            unknownE += validation.UnknownEvidenceHandleCount;
            unknownOwner += validation.UnknownOwnerIdCount;
            emptyOwners += validation.EmptyOwnerCount;
            if (validation.Accepted)
            {
                valid++;
                assigned += validation.AssignedOccurrences;
                unresolved += validation.UnresolvedOccurrences;
                owners += validation.OwnerCount;
                var targetOccurrenceScopes = frozen[documentId].Request.GetProperty("occurrences").EnumerateArray().ToDictionary(x => x.GetProperty("ref").GetString()!, x => x.GetProperty("sourceContainerIdentity").GetString()!, StringComparer.Ordinal);
                var assignments = response.RootElement.GetProperty("assignments").EnumerateArray().ToArray();
                foreach (var ownerGroup in assignments.GroupBy(x => x.GetProperty("owner").GetString()!, StringComparer.Ordinal))
                {
                    if (ownerGroup.Select(x => targetOccurrenceScopes[x.GetProperty("ref").GetString()!]).Distinct(StringComparer.Ordinal).Count() > 1) crossScopeOwners++;
                }
                var ownerByRef = assignments.ToDictionary(x => x.GetProperty("ref").GetString()!, x => x.GetProperty("owner").GetString()!, StringComparer.Ordinal);
                foreach (var scopeGroup in frozen[documentId].Request.GetProperty("parserOwnedScopeGroups").EnumerateArray())
                {
                    var scopeOwners = scopeGroup.GetProperty("occurrenceRefs").EnumerateArray().Select(x => x.GetString()!).Where(ownerByRef.ContainsKey).Select(x => ownerByRef[x]).Distinct(StringComparer.Ordinal).Count();
                    if (scopeOwners > 1) splitScopeGroups++;
                }
            }
            else invalid++;
            rows.Add(new { sequence, documentId, primaryStatus = attempt.GetProperty("status").GetString(), correctedStatus = validation.Accepted ? "VALID" : "INVALID_VALIDATION", rawResponseSha256 = rawHash, validation, deprojected });
        }
        Directory.CreateDirectory(output);
        await WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-v6c-v3-offline-revalidation-manifest-v1",
            status = "OFFLINE_REVALIDATION_COMPLETE",
            sourceExecution = V3ExecutionRelative,
            sourcePreflight = V3PreflightRelative,
            sourceAttemptManifestSha256 = Sha256File(Path.Combine(execution, "attempt-manifest.json")),
            sourceRawResponsesUnchanged = true,
            providerCalls = 0,
            modelCalls = 0,
            goldReadCount = 0,
            v5cReadCount = 0,
            v6aReadCount = 0,
            rawRepair = false,
            attemptMutation = false,
            rows = rows.Count,
            valid,
            invalid,
            assignedOccurrences = assigned,
            unresolvedOccurrences = unresolved,
            inducedOwnerCount = owners,
            unknownOccurrenceHandles = unknownU,
            duplicateUAssignments = duplicateU,
            missingUAssignments = missingU,
            unknownEvidenceHandles = unknownE,
            unknownOwnerIds = unknownOwner,
            emptyOwners,
            crossParserScopeOwners = crossScopeOwners,
            splitParserScopeGroups = splitScopeGroups,
        });
        await WriteAsync(Path.Combine(output, "predictions.json"), new
        {
            schemaVersion = "a99-v6c-v3-offline-revalidated-predictions-v1",
            status = "OWNER_PREDICTIONS_REVALIDATED_OFFLINE_BEFORE_EVALUATION",
            rows,
            goldReadCount = 0,
            v5cReadCount = 0,
            v6aReadCount = 0,
            providerCalls = 0,
            rawRepair = false,
        });
        Console.WriteLine($"V6C_V3_OFFLINE_REVALIDATION_COMPLETE VALID={valid} INVALID={invalid} ASSIGNED={assigned} MISSING={missingU} PROVIDER_CALLS=0 GOLD_READ_COUNT=0");
        return 0;
    }

    private static async Task<int> FinalizeV2Async(string root)
    {
        var preflight = Full(root, V2PreflightRelative);
        var execution = Full(root, V2ExecutionRelative);
        var frozen = LoadFrozenV2Requests(preflight);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(execution, "attempt-manifest.json")));
        var attempts = manifest.RootElement.GetProperty("attempts").EnumerateArray().ToArray();
        Require(attempts.Length == ExpectedRequests && manifest.RootElement.GetProperty("retryCount").GetInt32() == 0 && manifest.RootElement.GetProperty("goldReadCount").GetInt32() == 0 && manifest.RootElement.GetProperty("v1Mutation").GetBoolean() == false, "V6C_V2_FINALIZE_FIREWALL");
        var byDocument = frozen.ToDictionary(x => x.DocumentId, StringComparer.Ordinal);
        var valid = attempts.Where(x => x.GetProperty("status").GetString() == "VALID").ToArray();
        var assigned = 0;
        var unresolved = 0;
        var ownerCount = 0;
        var crossScopeOwners = 0;
        var splitScopeGroups = 0;
        var ownerSizes = new List<int>();
        var invalidRefs = 0;
        var invalidEvidenceRefs = 0;
        var duplicateAssignments = 0;
        var missingAssignments = 0;
        var diagnostics = new List<object>();
        foreach (var attempt in attempts)
        {
            var documentId = attempt.GetProperty("documentId").GetString()!;
            var rawPath = Path.Combine(execution, "raw-responses", $"{attempt.GetProperty("sequence").GetInt32():D3}.json");
            if (!File.Exists(rawPath)) continue;
            using var response = JsonDocument.Parse(await File.ReadAllTextAsync(rawPath));
            var d = DiagnoseOwnerResponse(byDocument[documentId].Request, response.RootElement);
            invalidRefs += d.BadMemberCount;
            invalidEvidenceRefs += d.BadEvidenceCount;
            duplicateAssignments += d.DuplicateAssignmentCount;
            missingAssignments += d.MissingCoverageCount;
            diagnostics.Add(new { sequence = attempt.GetProperty("sequence").GetInt32(), documentId, d.BadMemberCount, d.BadEvidenceCount, d.DuplicateAssignmentCount, d.MissingCoverageCount, d.PrimaryFailure });
        }
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
            foreach (var members in ownerMembers.Values) { ownerSizes.Add(members.Length); if (members.Select(id => occurrenceScopes[id]).Distinct(StringComparer.Ordinal).Count() > 1) crossScopeOwners++; }
            foreach (var group in request.GetProperty("parserOwnedScopeGroups").EnumerateArray())
            {
                var ids = group.GetProperty("occurrenceIds").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
                if (response.GetProperty("assignments").EnumerateArray().Where(x => ids.Contains(x.GetProperty("occurrenceId").GetString()!)).Select(x => x.GetProperty("ownerLocalId").GetString()!).Distinct(StringComparer.Ordinal).Count() > 1) splitScopeGroups++;
            }
        }
        var invalid = attempts.Count(x => x.GetProperty("status").GetString() is "INVALID_VALIDATION" or "INVALID_SCHEMA");
        var providerErrors = attempts.Count(x => x.GetProperty("status").GetString() == "PROVIDER_ERROR");
        var modelCalls = manifest.RootElement.GetProperty("actualModelCalls").GetInt32();
        var providerCalls = manifest.RootElement.GetProperty("actualProviderCalls").GetInt32();
        var summary = new
        {
            schemaVersion = "a99-v6c-v2-prediction-summary-v1", status = "OWNER_PREDICTIONS_FROZEN_BEFORE_EVALUATION", scheduledRequests = ExpectedRequests, completedAttempts = attempts.Length,
            validOwnerPredictions = valid.Length, invalidValidationPredictions = invalid, providerErrors, sourceOccurrenceDenominator = ExpectedOccurrences, assignedOccurrences = assigned, unresolvedOccurrences = unresolved,
            inducedOwnerCount = ownerCount, ownerSizeDistribution = ownerSizes.OrderBy(x => x).ToArray(), crossParserScopeOwners = crossScopeOwners, splitParserScopeGroups = splitScopeGroups,
            invalidOccurrenceRefs = invalidRefs, invalidEvidenceRefs, duplicateAssignments, missingAssignments, modelCalls, providerCalls, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0,
            v1Mutation = false, goldEvaluationOpened = false, semanticNodeInduction = false, hierarchyExecuted = false, rawResponsesPersistedBeforeParsing = true, retryCount = 0,
            diagnostics, note = "V6C-v2 addressability-only aggregation. No Gold/V5C/V6A reads, no V6C-v1 mutation, no semantic repair.",
        };
        await WriteAsync(Path.Combine(execution, "prediction-summary.json"), summary);
        await WriteAsync(Path.Combine(execution, "execution-final.json"), new { schemaVersion = "a99-v6c-v2-execution-final-v1", status = "OWNER_PREDICTION_FREEZE_COMPLETE", scheduledCalls = ExpectedRequests, completedAttempts = attempts.Length, validOwnerPredictions = valid.Length, invalidValidationPredictions = invalid, providerErrors, modelCalls, providerCalls, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, v1Mutation = false, frozenBeforeEvaluation = true, recoveryCohort = false, retries = 0, completedUtc = DateTimeOffset.UtcNow });
        Console.WriteLine($"V6C_V2_OFFLINE_SUMMARY_COMPLETE VALID={valid.Length} INVALID={invalid} PROVIDER_ERRORS={providerErrors} MODEL_CALLS={modelCalls} PROVIDER_CALLS={providerCalls} GOLD_READ_COUNT=0");
        return 0;
    }

    private static async Task<int> DiagnoseV2Async(string root)
    {
        var preflight = Full(root, V2PreflightRelative);
        var execution = Full(root, V2ExecutionRelative);
        var frozen = LoadFrozenV2Requests(preflight);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(execution, "attempt-manifest.json")));
        var attempts = manifest.RootElement.GetProperty("attempts").EnumerateArray().ToArray();
        Require(attempts.Length == ExpectedRequests && manifest.RootElement.GetProperty("actualModelCalls").GetInt32() == ExpectedRequests && manifest.RootElement.GetProperty("actualProviderCalls").GetInt32() == ExpectedRequests, "V6C_V2_DIAGNOSIS_ATTEMPT_COUNT");
        Require(manifest.RootElement.GetProperty("retryCount").GetInt32() == 0 && manifest.RootElement.GetProperty("goldReadCount").GetInt32() == 0 && manifest.RootElement.GetProperty("v5cReadCount").GetInt32() == 0 && manifest.RootElement.GetProperty("v6aReadCount").GetInt32() == 0 && !manifest.RootElement.GetProperty("v1Mutation").GetBoolean(), "V6C_V2_DIAGNOSIS_FIREWALL");
        var byDocument = frozen.ToDictionary(x => x.DocumentId, StringComparer.Ordinal);
        var cases = new List<object>();
        foreach (var attempt in attempts)
        {
            var sequence = attempt.GetProperty("sequence").GetInt32();
            var documentId = attempt.GetProperty("documentId").GetString()!;
            var rawPath = Path.Combine(execution, "raw-responses", $"{sequence:D3}.json");
            Require(File.Exists(rawPath), "V6C_V2_DIAGNOSIS_RAW_MISSING");
            using var response = JsonDocument.Parse(await File.ReadAllTextAsync(rawPath));
            var request = byDocument[documentId].Request;
            var allowed = request.GetProperty("allowedOccurrenceIds").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
            var validEvidence = request.GetProperty("allowedEvidenceRefs").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
            var owners = response.RootElement.GetProperty("owners");
            var badMembers = new List<string>();
            var unqualifiedMembers = new List<string>();
            var badEvidence = new List<string>();
            var duplicateMembers = new List<string>();
            var ownerMembers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var owner in owners.EnumerateArray())
            {
                var ownerId = owner.GetProperty("ownerLocalId").GetString()!;
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in owner.GetProperty("memberOccurrenceIds").EnumerateArray())
                {
                    var memberId = member.GetString()!;
                    if (!allowed.Contains(memberId))
                    {
                        badMembers.Add($"{ownerId}:{memberId}");
                        if (allowed.Any(x => x.EndsWith(":" + memberId, StringComparison.Ordinal))) unqualifiedMembers.Add($"{ownerId}:{memberId}");
                    }
                    if (!set.Add(memberId)) duplicateMembers.Add($"{ownerId}:{memberId}");
                }
                foreach (var evidence in owner.GetProperty("evidenceRefs").EnumerateArray())
                {
                    var evidenceId = evidence.GetString()!;
                    if (!validEvidence.Contains(evidenceId)) badEvidence.Add($"{ownerId}:{evidenceId}");
                }
                Require(ownerMembers.TryAdd(ownerId, set), "V6C_V2_DIAGNOSIS_DUPLICATE_OWNER");
            }
            var assignments = response.RootElement.GetProperty("assignments").EnumerateArray().Select(x => new { OccurrenceId = x.GetProperty("occurrenceId").GetString()!, OwnerId = x.GetProperty("ownerLocalId").GetString()! }).ToArray();
            var unresolved = response.RootElement.GetProperty("unresolvedOccurrenceIds").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
            var duplicateAssignments = assignments.GroupBy(x => x.OccurrenceId, StringComparer.Ordinal).Where(x => x.Count() > 1).Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var unknownAssignmentRefs = assignments.Where(x => !allowed.Contains(x.OccurrenceId) || !ownerMembers.ContainsKey(x.OwnerId)).Select(x => $"{x.OccurrenceId}->{x.OwnerId}").ToArray();
            var mismatchedAssignments = assignments.Where(x => ownerMembers.TryGetValue(x.OwnerId, out var members) && !members.Contains(x.OccurrenceId)).Select(x => $"{x.OccurrenceId}->{x.OwnerId}").ToArray();
            var missingOwnerMembers = ownerMembers.SelectMany(x => x.Value.Where(member => !assignments.Any(a => a.OccurrenceId == member && a.OwnerId == x.Key)).Select(member => $"{x.Key}:{member}")).ToArray();
            var missingCoverage = allowed.Except(assignments.Select(x => x.OccurrenceId), StringComparer.Ordinal).Except(unresolved, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var assignedAndUnresolved = assignments.Select(x => x.OccurrenceId).Intersect(unresolved, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var primaryFailure = badMembers.Count > 0 ? "OCCURRENCE_ADDRESSABILITY_DRIFT" : mismatchedAssignments.Length > 0 ? "OWNER_ASSIGNMENT_CONSISTENCY" : unknownAssignmentRefs.Length > 0 ? "ASSIGNMENT_REFERENCE_DRIFT" : duplicateAssignments.Length > 0 ? "DUPLICATE_ASSIGNMENT" : missingOwnerMembers.Length > 0 || missingCoverage.Length > 0 ? "INCOMPLETE_ASSIGNMENT_COVERAGE" : badEvidence.Count > 0 ? "EVIDENCE_ADDRESSABILITY_DRIFT" : "NONE_OBSERVED";
            cases.Add(new
            {
                sequence, documentId, rawResponseSha256 = Sha256File(rawPath), providerStatus = attempt.GetProperty("status").GetString(), ownerCount = owners.GetArrayLength(), assignmentCount = assignments.Length, unresolvedCount = unresolved.Count,
                badMemberCount = badMembers.Count, unqualifiedMemberCount = unqualifiedMembers.Count, badEvidenceCount = badEvidence.Count, duplicateMemberCount = duplicateMembers.Count, duplicateAssignmentCount = duplicateAssignments.Length,
                unknownAssignmentReferenceCount = unknownAssignmentRefs.Length, ownerAssignmentMismatchCount = mismatchedAssignments.Length, missingOwnerMemberCount = missingOwnerMembers.Length, missingCoverageCount = missingCoverage.Length, assignedAndUnresolvedCount = assignedAndUnresolved.Length,
                primaryFailure, examples = new { badMembers = badMembers.Take(8).ToArray(), unqualifiedMembers = unqualifiedMembers.Take(8).ToArray(), badEvidence = badEvidence.Take(8).ToArray(), mismatchedAssignments = mismatchedAssignments.Take(8).ToArray(), unknownAssignmentRefs = unknownAssignmentRefs.Take(8).ToArray(), missingOwnerMembers = missingOwnerMembers.Take(8).ToArray(), missingCoverage = missingCoverage.Take(8).ToArray() },
            });
        }
        var output = Path.Combine(execution, "diagnosis-v2-addressable");
        Directory.CreateDirectory(output);
        await WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-v6c-v2-addressability-diagnosis-v1", status = "OFFLINE_RAW_RESPONSE_FORENSIC_COMPLETE", source = "FROZEN_V6C_V2_RAW_RESPONSES", providerCalls = 0, modelCalls = 0, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, predictionMutation = false, validatorRelaxation = false, cases,
            conclusion = "Addressability-v2 failures are preserved as received; no semantic repair or validation relaxation was applied.",
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), "# V6C-v2 addressability diagnosis\n\nOffline forensic only. The three primary provider executions remain immutable; this artifact reads only frozen V2 requests and raw responses. No Gold, V5C, or V6A artifact was read and no prediction/validator was changed.\n\n" + string.Join("\n", cases.Select(x => $"- {JsonSerializer.Serialize(x, JsonOptions)}")) + "\n", new UTF8Encoding(false));
        Console.WriteLine("V6C_V2_DIAGNOSIS_COMPLETE PROVIDER_CALLS=0 GOLD_READ_COUNT=0 V5C_READ_COUNT=0 V6A_READ_COUNT=0");
        return 0;
    }

    private static IReadOnlyList<FrozenRequest> LoadFrozenV2Requests(string preflight)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(preflight, "manifest.json")));
        var root = manifest.RootElement;
        Require(root.GetProperty("status").GetString() == "READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION" && root.GetProperty("supersedes").GetString() == "preflight-v1", "V6C_V2_PREFLIGHT_STATUS");
        Require(root.GetProperty("requestCount").GetInt32() == ExpectedRequests && root.GetProperty("documentCount").GetInt32() == ExpectedRequests && root.GetProperty("occurrenceCount").GetInt32() == ExpectedOccurrences, "V6C_V2_PREFLIGHT_COUNTS");
        Require(root.GetProperty("providerCalls").GetInt32() == 0 && root.GetProperty("goldReadCount").GetInt32() == 0 && root.GetProperty("v5cReadCount").GetInt32() == 0 && root.GetProperty("v6aReadCount").GetInt32() == 0 && !root.GetProperty("v1PredictionMutation").GetBoolean(), "V6C_V2_PREFLIGHT_FIREWALL");
        using var requests = JsonDocument.Parse(File.ReadAllText(Path.Combine(preflight, "requests.json")));
        var items = requests.RootElement.GetProperty("records").EnumerateArray().Select(x =>
        {
            var request = x.GetProperty("request").Clone();
            var hash = x.GetProperty("requestHash").GetString()!;
            Require(hash == Sha256Text(JsonSerializer.Serialize(request, JsonOptions)), "V6C_V2_REQUEST_HASH_MISMATCH");
            Require(request.GetProperty("allowedOccurrenceIds").GetArrayLength() == request.GetProperty("occurrences").GetArrayLength() && request.GetProperty("allowedEvidenceRefs").GetArrayLength() > 0, "V6C_V2_ADDRESSABILITY_UNIVERSE");
            Require(!request.GetProperty("goldDerivedInput").GetBoolean() && !request.GetProperty("v5cEvaluationIncluded").GetBoolean() && !request.GetProperty("v6aDiagnosisIncluded").GetBoolean() && !request.GetProperty("semanticNodeRequested").GetBoolean() && !request.GetProperty("hierarchyRequested").GetBoolean(), "V6C_V2_REQUEST_FIREWALL");
            return new FrozenRequest(request.GetProperty("documentId").GetString()!, hash, request, request.GetProperty("occurrences").GetArrayLength());
        }).OrderBy(x => x.DocumentId, StringComparer.Ordinal).ToArray();
        Require(items.Length == ExpectedRequests && items.Select(x => x.DocumentId).SequenceEqual(ExpectedDocuments, StringComparer.Ordinal) && items.Sum(x => x.OccurrenceCount) == ExpectedOccurrences, "V6C_V2_REQUEST_SET");
        return items;
    }

    private static IReadOnlyList<FrozenRequest> LoadFrozenV3Requests(string preflight)
    {
        Require(File.Exists(Path.Combine(preflight, "manifest.json")) && File.Exists(Path.Combine(preflight, "requests.json")) && File.Exists(Path.Combine(preflight, "handle-map.json")), "V6C_V3_PREFLIGHT_MISSING");
        Require(Sha256File(Path.Combine(preflight, "handle-map.json")) == V3HandleMapSha256, "V6C_V3_HANDLE_MAP_FINGERPRINT_MISMATCH");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(preflight, "manifest.json")));
        var root = manifest.RootElement;
        Require(root.GetProperty("status").GetString() == "READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION" && root.GetProperty("supersedes").GetString() == "preflight-v2-addressable", "V6C_V3_PREFLIGHT_STATUS");
        Require(root.GetProperty("requestCount").GetInt32() == ExpectedRequests && root.GetProperty("documentCount").GetInt32() == ExpectedRequests && root.GetProperty("occurrenceCount").GetInt32() == ExpectedOccurrences, "V6C_V3_PREFLIGHT_COUNTS");
        Require(root.GetProperty("providerCalls").GetInt32() == 0 && root.GetProperty("goldReadCount").GetInt32() == 0 && root.GetProperty("v5cReadCount").GetInt32() == 0 && root.GetProperty("v6aReadCount").GetInt32() == 0 && root.GetProperty("semanticContractUnchanged").GetBoolean() && !root.GetProperty("duplicatedMembershipRepresentation").GetBoolean(), "V6C_V3_PREFLIGHT_FIREWALL");
        using var requests = JsonDocument.Parse(File.ReadAllText(Path.Combine(preflight, "requests.json")));
        var items = requests.RootElement.GetProperty("records").EnumerateArray().Select(x =>
        {
            var request = x.GetProperty("request").Clone();
            var hash = x.GetProperty("requestHash").GetString()!;
            Require(hash == Sha256Text(JsonSerializer.Serialize(request, JsonOptions)), "V6C_V3_REQUEST_HASH_MISMATCH");
            Require(request.GetProperty("membershipRepresentation").GetString() == "ASSIGNMENTS_ONLY_DERIVE_MEMBERS" && request.GetProperty("addressabilityContract").GetString() == "OUTPUT_ONLY_OPAQUE_OCCURRENCE_AND_EVIDENCE_HANDLES", "V6C_V3_REQUEST_CONTRACT");
            Require(request.GetProperty("allowedOccurrenceRefs").GetArrayLength() > 0 && request.GetProperty("allowedEvidenceRefs").GetArrayLength() > 0, "V6C_V3_HANDLE_UNIVERSE");
            Require(!request.GetProperty("goldDerivedInput").GetBoolean() && !request.GetProperty("v5cEvaluationIncluded").GetBoolean() && !request.GetProperty("v6aDiagnosisIncluded").GetBoolean() && !request.GetProperty("semanticNodeRequested").GetBoolean() && !request.GetProperty("hierarchyRequested").GetBoolean(), "V6C_V3_REQUEST_FIREWALL");
        return new FrozenRequest(request.GetProperty("documentId").GetString()!, hash, request, request.GetProperty("occurrences").GetArrayLength());
        }).OrderBy(x => x.DocumentId, StringComparer.Ordinal).ToArray();
        Require(items.Length == ExpectedRequests, "V6C_V3_REQUEST_COUNT");
        Require(items.Select(x => x.DocumentId).SequenceEqual(ExpectedDocuments, StringComparer.Ordinal), "V6C_V3_DOCUMENT_ORDER");
        Require(items.Sum(x => x.OccurrenceCount) == ExpectedOccurrences, "V6C_V3_OCCURRENCE_COUNT");
        return items;
    }

    private static V3Validation InspectV3Response(JsonElement request, JsonElement response)
    {
        var errors = new List<string>();
        if (response.ValueKind != JsonValueKind.Object) return new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, ["V6C_V3_RESPONSE_OBJECT_REQUIRED"]);
        if (!response.TryGetProperty("owners", out var owners) || owners.ValueKind != JsonValueKind.Array || !response.TryGetProperty("assignments", out var assignments) || assignments.ValueKind != JsonValueKind.Array || !response.TryGetProperty("unresolvedRefs", out var unresolved) || unresolved.ValueKind != JsonValueKind.Array)
            return new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, ["V6C_V3_REQUIRED_FIELDS"]);
        var allowedU = request.GetProperty("occurrences").EnumerateArray().Select(x => x.GetProperty("ref").GetString()!).ToHashSet(StringComparer.Ordinal);
        var allowedE = request.GetProperty("allowedEvidenceRefs").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
        var ownerIds = new HashSet<string>(StringComparer.Ordinal);
        var unknownEvidence = 0;
        var ownerCount = 0;
        foreach (var owner in owners.EnumerateArray())
        {
            ownerCount++;
            if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("owner", out var ownerValue) || ownerValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(ownerValue.GetString())) { errors.Add("V6C_V3_EMPTY_OWNER"); continue; }
            var ownerId = ownerValue.GetString()!;
            if (!System.Text.RegularExpressions.Regex.IsMatch(ownerId, "^O[0-9]{2,3}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) errors.Add("V6C_V3_OWNER_ID_FORMAT");
            if (!ownerIds.Add(ownerId)) errors.Add("V6C_V3_DUPLICATE_OWNER_ID");
            if (!owner.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(description.GetString())) errors.Add("V6C_V3_OWNER_DESCRIPTION");
            if (!owner.TryGetProperty("autonomous", out var autonomous) || autonomous.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) errors.Add("V6C_V3_OWNER_AUTONOMOUS");
            if (!owner.TryGetProperty("evidenceRefs", out var refs) || refs.ValueKind != JsonValueKind.Array) { errors.Add("V6C_V3_OWNER_EVIDENCE"); continue; }
            foreach (var reference in refs.EnumerateArray()) if (reference.ValueKind != JsonValueKind.String || !allowedE.Contains(reference.GetString()!)) unknownEvidence++;
            if (owner.TryGetProperty("memberOccurrenceIds", out _)) errors.Add("V6C_V3_DUPLICATED_MEMBERSHIP_FIELD");
        }
        var assignedRows = new List<(string Ref, string Owner)>();
        var unknownU = 0;
        var unknownOwner = 0;
        foreach (var assignment in assignments.EnumerateArray())
        {
            if (assignment.ValueKind != JsonValueKind.Object || !assignment.TryGetProperty("ref", out var reference) || reference.ValueKind != JsonValueKind.String || !assignment.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.String) { errors.Add("V6C_V3_ASSIGNMENT_SHAPE"); continue; }
            var referenceValue = reference.GetString()!;
            var ownerValue = owner.GetString()!;
            if (!allowedU.Contains(referenceValue)) unknownU++;
            if (!ownerIds.Contains(ownerValue)) unknownOwner++;
            assignedRows.Add((referenceValue, ownerValue));
        }
        var unresolvedRefs = new List<string>();
        foreach (var reference in unresolved.EnumerateArray())
        {
            if (reference.ValueKind != JsonValueKind.String) { errors.Add("V6C_V3_UNRESOLVED_SHAPE"); continue; }
            var referenceValue = reference.GetString()!;
            if (!allowedU.Contains(referenceValue)) unknownU++;
            unresolvedRefs.Add(referenceValue);
        }
        var duplicateAssignments = assignedRows.GroupBy(x => x.Ref, StringComparer.Ordinal).Count(x => x.Count() > 1);
        var assigned = assignedRows.Select(x => x.Ref).ToHashSet(StringComparer.Ordinal);
        var unresolvedSet = unresolvedRefs.ToHashSet(StringComparer.Ordinal);
        var missing = allowedU.Except(assigned, StringComparer.Ordinal).Except(unresolvedSet, StringComparer.Ordinal).Count();
        var overlap = assigned.Intersect(unresolvedSet, StringComparer.Ordinal).Count();
        var assignedPerOwner = assignedRows.GroupBy(x => x.Owner, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var emptyOwners = ownerIds.Count(ownerId => !assignedPerOwner.ContainsKey(ownerId));
        if (unknownU > 0) errors.Add("V6C_V3_UNKNOWN_OCCURRENCE_HANDLE");
        if (duplicateAssignments > 0) errors.Add("V6C_V3_DUPLICATE_U_ASSIGNMENT");
        if (missing > 0) errors.Add("V6C_V3_MISSING_U_ASSIGNMENT");
        if (overlap > 0) errors.Add("V6C_V3_ASSIGNED_AND_UNRESOLVED");
        if (unknownEvidence > 0) errors.Add("V6C_V3_UNKNOWN_EVIDENCE_HANDLE");
        if (unknownOwner > 0) errors.Add("V6C_V3_UNKNOWN_OWNER_ID");
        if (emptyOwners > 0) errors.Add("V6C_V3_EMPTY_OWNER");
        return new(errors.Count == 0, assignedRows.Count, unresolvedSet.Count, ownerCount, unknownU, duplicateAssignments, missing, unknownEvidence, unknownOwner, emptyOwners, errors.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static object DeprojectV3Response(JsonElement request, JsonElement response)
    {
        var occurrenceMap = request.GetProperty("occurrences").EnumerateArray().ToDictionary(x => x.GetProperty("ref").GetString()!, x => x.GetProperty("sourceOccurrenceId").GetString()!, StringComparer.Ordinal);
        var evidenceMap = request.GetProperty("evidenceCatalog").EnumerateArray().ToDictionary(x => x.GetProperty("ref").GetString()!, x => x.GetProperty("sourceEvidenceId").GetString()!, StringComparer.Ordinal);
        var owners = response.GetProperty("owners").EnumerateArray().Select(owner => new
        {
            owner = owner.GetProperty("owner").GetString(), description = owner.GetProperty("description").GetString(), autonomous = owner.GetProperty("autonomous").GetBoolean(),
            evidenceRefs = owner.GetProperty("evidenceRefs").EnumerateArray().Select(x => evidenceMap[x.GetString()!]).ToArray(),
        }).ToArray();
        var assignments = response.GetProperty("assignments").EnumerateArray().Select(row => new { occurrenceId = occurrenceMap[row.GetProperty("ref").GetString()!], owner = row.GetProperty("owner").GetString() }).ToArray();
        var unresolved = response.GetProperty("unresolvedRefs").EnumerateArray().Select(x => occurrenceMap[x.GetString()!]).ToArray();
        return new { owners, assignments, unresolvedOccurrenceIds = unresolved };
    }

    private static OwnerDiagnostics DiagnoseOwnerResponse(JsonElement request, JsonElement response)
    {
        var allowed = request.TryGetProperty("allowedOccurrenceIds", out var ids) ? ids.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal) : request.GetProperty("occurrences").EnumerateArray().Select(x => x.GetProperty("occurrenceId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var evidence = request.TryGetProperty("allowedEvidenceRefs", out var refs) ? refs.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(allowed, StringComparer.Ordinal);
        var badMembers = 0;
        var badEvidence = 0;
        foreach (var owner in response.GetProperty("owners").EnumerateArray())
        {
            foreach (var member in owner.GetProperty("memberOccurrenceIds").EnumerateArray()) if (!allowed.Contains(member.GetString()!)) badMembers++;
            foreach (var reference in owner.GetProperty("evidenceRefs").EnumerateArray()) if (!evidence.Contains(reference.GetString()!)) badEvidence++;
        }
        var assignments = response.GetProperty("assignments").EnumerateArray().Select(x => x.GetProperty("occurrenceId").GetString()!).ToArray();
        var unresolved = response.GetProperty("unresolvedOccurrenceIds").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
        var duplicate = assignments.GroupBy(x => x, StringComparer.Ordinal).Count(x => x.Count() > 1);
        var missing = allowed.Except(assignments, StringComparer.Ordinal).Except(unresolved, StringComparer.Ordinal).Count();
        var failure = badMembers > 0 ? "INVALID_OCCURRENCE_REF" : badEvidence > 0 ? "INVALID_EVIDENCE_REF" : duplicate > 0 ? "DUPLICATE_ASSIGNMENT" : missing > 0 ? "MISSING_ASSIGNMENT" : "NONE_OBSERVED";
        return new OwnerDiagnostics(badMembers, badEvidence, duplicate, missing, failure);
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

    private static async Task<int> DiagnosePrimaryAsync(string root)
    {
        var preflight = Full(root, OutputRelative);
        var execution = Full(root, ExecutionRelative);
        var frozen = LoadFrozenRequests(preflight);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(execution, "attempt-manifest.json")));
        var attempts = manifest.RootElement.GetProperty("attempts").EnumerateArray().ToArray();
        Require(attempts.Length == ExpectedRequests, "V6C_DIAGNOSIS_ATTEMPT_COUNT");
        var byDocument = frozen.ToDictionary(x => x.DocumentId, StringComparer.Ordinal);
        var cases = new List<object>();
        foreach (var attempt in attempts)
        {
            var sequence = attempt.GetProperty("sequence").GetInt32();
            var documentId = attempt.GetProperty("documentId").GetString()!;
            var rawPath = Path.Combine(execution, "raw-responses", $"{sequence:D3}.json");
            Require(File.Exists(rawPath), "V6C_DIAGNOSIS_RAW_MISSING");
            using var response = JsonDocument.Parse(await File.ReadAllTextAsync(rawPath));
            var request = byDocument[documentId].Request;
            var allowed = new HashSet<string>(request.GetProperty("occurrences").EnumerateArray().Select(x => x.GetProperty("occurrenceId").GetString()!), StringComparer.Ordinal);
            var validEvidence = new HashSet<string>(allowed, StringComparer.Ordinal);
            validEvidence.UnionWith(request.GetProperty("occurrences").EnumerateArray().Select(x => x.GetProperty("sourceContainerIdentity").GetString()!));
            validEvidence.UnionWith(request.GetProperty("parserOwnedScopeGroups").EnumerateArray().Select(x => x.GetProperty("sourceContainerIdentity").GetString()!));
            var badMembers = new List<string>();
            var badEvidence = new List<string>();
            var duplicateMembers = new List<string>();
            var owners = response.RootElement.GetProperty("owners");
            foreach (var owner in owners.EnumerateArray())
            {
                var ownerId = owner.GetProperty("ownerLocalId").GetString()!;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in owner.GetProperty("memberOccurrenceIds").EnumerateArray())
                {
                    var memberId = member.GetString()!;
                    if (!allowed.Contains(memberId)) badMembers.Add($"{ownerId}:{memberId}");
                    if (!seen.Add(memberId)) duplicateMembers.Add($"{ownerId}:{memberId}");
                }
                foreach (var evidence in owner.GetProperty("evidenceRefs").EnumerateArray())
                {
                    var evidenceId = evidence.GetString()!;
                    if (!validEvidence.Contains(evidenceId)) badEvidence.Add($"{ownerId}:{evidenceId}");
                }
            }
            var assigned = response.RootElement.GetProperty("assignments").EnumerateArray().Select(x => x.GetProperty("occurrenceId").GetString()!).ToArray();
            var unresolved = response.RootElement.GetProperty("unresolvedOccurrenceIds").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
            var duplicateAssignments = assigned.GroupBy(x => x, StringComparer.Ordinal).Where(x => x.Count() > 1).Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var missingCoverage = allowed.Except(assigned, StringComparer.Ordinal).Except(unresolved, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var primaryFailure = badMembers.Count > 0 || duplicateMembers.Count > 0 ? "UNKNOWN_OR_DUPLICATE_MEMBER" : badEvidence.Count > 0 ? "FABRICATED_EVIDENCE_REFERENCE" : duplicateAssignments.Length > 0 ? "MULTIPLE_ASSIGNMENT" : missingCoverage.Length > 0 ? "INCOMPLETE_ASSIGNMENT_COVERAGE" : "NONE_OBSERVED";
            cases.Add(new
            {
                sequence,
                documentId,
                rawResponseSha256 = Sha256File(rawPath),
                providerStatus = attempt.GetProperty("status").GetString(),
                ownerCount = owners.GetArrayLength(),
                assignmentCount = assigned.Length,
                unresolvedCount = unresolved.Count,
                badMemberCount = badMembers.Count,
                badEvidenceCount = badEvidence.Count,
                duplicateMemberCount = duplicateMembers.Count,
                duplicateAssignmentCount = duplicateAssignments.Length,
                missingCoverageCount = missingCoverage.Length,
                primaryFailure,
                examples = new { badMembers = badMembers.Take(5).ToArray(), badEvidence = badEvidence.Take(5).ToArray(), duplicateMembers = duplicateMembers.Take(5).ToArray(), missingCoverage = missingCoverage.Take(5).ToArray() },
            });
        }
        var output = Path.Combine(execution, "diagnosis-v1");
        Directory.CreateDirectory(output);
        await WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-v6c-primary-failure-diagnosis-v1",
            status = "OFFLINE_RAW_RESPONSE_FORENSIC_COMPLETE",
            source = "FROZEN_V6C_PRIMARY_RAW_RESPONSES",
            providerCalls = 0,
            modelCalls = 0,
            goldReadCount = 0,
            v5cReadCount = 0,
            v6aReadCount = 0,
            predictionMutation = false,
            validatorRelaxation = false,
            cases,
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), "# V6C primary failure diagnosis\n\nOffline raw-response forensic only. No provider, Gold, V5C, or V6A reads; no prediction or validator mutation.\n\n" + string.Join("\n", cases.Select(x => $"- {JsonSerializer.Serialize(x, JsonOptions)}")) + "\n", new UTF8Encoding(false));
        Console.WriteLine("V6C_DIAGNOSIS_COMPLETE PROVIDER_CALLS=0 GOLD_READ_COUNT=0 V5C_READ_COUNT=0 V6A_READ_COUNT=0");
        return 0;
    }

    private static async Task<int> PrepareV2Async(string root)
    {
        var v1 = Full(root, OutputRelative);
        var output = Full(root, "artifacts/identity-benchmark/v6/owner-induction/preflight-v2-addressable");
        Require(File.Exists(Path.Combine(v1, "manifest.json")) && File.Exists(Path.Combine(v1, "requests.json")), "V6C_V2_V1_PREFLIGHT_MISSING");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(v1, "manifest.json")));
        Require(manifest.RootElement.GetProperty("status").GetString() == "READY_FOR_PROVIDER_EXECUTION", "V6C_V2_V1_STATUS");
        using var requests = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(v1, "requests.json")));
        var records = new List<object>();
        foreach (var record in requests.RootElement.GetProperty("requests").EnumerateArray())
        {
            var source = JsonNode.Parse(record.GetProperty("request").GetRawText())!.AsObject();
            var occurrenceIds = source["occurrences"]!.AsArray().Select(x => x!["occurrenceId"]!.GetValue<string>()).ToArray();
            var evidenceRefs = new HashSet<string>(occurrenceIds, StringComparer.Ordinal);
            foreach (var occurrence in source["occurrences"]!.AsArray())
            {
                foreach (var key in new[] { "clusterIds", "candidateIds", "evidenceReasons" })
                    foreach (var value in occurrence![key]!.AsArray()) evidenceRefs.Add(value!.GetValue<string>());
                evidenceRefs.Add(occurrence["sourceContainerIdentity"]!.GetValue<string>());
            }
            foreach (var group in source["parserOwnedScopeGroups"]!.AsArray()) evidenceRefs.Add(group!["sourceContainerIdentity"]!.GetValue<string>());
            source["allowedOccurrenceIds"] = new JsonArray(occurrenceIds.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            source["allowedEvidenceRefs"] = new JsonArray(evidenceRefs.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            source["addressabilityContract"] = "COPY_EXACT_ID_FROM_ALLOWED_UNIVERSE_NO_QUALIFICATION";
            var serialized = source.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var hash = Sha256Text(serialized);
            records.Add(new { request = JsonDocument.Parse(serialized).RootElement.Clone(), requestHash = hash, documentId = source["documentId"]!.GetValue<string>(), occurrenceCount = occurrenceIds.Length, allowedEvidenceRefCount = evidenceRefs.Count });
        }
        Directory.CreateDirectory(output);
        await WriteAsync(Path.Combine(output, "request-contract.json"), new
        {
            schemaVersion = "a99-v6c-owner-induction-v2-addressable",
            supersedes = "a99-v6c-structural-owner-induction-v1",
            changes = new[] { "explicit allowedOccurrenceIds", "explicit allowedEvidenceRefs", "exact copy/no qualification instruction" },
            providerCalls = 0,
            goldReadCount = 0,
            v1PredictionMutation = false,
            note = "Offline challenger preflight only. This is a new request boundary and needs separate provider authorization; V6C-v1 remains frozen and authoritative for its three calls."
        });
        await WriteAsync(Path.Combine(output, "requests.json"), new { schemaVersion = "a99-v6c-owner-induction-requests-v2-addressable", status = "FROZEN_SOURCE_ONLY_OWNER_INDUCTION_REQUESTS_V2", requestCount = records.Count, sourcePreflight = "../preflight-v1", goldDerivedInput = false, v5cEvaluationIncluded = false, v6aDiagnosisIncluded = false, semanticNodeRequested = false, hierarchyRequested = false, records });
        await WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-v6c-preflight-manifest-v2-addressable",
            status = "READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION",
            supersedes = "preflight-v1",
            documentCount = records.Count,
            occurrenceCount = records.Sum(x => x.GetType().GetProperty("occurrenceCount")!.GetValue(x) as int? ?? 0),
            requestCount = records.Count,
            requestHashes = records.Select(x => x.GetType().GetProperty("requestHash")!.GetValue(x)!.ToString()).ToArray(),
            modelCalls = 0,
            providerCalls = 0,
            goldReadCount = 0,
            v5cReadCount = 0,
            v6aReadCount = 0,
            v1PredictionMutation = false,
            note = "Addressability challenger prepared offline after V6C-v1 failure diagnosis. No provider execution authorized or performed."
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), "# A99 V6C-v2 — addressable owner induction preflight\n\nV1 is preserved. This challenger makes the exact allowed occurrence/evidence universes explicit to reduce identifier qualification and evidence-reference drift. It has **0 provider calls** and requires separate authorization.\n", new UTF8Encoding(false));
        Console.WriteLine($"V6C_V2_PREFLIGHT_COMPLETE REQUESTS={records.Count} PROVIDER_CALLS=0 GOLD_READ_COUNT=0 V1_MUTATION=false");
        return 0;
    }

    private static async Task<int> PrepareV3Async(string root)
    {
        var v2 = Full(root, V2PreflightRelative);
        var output = Full(root, "artifacts/identity-benchmark/v6/owner-induction/preflight-v3-opaque-handles");
        using var v2Manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(v2, "manifest.json")));
        Require(v2Manifest.RootElement.GetProperty("status").GetString() == "READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION", "V6C_V3_V2_STATUS");
        Require(v2Manifest.RootElement.GetProperty("providerCalls").GetInt32() == 0 && v2Manifest.RootElement.GetProperty("goldReadCount").GetInt32() == 0 && v2Manifest.RootElement.GetProperty("v5cReadCount").GetInt32() == 0 && v2Manifest.RootElement.GetProperty("v6aReadCount").GetInt32() == 0, "V6C_V3_V2_FIREWALL");
        using var v2Requests = JsonDocument.Parse(File.ReadAllText(Path.Combine(v2, "requests.json")));
        var records = new List<object>();
        var handleMaps = new List<object>();
        foreach (var record in v2Requests.RootElement.GetProperty("records").EnumerateArray().OrderBy(x => x.GetProperty("documentId").GetString(), StringComparer.Ordinal))
        {
            var source = JsonNode.Parse(record.GetProperty("request").GetRawText())!.AsObject();
            var documentId = source["documentId"]!.GetValue<string>();
            var occurrences = source["occurrences"]!.AsArray().OrderBy(x => x!["documentOrder"]!.GetValue<int>()).ThenBy(x => x!["occurrenceId"]!.GetValue<string>(), StringComparer.Ordinal).ToArray();
            var occurrenceIds = occurrences.Select(x => x!["occurrenceId"]!.GetValue<string>()).ToArray();
            var orderByOccurrence = occurrences.ToDictionary(x => x!["occurrenceId"]!.GetValue<string>(), x => x!["documentOrder"]!.GetValue<int>(), StringComparer.Ordinal);
            var allOccurrenceIds = occurrenceIds.ToHashSet(StringComparer.Ordinal);
            foreach (var occurrence in occurrences)
            {
                foreach (var context in occurrence!["previousSourceOccurrences"]!.AsArray().Concat(occurrence["nextSourceOccurrences"]!.AsArray())) allOccurrenceIds.Add(context!["occurrenceId"]!.GetValue<string>());
            }
            foreach (var group in source["parserOwnedScopeGroups"]!.AsArray()) foreach (var id in StringArray(group!["occurrenceIds"]!)) allOccurrenceIds.Add(id);
            var occurrenceMap = allOccurrenceIds.OrderBy(id => orderByOccurrence.TryGetValue(id, out var order) ? order : int.MaxValue).ThenBy(id => id, StringComparer.Ordinal).Select((id, index) => new { id, handle = $"U{index + 1:D3}" }).ToDictionary(x => x.id, x => x.handle, StringComparer.Ordinal);
            var evidenceKinds = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            void AddEvidence(string value, string kind)
            {
                if (!evidenceKinds.TryGetValue(value, out var kinds)) evidenceKinds[value] = kinds = new SortedSet<string>(StringComparer.Ordinal);
                kinds.Add(kind);
            }
            foreach (var occurrence in occurrences)
            {
                AddEvidence(occurrence!["sourceContainerIdentity"]!.GetValue<string>(), "sourceContainerIdentity");
                foreach (var value in StringArray(occurrence["clusterIds"]!)) AddEvidence(value, "clusterId");
                foreach (var value in StringArray(occurrence["candidateIds"]!)) AddEvidence(value, "candidateId");
                foreach (var value in StringArray(occurrence["evidenceReasons"]!)) AddEvidence(value, "evidenceReason");
            }
            foreach (var group in source["parserOwnedScopeGroups"]!.AsArray()) AddEvidence(group!["sourceContainerIdentity"]!.GetValue<string>(), "parserScopeGroup");
            var evidenceMap = evidenceKinds.Keys.OrderBy(x => x, StringComparer.Ordinal).Select((id, index) => new { id, handle = $"E{index + 1:D3}", kinds = evidenceKinds[id].ToArray() }).ToArray();
            var evidenceLookup = evidenceMap.ToDictionary(x => x.id, x => x.handle, StringComparer.Ordinal);
            var projectedOccurrences = new JsonArray();
            foreach (var occurrence in occurrences)
            {
                var item = occurrence!;
                var canonicalId = item["occurrenceId"]!.GetValue<string>();
                var projected = new JsonObject
                {
                    ["ref"] = occurrenceMap[canonicalId],
                    ["sourceOccurrenceId"] = canonicalId,
                    ["text"] = item["text"]!.GetValue<string>(),
                    ["documentOrder"] = item["documentOrder"]!.GetValue<int>(),
                    ["sourceContainerRef"] = evidenceLookup[item["sourceContainerIdentity"]!.GetValue<string>()],
                    ["sourceContainerIdentity"] = item["sourceContainerIdentity"]!.GetValue<string>(),
                    ["sourceUnitKind"] = item["sourceUnitKind"]!.GetValue<string>(),
                    ["previousSourceOccurrences"] = ProjectOccurrenceContexts(item["previousSourceOccurrences"]!, occurrenceMap),
                    ["nextSourceOccurrences"] = ProjectOccurrenceContexts(item["nextSourceOccurrences"]!, occurrenceMap),
                    ["clusterRefs"] = ProjectEvidenceRefs(item["clusterIds"]!, evidenceLookup),
                    ["candidateRefs"] = ProjectEvidenceRefs(item["candidateIds"]!, evidenceLookup),
                    ["evidenceReasonRefs"] = ProjectEvidenceRefs(item["evidenceReasons"]!, evidenceLookup),
                    ["packetClasses"] = item["packetClasses"]!.DeepClone(),
                };
                projectedOccurrences.Add(projected);
            }
            var projectedGroups = new JsonArray();
            foreach (var group in source["parserOwnedScopeGroups"]!.AsArray())
            {
                var item = group!;
                var ids = StringArray(item["occurrenceIds"]!);
                projectedGroups.Add(new JsonObject
                {
                    ["sourceContainerRef"] = evidenceLookup[item["sourceContainerIdentity"]!.GetValue<string>()],
                    ["sourceContainerIdentity"] = item["sourceContainerIdentity"]!.GetValue<string>(),
                    ["sourceUnitKind"] = item["sourceUnitKind"]!.GetValue<string>(),
                    ["occurrenceCount"] = item["occurrenceCount"]!.GetValue<int>(),
                    ["occurrenceRefs"] = new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(occurrenceMap[id])!).ToArray()),
                });
            }
            var request = new JsonObject
            {
                ["schemaVersion"] = "a99-v6c-structural-owner-induction-v3-opaque-handles",
                ["documentId"] = documentId,
                ["occurrences"] = projectedOccurrences,
                ["parserOwnedScopeGroups"] = projectedGroups,
                ["evidenceCatalog"] = new JsonArray(evidenceMap.Select(x => (JsonNode)new JsonObject { ["ref"] = x.handle, ["sourceEvidenceId"] = x.id, ["evidenceKinds"] = new JsonArray(x.kinds.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()) }).ToArray()),
                ["allowedOccurrenceRefs"] = new JsonArray(occurrenceMap.Values.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["allowedEvidenceRefs"] = new JsonArray(evidenceMap.Select(x => (JsonNode)JsonValue.Create(x.handle)!).ToArray()),
                ["goldDerivedInput"] = false,
                ["v5cEvaluationIncluded"] = false,
                ["v6aDiagnosisIncluded"] = false,
                ["semanticNodeRequested"] = false,
                ["hierarchyRequested"] = false,
                ["addressabilityContract"] = "OUTPUT_ONLY_OPAQUE_OCCURRENCE_AND_EVIDENCE_HANDLES",
                ["membershipRepresentation"] = "ASSIGNMENTS_ONLY_DERIVE_MEMBERS",
            };
            var serialized = request.ToJsonString(JsonOptions);
            records.Add(new { documentId, request = JsonDocument.Parse(serialized).RootElement.Clone(), requestHash = Sha256Text(serialized), occurrenceCount = occurrenceIds.Length, occurrenceHandleCount = occurrenceMap.Count, evidenceHandleCount = evidenceMap.Length });
            handleMaps.Add(new { documentId, occurrenceHandles = occurrenceMap.OrderBy(x => x.Value, StringComparer.Ordinal).Select(x => new { @ref = x.Value, sourceOccurrenceId = x.Key }).ToArray(), evidenceHandles = evidenceMap.Select(x => new { @ref = x.handle, sourceEvidenceId = x.id, evidenceKinds = x.kinds }).ToArray() });
        }
        var requestHashes = records.Select(x => (string)x.GetType().GetProperty("requestHash")!.GetValue(x)!).ToArray();
        var requestSetSha = Sha256Text(string.Join("\n", requestHashes));
        var sourceV2Fingerprint = new { v2ManifestSha256 = Sha256File(Path.Combine(v2, "manifest.json")), v2RequestsSha256 = Sha256File(Path.Combine(v2, "requests.json")) };
        Directory.CreateDirectory(output);
        await WriteAsync(Path.Combine(output, "request-contract.json"), new
        {
            schemaVersion = "a99-v6c-owner-induction-v3-opaque-handles",
            supersedes = "a99-v6c-owner-induction-v2-addressable",
            changes = new[] { "opaque short occurrence handles", "opaque short evidence handles", "single assignments-only membership authority", "deterministic deprojection map" },
            semanticContractUnchanged = true, providerCalls = 0, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, v1Mutation = false, v2Mutation = false,
            forbiddenSemanticChanges = new[] { "owner definition", "autonomy definition", "source context", "structural evidence", "document-global reasoning", "Gold/V5C/V6A-derived rules" },
            output = new { owners = new[] { "owner", "description", "autonomous", "evidenceRefs" }, assignments = new[] { "ref", "owner" }, unresolvedRefs = "allowed", forbiddenOutputFields = new[] { "memberOccurrenceIds", "canonical occurrence IDs", "canonical evidence IDs" } },
        });
        await WriteAsync(Path.Combine(output, "source-fingerprint.json"), sourceV2Fingerprint);
        await WriteAsync(Path.Combine(output, "handle-map.json"), new { schemaVersion = "a99-v6c-v3-handle-map-v1", status = "FROZEN_DETERMINISTIC_DEPROJECTION_AUTHORITY", maps = handleMaps, requestSetSha256 = requestSetSha });
        await WriteAsync(Path.Combine(output, "requests.json"), new { schemaVersion = "a99-v6c-owner-induction-requests-v3-opaque-handles", status = "FROZEN_SOURCE_ONLY_OWNER_INDUCTION_REQUESTS_V3", supersedes = "preflight-v2-addressable", requestCount = records.Count, occurrenceCount = records.Sum(x => (int)x.GetType().GetProperty("occurrenceCount")!.GetValue(x)!), requestHashes, requestSetSha256 = requestSetSha, sourceFingerprint = sourceV2Fingerprint, goldDerivedInput = false, v5cEvaluationIncluded = false, v6aDiagnosisIncluded = false, semanticNodeRequested = false, hierarchyRequested = false, records });
        await WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-v6c-preflight-manifest-v3-opaque-handles", status = "READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION", supersedes = "preflight-v2-addressable", documentCount = records.Count, requestCount = records.Count, occurrenceCount = records.Sum(x => (int)x.GetType().GetProperty("occurrenceCount")!.GetValue(x)!), requestHashes, requestSetSha256 = requestSetSha,
            sourceFingerprint = sourceV2Fingerprint, occurrenceHandleCoverage = "100%", evidenceHandleCoverage = "100%", duplicatedMembershipRepresentation = false, semanticContractUnchanged = true,
            providerCalls = 0, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, v1Mutation = false, v2Mutation = false,
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), "# A99 V6C-v3 — opaque addressing preflight\n\nThis is an offline challenger boundary. Canonical occurrence/evidence IDs remain input provenance, while model output is restricted to deterministic short handles. Membership is represented once by assignments; owner members are derived by the harness. V1 and V2 artifacts remain immutable. Gold, V5C, V6A, and provider execution are excluded.\n", new UTF8Encoding(false));
        Console.WriteLine($"V6C_V3_PREFLIGHT_COMPLETE REQUESTS={records.Count} OCCURRENCES={records.Sum(x => (int)x.GetType().GetProperty("occurrenceCount")!.GetValue(x)!)} PROVIDER_CALLS=0 GOLD_READ_COUNT=0 V2_MUTATION=false");
        return 0;
    }

    private static string[] StringArray(JsonNode node) => node.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    private static JsonArray ProjectEvidenceRefs(JsonNode node, IReadOnlyDictionary<string, string> evidenceLookup) => new(StringArray(node).Select(value => (JsonNode)JsonValue.Create(evidenceLookup[value])!).ToArray());

    private static JsonArray ProjectOccurrenceContexts(JsonNode node, IReadOnlyDictionary<string, string> occurrenceMap)
    {
        var result = new JsonArray();
        foreach (var context in node.AsArray())
        {
            var item = context!.AsObject();
            var id = item["occurrenceId"]!.GetValue<string>();
            Require(occurrenceMap.TryGetValue(id, out var handle), "V6C_V3_CONTEXT_OCCURRENCE_NOT_IN_UNIVERSE");
            result.Add(new JsonObject { ["ref"] = handle, ["sourceOccurrenceId"] = id, ["text"] = item["text"]!.GetValue<string>(), ["documentOrder"] = item["documentOrder"]!.GetValue<int>() });
        }
        return result;
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
        var allowed = request.TryGetProperty("allowedOccurrenceIds", out var allowedIds)
            ? allowedIds.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal)
            : request.GetProperty("occurrences").EnumerateArray().Select(x => x.GetProperty("occurrenceId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var validEvidence = request.TryGetProperty("allowedEvidenceRefs", out var allowedEvidence)
            ? allowedEvidence.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(allowed, StringComparer.Ordinal);
        if (!request.TryGetProperty("allowedEvidenceRefs", out _))
        {
            validEvidence.UnionWith(request.GetProperty("occurrences").EnumerateArray().Select(x => x.GetProperty("sourceContainerIdentity").GetString()!));
            validEvidence.UnionWith(request.GetProperty("parserOwnedScopeGroups").EnumerateArray().Select(x => x.GetProperty("sourceContainerIdentity").GetString()!));
        }
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

    private static object ResponseSchemaV3() => new
    {
        type = "object", additionalProperties = false, required = new[] { "owners", "assignments", "unresolvedRefs" }, properties = new
        {
            owners = new { type = "array", items = new { type = "object", additionalProperties = false, required = new[] { "owner", "description", "autonomous", "evidenceRefs" }, properties = new { owner = new { type = "string", pattern = "^O[0-9]{2,3}$" }, description = new { type = "string", minLength = 1 }, autonomous = new { type = "boolean" }, evidenceRefs = new { type = "array", items = new { type = "string", pattern = "^E[0-9]{3}$" } } } } },
            assignments = new { type = "array", items = new { type = "object", additionalProperties = false, required = new[] { "ref", "owner" }, properties = new { @ref = new { type = "string", pattern = "^U[0-9]{3}$" }, owner = new { type = "string", pattern = "^O[0-9]{2,3}$" } } } },
            unresolvedRefs = new { type = "array", items = new { type = "string", pattern = "^U[0-9]{3}$" } },
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
    private sealed record OwnerDiagnostics(int BadMemberCount, int BadEvidenceCount, int DuplicateAssignmentCount, int MissingCoverageCount, string PrimaryFailure);
    private sealed record V3Validation(bool Accepted, int AssignedOccurrences, int UnresolvedOccurrences, int OwnerCount, int UnknownOccurrenceHandleCount, int DuplicateAssignmentCount, int MissingAssignmentCount, int UnknownEvidenceHandleCount, int UnknownOwnerIdCount, int EmptyOwnerCount, IReadOnlyList<string> Errors);
}
