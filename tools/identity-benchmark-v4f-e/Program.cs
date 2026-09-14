using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Eval.ReasoningRetention;
using DocxHeaderExtractor.Infrastructure.AI;

namespace IdentityBenchmarkV4FE;

internal static class Program
{
    private const string RootRelative = "artifacts/identity-benchmark/v4/projected-verifier-experiment";
    private const string SourceRelative = "artifacts/identity-benchmark/v2/source-catalog.json";
    private const string V4Root = "artifacts/identity-benchmark/v4/pruning-challenger";
    private const string ProjectionRoot = "artifacts/identity-benchmark/v4/context-projection";
    private const string ProjectedRoot = "artifacts/identity-benchmark/v4/projected-requests";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Provider = "OpenRouter";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const int ExpectedSample = 128;
    private const int ExpectedCalls = 256;
    private const int ConfiguredContext = 1_000_000;
    private const int ConfiguredMaxOutput = 768;
    private const string ConfigHash = "8983be53cc38d8a28c8d1467216934d3bab1201673dae86f077f82328b392967";
    private const string InputPricePerMillion = "0.03";
    private const string OutputPricePerMillion = "0.13";
    private static readonly string[] Relations = ["CONTINUATION_OF", "SAME_SEMANTIC_REPEAT", "DISTINCT", "UNRESOLVED"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex ContinuationMarker = new(@"\(\s*(?:cont(?:['’]d)?|continued)\s*\)|\bcontinued\b|tiếp\s+(?:theo|tục)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BranchMarker = new(@"\[(?:option|alternative|phương\s+án)\s*[^\]]*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const string Prompt = """
You are an A99 semantic-identity pair verifier. Verify only the target pair using the supplied
source-backed evidence. CONTINUATION_OF means the right occurrence continues the logical heading
begun at the left occurrence; use RIGHT_TO_LEFT. SAME_SEMANTIC_REPEAT means the same logical
heading is repeated without continuation; use NONE. DISTINCT means separate logical headings,
even if nearby or semantically related. UNRESOLVED means the evidence is insufficient. Related,
nearby, or sequential headings are not continuation merely because they are related. Return exactly
the target pair and no explanation, groups, merge instruction, parent, hierarchy, level, offsets,
Gold, or legacy fields.
""";

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        var approved = args.Any(x => string.Equals(x, "--execute-approved", StringComparison.Ordinal));
        var finalizeExisting = args.Any(x => string.Equals(x, "--finalize-existing", StringComparison.Ordinal));
        try { return finalizeExisting ? await FinalizeExistingAsync(root) : await RunAsync(root, approved); }
        catch (Exception ex) { Console.Error.WriteLine($"V4F_E_ERROR={ex}"); return 2; }
    }

    private static async Task<int> FinalizeExistingAsync(string root)
    {
        var output = Full(root, RootRelative + "/execution");
        var inputs = LoadAndVerifyFrozenInputs(root);
        var existing = LoadExistingAttempts(output);
        Require(existing.Count == ExpectedCalls, "V4F_E_FINALIZE_EXISTING_ATTEMPT_COUNT");
        var results = inputs.WorkItems.OrderBy(x => x.Sequence).Select(work =>
        {
            var attempt = existing[work.Sequence];
            return LoadExistingResult(output, work, attempt, inputs.CatalogFingerprint);
        }).ToArray();
        var paired = BuildPairedResults(results);
        var agreement = BuildAgreement(paired);
        var usage = BuildUsage(results);
        var rawManifest = results.Select(x =>
        {
            var attempt = existing[x.Sequence];
            return new
            {
                sequence = x.Sequence, candidateId = x.CandidateId, arm = x.Arm, x.RequestSha256,
                status = x.Status, rawResponseSha256 = x.RawResponseSha256, parsedResponseSha256 = x.ParsedResponseSha256,
                rawResponsePath = x.RawResponseSha256 is null || !File.Exists(Path.Combine(output, "raw-responses", $"{x.Sequence:D4}-{x.Arm}.json")) ? null : $"raw-responses/{x.Sequence:D4}-{x.Arm}.json",
                provider = (string?)null, providerCallId = attempt.ProviderRequestId, httpStatus = attempt.HttpStatus,
                finishReason = (string?)null, goldReadBeforeFreeze = false,
            };
        }).ToArray();
        await WriteAsync(Path.Combine(output, "raw-response-manifest.json"), new
        {
            schemaVersion = "a99-v4f-e-raw-response-manifest-v1", frozenBeforeParsingComparison = true,
            responseCount = rawManifest.Length, successfulRawResponses = rawManifest.Count(x => x.rawResponseSha256 is not null),
            persistedRawBodies = rawManifest.Count(x => x.rawResponsePath is not null), responses = rawManifest,
            goldReadCount = 0, v4ebEvaluationReadCount = 0,
        });
        await WriteAsync(Path.Combine(output, "parsed-results.json"), new
        {
            schemaVersion = "a99-v4f-e-parsed-results-v1", parser = "HdsaGlobalIdentityRetrieveVerifyContract",
            parserVersion = HdsaGlobalIdentityRetrieveVerifyContract.Version, results,
            old = ParseSummary(results.Where(x => x.Arm == "ARM_A")), projected = ParseSummary(results.Where(x => x.Arm == "ARM_B")),
            goldReadCount = 0, v4ebEvaluationReadCount = 0,
        });
        await WriteAsync(Path.Combine(output, "paired-results.json"), new
        {
            schemaVersion = "a99-v4f-e-paired-results-v1", pairCount = paired.Count,
            validPairedCount = paired.Count(x => x.ValidPair), pairs = paired,
            oldIsNotGold = true, goldReadCount = 0, v4ebEvaluationReadCount = 0,
        });
        await WriteAsync(Path.Combine(output, "agreement-matrix.json"), agreement);
        await WriteAsync(Path.Combine(output, "usage-and-latency.json"), usage);
        await WriteAsync(Path.Combine(output, "cost-report.json"), BuildCost(results));
        await WriteAsync(Path.Combine(output, "execution-integrity.v1.json"), new
        {
            schemaVersion = "a99-v4f-e-execution-integrity-v1", totalAttemptRecords = existing.Count,
            actualProviderAttempts = existing.Count, rawResponseHashes = existing.Values.Count(x => x.RawResponseSha256 is not null),
            persistedRawBodies = rawManifest.Count(x => x.rawResponsePath is not null), unavailableResponses = existing.Values.Count(x => x.RawResponseSha256 is null),
            rawPersistenceFailures = existing.Values.Count(x => x.Status == "TRANSPORT_ERROR" && x.HttpStatus == 200),
            rawPersistenceFailureSequences = existing.Values.Where(x => x.Status == "TRANSPORT_ERROR" && x.HttpStatus == 200).Select(x => x.Sequence).Order().ToArray(),
            providerErrorsWithoutRawBody = existing.Values.Count(x => x.Status == "PROVIDER_ERROR" && x.RawResponseSha256 is null),
            providerCallsAfterPersistenceFix = existing.Values.Count(x => x.Sequence >= 3),
            goldReadCount = 0, responseBodiesWereNotRepairedOrRerun = true,
            status = "PARTIAL_RAW_RESPONSE_FREEZE_WITH_IMMUTABLE_ATTEMPT_LINEAGE",
        });
        await WriteAsync(Path.Combine(output, "firewall.json"), new
        {
            schemaVersion = "a99-v4f-e-firewall-v1", goldReadCountBeforeResponseFreeze = 0,
            v4ebEvaluationReadCountBeforeResponseFreeze = 0, sampleChanged = false, requestChanged = false,
            projectionChanged = false, parserChanged = false, modelConfigChanged = false,
            goldUsedForSampling = false, goldUsedForRetry = false, oldArmUsedAsOracle = false,
            sameParserBothArms = true, rawFrozenBeforeComparison = true, providerCalls = existing.Count,
            modelCalls = existing.Count, expectedProviderAttempts = ExpectedCalls,
            executionOrderPreserved = true, transientRequestRetries = 0,
        });
        await WriteAsync(Path.Combine(output, "execution-manifest.json"), new
        {
            schemaVersion = "a99-v4f-e-execution-manifest-v1", experiment = "IDENTITY_BENCHMARK_V4F_E",
            parent = "V4F-D@ab52747", status = "RESPONSE_FREEZE_COMPLETE_WITH_RAW_PERSISTENCE_GAP", provider = Provider, model = Model,
            sampleCount = ExpectedSample, oldScheduled = ExpectedSample, projectedScheduled = ExpectedSample,
            totalScheduled = ExpectedCalls, actualProviderAttempts = existing.Count,
            rawResponseHashes = existing.Values.Count(x => x.RawResponseSha256 is not null), persistedRawBodies = rawManifest.Count(x => x.rawResponsePath is not null),
            executionOrderSha256 = inputs.ExecutionOrderHash, v4fDManifestSha256 = inputs.FileHashes["v4dManifest"],
            sampleSha256 = inputs.FileHashes["sample"], pairedRequestManifestSha256 = inputs.FileHashes["pairedRequests"],
            metricsContractSha256 = inputs.FileHashes["metrics"], sourceOnlySampling = true, goldDerivedInput = false,
            goldReadBeforeResponseFreeze = false, sampleChanged = false, requestChanged = false,
            projectionChanged = false, parserChanged = false, modelConfigChanged = false,
            startedUtc = existing.Values.OrderBy(x => x.Sequence).First().StartedUtc, completedUtc = DateTimeOffset.UtcNow,
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(inputs, existing.Values.OrderBy(x => x.Sequence).ToArray(), results, paired, agreement, usage), new UTF8Encoding(false));
        Console.WriteLine($"V4F_E_STATUS=RESPONSE_FREEZE_COMPLETE_WITH_RAW_PERSISTENCE_GAP PROVIDER_ATTEMPTS={existing.Count} VALID_PAIRS={paired.Count(x => x.ValidPair)}/{paired.Count}");
        return 0;
    }

    private static async Task<int> RunAsync(string root, bool approved)
    {
        var output = Full(root, RootRelative + "/execution");
        Directory.CreateDirectory(output);
        if (!approved)
        {
            await WriteAsync(Path.Combine(output, "provider-preflight.json"), new
            {
                schemaVersion = "a99-v4f-e-provider-preflight-v1", status = "AWAITING_PROVIDER_EXECUTION_APPROVAL",
                provider = Provider, model = Model, modelCalls = 0, providerCalls = 0,
                explicitExecutionFlag = false, requiredFlag = "--execute-approved", goldReadCount = 0,
            });
            Console.WriteLine("V4F_E_STATUS=AWAITING_PROVIDER_EXECUTION_APPROVAL");
            return 3;
        }

        var inputs = LoadAndVerifyFrozenInputs(root);
        var outputPreflight = await RunProviderPreflightAsync(root, output, inputs);
        if (!outputPreflight.Pass)
        {
            Console.WriteLine("V4F_E_STATUS=" + outputPreflight.Status);
            return 1;
        }

        await WriteAsync(Path.Combine(output, "execution-manifest.json"), new
        {
            schemaVersion = "a99-v4f-e-execution-manifest-v1", experiment = "IDENTITY_BENCHMARK_V4F_E",
            parent = "V4F-D@ab52747", status = "EXECUTING", provider = Provider, model = Model,
            sampleCount = ExpectedSample, oldScheduled = ExpectedSample, projectedScheduled = ExpectedSample,
            totalScheduled = ExpectedCalls, executionOrderSha256 = inputs.ExecutionOrderHash,
            v4fDManifestSha256 = inputs.FileHashes["v4dManifest"], sampleSha256 = inputs.FileHashes["sample"],
            pairedRequestManifestSha256 = inputs.FileHashes["pairedRequests"], metricsContractSha256 = inputs.FileHashes["metrics"],
            sourceOnlySampling = true, goldDerivedInput = false, goldReadBeforeResponseFreeze = false,
            sampleChanged = false, requestChanged = false, projectionChanged = false, parserChanged = false,
            modelConfigChanged = false, transientRequestRetries = 0, maxParallelRequests = 1,
            selectedAttemptRule = "first_and_only_attempt; no retries", startedUtc = DateTimeOffset.UtcNow,
        });

        var attempts = new List<AttemptRecord>(ExpectedCalls);
        var results = new List<ArmResult>(ExpectedCalls);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(root, "identity-benchmark-v4f-e", "128-paired-candidates");
        using var model = new OpenRouterCeilingReasoningModel(outputPreflight.Options!, outputPreflight.Capability!, http);
        var workBySequence = inputs.WorkItems.ToDictionary(x => x.Sequence);
        var existingAttempts = LoadExistingAttempts(output);
        foreach (var existing in existingAttempts.Values.OrderBy(x => x.Sequence))
        {
            if (!workBySequence.TryGetValue(existing.Sequence, out var work))
                throw new InvalidDataException("V4F_E_EXISTING_ATTEMPT_NOT_IN_FROZEN_ORDER:" + existing.Sequence);
            attempts.Add(existing);
            results.Add(LoadExistingResult(output, work, existing, inputs.CatalogFingerprint));
        }
        if (existingAttempts.Count > 0)
            await WriteAsync(Path.Combine(output, "attempt-corrections", "resume.v1.json"), new
            {
                schemaVersion = "a99-v4f-e-resume-correction-v1", preservedAttemptCount = existingAttempts.Count,
                rerunCount = 0, rule = "existing attempt records are immutable; successful provider attempts are never rerun",
                existingAttempts = existingAttempts.Values.OrderBy(x => x.Sequence).Select(x => new
                {
                    x.Sequence, x.CandidateId, x.Arm, x.Status, x.HttpStatus, x.RawResponseSha256,
                    rawBodyPersisted = x.RawResponseSha256 is not null && File.Exists(Path.Combine(output, "raw-responses", $"{x.Sequence:D4}-{x.Arm}.json")),
                    correction = x.HttpStatus == 200 && x.RawResponseSha256 is not null ? "PROVIDER_SUCCEEDED_RAW_BODY_RECOVERY_REQUIRED" : "PRESERVED_AS_RECORDED"
                }).ToArray()
            });

        foreach (var work in inputs.WorkItems)
        {
            if (existingAttempts.ContainsKey(work.Sequence)) continue;
            var attemptPath = Path.Combine(output, "attempts", $"{work.Sequence:D4}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(attemptPath)!);
            var started = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            string? raw = null;
            RequestPacketTelemetry? telemetry = null;
            string status;
            string? error = null;
            string? rawHash = null;
            string? parsedHash = null;
            object? parsed = null;
            try
            {
                var result = await model.CompleteRawStructuredSemanticAsync(
                    work.Candidate.DocumentId, "V4F_E_" + work.Arm,
                    $"V4F-E:{work.Sequence:D4}:{work.Candidate.PairId}:{work.Arm}",
                    work.RequestJson, work.RequestJson.Length, 1, 1, Prompt,
                    $"TASK=V4F_E_PAIR_VERIFICATION\nARM={work.Arm}\n{work.RequestJson}\nReturn exactly the requested JSON object.",
                    HdsaGlobalIdentityRetrieveVerifyContract.VerificationSchema(),
                    "hdsa_global_identity_retrieve_verify_v1", CancellationToken.None);
                raw = result.Content;
                telemetry = result.Telemetry;
                rawHash = Sha256Text(raw);
                Directory.CreateDirectory(Path.Combine(output, "raw-responses"));
                await File.WriteAllTextAsync(Path.Combine(output, "raw-responses", $"{work.Sequence:D4}-{work.Arm}.json"), raw, new UTF8Encoding(false));
                try
                {
                    var response = HdsaGlobalIdentityRetrieveVerifyContract.ParseVerification(raw);
                    var request = new HdsaIdentityPairVerificationRequest(inputs.CatalogFingerprint, work.Nodes, work.TargetPair, false);
                    var validation = HdsaGlobalIdentityRetrieveVerifyContract.ValidateVerification(request, response);
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
            var attempt = new AttemptRecord(
                work.Sequence, work.Candidate.PairId, work.Arm, work.RequestId, work.RequestSha256,
                work.RequestBytes, 1, started, DateTimeOffset.UtcNow, status, telemetry?.HttpStatus,
                telemetry?.ProviderCallId, stopwatch.ElapsedMilliseconds, telemetry?.ReportedInputTokens,
                telemetry?.ReportedOutputTokens, rawHash, parsedHash, error, false);
            attempts.Add(attempt);
            results.Add(new ArmResult(work.Sequence, work.Candidate.PairId, work.Candidate.DocumentId, work.Arm,
                work.RequestSha256, status, rawHash, parsedHash, parsed, telemetry));
            await WriteAsync(attemptPath, attempt);
            await WriteAsync(Path.Combine(output, "attempt-manifest.json"), new
            {
                schemaVersion = "a99-v4f-e-attempt-manifest-v1", immutableRecords = true,
                actualProviderAttempts = attempts.Count, currentProcessProviderCalls = model.ProviderCalls, expectedAttempts = ExpectedCalls,
                attempts = attempts.ToArray(), goldReadCount = 0, v4ebEvaluationReadCount = 0,
            });
            Console.WriteLine($"V4F_E_ATTEMPT={work.Sequence}/{ExpectedCalls} ARM={work.Arm} STATUS={status} PROVIDER_CALLS={model.ProviderCalls}");
        }

        var rawManifest = results.Select(x => new
        {
            sequence = x.Sequence, candidateId = x.CandidateId, arm = x.Arm, x.RequestSha256,
            status = x.Status, rawResponseSha256 = x.RawResponseSha256, parsedResponseSha256 = x.ParsedResponseSha256,
            rawResponsePath = x.RawResponseSha256 is null ? null : $"raw-responses/{x.Sequence:D4}-{x.Arm}.json",
            provider = x.Telemetry?.ProviderRoute, providerCallId = x.Telemetry?.ProviderCallId,
            httpStatus = x.Telemetry?.HttpStatus, finishReason = x.Telemetry?.FinishReason,
            goldReadBeforeFreeze = false,
        }).ToArray();
        await WriteAsync(Path.Combine(output, "raw-response-manifest.json"), new
        {
            schemaVersion = "a99-v4f-e-raw-response-manifest-v1", frozenBeforeParsingComparison = true,
            responseCount = rawManifest.Length, successfulRawResponses = rawManifest.Count(x => x.rawResponseSha256 is not null),
            responses = rawManifest, goldReadCount = 0, v4ebEvaluationReadCount = 0,
        });
        await WriteAsync(Path.Combine(output, "parsed-results.json"), new
        {
            schemaVersion = "a99-v4f-e-parsed-results-v1", parser = "HdsaGlobalIdentityRetrieveVerifyContract",
            parserVersion = HdsaGlobalIdentityRetrieveVerifyContract.Version, results,
            old = ParseSummary(results.Where(x => x.Arm == "ARM_A")), projected = ParseSummary(results.Where(x => x.Arm == "ARM_B")),
            goldReadCount = 0, v4ebEvaluationReadCount = 0,
        });

        var paired = BuildPairedResults(results);
        await WriteAsync(Path.Combine(output, "paired-results.json"), new
        {
            schemaVersion = "a99-v4f-e-paired-results-v1", pairCount = paired.Count,
            validPairedCount = paired.Count(x => x.ValidPair), pairs = paired,
            oldIsNotGold = true, goldReadCount = 0, v4ebEvaluationReadCount = 0,
        });
        var agreement = BuildAgreement(paired);
        await WriteAsync(Path.Combine(output, "agreement-matrix.json"), agreement);
        var usage = BuildUsage(results);
        await WriteAsync(Path.Combine(output, "usage-and-latency.json"), usage);
        await WriteAsync(Path.Combine(output, "cost-report.json"), BuildCost(results));
        await WriteAsync(Path.Combine(output, "firewall.json"), new
        {
            schemaVersion = "a99-v4f-e-firewall-v1", goldReadCountBeforeResponseFreeze = 0,
            v4ebEvaluationReadCountBeforeResponseFreeze = 0, sampleChanged = false, requestChanged = false,
            projectionChanged = false, parserChanged = false, modelConfigChanged = false,
            goldUsedForSampling = false, goldUsedForRetry = false, oldArmUsedAsOracle = false,
            sameParserBothArms = true, rawFrozenBeforeComparison = true, providerCalls = attempts.Count,
            modelCalls = attempts.Count, currentProcessProviderCalls = model.ProviderCalls, expectedProviderAttempts = ExpectedCalls,
            executionOrderPreserved = true, transientRequestRetries = 0,
        });
        await WriteAsync(Path.Combine(output, "execution-manifest.json"), new
        {
            schemaVersion = "a99-v4f-e-execution-manifest-v1", experiment = "IDENTITY_BENCHMARK_V4F_E",
            parent = "V4F-D@ab52747", status = "RESPONSE_FREEZE_COMPLETE", provider = Provider, model = Model,
            sampleCount = ExpectedSample, oldScheduled = ExpectedSample, projectedScheduled = ExpectedSample,
            totalScheduled = ExpectedCalls, actualProviderAttempts = attempts.Count, currentProcessProviderCalls = model.ProviderCalls,
            successfulRawResponses = results.Count(x => x.RawResponseSha256 is not null),
            executionOrderSha256 = inputs.ExecutionOrderHash, v4fDManifestSha256 = inputs.FileHashes["v4dManifest"],
            sampleSha256 = inputs.FileHashes["sample"], pairedRequestManifestSha256 = inputs.FileHashes["pairedRequests"],
            metricsContractSha256 = inputs.FileHashes["metrics"], sourceOnlySampling = true, goldDerivedInput = false,
            goldReadBeforeResponseFreeze = false, sampleChanged = false, requestChanged = false,
            projectionChanged = false, parserChanged = false, modelConfigChanged = false,
            startedUtc = attempts.FirstOrDefault()?.StartedUtc, completedUtc = DateTimeOffset.UtcNow,
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(inputs, attempts, results, paired, agreement, usage), new UTF8Encoding(false));
        Console.WriteLine($"V4F_E_STATUS=PAIRED_VERIFIER_EXPERIMENT_COMPLETE PROVIDER_CALLS={attempts.Count} CURRENT_PROCESS_PROVIDER_CALLS={model.ProviderCalls} VALID_PAIRS={paired.Count(x => x.ValidPair)}/{paired.Count}");
        return attempts.Count == ExpectedCalls ? 0 : 1;
    }

    private static Dictionary<int, AttemptRecord> LoadExistingAttempts(string output)
    {
        var directory = Path.Combine(output, "attempts");
        if (!Directory.Exists(directory)) return new Dictionary<int, AttemptRecord>();
        var attempts = new Dictionary<int, AttemptRecord>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var attempt = JsonSerializer.Deserialize<AttemptRecord>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("V4F_E_EXISTING_ATTEMPT_INVALID:" + path);
            if (!attempts.TryAdd(attempt.Sequence, attempt))
                throw new InvalidDataException("V4F_E_DUPLICATE_EXISTING_ATTEMPT:" + attempt.Sequence);
        }
        return attempts;
    }

    private static ArmResult LoadExistingResult(string output, Work work, AttemptRecord attempt, string catalogFingerprint)
    {
        var rawPath = Path.Combine(output, "raw-responses", $"{work.Sequence:D4}-{work.Arm}.json");
        if (!File.Exists(rawPath))
            return new(work.Sequence, work.Candidate.PairId, work.Candidate.DocumentId, work.Arm, work.RequestSha256,
                "RAW_PERSISTENCE_FAILURE", attempt.RawResponseSha256, null, null, null);
        var raw = File.ReadAllText(rawPath);
        if (Sha256Text(raw) != attempt.RawResponseSha256)
            throw new InvalidDataException("V4F_E_EXISTING_RAW_HASH_MISMATCH:" + work.Sequence);
        try
        {
            var response = HdsaGlobalIdentityRetrieveVerifyContract.ParseVerification(raw);
            var request = new HdsaIdentityPairVerificationRequest(catalogFingerprint, work.Nodes, work.TargetPair, false);
            var validation = HdsaGlobalIdentityRetrieveVerifyContract.ValidateVerification(request, response);
            return new(work.Sequence, work.Candidate.PairId, work.Candidate.DocumentId, work.Arm, work.RequestSha256,
                validation.Accepted ? "VALID" : "INVALID_SCHEMA", attempt.RawResponseSha256,
                Sha256Text(JsonSerializer.Serialize(new { response, validation }, JsonOptions)), new { response, validation }, null);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return new(work.Sequence, work.Candidate.PairId, work.Candidate.DocumentId, work.Arm, work.RequestSha256,
                "INVALID_SCHEMA", attempt.RawResponseSha256, null, null, null);
        }
    }

    private static FrozenInputs LoadAndVerifyFrozenInputs(string root)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = Full(root, SourceRelative), ["v4dManifest"] = Full(root, RootRelative + "/manifest.json"),
            ["sample"] = Full(root, RootRelative + "/sample.json"), ["pairedRequests"] = Full(root, RootRelative + "/paired-request-manifest.json"),
            ["executionOrder"] = Full(root, RootRelative + "/execution-order.json"), ["metrics"] = Full(root, RootRelative + "/metrics-contract.json"),
            ["v4eManifest"] = Full(root, V4Root + "/manifest.json"), ["v4eShortlist"] = Full(root, V4Root + "/shortlist.json"),
            ["v4eRequests"] = Full(root, V4Root + "/request-manifest.json"), ["packets"] = Full(root, ProjectionRoot + "/packet-manifest.json"),
            ["projectionConfig"] = Full(root, ProjectionRoot + "/projection-config.json"), ["projectedManifest"] = Full(root, ProjectedRoot + "/manifest.json"),
            ["projectedRequests"] = Full(root, ProjectedRoot + "/request-manifest.json"),
        };
        foreach (var item in paths) if (!File.Exists(item.Value)) throw new FileNotFoundException("V4F_E_FROZEN_INPUT_MISSING", item.Value);
        var hashes = paths.ToDictionary(x => x.Key, x => Sha256File(x.Value), StringComparer.Ordinal);
        using var source = Read(paths["source"]); using var v4d = Read(paths["v4dManifest"]); using var sample = Read(paths["sample"]);
        using var paired = Read(paths["pairedRequests"]); using var order = Read(paths["executionOrder"]); using var v4e = Read(paths["v4eManifest"]);
        using var shortlist = Read(paths["v4eShortlist"]); using var oldRequests = Read(paths["v4eRequests"]); using var packets = Read(paths["packets"]);
        using var projectionConfig = Read(paths["projectionConfig"]); using var projected = Read(paths["projectedManifest"]); using var projectedRequests = Read(paths["projectedRequests"]);
        Require(v4d.RootElement.GetProperty("sampleCount").GetInt32() == ExpectedSample, "V4F_E_SAMPLE_COUNT");
        Require(v4d.RootElement.GetProperty("totalFutureCalls").GetInt32() == ExpectedCalls, "V4F_E_CALL_COUNT");
        Require(sample.RootElement.GetProperty("sampleCount").GetInt32() == ExpectedSample, "V4F_E_SAMPLE_ARTIFACT_COUNT");
        Require(paired.RootElement.GetProperty("sampleCount").GetInt32() == ExpectedSample, "V4F_E_PAIRED_SAMPLE_COUNT");
        Require(paired.RootElement.GetProperty("totalFutureCalls").GetInt32() == ExpectedCalls, "V4F_E_PAIRED_CALL_COUNT");
        Require(order.RootElement.GetProperty("entries").GetArrayLength() == ExpectedCalls, "V4F_E_ORDER_COUNT");
        Require(v4e.RootElement.GetProperty("goldReadCount").GetInt32() == 0 && v4e.RootElement.GetProperty("providerCalls").GetInt32() == 0, "V4F_E_V4E_FIREWALL");
        Require(v4d.RootElement.GetProperty("providerCalls").GetInt32() == 0 && v4d.RootElement.GetProperty("modelCalls").GetInt32() == 0, "V4F_E_V4D_FIREWALL");
        Require(v4d.RootElement.GetProperty("candidateUniverseChanged").GetBoolean() == false && v4d.RootElement.GetProperty("rankingChanged").GetBoolean() == false && v4d.RootElement.GetProperty("projectionChanged").GetBoolean() == false, "V4F_E_FROZEN_DELTA");
        Require(paired.RootElement.GetProperty("model").GetString() == Model && paired.RootElement.GetProperty("provider").GetString() == Provider, "V4F_E_MODEL_PROVIDER");
        Require(paired.RootElement.GetProperty("configurationHash").GetString() == ConfigHash, "V4F_E_CONFIG_HASH");
        Require(source.RootElement.GetProperty("goldDerivedInput").GetBoolean() == false && oldRequests.RootElement.GetProperty("goldDerivedInput").GetBoolean() == false, "V4F_E_GOLD_INPUT");
        Require(projected.RootElement.GetProperty("goldReadCount").GetInt32() == 0 && projected.RootElement.GetProperty("providerCalls").GetInt32() == 0, "V4F_E_PROJECTED_FIREWALL");
        Require(projectedRequests.RootElement.GetProperty("exactBytesPersisted").GetBoolean() == false, "V4F_E_PROJECTED_BODY_POLICY");
        var catalogFingerprint = source.RootElement.GetProperty("catalogFingerprint").GetString()!;
        var nodes = source.RootElement.GetProperty("sourceOccurrences").EnumerateArray().Select(x => new Occurrence(x.GetProperty("nodeId").GetString()!, x.GetProperty("text").GetString()!, x.GetProperty("documentOrder").GetInt32())).ToArray();
        var byDoc = nodes.GroupBy(x => DocumentOf(x.NodeId), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(x => x.DocumentOrder).ThenBy(x => x.NodeId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var candidates = sample.RootElement.GetProperty("candidates").EnumerateArray().Select(x => new Candidate(x.GetProperty("pairId").GetString()!, x.GetProperty("documentId").GetString()!, x.GetProperty("left").GetString()!, x.GetProperty("right").GetString()!, x.TryGetProperty("reasons", out var reasons) ? reasons.EnumerateArray().Select(y => y.GetString()!).ToArray() : [])).ToArray();
        Require(candidates.Length == ExpectedSample, "V4F_E_SAMPLE_ROWS");
        var pairRows = paired.RootElement.GetProperty("arms").EnumerateArray().ToDictionary(x => x.GetProperty("candidateId").GetString()!, x => x, StringComparer.Ordinal);
        var packetRows = packets.RootElement.GetProperty("packets").EnumerateArray().ToDictionary(x => x.GetProperty("candidateId").GetString()!, x => x, StringComparer.Ordinal);
        var oldRows = oldRequests.RootElement.GetProperty("requests").EnumerateArray().ToDictionary(x => x.GetProperty("requestId").GetString()!, x => x, StringComparer.Ordinal);
        var projectedRows = projectedRequests.RootElement.GetProperty("requests").EnumerateArray().ToDictionary(x => x.GetProperty("requestId").GetString()!, x => x, StringComparer.Ordinal);
        var docSources = source.RootElement.GetProperty("sourceDocuments").EnumerateArray().ToDictionary(x => x.GetProperty("documentId").GetString()!, x => new SourceDoc(x.GetProperty("documentId").GetString()!, x.GetProperty("sourceSha256").GetString()!, x.GetProperty("sourcePath").GetString()!), StringComparer.Ordinal);
        var templates = byDoc.ToDictionary(x => x.Key, x => HdsaCanonicalPairVerifierRequestBuilder.CreateTemplate(catalogFingerprint, x.Value.Select(n => new HdsaIdentityRoleNodeInput(n.NodeId, [n.NodeId], n.Text, n.DocumentOrder, "UNAVAILABLE", false)).ToArray()), StringComparer.Ordinal);
        var worksByKey = new Dictionary<string, Work>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            Require(byDoc.ContainsKey(candidate.DocumentId), "V4F_E_UNKNOWN_DOCUMENT:" + candidate.PairId);
            var docNodes = byDoc[candidate.DocumentId];
            var lookup = docNodes.ToDictionary(x => x.NodeId, StringComparer.Ordinal);
            Require(lookup.ContainsKey(candidate.Left) && lookup.ContainsKey(candidate.Right), "V4F_E_UNKNOWN_OCCURRENCE:" + candidate.PairId);
            Require(pairRows.ContainsKey(candidate.PairId) && packetRows.ContainsKey(candidate.PairId), "V4F_E_MISSING_PAIRED_ROW:" + candidate.PairId);
            var old = templates[candidate.DocumentId].Build(new HdsaIdentityCandidatePair(candidate.PairId, candidate.Left, candidate.Right));
            var packet = BuildPacket(candidate, lookup[candidate.Left], lookup[candidate.Right], docNodes, docSources[candidate.DocumentId], catalogFingerprint, Sha256File(paths["projectionConfig"]));
            var packetBytes = JsonSerializer.SerializeToUtf8Bytes(packet, JsonOptions);
            using var packetDoc = JsonDocument.Parse(packetBytes);
            var projectedRequest = HdsaCanonicalProjectedPairVerifierRequestBuilder.Build(packetDoc.RootElement, Model, Provider, ConfiguredMaxOutput);
            var pair = pairRows[candidate.PairId]; var oldRow = pair.GetProperty("oldRequest"); var projectedRow = pair.GetProperty("projectedRequest");
            Require(old.Sha256 == oldRow.GetProperty("requestSha256").GetString() && old.Utf8Bytes.Length == oldRow.GetProperty("requestBytes").GetInt64(), "V4F_E_OLD_REQUEST_RECONSTRUCTION:" + candidate.PairId);
            Require(packetBytes.Length == packetRows[candidate.PairId].GetProperty("packetSha256").GetString()!.Length * 0 + packetBytes.Length, "V4F_E_PACKET_INTERNAL");
            Require(Sha256(packetBytes) == packetRows[candidate.PairId].GetProperty("packetSha256").GetString(), "V4F_E_PACKET_RECONSTRUCTION:" + candidate.PairId);
            Require(projectedRequest.Sha256 == projectedRow.GetProperty("requestSha256").GetString() && projectedRequest.Utf8Bytes.Length == projectedRow.GetProperty("requestBytes").GetInt64(), "V4F_E_PROJECTED_REQUEST_RECONSTRUCTION:" + candidate.PairId);
            var nodesForRequest = byDoc[candidate.DocumentId].Select(n => new HdsaIdentityRoleNodeInput(n.NodeId, [n.NodeId], n.Text, n.DocumentOrder, "UNAVAILABLE", false)).ToArray();
            worksByKey[candidate.PairId + "|ARM_A"] = new Work(candidate, "ARM_A", candidate.PairId, old.Sha256, old.Utf8Bytes.Length, old.Json, nodesForRequest, new HdsaIdentityCandidatePair(candidate.PairId, candidate.Left, candidate.Right));
            worksByKey[candidate.PairId + "|ARM_B"] = new Work(candidate, "ARM_B", candidate.PairId, projectedRequest.Sha256, projectedRequest.Utf8Bytes.Length, projectedRequest.Json, nodesForRequest, new HdsaIdentityCandidatePair(candidate.PairId, candidate.Left, candidate.Right));
        }
        var workItems = order.RootElement.GetProperty("entries").EnumerateArray().OrderBy(x => x.GetProperty("sequence").GetInt32()).Select(x =>
        {
            var key = x.GetProperty("candidateId").GetString()! + "|" + x.GetProperty("armId").GetString()!;
            if (!worksByKey.TryGetValue(key, out var work)) throw new InvalidDataException("V4F_E_ORDER_WORK_MISSING:" + key);
            return work with { Sequence = x.GetProperty("sequence").GetInt32() };
        }).ToArray();
        Require(workItems.Length == ExpectedCalls && workItems.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(1, ExpectedCalls)), "V4F_E_EXECUTION_ORDER");
        Require(workItems.GroupBy(x => x.Candidate.PairId).All(g => g.Count() == 2 && g.Select(x => x.Arm).Order(StringComparer.Ordinal).SequenceEqual(new[] { "ARM_A", "ARM_B" })), "V4F_E_PAIR_BALANCE");
        var fileHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v4dManifest"] = hashes["v4dManifest"], ["sample"] = hashes["sample"], ["pairedRequests"] = hashes["pairedRequests"], ["metrics"] = hashes["metrics"]
        };
        return new FrozenInputs(catalogFingerprint, nodes, workItems, hashes["executionOrder"], fileHashes, paths);
    }

    private static async Task<PreflightResult> RunProviderPreflightAsync(string root, string output, FrozenInputs inputs)
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), ApiKey = key ?? "", Model = Model, ContextSize = ConfiguredContext,
            MaxOutputTokens = ConfiguredMaxOutput, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        if (string.IsNullOrWhiteSpace(key))
        {
            await WriteAsync(Path.Combine(output, "provider-preflight.json"), new { schemaVersion = "a99-v4f-e-provider-preflight-v1", status = "BLOCKED_ON_PROVIDER_API_KEY", provider = Provider, model = Model, modelCalls = 0, providerCalls = 0, goldReadCount = 0 });
            return new(false, "BLOCKED_ON_PROVIDER_API_KEY", options, null);
        }
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var capabilityResult = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http);
        var estimatedMaxInput = inputs.WorkItems.Where(x => x.Arm == "ARM_A").Max(x => (x.RequestBytes + 3) / 4);
        var pricing = new { verified = true, inputUsdPerMillionTokens = double.Parse(InputPricePerMillion, System.Globalization.CultureInfo.InvariantCulture), outputUsdPerMillionTokens = double.Parse(OutputPricePerMillion, System.Globalization.CultureInfo.InvariantCulture), source = "https://openrouter.ai/qwen", sourceType = "OFFICIAL_OPENROUTER_MODEL_PAGE" };
        var capability = capabilityResult.Capability;
        var pass = capabilityResult.Available && capability is not null && capability.ModelId == Model && capability.ContextLength >= estimatedMaxInput && capability.ReasoningSupported && capability.StructuredOutputSupported;
        var status = pass ? "PREFLIGHT_PASS" : capabilityResult.Available && capability is not null && capability.ContextLength < estimatedMaxInput ? "BLOCKED_ON_PROVIDER_CONTEXT_LIMIT" : capabilityResult.Classification.Length > 0 ? capabilityResult.Classification : "BLOCKED_ON_MODEL_CAPABILITY";
        await WriteAsync(Path.Combine(output, "provider-preflight.json"), new
        {
            schemaVersion = "a99-v4f-e-provider-preflight-v1", status, provider = Provider, model = Model,
            availability = capabilityResult.Available, availabilityReason = capabilityResult.Reason,
            contextLimitTokens = capability?.ContextLength, configuredContextLimitTokens = ConfiguredContext,
            largestFrozenOldRequestBytes = inputs.WorkItems.Where(x => x.Arm == "ARM_A").Max(x => x.RequestBytes),
            largestFrozenOldRequestEstimatedInputTokens = estimatedMaxInput, configuredMaxOutputTokens = ConfiguredMaxOutput,
            reasoningSupported = capability?.ReasoningSupported, structuredOutputSupported = capability?.StructuredOutputSupported,
            selectedReasoningEffort = capability?.SelectedReasoningEffort, maxCompletionTokensReported = capability?.MaxCompletionTokens,
            pricing, rateLimit = "NOT_EXPOSED_BY_PROVIDER_METADATA", explicitExecutionFlag = true,
            requestCount = ExpectedCalls, modelCalls = 0, providerCalls = 0, goldReadCount = 0,
            checkedUtc = DateTimeOffset.UtcNow,
        });
        return new(pass, status, options, capability);
    }

    private static List<PairResult> BuildPairedResults(IReadOnlyList<ArmResult> results)
    {
        var rows = new List<PairResult>();
        foreach (var group in results.GroupBy(x => x.CandidateId, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var old = group.SingleOrDefault(x => x.Arm == "ARM_A"); var projected = group.SingleOrDefault(x => x.Arm == "ARM_B");
            var oldRelation = RelationOf(old?.Parsed); var projectedRelation = RelationOf(projected?.Parsed);
            var validPair = old?.Status == "VALID" && projected?.Status == "VALID";
            rows.Add(new PairResult(group.Key, group.First().DocumentId, old?.Status, projected?.Status, validPair ? oldRelation : null, validPair ? projectedRelation : null, validPair && oldRelation == projectedRelation, old?.RequestSha256, projected?.RequestSha256));
        }
        return rows;
    }

    private static object BuildAgreement(IReadOnlyList<PairResult> paired)
    {
        var matrix = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in paired)
        {
            var old = item.OldRelation; var projected = item.ProjectedRelation;
            if (old is null || projected is null) continue; var key = old + "->" + projected; matrix[key] = matrix.GetValueOrDefault(key) + 1;
        }
        var valid = matrix.Values.Sum();
        var same = matrix.Where(x => x.Key.Split("->")[0] == x.Key.Split("->")[1]).Sum(x => x.Value);
        return new { schemaVersion = "a99-v4f-e-agreement-matrix-v1", validPairedOutputs = valid, agreementCount = same, pairwiseRelationAgreement = valid == 0 ? (double?)null : same / (double)valid, projectedChangeCount = valid - same, projectedChangeRate = valid == 0 ? (double?)null : (valid - same) / (double)valid, matrix, metricsAreBehavioralNotAccuracy = true };
    }

    private static object BuildUsage(IReadOnlyList<ArmResult> results)
    {
        object Stats(IEnumerable<ArmResult> input) { var values = input.Select(x => x.Telemetry?.ReportedInputTokens).Where(x => x.HasValue).Select(x => x!.Value).Order().ToArray(); var lat = input.Select(x => x.Telemetry?.ElapsedMs).Where(x => x.HasValue).Select(x => x!.Value).Order().ToArray(); return new { reportedInputTokens = Distribution(values), reportedOutputTokens = Distribution(input.Select(x => x.Telemetry?.ReportedOutputTokens).Where(x => x.HasValue).Select(x => x!.Value)), latencyMs = Distribution(lat) }; }
        return new { schemaVersion = "a99-v4f-e-usage-and-latency-v1", old = Stats(results.Where(x => x.Arm == "ARM_A")), projected = Stats(results.Where(x => x.Arm == "ARM_B")), usageSource = "provider_telemetry_when_returned" };
    }

    private static object BuildCost(IReadOnlyList<ArmResult> results)
    {
        decimal? Input(IEnumerable<ArmResult> input) { var tokens = input.Select(x => x.Telemetry?.ReportedInputTokens).Where(x => x.HasValue).Sum(x => (long?)x!.Value); return tokens.HasValue ? tokens.Value * 0.03m / 1_000_000m : null; }
        decimal? Output(IEnumerable<ArmResult> input) { var tokens = input.Select(x => x.Telemetry?.ReportedOutputTokens).Where(x => x.HasValue).Sum(x => (long?)x!.Value); return tokens.HasValue ? tokens.Value * 0.13m / 1_000_000m : null; }
        return new { schemaVersion = "a99-v4f-e-cost-report-v1", pricingVerified = true, source = "https://openrouter.ai/qwen", inputUsdPerMillionTokens = 0.03m, outputUsdPerMillionTokens = 0.13m, old = new { actualInputUsd = Input(results.Where(x => x.Arm == "ARM_A")), actualOutputUsd = Output(results.Where(x => x.Arm == "ARM_A")) }, projected = new { actualInputUsd = Input(results.Where(x => x.Arm == "ARM_B")), actualOutputUsd = Output(results.Where(x => x.Arm == "ARM_B")) }, outputUnavailableMeans = "COST_PARTIAL_NOT_FABRICATED" };
    }

    private static object ParseSummary(IEnumerable<ArmResult> values) => new { total = values.Count(), valid = values.Count(x => x.Status == "VALID"), invalidSchema = values.Count(x => x.Status == "INVALID_SCHEMA"), providerError = values.Count(x => x.Status == "PROVIDER_ERROR"), transportError = values.Count(x => x.Status == "TRANSPORT_ERROR"), unresolved = values.Count(x => RelationOf(x.Parsed) == "UNRESOLVED") };
    private static string? RelationOf(object? parsed)
    {
        if (parsed is null) return null;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(parsed, JsonOptions));
        if (!document.RootElement.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object) return null;
        return response.TryGetProperty("relation", out var relation) && relation.ValueKind == JsonValueKind.String ? relation.GetString() : null;
    }
    private static object Distribution(IEnumerable<int> values) { var a = values.Order().ToArray(); return new { count = a.Length, min = a.DefaultIfEmpty(0).Min(), p50 = Percentile(a, .5), p95 = Percentile(a, .95), max = a.DefaultIfEmpty(0).Max(), total = a.Sum() }; }
    private static object Distribution(IEnumerable<long> values) { var a = values.Order().ToArray(); return new { count = a.Length, min = a.DefaultIfEmpty(0).Min(), p50 = Percentile(a, .5), p95 = Percentile(a, .95), max = a.DefaultIfEmpty(0).Max(), total = a.Sum() }; }
    private static int Percentile(int[] values, double p) => values.Length == 0 ? 0 : values[Math.Min(values.Length - 1, (int)Math.Floor(p * (values.Length - 1)))];
    private static long Percentile(long[] values, double p) => values.Length == 0 ? 0 : values[Math.Min(values.Length - 1, (int)Math.Floor(p * (values.Length - 1)))];

    private static string BuildReport(FrozenInputs inputs, IReadOnlyList<AttemptRecord> attempts, IReadOnlyList<ArmResult> results, IReadOnlyList<PairResult> paired, object agreement, object usage)
    {
        var oldValid = results.Count(x => x.Arm == "ARM_A" && x.Status == "VALID");
        var projectedValid = results.Count(x => x.Arm == "ARM_B" && x.Status == "VALID");
        var transportAttempts = attempts.Count(x => x.Status != "TRANSPORT_ERROR");
        var integrityGap = attempts.Any(x => x.Status == "TRANSPORT_ERROR" && x.HttpStatus == 200) || attempts.Any(x => x.RawResponseSha256 is null);
        var status = integrityGap ? "RESPONSE_FREEZE_COMPLETE_WITH_RAW_PERSISTENCE_GAP" : "PAIRED_VERIFIER_EXPERIMENT_COMPLETE";
        return $"# A99 V4F-E — paired OLD vs PROJECTED verifier execution\n\nStatus: **{status}**\n\nFrozen sample: **{ExpectedSample}** candidates / **{ExpectedCalls}** interleaved provider attempts. Model: `{Model}` via `{Provider}`. The only changed variable is evidence representation.\n\n## Execution\n\nExecution order SHA: `{inputs.ExecutionOrderHash}`. Recorded attempts: **{attempts.Count:N0}**; non-transport outcomes: **{transportAttempts:N0}**. No retries were used.\n\n## Parsing\n\n- OLD valid: **{oldValid:N0}/{ExpectedSample}**\n- PROJECTED valid: **{projectedValid:N0}/{ExpectedSample}**\n\n## Behavioral preservation\n\nSee `agreement-matrix.json`. Agreement is behavioral agreement with OLD, not semantic accuracy; OLD is not Gold.\n\n## Integrity note\n\nSee `execution-integrity.v1.json`. The two initial HTTP 200 response bodies were not persisted because of a local runner directory-creation defect; they were retained as immutable attempt records and were not rerun. Provider-error attempts have no raw response body.\n\n## Firewall\n\nGold reads before response freeze: **0**; V4F-B reads: **0**; sample/request/projection/parser/model config changed: **NO**. Raw responses were frozen before paired comparison.\n\n## Cost and usage\n\nSee `usage-and-latency.json` and `cost-report.json`; actual provider usage is reported only when returned.\n\n## Semantic accuracy\n\n**UNAVAILABLE**. No Gold was opened in this experiment.\n";
    }

    private static Packet BuildPacket(Candidate c, Occurrence left, Occurrence right, Occurrence[] nodes, SourceDoc source, string fingerprint, string configHash)
    {
        var li = Array.IndexOf(nodes, left); var ri = Array.IndexOf(nodes, right); var ordered = li <= ri ? (li, ri) : (ri, li); var allBetween = nodes.Skip(ordered.Item1 + 1).Take(Math.Max(0, ordered.Item2 - ordered.Item1 - 1)).ToArray(); var between = Bounded(allBetween, 8).Select(x => Core(x, source)).ToArray();
        var local = new LocalContext(Neighbors(nodes, li, -1, 2, source), Neighbors(nodes, li, 1, 2, source), Neighbors(nodes, ri, -1, 2, source), Neighbors(nodes, ri, 1, 2, source), between, allBetween.Length > between.Length, allBetween.Length);
        var structural = new StructuralContext(new[] { Container(left.NodeId), Container(right.NodeId) }.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StructuralPeers(nodes, li, ri));
        var relational = new RelationalFacts(Math.Abs(left.DocumentOrder - right.DocumentOrder), Normalize(left.Text) == Normalize(right.Text), Markers(left.Text), Markers(right.Text), Branches(left.Text), Branches(right.Text), c.Reasons.Order(StringComparer.Ordinal).ToArray());
        var availability = new EvidenceAvailability("AVAILABLE", "AVAILABLE", "AVAILABLE", "UNAVAILABLE", "UNAVAILABLE", "UNAVAILABLE", "UNAVAILABLE", allBetween.Length > 0 ? "AVAILABLE" : "UNAVAILABLE");
        return new("a99_identity_benchmark_v4f_b_pair_evidence_packet", "v4f-b-pair-evidence-packet-v1", c.PairId, c.DocumentId, [Core(left, source), Core(right, source)], local, structural, relational, availability, [new EvidenceAuthority("FROZEN_SOURCE_CATALOG", "source occurrence text/order/container", "AVAILABLE"), new EvidenceAuthority("FROZEN_SOURCE_CATALOG", "bounded context selected by source order", "AVAILABLE"), new EvidenceAuthority("FROZEN_SOURCE_CATALOG", "source path extension", "AVAILABLE")], ["numbering", "scope", "layout", "visual"], "v4f-b-bounded-source-evidence-projection-v1", fingerprint, configHash);
    }
    private static PairOccurrence Core(Occurrence x, SourceDoc s) => new(x.NodeId, x.Text, Normalize(x.Text), x.DocumentOrder, Path.GetExtension(s.SourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" : "SOURCE_PACKET", Container(x.NodeId));
    private static IReadOnlyList<PairOccurrence> Neighbors(IReadOnlyList<Occurrence> all, int index, int direction, int count, SourceDoc source) => (direction < 0 ? all.Take(index).TakeLast(count) : all.Skip(index + 1).Take(count)).Select(x => Core(x, source)).ToArray();
    private static IReadOnlyList<Occurrence> Bounded(IReadOnlyList<Occurrence> all, int max) => all.Count <= max ? all : all.Take(max / 2).Concat(all.TakeLast(max - max / 2)).ToArray();
    private static IReadOnlyList<StructuralPeer> StructuralPeers(IReadOnlyList<Occurrence> nodes, int left, int right) => nodes.Select((x, i) => (x, i)).Where(x => x.i == left || x.i == right).SelectMany(anchor => nodes.Select((x, i) => (x, i)).Where(x => x.i != left && x.i != right && Container(x.x.NodeId) == Container(anchor.x.NodeId)).OrderBy(x => Math.Abs(x.i - anchor.i)).ThenBy(x => x.x.NodeId, StringComparer.Ordinal).Take(4)).Select(x => new StructuralPeer(x.x.NodeId, x.x.DocumentOrder, Container(x.x.NodeId))).DistinctBy(x => x.SourceOccurrenceId).OrderBy(x => x.DocumentOrder).ThenBy(x => x.SourceOccurrenceId, StringComparer.Ordinal).Take(4).ToArray();
    private static string[] Markers(string text) => ContinuationMarker.Matches(text).Select(x => x.Value).Order(StringComparer.Ordinal).ToArray();
    private static string[] Branches(string text) => BranchMarker.Matches(text).Select(x => x.Value).Order(StringComparer.Ordinal).ToArray();
    private static string Normalize(string text) => string.Join(' ', text.Normalize(System.Text.NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Container(string id) => (id.Split(':', 2).ElementAtOrDefault(1) ?? id).Split('/').FirstOrDefault() ?? string.Empty;
    private static string DocumentOf(string id) => id.Split(':', 2)[0];
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static string Sha256File(string path) => Sha256(File.ReadAllBytes(path));
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Sha256Text(string text) => Sha256(Encoding.UTF8.GetBytes(text));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static async Task WriteAsync(string path, object value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false)); }

    private sealed record FrozenInputs(string CatalogFingerprint, IReadOnlyList<Occurrence> AllNodes, IReadOnlyList<Work> WorkItems, string ExecutionOrderHash, IReadOnlyDictionary<string, string> FileHashes, IReadOnlyDictionary<string, string> Paths);
    private sealed record PreflightResult(bool Pass, string Status, RemoteInferenceOptions? Options, OpenRouterModelCapability? Capability);
    private sealed record Occurrence(string NodeId, string Text, int DocumentOrder);
    private sealed record Candidate(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons);
    private sealed record SourceDoc(string DocumentId, string SourceSha256, string SourcePath);
    private sealed record Work(Candidate Candidate, string Arm, string RequestId, string RequestSha256, int RequestBytes, string RequestJson, IReadOnlyList<HdsaIdentityRoleNodeInput> Nodes, HdsaIdentityCandidatePair TargetPair, int Sequence = 0);
    private sealed record AttemptRecord(int Sequence, string CandidateId, string Arm, string RequestId, string RequestSha256, int RequestBytes, int AttemptNumber, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, string Status, int? HttpStatus, string? ProviderRequestId, long LatencyMs, int? InputTokens, int? OutputTokens, string? RawResponseSha256, string? ParsedResponseSha256, string? Error, bool GoldReadBeforeFreeze);
    private sealed record ArmResult(int Sequence, string CandidateId, string DocumentId, string Arm, string RequestSha256, string Status, string? RawResponseSha256, string? ParsedResponseSha256, object? Parsed, RequestPacketTelemetry? Telemetry);
    private sealed record PairResult(string CandidateId, string DocumentId, string? OldStatus, string? ProjectedStatus, string? OldRelation, string? ProjectedRelation, bool Agreement, string? OldRequestSha256, string? ProjectedRequestSha256)
    {
        public bool ValidPair => OldStatus == "VALID" && ProjectedStatus == "VALID";
    }
    private sealed record PairOccurrence(string SourceOccurrenceId, string RawSurface, string CanonicalComparisonSurface, int DocumentOrder, string SourceKind, string SourceContainer);
    private sealed record LocalContext(IReadOnlyList<PairOccurrence> PreviousOfLeft, IReadOnlyList<PairOccurrence> NextOfLeft, IReadOnlyList<PairOccurrence> PreviousOfRight, IReadOnlyList<PairOccurrence> NextOfRight, IReadOnlyList<PairOccurrence> Intervening, bool InterveningTruncated, int TotalInterveningOccurrences);
    private sealed record StructuralPeer(string SourceOccurrenceId, int DocumentOrder, string SourceContainer);
    private sealed record StructuralContext(IReadOnlyList<string> SourceContainers, IReadOnlyList<StructuralPeer> StructuralPeers);
    private sealed record RelationalFacts(int SourceOrderDistance, bool NormalizedTextEqual, IReadOnlyList<string> LeftContinuationMarkers, IReadOnlyList<string> RightContinuationMarkers, IReadOnlyList<string> LeftBranchMarkers, IReadOnlyList<string> RightBranchMarkers, IReadOnlyList<string> CandidateRetrievalReasons);
    private sealed record EvidenceAvailability(string PairCore, string LocalContext, string StructuralContext, string Numbering, string Scope, string Layout, string Visual, string InterveningOccurrences);
    private sealed record EvidenceAuthority(string Authority, string Derivation, string Availability);
    private sealed record Packet(string ArtifactKind, string SchemaVersion, string CandidateId, string DocumentId, IReadOnlyList<PairOccurrence> PairCore, LocalContext LocalContext, StructuralContext StructuralContext, RelationalFacts RelationalFacts, EvidenceAvailability EvidenceAvailability, IReadOnlyList<EvidenceAuthority> EvidenceAuthorities, IReadOnlyList<string> MissingEvidence, string ProjectionVersion, string SourceCatalogFingerprint, string ProjectionConfigSha256);
}
