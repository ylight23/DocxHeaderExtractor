using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Eval.ReasoningRetention;
using DocxHeaderExtractor.Infrastructure.AI;

namespace IdentityBenchmarkV4GB;

internal static class Program
{
    private const string ExpectedHead = "6edb12d07f3d5f468705afca32862440c7fd4077";
    private const string OutputRelative = "artifacts/identity-benchmark/v4/target-grounding-challenger/execution";
    private const string V4GaRelative = "artifacts/identity-benchmark/v4/target-grounding-challenger";
    private const string V4FeRelative = "artifacts/identity-benchmark/v4/projected-verifier-experiment/execution";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Provider = "OpenRouter";
    private const string ConfigHash = "8983be53cc38d8a28c8d1467216934d3bab1201673dae86f077f82328b392967";
    private const int ExpectedSample = 128;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        var approved = args.Any(x => string.Equals(x, "--execute-approved", StringComparison.Ordinal));
        try
        {
            if (approved)
                return await RunApprovedAsync(root);

            if (args.Any(x => string.Equals(x, "--finalize-approved", StringComparison.Ordinal)))
                return await FinalizeApprovedAsync(root);

            await RunPreflightAsync(root);
            Console.WriteLine("V4G_B_STATUS=AWAITING_V4G_PROVIDER_EXECUTION_APPROVAL");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V4G_B_ERROR={ex}");
            return 2;
        }
    }

    private static async Task<int> RunApprovedAsync(string root)
    {
        var output = Full(root, OutputRelative);
        Require(File.Exists(Path.Combine(output, "provider-preflight.json")), "V4G_B_PREFLIGHT_MISSING");
        Require(!Directory.Exists(Path.Combine(output, "attempts")) || !Directory.EnumerateFiles(Path.Combine(output, "attempts"), "*.json").Any(), "V4G_B_ATTEMPTS_ALREADY_EXIST_RESUME_NOT_ALLOWED");
        var inputs = LoadV4GWork(root);
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            await WriteAsync(Path.Combine(output, "provider-preflight.json"), new { schemaVersion = "a99-v4g-b-provider-preflight-v1", status = "BLOCKED_ON_PROVIDER_API_KEY", provider = Provider, model = Model, modelCalls = 0, providerCalls = 0, goldReadCount = 0 });
            return 1;
        }

        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri("https://openrouter.ai/api/v1/chat/completions"),
            ApiKey = key,
            Model = Model,
            ContextSize = 1_000_000,
            MaxOutputTokens = 768,
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
        await WriteAsync(Path.Combine(output, "provider-preflight.json"), new
        {
            schemaVersion = "a99-v4g-b-provider-preflight-v1", status = "PREFLIGHT_PASS_FROZEN_CAPABILITY",
            provider = Provider, model = Model, configurationHash = ConfigHash,
            requestCount = ExpectedSample, transientRequestRetries = 0,
            capabilitySource = "FROZEN_V4F_E_PROVIDER_PREFLIGHT_METADATA",
            contextLimitTokens = capability.ContextLength, maxCompletionTokensReported = capability.MaxCompletionTokens,
            modelCalls = 0, providerCalls = 0, goldReadCount = 0, explicitExecutionFlag = true,
            checkedUtc = DateTimeOffset.UtcNow,
        });
        await WriteAsync(Path.Combine(output, "execution-manifest.json"), new
        {
            schemaVersion = "a99-v4g-b-execution-manifest-v1", experiment = "IDENTITY_BENCHMARK_V4G_B",
            parent = "V4G-A@6edb12d", status = "EXECUTING", provider = Provider, model = Model,
            candidateUniverse = 7_702, frozenSampleCount = ExpectedSample, scheduledCalls = ExpectedSample,
            actualModelCalls = 0, actualProviderCalls = 0, transientRequestRetries = 0,
            requestBodiesPersisted = false, goldReadCount = 0, temporalProviderDriftControlled = false,
            executionOrder = "frozen V4G-A sample request manifest candidateId ASC", startedUtc = DateTimeOffset.UtcNow,
        });

        Directory.CreateDirectory(Path.Combine(output, "attempts"));
        Directory.CreateDirectory(Path.Combine(output, "raw-responses"));
        var attempts = new List<AttemptRecord>(ExpectedSample);
        var results = new List<V2Result>(ExpectedSample);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(root, "identity-benchmark-v4g-b", "128-target-grounded-candidates");
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);

        foreach (var work in inputs)
        {
            var attemptPath = Path.Combine(output, "attempts", $"{work.Sequence:D4}.json");
            var started = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            string status;
            string? error = null;
            string? rawHash = null;
            string? parsedHash = null;
            object? parsed = null;
            RequestPacketTelemetry? telemetry = null;
            try
            {
                VerifyWorkIntegrity(work);
                var result = await model.CompleteRawStructuredSemanticAsync(
                    work.DocumentId, "V4G_B_TARGET_GROUNDED", work.RequestId,
                    work.RequestJson, work.RequestBytes, 1, 1,
                    HdsaCanonicalTargetGroundedProjectedPairVerifierRequestBuilder.SystemContract,
                    $"TASK=V4G_B_TARGET_GROUNDED_PAIR_VERIFICATION\n{work.RequestJson}\nReturn exactly the requested JSON object.",
                    HdsaGlobalIdentityRetrieveVerifyContract.VerificationSchema(),
                    "hdsa_global_identity_retrieve_verify_v1", CancellationToken.None);
                telemetry = result.Telemetry;
                rawHash = Sha256Text(result.Content);
                var rawPath = Path.Combine(output, "raw-responses", $"{work.Sequence:D4}.json");
                await File.WriteAllTextAsync(rawPath, result.Content, new UTF8Encoding(false));
                try
                {
                    var response = HdsaGlobalIdentityRetrieveVerifyContract.ParseVerification(result.Content);
                    var validation = HdsaGlobalIdentityRetrieveVerifyContract.ValidateVerification(work.ValidationRequest, response);
                    parsed = new { response, validation };
                    parsedHash = Sha256Text(JsonSerializer.Serialize(parsed, JsonOptions));
                    status = validation.Accepted ? "VALID" : "INVALID_SCHEMA";
                }
                catch (Exception ex) when (ex is FormatException or JsonException)
                {
                    status = "INVALID_SCHEMA";
                    error = ex.Message;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or ReasoningCompletionException or FormatException)
            {
                status = ex is ReasoningCompletionException ? "PROVIDER_ERROR" : "TRANSPORT_ERROR";
                error = ex.GetType().Name + ":" + ex.Message;
            }
            stopwatch.Stop();
            var attempt = new AttemptRecord(work.Sequence, work.CandidateId, work.RequestId, work.RequestSha256, work.RequestBytes,
                started, DateTimeOffset.UtcNow, status, telemetry?.HttpStatus, telemetry?.ProviderCallId,
                stopwatch.ElapsedMilliseconds, telemetry?.ReportedInputTokens, telemetry?.ReportedReasoningTokens,
                telemetry?.ReportedOutputTokens, rawHash, parsedHash, error, false);
            attempts.Add(attempt);
            results.Add(new V2Result(work.Sequence, work.CandidateId, work.RequestSha256, status, rawHash, parsedHash, parsed, telemetry));
            await WriteAsync(attemptPath, attempt);
            await WriteAsync(Path.Combine(output, "attempt-manifest.json"), new
            {
                schemaVersion = "a99-v4g-b-attempt-manifest-v1", immutableRecords = true,
                expectedAttemptCount = ExpectedSample, actualAttemptCount = attempts.Count,
                currentProcessProviderCalls = model.ProviderCalls, retries = 0, attempts, goldReadCount = 0,
            });
            Console.WriteLine($"V4G_B_ATTEMPT={work.Sequence}/{ExpectedSample} STATUS={status} PROVIDER_CALLS={model.ProviderCalls}");
        }

        await FinalizeExecutionAsync(root, output, attempts, results, model.ProviderCalls);
        Console.WriteLine($"V4G_B_STATUS=TARGET_GROUNDING_EXECUTION_COMPLETE PROVIDER_CALLS={model.ProviderCalls}");
        return attempts.Count == ExpectedSample && model.ProviderCalls == ExpectedSample ? 0 : 1;
    }

    private static async Task<int> FinalizeApprovedAsync(string root)
    {
        var output = Full(root, OutputRelative);
        var attemptsPath = Path.Combine(output, "attempt-manifest.json");
        var parsedPath = Path.Combine(output, "parsed-results.json");
        Require(File.Exists(attemptsPath) && File.Exists(parsedPath), "V4G_B_FINALIZE_ARTIFACTS_MISSING");
        using var attemptsDocument = Read(attemptsPath);
        using var resultsDocument = Read(parsedPath);
        var attempts = JsonSerializer.Deserialize<List<AttemptRecord>>(attemptsDocument.RootElement.GetProperty("attempts").GetRawText(), JsonOptions) ?? new List<AttemptRecord>();
        var results = resultsDocument.RootElement.GetProperty("results").EnumerateArray().Select(x => new V2Result(
            x.GetProperty("sequence").GetInt32(),
            x.GetProperty("candidateId").GetString()!,
            x.GetProperty("requestSha256").GetString()!,
            x.GetProperty("status").GetString()!,
            x.TryGetProperty("rawResponseSha256", out var raw) && raw.ValueKind == JsonValueKind.String ? raw.GetString() : null,
            x.TryGetProperty("parsedResponseSha256", out var parsedHash) && parsedHash.ValueKind == JsonValueKind.String ? parsedHash.GetString() : null,
            x.TryGetProperty("parsed", out var parsed) ? parsed.Clone() : null,
            null)).ToArray();
        Require(attempts.Count == ExpectedSample && results.Length == ExpectedSample, "V4G_B_FINALIZE_COUNT");
        var providerCalls = attempts.Count(x => x.Status is "VALID" or "INVALID_SCHEMA" or "PROVIDER_ERROR" or "TRANSPORT_ERROR");
        await FinalizeExecutionAsync(root, output, attempts, results, providerCalls);
        Console.WriteLine($"V4G_B_STATUS=OFFLINE_FINALIZATION_COMPLETE PROVIDER_CALLS={providerCalls}");
        return 0;
    }

    private static void VerifyWorkIntegrity(V2Work work)
    {
        Require(work.CandidateId == work.Request.GetProperty("targetPair").GetProperty("candidateId").GetString(), "V4G_B_CANDIDATE_ID_DRIFT:" + work.CandidateId);
        Require(work.RequestBytes == Encoding.UTF8.GetByteCount(work.RequestJson), "V4G_B_REQUEST_BYTES_DRIFT:" + work.CandidateId);
        Require(work.RequestSha256 == Sha256Text(work.RequestJson), "V4G_B_REQUEST_SHA_DRIFT:" + work.CandidateId);
    }

    private static async Task FinalizeExecutionAsync(string root, string output, IReadOnlyList<AttemptRecord> attempts, IReadOnlyList<V2Result> results, int providerCalls)
    {
        var evaluable = results.Where(x => x.Status is "VALID" or "INVALID_SCHEMA").ToArray();
        var mismatches = evaluable.Count(x => RejectionReason(x.Parsed) == "TARGET_PAIR_MISMATCH");
        var valid = results.Count(x => x.Status == "VALID");
        var providerErrors = results.Count(x => x.Status == "PROVIDER_ERROR");
        var otherInvalid = evaluable.Length - mismatches - valid;
        var classification = evaluable.Length < 20 ? "BLOCKED_ON_PROVIDER_EXECUTION_QUALITY" :
            mismatches / (double)evaluable.Length <= 2d / 111d + .05 && valid / (double)evaluable.Length >= 109d / 111d - .05 ? "TARGET_GROUNDING_REPAIRED_ON_DEV_SAMPLE" :
            mismatches / (double)evaluable.Length < 45d / 108d ? "TARGET_GROUNDING_SUBSTANTIALLY_IMPROVED" : "TARGET_GROUNDING_NOT_IMPROVED";
        await WriteAsync(Path.Combine(output, "raw-response-manifest.json"), new
        {
            schemaVersion = "a99-v4g-b-raw-response-manifest-v1", frozenBeforeParsingComparison = true,
            responseCount = results.Count, successfulRawResponses = results.Count(x => x.RawResponseSha256 is not null),
            responses = attempts.Select(x => new { x.Sequence, x.CandidateId, x.RequestSha256, status = x.Status, rawResponseSha256 = x.RawResponseSha256, providerRequestId = x.ProviderRequestId, httpStatus = x.HttpStatus, goldReadBeforeFreeze = false }),
            goldReadCount = 0,
        });
        await WriteAsync(Path.Combine(output, "parsed-results.json"), new
        {
            schemaVersion = "a99-v4g-b-parsed-results-v1", parser = "HdsaGlobalIdentityRetrieveVerifyContract",
            parserVersion = HdsaGlobalIdentityRetrieveVerifyContract.Version, results, goldReadCount = 0,
        });
        await WriteAsync(Path.Combine(output, "target-grounding-results.json"), new
        {
            schemaVersion = "a99-v4g-b-target-grounding-results-v1", status = "FROZEN_BEFORE_GOLD",
            classification, scheduled = ExpectedSample, evaluable = evaluable.Length, targetMismatch = mismatches,
            valid, providerErrors, otherInvalid, targetMismatchScheduledRate = mismatches / (double)ExpectedSample,
            targetMismatchEvaluableRate = evaluable.Length == 0 ? (double?)null : mismatches / (double)evaluable.Length,
            validScheduledRate = valid / (double)ExpectedSample,
            validEvaluableRate = evaluable.Length == 0 ? (double?)null : valid / (double)evaluable.Length,
            goldReadCount = 0, semanticAccuracyMeasured = false,
        });
        await WriteAsync(Path.Combine(output, "usage-and-latency.json"), new
        {
            schemaVersion = "a99-v4g-b-usage-and-latency-v1", calls = attempts.Count,
            inputTokens = Distribution(attempts.Where(x => x.InputTokens.HasValue).Select(x => (long)x.InputTokens!.Value)),
            reasoningTokens = Distribution(attempts.Where(x => x.ReasoningTokens.HasValue).Select(x => (long)x.ReasoningTokens!.Value)),
            outputTokens = Distribution(attempts.Where(x => x.OutputTokens.HasValue).Select(x => (long)x.OutputTokens!.Value)),
            latencyMs = Distribution(attempts.Select(x => x.LatencyMs)),
        });
        var inputTokens = attempts.Where(x => x.InputTokens.HasValue).Sum(x => (long)x.InputTokens!.Value);
        var outputTokens = attempts.Where(x => x.OutputTokens.HasValue).Sum(x => (long)x.OutputTokens!.Value);
        await WriteAsync(Path.Combine(output, "cost-report.json"), new { schemaVersion = "a99-v4g-b-cost-report-v1", provider = Provider, model = Model, inputUsdPerMillionTokens = .03m, outputUsdPerMillionTokens = .13m, inputTokens, outputTokens, estimatedCostUsd = inputTokens * .03m / 1_000_000m + outputTokens * .13m / 1_000_000m });
        await WriteAsync(Path.Combine(output, "firewall.json"), new { schemaVersion = "a99-v4g-b-firewall-v1", status = "COMPLETE", goldReadCount = 0, ir018ToIr022ReadCount = 0, v4ebEvaluationReadCount = 0, modelCalls = providerCalls, providerCalls, candidateUniverseChanged = false, evidenceChanged = false, projectionChanged = false, parserChanged = false, validatorChanged = false, targetGroundingRepresentationChanged = true, all128Scheduled = true, historical45UsedForSelection = false, rawFrozenBeforeParse = true, noFuzzyRepair = true });
        await WriteAsync(Path.Combine(output, "execution-manifest.json"), new { schemaVersion = "a99-v4g-b-execution-manifest-v1", experiment = "IDENTITY_BENCHMARK_V4G_B", status = "RESPONSE_FREEZE_COMPLETE", scheduledCalls = ExpectedSample, actualModelCalls = providerCalls, actualProviderCalls = providerCalls, goldReadCount = 0, temporalProviderDriftControlled = false, completedUtc = DateTimeOffset.UtcNow });
        var paired = BuildHistoricalPairedAnalysis(root, results);
        await WriteAsync(Path.Combine(output, "historical-paired-analysis.json"), paired);
        var pairedJson = JsonSerializer.Serialize(paired, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), $"# A99 V4G-B — target-grounding challenger\n\nStatus: **RESPONSE_FREEZE_COMPLETE**\n\nClassification: **{classification}**\n\nCalls: **{providerCalls}/{ExpectedSample}**. Gold reads: **0**. Semantic accuracy: **not measured**.\n\nTarget mismatch: **{mismatches}/{ExpectedSample} scheduled**, **{mismatches}/{evaluable.Length} evaluable**. Valid: **{valid}/{ExpectedSample} scheduled**, **{valid}/{evaluable.Length} evaluable**. Provider errors: **{providerErrors}**. Other invalid: **{otherInvalid}**.\n\n## Historical paired diagnostic\n\nThis is a post-freeze, Gold-free comparison against frozen V4F-E PROJECTED_V1 artifacts. It was not used for sampling, request construction, or classification.\n\n```json\n{pairedJson}\n```\n", new UTF8Encoding(false));
    }

    private static object BuildHistoricalPairedAnalysis(string root, IReadOnlyList<V2Result> v2Results)
    {
        var v1Root = Full(root, V4FeRelative);
        var parsedPath = Path.Combine(v1Root, "parsed-results.json");
        var rawDir = Path.Combine(v1Root, "raw-responses");
        if (!File.Exists(parsedPath) || !Directory.Exists(rawDir))
            return new { schemaVersion = "a99-v4g-b-historical-paired-analysis-v1", status = "UNAVAILABLE_MISSING_V4F_E_ARTIFACTS", pairs = Array.Empty<object>() };

        using var document = Read(parsedPath);
        var v1 = document.RootElement.GetProperty("results").EnumerateArray()
            .Where(x => x.GetProperty("arm").GetString() == "ARM_B")
            .ToDictionary(x => x.GetProperty("candidateId").GetString()!, x => x, StringComparer.Ordinal);
        var pairs = new List<object>(v2Results.Count);
        var v1Evaluable = 0;
        var v2Evaluable = 0;
        var bothEvaluable = 0;
        var v1Mismatch = 0;
        var v2Mismatch = 0;
        var repaired = 0;
        var regressed = 0;
        var unchangedMismatch = 0;
        var unavailable = 0;

        foreach (var v2 in v2Results.OrderBy(x => x.CandidateId, StringComparer.Ordinal))
        {
            if (!v1.TryGetValue(v2.CandidateId, out var historical))
            {
                pairs.Add(new { candidateId = v2.CandidateId, status = "MISSING_HISTORICAL_PROJECTED_V1" });
                continue;
            }

            var historicalRaw = RawPersisted(historical, rawDir);
            var v1Status = historical.GetProperty("status").GetString()!;
            var v1Reason = historical.TryGetProperty("parsed", out var historicalParsed) && historicalParsed.ValueKind == JsonValueKind.Object && historicalParsed.TryGetProperty("validation", out var historicalValidation) && historicalValidation.ValueKind == JsonValueKind.Object && historicalValidation.TryGetProperty("rejectionReason", out var historicalReason) && historicalReason.ValueKind == JsonValueKind.String ? historicalReason.GetString() : null;
            var v1IsEvaluable = historicalRaw && (v1Status is "VALID" or "INVALID_SCHEMA");
            var v2Reason = RejectionReason(v2.Parsed);
            var v2IsEvaluable = v2.Status is "VALID" or "INVALID_SCHEMA";
            var v1IsMismatch = v1IsEvaluable && v1Reason == "TARGET_PAIR_MISMATCH";
            var v2IsMismatch = v2IsEvaluable && v2Reason == "TARGET_PAIR_MISMATCH";
            var pairStatus = !v1IsEvaluable || !v2IsEvaluable ? "UNAVAILABLE" :
                v1IsMismatch && !v2IsMismatch ? "REPAIRED_TARGET_GROUNDING" :
                !v1IsMismatch && v2IsMismatch ? "REGRESSED_TARGET_GROUNDING" :
                v1IsMismatch ? "UNCHANGED_TARGET_MISMATCH" : "GROUNDED_BOTH";
            if (v1IsEvaluable) v1Evaluable++;
            if (v2IsEvaluable) v2Evaluable++;
            if (v1IsEvaluable && v2IsEvaluable) bothEvaluable++;
            if (v1IsMismatch) v1Mismatch++;
            if (v2IsMismatch) v2Mismatch++;
            if (pairStatus == "REPAIRED_TARGET_GROUNDING") repaired++;
            if (pairStatus == "REGRESSED_TARGET_GROUNDING") regressed++;
            if (pairStatus == "UNCHANGED_TARGET_MISMATCH") unchangedMismatch++;
            if (pairStatus == "UNAVAILABLE") unavailable++;
            pairs.Add(new
            {
                candidateId = v2.CandidateId,
                historicalStatus = v1Status,
                historicalRawPersisted = historicalRaw,
                historicalRejectionReason = v1Reason,
                v2Status = v2.Status,
                v2RejectionReason = v2Reason,
                outcome = pairStatus,
            });
        }

        return new
        {
            schemaVersion = "a99-v4g-b-historical-paired-analysis-v1",
            status = "COMPLETE_AFTER_RESPONSE_FREEZE",
            comparisonArm = "PROJECTED_V1",
            source = "FROZEN_V4F_E_EXECUTION_ARTIFACTS",
            goldReadCount = 0,
            historicalOutputsUsedForSelection = false,
            semanticAccuracyMeasured = false,
            scheduledPairs = pairs.Count,
            v1Evaluable,
            v2Evaluable,
            bothEvaluable,
            v1TargetMismatch = v1Mismatch,
            v2TargetMismatch = v2Mismatch,
            repairedTargetGrounding = repaired,
            regressedTargetGrounding = regressed,
            unchangedTargetMismatch = unchangedMismatch,
            unavailable,
            pairs,
        };
    }

    private static IReadOnlyList<V2Work> LoadV4GWork(string root)
    {
        var ga = Full(root, V4GaRelative);
        var sourcePath = Full(root, "artifacts/identity-benchmark/v2/source-catalog.json");
        var shortlistPath = Full(root, "artifacts/identity-benchmark/v4/pruning-challenger/shortlist.json");
        var packetPath = Full(root, "artifacts/identity-benchmark/v4/context-projection/packet-manifest.json");
        var projectionPath = Full(root, "artifacts/identity-benchmark/v4/context-projection/projection-config.json");
        foreach (var path in new[] { sourcePath, shortlistPath, packetPath, projectionPath, Path.Combine(ga, "request-manifest.json"), Path.Combine(ga, "challenger-sample-request-manifest.json") })
            Require(File.Exists(path), "V4G_B_MISSING_INPUT:" + path);
        using var source = Read(sourcePath);
        using var shortlist = Read(shortlistPath);
        using var packets = Read(packetPath);
        using var requestManifest = Read(Path.Combine(ga, "request-manifest.json"));
        using var sample = Read(Path.Combine(ga, "challenger-sample-request-manifest.json"));
        var fingerprint = source.RootElement.GetProperty("catalogFingerprint").GetString()!;
        var docs = source.RootElement.GetProperty("sourceDocuments").EnumerateArray().ToDictionary(x => x.GetProperty("documentId").GetString()!, x => new SourceDoc(x.GetProperty("documentId").GetString()!, x.GetProperty("sourceSha256").GetString()!, x.GetProperty("sourcePath").GetString()!), StringComparer.Ordinal);
        var allNodes = source.RootElement.GetProperty("sourceOccurrences").EnumerateArray().Select(x => new Occurrence(x.GetProperty("nodeId").GetString()!, x.GetProperty("text").GetString()!, x.GetProperty("documentOrder").GetInt32())).ToArray();
        var byDocument = allNodes.GroupBy(x => DocumentOf(x.NodeId), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(x => x.DocumentOrder).ThenBy(x => x.NodeId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var candidates = shortlist.RootElement.GetProperty("candidates").EnumerateArray().Select(x => new Candidate(x.GetProperty("pairId").GetString()!, x.GetProperty("documentId").GetString()!, x.GetProperty("left").GetString()!, x.GetProperty("right").GetString()!, x.TryGetProperty("reasons", out var reasons) ? reasons.EnumerateArray().Select(y => y.GetString()!).ToArray() : Array.Empty<string>())).ToDictionary(x => x.PairId, StringComparer.Ordinal);
        var packetRows = packets.RootElement.GetProperty("packets").EnumerateArray().ToDictionary(x => x.GetProperty("candidateId").GetString()!, StringComparer.Ordinal);
        var expectedRows = requestManifest.RootElement.GetProperty("requests").EnumerateArray().ToDictionary(x => x.GetProperty("candidateId").GetString()!, StringComparer.Ordinal);
        var sampleRows = sample.RootElement.GetProperty("requests").EnumerateArray().OrderBy(x => x.GetProperty("candidateId").GetString(), StringComparer.Ordinal).ToArray();
        Require(sampleRows.Length == ExpectedSample, "V4G_B_SAMPLE_COUNT_RUNTIME");
        var result = new List<V2Work>(ExpectedSample);
        var projectionSha = Sha256File(projectionPath);
        var sequence = 0;
        foreach (var sampleRow in sampleRows)
        {
            var id = sampleRow.GetProperty("candidateId").GetString()!;
            Require(candidates.TryGetValue(id, out var candidate), "V4G_B_UNKNOWN_SAMPLE_CANDIDATE:" + id);
            Require(expectedRows.TryGetValue(id, out var expected), "V4G_B_MISSING_FROZEN_REQUEST:" + id);
            Require(byDocument.TryGetValue(candidate!.DocumentId, out var nodes), "V4G_B_UNKNOWN_DOCUMENT:" + id);
            var nodeMap = nodes!.ToDictionary(x => x.NodeId, StringComparer.Ordinal);
            Require(nodeMap.TryGetValue(candidate.Left, out var left), "V4G_B_UNKNOWN_LEFT_TARGET:" + id);
            Require(nodeMap.TryGetValue(candidate.Right, out var right), "V4G_B_UNKNOWN_RIGHT_TARGET:" + id);
            Require(packetRows.TryGetValue(id, out var packetRow), "V4G_B_MISSING_PACKET:" + id);
            var packet = BuildPacket(candidate, left!, right!, nodes!, docs[candidate.DocumentId], fingerprint, projectionSha);
            var packetBytes = JsonSerializer.SerializeToUtf8Bytes(packet, JsonOptions);
            Require(Sha256(packetBytes) == packetRow.GetProperty("packetSha256").GetString(), "V4G_B_PACKET_RECONSTRUCTION:" + id);
            using var packetDocument = JsonDocument.Parse(packetBytes);
            var built = HdsaCanonicalTargetGroundedProjectedPairVerifierRequestBuilder.Build(packetDocument.RootElement, Model, Provider, 768);
            Require(built.Sha256 == expected!.GetProperty("requestSha256").GetString(), "V4G_B_REQUEST_RECONSTRUCTION:" + id);
            Require(built.Utf8Bytes.Length == expected.GetProperty("byteLength").GetInt32(), "V4G_B_REQUEST_LENGTH_RECONSTRUCTION:" + id);
            var nodeInputs = nodes.Select(n => new HdsaIdentityRoleNodeInput(n.NodeId, [n.NodeId], n.Text, n.DocumentOrder, "UNAVAILABLE", false)).ToArray();
            var pair = new HdsaIdentityCandidatePair(candidate.PairId, candidate.Left, candidate.Right);
            using var requestDocument = JsonDocument.Parse(built.Json);
            result.Add(new V2Work(++sequence, id, id, built.Json, built.Sha256, built.Utf8Bytes.Length, requestDocument.RootElement.Clone(), new HdsaIdentityPairVerificationRequest(fingerprint, nodeInputs, pair, false), candidate.DocumentId, candidate));
        }
        Require(result.Select(x => x.CandidateId).Distinct(StringComparer.Ordinal).Count() == ExpectedSample, "V4G_B_RUNTIME_SAMPLE_DUPLICATE");
        return result;
    }

    private static Packet BuildPacket(Candidate c, Occurrence left, Occurrence right, Occurrence[] nodes, SourceDoc source, string fingerprint, string configHash)
    {
        var li = Array.IndexOf(nodes, left); var ri = Array.IndexOf(nodes, right); var ordered = li <= ri ? (li, ri) : (ri, li);
        var allBetween = nodes.Skip(ordered.Item1 + 1).Take(Math.Max(0, ordered.Item2 - ordered.Item1 - 1)).ToArray();
        var between = Bounded(allBetween, 8).Select(x => Core(x, source)).ToArray();
        var local = new LocalContext(Neighbors(nodes, li, -1, 2, source), Neighbors(nodes, li, 1, 2, source), Neighbors(nodes, ri, -1, 2, source), Neighbors(nodes, ri, 1, 2, source), between, allBetween.Length > between.Length, allBetween.Length);
        var structural = new StructuralContext(new[] { Container(left.NodeId), Container(right.NodeId) }.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StructuralPeers(nodes, li, ri));
        var relational = new RelationalFacts(Math.Abs(left.DocumentOrder - right.DocumentOrder), Normalize(left.Text) == Normalize(right.Text), Markers(left.Text), Markers(right.Text), Branches(left.Text), Branches(right.Text), c.Reasons.Order(StringComparer.Ordinal).ToArray());
        var availability = new EvidenceAvailability("AVAILABLE", "AVAILABLE", "AVAILABLE", "UNAVAILABLE", "UNAVAILABLE", "UNAVAILABLE", "UNAVAILABLE", allBetween.Length > 0 ? "AVAILABLE" : "UNAVAILABLE");
        return new("a99_identity_benchmark_v4f_b_pair_evidence_packet", "v4f-b-pair-evidence-packet-v1", c.PairId, c.DocumentId, [Core(left, source), Core(right, source)], local, structural, relational, availability, [new EvidenceAuthority("FROZEN_SOURCE_CATALOG", "source occurrence text/order/container", "AVAILABLE"), new EvidenceAuthority("FROZEN_SOURCE_CATALOG", "bounded context selected by source order", "AVAILABLE"), new EvidenceAuthority("FROZEN_SOURCE_CATALOG", "source path extension", "AVAILABLE")], ["numbering", "scope", "layout", "visual"], "v4f-b-bounded-source-evidence-projection-v1", fingerprint, configHash);
    }

    private static PairOccurrence Core(Occurrence x, SourceDoc source) => new(x.NodeId, x.Text, Normalize(x.Text), x.DocumentOrder, Path.GetExtension(source.SourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" : "SOURCE_PACKET", Container(x.NodeId));
    private static IReadOnlyList<PairOccurrence> Neighbors(IReadOnlyList<Occurrence> all, int index, int direction, int count, SourceDoc source) => (direction < 0 ? all.Take(index).TakeLast(count) : all.Skip(index + 1).Take(count)).Select(x => Core(x, source)).ToArray();
    private static IReadOnlyList<Occurrence> Bounded(IReadOnlyList<Occurrence> all, int max) => all.Count <= max ? all : all.Take(max / 2).Concat(all.TakeLast(max - max / 2)).ToArray();
    private static IReadOnlyList<StructuralPeer> StructuralPeers(IReadOnlyList<Occurrence> nodes, int left, int right) => nodes.Select((x, i) => (x, i)).Where(x => x.i == left || x.i == right).SelectMany(anchor => nodes.Select((x, i) => (x, i)).Where(x => x.i != left && x.i != right && Container(x.x.NodeId) == Container(anchor.x.NodeId)).OrderBy(x => Math.Abs(x.i - anchor.i)).ThenBy(x => x.x.NodeId, StringComparer.Ordinal).Take(4)).Select(x => new StructuralPeer(x.x.NodeId, x.x.DocumentOrder, Container(x.x.NodeId))).DistinctBy(x => x.SourceOccurrenceId).OrderBy(x => x.DocumentOrder).ThenBy(x => x.SourceOccurrenceId, StringComparer.Ordinal).Take(4).ToArray();
    private static string[] Markers(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"\(\s*(?:cont(?:['’]d)?|continued)\s*\)|\bcontinued\b|tiếp\s+(?:theo|tục)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant).Select(x => x.Value).Order(StringComparer.Ordinal).ToArray();
    private static string[] Branches(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"\[(?:option|alternative|phương\s+án)\s*[^\]]*\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant).Select(x => x.Value).Order(StringComparer.Ordinal).ToArray();
    private static string Normalize(string text) => string.Join(' ', text.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Container(string id) => (id.Split(':', 2).ElementAtOrDefault(1) ?? id).Split('/').FirstOrDefault() ?? string.Empty;
    private static string DocumentOf(string id) => id.Split(':', 2)[0];

    private static async Task RunPreflightAsync(string root)
    {
        var head = Git(root, "rev-parse HEAD");
        Require(head == ExpectedHead, $"V4G_B_UNEXPECTED_HEAD:{head}");

        var v4ga = Full(root, V4GaRelative);
        var v4fe = Full(root, V4FeRelative);
        var output = Full(root, OutputRelative);
        Require(!Directory.Exists(output), "V4G_B_EXECUTION_ALREADY_EXISTS_USE_RESUME_PHASE");
        Directory.CreateDirectory(output);

        using var gaManifest = Read(Path.Combine(v4ga, "manifest.json"));
        using var gaRequests = Read(Path.Combine(v4ga, "request-manifest.json"));
        using var gaSample = Read(Path.Combine(v4ga, "challenger-sample-request-manifest.json"));
        using var gaFirewall = Read(Path.Combine(v4ga, "firewall.json"));
        ValidateV4Ga(gaManifest.RootElement, gaRequests.RootElement, gaSample.RootElement, gaFirewall.RootElement);

        var baseline = ReconstructHistoricalBaseline(v4fe);
        var sampleRows = gaSample.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        var sampleIds = sampleRows.Select(x => x.GetProperty("candidateId").GetString()!).ToArray();
        var allRows = gaRequests.RootElement.GetProperty("requests").EnumerateArray().ToDictionary(x => x.GetProperty("candidateId").GetString()!, StringComparer.Ordinal);
        var sampleRequestRows = sampleIds.Select(id => allRows[id]).ToArray();

        var now = DateTimeOffset.UtcNow;
        await WriteAsync(Path.Combine(output, "provider-preflight.json"), new
        {
            schemaVersion = "a99-v4g-b-provider-preflight-v1",
            status = "AWAITING_V4G_PROVIDER_EXECUTION_APPROVAL",
            requiredExplicitApproval = true,
            requiredFlag = "--execute-approved",
            expectedHead = ExpectedHead,
            actualHead = head,
            sampleCount = sampleRows.Length,
            model = Model,
            provider = Provider,
            configurationHash = ConfigHash,
            transientRequestRetries = 0,
            goldReadCount = 0,
            modelCalls = 0,
            providerCalls = 0,
            requestBodiesPersisted = false,
            requestIntegrityReady = true,
            historicalBaselineReconstructed = true,
            temporalProviderDriftControlled = false,
            createdUtc = now,
        });

        await WriteAsync(Path.Combine(output, "historical-baseline.json"), baseline);
        await WriteAsync(Path.Combine(output, "execution-manifest.json"), new
        {
            schemaVersion = "a99-v4g-b-execution-manifest-v1",
            experiment = "IDENTITY_BENCHMARK_V4G_B",
            parent = "V4G-A@6edb12d",
            status = "AWAITING_V4G_PROVIDER_EXECUTION_APPROVAL",
            historicalControlComparison = true,
            temporalProviderDriftControlled = false,
            candidateUniverse = 7_702,
            frozenSampleCount = ExpectedSample,
            scheduledCalls = ExpectedSample,
            actualModelCalls = 0,
            actualProviderCalls = 0,
            sampleCandidateIdsSha256 = Sha256Text(string.Join("\n", sampleIds)),
            v4gManifestSha256 = Sha256File(Path.Combine(v4ga, "manifest.json")),
            v4gRequestManifestSha256 = Sha256File(Path.Combine(v4ga, "request-manifest.json")),
            v4gSampleRequestManifestSha256 = Sha256File(Path.Combine(v4ga, "challenger-sample-request-manifest.json")),
            parserChanged = false,
            validatorChanged = false,
            projectionChanged = false,
            targetGroundingRepresentationChanged = true,
            goldReadCount = 0,
            createdUtc = now,
        });

        await WriteAsync(Path.Combine(output, "attempt-manifest.json"), new
        {
            schemaVersion = "a99-v4g-b-attempt-manifest-v1",
            status = "PENDING_PROVIDER_EXECUTION",
            expectedAttemptCount = ExpectedSample,
            actualAttemptCount = 0,
            retries = 0,
            attempts = Array.Empty<object>(),
        });
        await WriteAsync(Path.Combine(output, "raw-response-manifest.json"), new
        {
            schemaVersion = "a99-v4g-b-raw-response-manifest-v1",
            status = "PENDING_PROVIDER_EXECUTION",
            frozenBeforeParsing = true,
            responseCount = 0,
            persistedRawBodies = 0,
            responses = Array.Empty<object>(),
            goldReadCount = 0,
        });
        await WriteAsync(Path.Combine(output, "parsed-results.json"), new
        {
            schemaVersion = "a99-v4g-b-parsed-results-v1",
            status = "PENDING_PROVIDER_EXECUTION",
            parser = "HdsaGlobalIdentityRetrieveVerifyContract",
            parserChanged = false,
            responseCount = 0,
            results = Array.Empty<object>(),
            goldReadCount = 0,
        });
        await WriteAsync(Path.Combine(output, "target-grounding-results.json"), new
        {
            schemaVersion = "a99-v4g-b-target-grounding-results-v1",
            status = "PENDING_PROVIDER_EXECUTION",
            primaryMetric = "TARGET_MISMATCH",
            scheduledDenominator = ExpectedSample,
            evaluableDenominator = (int?)null,
            targetMismatchScheduled = (int?)null,
            targetMismatchEvaluable = (int?)null,
            targetMismatchScheduledRate = (double?)null,
            targetMismatchEvaluableRate = (double?)null,
            validScheduledRate = (double?)null,
            validEvaluableRate = (double?)null,
            goldReadCount = 0,
        });
        await WriteAsync(Path.Combine(output, "historical-paired-analysis.json"), new
        {
            schemaVersion = "a99-v4g-b-historical-paired-analysis-v1",
            status = "PENDING_PROVIDER_EXECUTION",
            note = "No V2 response exists; paired analysis is deferred until all 128 V2 responses freeze.",
            pairs = Array.Empty<object>(),
        });
        await WriteAsync(Path.Combine(output, "usage-and-latency.json"), new
        {
            schemaVersion = "a99-v4g-b-usage-and-latency-v1",
            status = "PENDING_PROVIDER_EXECUTION",
            calls = 0,
            inputTokens = Array.Empty<int>(),
            outputTokens = Array.Empty<int>(),
            latencyMs = Array.Empty<long>(),
        });
        await WriteAsync(Path.Combine(output, "cost-report.json"), new
        {
            schemaVersion = "a99-v4g-b-cost-report-v1",
            status = "PENDING_PROVIDER_EXECUTION",
            provider = Provider,
            model = Model,
            actualProviderCalls = 0,
            estimatedCostUsd = 0m,
        });
        await WriteAsync(Path.Combine(output, "firewall.json"), new
        {
            schemaVersion = "a99-v4g-b-firewall-v1",
            status = "AWAITING_V4G_PROVIDER_EXECUTION_APPROVAL",
            goldReadCount = 0,
            ir018ToIr022ReadCount = 0,
            v4ebEvaluationReadCount = 0,
            modelCalls = 0,
            providerCalls = 0,
            candidateUniverseChanged = false,
            evidenceChanged = false,
            projectionChanged = false,
            parserChanged = false,
            validatorChanged = false,
            targetGroundingRepresentationChanged = true,
            all128Scheduled = true,
            historical45UsedForSelection = false,
            noProviderTransport = true,
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(baseline, sampleRequestRows.Length), new UTF8Encoding(false));
    }

    private static object ReconstructHistoricalBaseline(string v4fe)
    {
        var executionManifestPath = Path.Combine(v4fe, "execution-manifest.json");
        var parsedPath = Path.Combine(v4fe, "parsed-results.json");
        var attemptsDir = Path.Combine(v4fe, "attempts");
        var rawDir = Path.Combine(v4fe, "raw-responses");
        Require(File.Exists(executionManifestPath) && File.Exists(parsedPath) && Directory.Exists(attemptsDir), "V4G_B_MISSING_V4F_E_ARTIFACTS");
        using var execution = Read(executionManifestPath);
        using var parsed = Read(parsedPath);
        var attempts = Directory.GetFiles(attemptsDir, "*.json").Select(path => Read(path).RootElement.Clone()).ToArray();
        var results = parsed.RootElement.GetProperty("results").EnumerateArray().Select(x => x.Clone()).ToArray();
        Require(attempts.Length == 256 && results.Length == 256, "V4G_B_V4F_E_COUNT");

        var arms = new[] { (Arm: "ARM_A", Name: "OLD"), (Arm: "ARM_B", Name: "PROJECTED_V1") };
        var summaries = arms.Select(arm => BuildArmBaseline(arm.Arm, arm.Name, attempts, results, rawDir)).ToArray();
        Require(summaries.Single(x => x.arm == "OLD").targetMismatch == 2, "V4G_B_OLD_BASELINE_MISMATCH");
        Require(summaries.Single(x => x.arm == "PROJECTED_V1").targetMismatch == 45, "V4G_B_PROJECTED_BASELINE_MISMATCH");
        Require(summaries.Single(x => x.arm == "OLD").valid == 109, "V4G_B_OLD_BASELINE_VALID");
        Require(summaries.Single(x => x.arm == "PROJECTED_V1").valid == 63, "V4G_B_PROJECTED_BASELINE_VALID");
        return new
        {
            schemaVersion = "a99-v4g-b-historical-baseline-v1",
            source = "FROZEN_V4F_E_EXECUTION_ARTIFACTS",
            executionManifestSha256 = Sha256File(executionManifestPath),
            parsedResultsSha256 = Sha256File(parsedPath),
            temporalProviderDriftControlled = false,
            goldReadCount = 0,
            v4ebEvaluationReadCount = 0,
            arms = summaries,
        };
    }

    private static BaselineArm BuildArmBaseline(string arm, string name, JsonElement[] attempts, JsonElement[] results, string rawDir)
    {
        var a = attempts.Where(x => x.GetProperty("arm").GetString() == arm).ToArray();
        var r = results.Where(x => x.GetProperty("arm").GetString() == arm).ToArray();
        var rawPersisted = r.Where(x => RawPersisted(x, rawDir)).ToArray();
        var evaluable = rawPersisted.Where(x => x.GetProperty("status").GetString() is "VALID" or "INVALID_SCHEMA").ToArray();
        var mismatches = evaluable.Count(x => x.GetProperty("parsed").GetProperty("validation").GetProperty("rejectionReason").GetString() == "TARGET_PAIR_MISMATCH");
        var valid = evaluable.Count(x => x.GetProperty("status").GetString() == "VALID");
        var otherInvalid = evaluable.Length - mismatches - valid;
        return new BaselineArm(
            name,
            a.Length,
            a.Count(x => x.TryGetProperty("httpStatus", out var status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() == 200),
            rawPersisted.Length,
            evaluable.Length,
            mismatches,
            valid,
            otherInvalid,
            a.Count(x => x.GetProperty("status").GetString() == "PROVIDER_ERROR"),
            a.Count(x => x.GetProperty("status").GetString() == "TRANSPORT_ERROR" && x.TryGetProperty("httpStatus", out var status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() == 200));
    }

    private static bool RawPersisted(JsonElement result, string rawDir)
    {
        var sequence = result.GetProperty("sequence").GetInt32();
        var arm = result.GetProperty("arm").GetString()!;
        var hash = result.GetProperty("rawResponseSha256").GetString();
        if (hash is null) return false;
        var path = Path.Combine(rawDir, $"{sequence:D4}-{arm}.json");
        return File.Exists(path) && Sha256File(path) == hash;
    }

    private static void ValidateV4Ga(JsonElement manifest, JsonElement requests, JsonElement sample, JsonElement firewall)
    {
        Require(manifest.GetProperty("candidateCount").GetInt32() == 7_702, "V4G_B_CANDIDATE_UNIVERSE");
        Require(manifest.GetProperty("frozenSampleCount").GetInt32() == ExpectedSample, "V4G_B_SAMPLE_MANIFEST");
        Require(manifest.GetProperty("goldReadCount").GetInt32() == 0, "V4G_B_GA_GOLD");
        Require(manifest.GetProperty("providerCalls").GetInt32() == 0, "V4G_B_GA_PROVIDER");
        Require(firewall.GetProperty("goldReadCount").GetInt32() == 0, "V4G_B_GA_FIREWALL");
        Require(requests.GetProperty("requestCount").GetInt32() == 7_702, "V4G_B_REQUEST_UNIVERSE");
        Require(sample.GetProperty("sampleCount").GetInt32() == ExpectedSample, "V4G_B_SAMPLE_COUNT");
        var rows = sample.GetProperty("requests").EnumerateArray().ToArray();
        Require(rows.Select(x => x.GetProperty("candidateId").GetString()).Distinct(StringComparer.Ordinal).Count() == ExpectedSample, "V4G_B_SAMPLE_DUPLICATE");
        foreach (var row in rows)
        {
            Require(row.GetProperty("model").GetString() == Model, "V4G_B_MODEL_DRIFT");
            Require(row.GetProperty("provider").GetString() == Provider, "V4G_B_PROVIDER_DRIFT");
            Require(row.GetProperty("configurationHash").GetString() == ConfigHash, "V4G_B_CONFIG_DRIFT");
            Require(row.GetProperty("byteLength").GetInt32() > 0, "V4G_B_REQUEST_LENGTH");
            Require(row.GetProperty("requestSha256").GetString() is { Length: 64 }, "V4G_B_REQUEST_SHA");
        }
    }

    private static string BuildReport(object baseline, int sampleCount)
    {
        var json = JsonSerializer.Serialize(baseline, JsonOptions);
        return string.Join(Environment.NewLine, new[]
        {
            "# A99 V4G-B — frozen target-grounding challenger preflight",
            "",
            "Status: **AWAITING_V4G_PROVIDER_EXECUTION_APPROVAL**",
            "",
            "This phase intentionally stopped before transport. The V4G-B task requires a new explicit operator approval; earlier V4F-E approval is not reused.",
            "",
            "## Frozen execution",
            "",
            $"- Expected HEAD: `{ExpectedHead}`.",
            $"- Target-grounded V2 sample: **{sampleCount}/128**.",
            "- Scheduled provider calls: **128**.",
            "- Actual model/provider calls: **0/0**.",
            "- Retries: **0**.",
            "- Gold reads: **0**.",
            "- Historical comparison: **true**; `temporalProviderDriftControlled=false`.",
            "- The historical OLD and PROJECTED_V1 denominators below were reconstructed from frozen V4F-E attempts, parsed results, and persisted raw bodies before any V2 transport.",
            "",
            "## Historical baseline",
            "",
            "```json",
            json,
            "```",
            "",
            "## Integrity and firewall",
            "",
            "The V4G-A sample IDs and request hashes were checked without persisting request bodies. Future transport must reconstruct each V4G request, verify candidate ID, byte length, SHA256, model, provider, and configuration before the single attempt. A mismatch must fail closed.",
            "",
            "No Gold, IR-018..022, V4E-B labels, OLD outputs, or PROJECTED_V1 outputs were used to select the sample. V4F-E artifacts were read-only inputs and V4G-A artifacts were not modified.",
            "",
            "This is not a semantic validation result. It is an execution-gated target-grounding experiment.",
            ""
        });
    }

    private static string? RejectionReason(object? parsed)
    {
        if (parsed is null) return null;
        if (parsed is JsonElement element && element.ValueKind != JsonValueKind.Object) return null;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(parsed, JsonOptions));
        return document.RootElement.TryGetProperty("validation", out var validation) && validation.TryGetProperty("rejectionReason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
    }

    private static object Distribution(IEnumerable<long> values)
    {
        var a = values.Order().ToArray();
        return new { count = a.Length, min = a.DefaultIfEmpty(0).Min(), p50 = Percentile(a, .5), p95 = Percentile(a, .95), max = a.DefaultIfEmpty(0).Max(), total = a.Sum() };
    }

    private static int Percentile(long[] values, double percentile) => values.Length == 0 ? 0 : (int)values[Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1)];

    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Git(string root, string args)
    {
        using var process = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        process.WaitForExit();
        Require(process.ExitCode == 0, "V4G_B_GIT_FAILED:" + process.StandardError.ReadToEnd());
        return process.StandardOutput.ReadToEnd().Trim();
    }
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed record BaselineArm(string arm, int scheduled, int transportSuccess, int rawAvailable, int validatorEvaluable, int targetMismatch, int valid, int otherInvalid, int providerError, int rawPersistenceFailure);
    private sealed record V2Work(int Sequence, string CandidateId, string RequestId, string RequestJson, string RequestSha256, int RequestBytes, JsonElement Request, HdsaIdentityPairVerificationRequest ValidationRequest, string DocumentId, Candidate Candidate);
    private sealed record AttemptRecord(int Sequence, string CandidateId, string RequestId, string RequestSha256, int RequestBytes, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, string Status, int? HttpStatus, string? ProviderRequestId, long LatencyMs, int? InputTokens, int? ReasoningTokens, int? OutputTokens, string? RawResponseSha256, string? ParsedResponseSha256, string? Error, bool GoldReadBeforeFreeze);
    private sealed record V2Result(int Sequence, string CandidateId, string RequestSha256, string Status, string? RawResponseSha256, string? ParsedResponseSha256, object? Parsed, RequestPacketTelemetry? Telemetry);
    private sealed record Occurrence(string NodeId, string Text, int DocumentOrder);
    private sealed record Candidate(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons);
    private sealed record SourceDoc(string DocumentId, string SourceSha256, string SourcePath);
    private sealed record PairOccurrence(string SourceOccurrenceId, string RawSurface, string CanonicalComparisonSurface, int DocumentOrder, string SourceKind, string SourceContainer);
    private sealed record LocalContext(IReadOnlyList<PairOccurrence> PreviousOfLeft, IReadOnlyList<PairOccurrence> NextOfLeft, IReadOnlyList<PairOccurrence> PreviousOfRight, IReadOnlyList<PairOccurrence> NextOfRight, IReadOnlyList<PairOccurrence> Intervening, bool InterveningTruncated, int TotalInterveningOccurrences);
    private sealed record StructuralPeer(string SourceOccurrenceId, int DocumentOrder, string SourceContainer);
    private sealed record StructuralContext(IReadOnlyList<string> SourceContainers, IReadOnlyList<StructuralPeer> StructuralPeers);
    private sealed record RelationalFacts(int SourceOrderDistance, bool NormalizedTextEqual, IReadOnlyList<string> LeftContinuationMarkers, IReadOnlyList<string> RightContinuationMarkers, IReadOnlyList<string> LeftBranchMarkers, IReadOnlyList<string> RightBranchMarkers, IReadOnlyList<string> CandidateRetrievalReasons);
    private sealed record EvidenceAvailability(string PairCore, string LocalContext, string StructuralContext, string Numbering, string Scope, string Layout, string Visual, string InterveningOccurrences);
    private sealed record EvidenceAuthority(string Authority, string Derivation, string Availability);
    private sealed record Packet(string ArtifactKind, string SchemaVersion, string CandidateId, string DocumentId, IReadOnlyList<PairOccurrence> PairCore, LocalContext LocalContext, StructuralContext StructuralContext, RelationalFacts RelationalFacts, EvidenceAvailability EvidenceAvailability, IReadOnlyList<EvidenceAuthority> EvidenceAuthorities, IReadOnlyList<string> MissingEvidence, string ProjectionVersion, string SourceCatalogFingerprint, string ProjectionConfigSha256);
}
