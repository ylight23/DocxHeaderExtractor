using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Fresh EXEC_V4 orchestration. It deliberately uses the source inventory only; canonical
/// occurrence/identity/hierarchy/level authority is not an input to this prediction campaign.
/// </summary>
public static class CanonicalDevV4Runner
{
    private const string Benchmark = "CANONICAL_DEV_V1";
    private const string DefaultCampaignId = "CANONICAL_DEV_V1_EXEC_V4";
    private const string DefaultOutputRoot = "artifacts/level-accuracy/canonical-dev-v1-exec-v4";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private static string CampaignId => Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_CAMPAIGN_ID") ?? DefaultCampaignId;
    private static string OutputRoot => Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_OUTPUT_ROOT") ?? DefaultOutputRoot;
    private static string ProductionSemanticCheckpoint => Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_SEMANTIC_CHECKPOINT") ?? "8b2d366";
    private static string ExecutionHarnessCheckpoint => Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_HARNESS_CHECKPOINT") ?? "8b2d366";
    private static string SchemaPrefix => $"a99-canonical-dev-v1-{CampaignId.Replace("CANONICAL_DEV_V1_", "", StringComparison.Ordinal).ToLowerInvariant()}";
    private const string ExpectedProductionSemanticHash = "5f0eb27dfa44068fc60c69e0dcf8a05b14a061ed7526610e4592d68c268fe5e6";
    private const int ExpectedDocuments = 15;
    private const int PerAttemptSeconds = 300;
    private const int DocumentSafetySeconds = 7200;

    private static readonly string[] CohortIds =
    [
        "DOC-0001", "DOC-0116", "DOC-0122", "DOC-0185", "DOC-0200",
        "DOC-0201", "DOC-0205", "DOC-0216", "DOC-0219", "DOC-0243",
        "DOC-0252", "DOC-0256", "DOC-0258", "DOC-0264", "DOC-0265",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunV5Async(string repoRoot, CancellationToken ct = default)
    {
        var previous = new[]
        {
            Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_CAMPAIGN_ID"),
            Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_OUTPUT_ROOT"),
            Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_SEMANTIC_CHECKPOINT"),
            Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_HARNESS_CHECKPOINT"),
        };
        Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_CAMPAIGN_ID", "CANONICAL_DEV_V1_EXEC_V5");
        Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_OUTPUT_ROOT", "artifacts/level-accuracy/canonical-dev-v1-exec-v5");
        Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_SEMANTIC_CHECKPOINT", "60de937");
        Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_HARNESS_CHECKPOINT", "60de937");
        try { return await RunAsync(repoRoot, ct); }
        finally
        {
            Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_CAMPAIGN_ID", previous[0]);
            Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_OUTPUT_ROOT", previous[1]);
            Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_SEMANTIC_CHECKPOINT", previous[2]);
            Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_HARNESS_CHECKPOINT", previous[3]);
        }
    }

    public static async Task<int> RunV6Async(string repoRoot, CancellationToken ct = default)
    {
        var lifecycleStatus = await CanonicalWorkerLaunchIntegrityRunner.RunLifecycleSelfTestAsync(repoRoot, ct);
        if (lifecycleStatus != 0) return 2;
        var previous = new[]
        {
            Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_CAMPAIGN_ID"),
            Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_OUTPUT_ROOT"),
            Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_SEMANTIC_CHECKPOINT"),
            Environment.GetEnvironmentVariable("A99_CANONICAL_DEV_HARNESS_CHECKPOINT"),
        };
        Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_CAMPAIGN_ID", "CANONICAL_DEV_V1_EXEC_V6");
        Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_OUTPUT_ROOT", "artifacts/level-accuracy/canonical-dev-v1-exec-v6");
        Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_SEMANTIC_CHECKPOINT", "f1686fb");
        Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_HARNESS_CHECKPOINT", "f1686fb");
        try { return await RunAsync(repoRoot, ct); }
        finally
        {
            Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_CAMPAIGN_ID", previous[0]);
            Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_OUTPUT_ROOT", previous[1]);
            Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_SEMANTIC_CHECKPOINT", previous[2]);
            Environment.SetEnvironmentVariable("A99_CANONICAL_DEV_HARNESS_CHECKPOINT", previous[3]);
        }
    }

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            await WriteJsonAsync(Path.Combine(output, "failure.v1.json"), new
            {
                schemaVersion = $"{SchemaPrefix}-failure-v1",
                status = "BLOCKED",
                reason = "V4_OUTPUT_ALREADY_EXISTS",
                campaignId = CampaignId,
                goldReadCount = 0,
                providerCalls = 0,
            }, CancellationToken.None);
            return 2;
        }
        Directory.CreateDirectory(output);

        var inventory = LoadInventory(repoRoot);
        var selected = CohortIds.Select(id => inventory.FirstOrDefault(item => item.DocumentId == id))
            .ToArray();
        if (selected.Any(item => item is null) || selected.Length != ExpectedDocuments)
            return await BlockAsync(output, "SOURCE_COHORT_NOT_FOUND", ct);
        var sources = selected!.Select(item => ResolveSource(repoRoot, item!)).ToArray();
        if (sources.Any(item => !item.Exists || !string.Equals(item.ActualSha256, item.ExpectedSha256, StringComparison.OrdinalIgnoreCase)))
        {
            await WriteJsonAsync(Path.Combine(output, "manifest.json"), new
            {
                schemaVersion = $"{SchemaPrefix}-manifest-v1",
                status = "BLOCKED_ON_SOURCE_LINEAGE",
                campaignId = CampaignId,
                documents = sources,
                goldReadCount = 0,
                providerCalls = 0,
            }, ct);
            return 2;
        }

        var perAttempt = ProviderTimeoutPolicyV2.ResolvePerAttemptHardTimeout();
        var safety = ProviderTimeoutPolicyV2.ResolveDocumentSafetyCeiling();
        if (perAttempt.TotalSeconds != PerAttemptSeconds || safety.TotalSeconds != DocumentSafetySeconds)
            return await BlockAsync(output, "TIMEOUT_POLICY_NOT_FROZEN_TO_V4_AUTHORIZATION", ct);
        var remote = RemoteInferenceOptions.FromEnvironment("openrouter");
        remote.RequestTimeoutSeconds = PerAttemptSeconds;
        var productionSemanticHash = CanonicalDevV1BaselineRunner.ComputeProductionSemanticHashForIntegrity(repoRoot);
        if (!string.Equals(productionSemanticHash, ExpectedProductionSemanticHash, StringComparison.OrdinalIgnoreCase))
            return await BlockAsync(output, "PRODUCTION_SEMANTIC_HASH_MISMATCH", ct);
        var executionHarnessHash = ComputeV4HarnessHash(repoRoot);
        var sourceUniverseHash = Sha256Text(JsonSerializer.Serialize(sources.Select(item => new
        {
            item.DocumentId, item.SourcePath, item.ExpectedSha256,
        }).OrderBy(item => item.DocumentId, StringComparer.Ordinal), JsonOptions));
        var config = new
        {
            schemaVersion = $"{SchemaPrefix}-run-configuration-v1",
            benchmark = Benchmark,
            campaignId = CampaignId,
            productionSemanticCheckpoint = ProductionSemanticCheckpoint,
            executionHarnessCheckpoint = ExecutionHarnessCheckpoint,
            productionSemanticHash,
            executionHarnessHash,
            provider = "OpenRouter",
            model = remote.Model,
            endpoint = remote.Endpoint.ToString(),
            contextSize = remote.ContextSize,
            maxOutputTokens = remote.MaxOutputTokens,
            requestTimeoutSeconds = PerAttemptSeconds,
            perAttemptHardTimeoutSeconds = PerAttemptSeconds,
            documentSafetyCeilingSeconds = DocumentSafetySeconds,
            transientRequestRetries = remote.TransientRequestRetries,
            missingIdRetries = remote.MissingIdRetries,
            maxParallelRequests = remote.MaxParallelRequests,
            promptHash = "PIPELINE_OWNED_PROMPTS_NOT_EXPOSED_AS_SINGLE_TEMPLATE",
            candidateGenerationHash = FileHashOrMissing(repoRoot, "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/DocxAuthorityPipeline.cs"),
            parserHash = FileHashOrMissing(repoRoot, "src/DocxHeaderExtractor.DocumentProcessing/OpenXmlLayer/OpenXmlDocumentSource.cs"),
            bindingHash = FileHashOrMissing(repoRoot, "src/DocxHeaderExtractor.DocumentProcessing/Authority/RouteOccurrenceTraceBuilder.cs"),
            sourceUniverseHash,
            sourceManifest = sources,
            retryPolicy = "PIPELINE_FROZEN_PROVIDER_RETRY_POLICY",
            goldReadsBeforePredictionFreeze = 0,
            historicalLevelReadsBeforePredictionFreeze = 0,
            historicalParentReadsBeforePredictionFreeze = 0,
        };
        var configPath = Path.Combine(output, "run-configuration.json");
        await WriteJsonAsync(configPath, config, CancellationToken.None);
        var configHash = Sha256File(configPath);
        var preflight = new
        {
            schemaVersion = $"{SchemaPrefix}-preflight-v1",
            status = "FROZEN_BEFORE_PROVIDER_CALL_1",
            benchmark = Benchmark,
            campaignId = CampaignId,
            sourceUniverseHash,
            runConfigurationHash = configHash,
            productionSemanticHash,
            executionHarnessHash,
            documentsScheduled = ExpectedDocuments,
            documentIds = CohortIds,
            perAttemptHardTimeoutSeconds = PerAttemptSeconds,
            documentSafetyCeilingSeconds = DocumentSafetySeconds,
            goldReads = 0,
            historicalLevelReads = 0,
            historicalParentReads = 0,
            reusedCampaigns = Array.Empty<string>(),
            predictionReuse = false,
            frozenAt = DateTimeOffset.UtcNow,
        };
        await WriteJsonAsync(Path.Combine(output, "preflight.v1.json"), preflight, CancellationToken.None);
        await WriteJsonAsync(Path.Combine(output, "production-run-manifest.json"), new
        {
            schemaVersion = $"{SchemaPrefix}-production-run-manifest-v1",
            status = "PRODUCTION_RUN_STARTED",
            benchmark = Benchmark,
            campaignId = CampaignId,
            runConfigurationHash = configHash,
            productionSemanticHash,
            executionHarnessHash,
            documents = sources.Select(item => new { documentId = item.DocumentId, status = "SCHEDULED", sourceSha256 = item.ExpectedSha256 }).ToArray(),
            providerCalls = 0,
            goldReadCount = 0,
            predictionMutationAfterFreeze = false,
        }, CancellationToken.None);

        var runs = new List<object>();
        var providerCallsTotal = 0;
        foreach (var source in sources)
        {
            if (ct.IsCancellationRequested) return await AbortAsync(output, runs, providerCallsTotal, configHash, productionSemanticHash, executionHarnessHash, "CAMPAIGN_CANCELLED", ct);
            var docDir = Path.Combine(output, source.DocumentId);
            Directory.CreateDirectory(docDir);
            var started = DateTimeOffset.UtcNow;
            var attemptId = $"{CampaignId}:{source.DocumentId}:A01:{started:yyyyMMddTHHmmssfffZ}";
            var workDir = Path.Combine(output, "work", source.DocumentId, "A01");
            Directory.CreateDirectory(workDir);
            var requestHash = Sha256Text(JsonSerializer.Serialize(new { source.DocumentId, source.ExpectedSha256, configHash }, JsonOptions));
            await WriteJsonAsync(Path.Combine(docDir, "attempt.started.v1.json"), new
            {
                schemaVersion = $"{SchemaPrefix}-attempt-start-v1",
                benchmark = Benchmark,
                campaignId = CampaignId,
                documentId = source.DocumentId,
                sourcePath = source.SourcePath,
                sourceSha256 = source.ExpectedSha256,
                attemptId,
                requestHash,
                started,
                perAttemptHardTimeoutSeconds = PerAttemptSeconds,
                documentSafetyCeilingSeconds = DocumentSafetySeconds,
                runConfigurationHash = configHash,
                goldReadBeforePredictionFreeze = false,
                goldReadCount = 0,
            }, CancellationToken.None);
            var jobPath = Path.Combine(workDir, "worker-job.json");
            await WriteJsonAsync(jobPath, new
            {
                benchmark = Benchmark,
                campaignId = CampaignId,
                productionSemanticCheckpoint = ProductionSemanticCheckpoint,
                executionHarnessCheckpoint = ExecutionHarnessCheckpoint,
                productionSemanticHash,
                executionHarnessHash,
                documentId = source.DocumentId,
                sourcePath = source.SourcePath,
                sourceSha256 = source.ExpectedSha256,
                outputDir = workDir,
                runConfigurationHash = configHash,
                attemptId,
                requestHash,
                started,
                perAttemptHardTimeoutSeconds = PerAttemptSeconds,
            }, CancellationToken.None);
            var watchdog = await ProviderHardTimeoutIntegrity.RunWorkerAsync(jobPath, workDir, safety, ct);
            if (watchdog.Status != "COMPLETE")
            {
                await WriteJsonAsync(Path.Combine(docDir, "failure.v1.json"), new
                {
                    schemaVersion = $"{SchemaPrefix}-failure-v1",
                    benchmark = Benchmark,
                    campaignId = CampaignId,
                    documentId = source.DocumentId,
                    attemptId,
                    requestHash,
                    runConfigurationHash = configHash,
                    sourceSha256 = source.ExpectedSha256,
                    status = watchdog.Status,
                    terminationMode = watchdog.TerminationMode,
                    childPid = watchdog.ChildPid,
                    started,
                    completed = watchdog.CompletedAt,
                    goldReadBeforePredictionFreeze = false,
                }, CancellationToken.None);
                runs.Add(new { documentId = source.DocumentId, status = watchdog.Status, providerCalls = 0, terminationMode = watchdog.TerminationMode, childPid = watchdog.ChildPid });
                return await AbortAsync(output, runs, providerCallsTotal, configHash, productionSemanticHash, executionHarnessHash, "BLOCKED_ON_PROVIDER_EXECUTION_INTEGRITY", ct);
            }
            var workerPrediction = Path.Combine(workDir, "worker-prediction.v1.json");
            var workerAttempts = Path.Combine(workDir, "worker-attempts.v1.json");
            if (!File.Exists(workerPrediction) || !File.Exists(workerAttempts))
            {
                runs.Add(new { documentId = source.DocumentId, status = "MISSING_WORKER_ARTIFACTS", providerCalls = 0 });
                return await AbortAsync(output, runs, providerCallsTotal, configHash, productionSemanticHash, executionHarnessHash, "BLOCKED_ON_PROVIDER_EXECUTION_INTEGRITY", ct);
            }
            PromoteAtomic(workerPrediction, Path.Combine(docDir, "prediction.v1.json"));
            PromoteAtomic(workerAttempts, Path.Combine(docDir, "attempts.v1.json"));
            var calls = ReadProviderCalls(Path.Combine(docDir, "attempts.v1.json"));
            providerCallsTotal += calls;
            runs.Add(new { documentId = source.DocumentId, status = "COMPLETE", providerCalls = calls, childPid = watchdog.ChildPid, elapsedMs = (watchdog.CompletedAt - started).TotalMilliseconds });
        }

        var predictionFiles = Directory.EnumerateFiles(output, "prediction.v1.json", SearchOption.AllDirectories).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
        if (predictionFiles.Length != ExpectedDocuments)
            return await AbortAsync(output, runs, providerCallsTotal, configHash, productionSemanticHash, executionHarnessHash, "BLOCKED_ON_PROVIDER_EXECUTION_INTEGRITY", ct);
        var freeze = new
        {
            schemaVersion = $"{SchemaPrefix}-prediction-freeze-v1",
            status = "PREDICTIONS_FROZEN_BEFORE_SCORING",
            benchmark = Benchmark,
            campaignId = CampaignId,
            runConfigurationHash = configHash,
            productionSemanticHash,
            executionHarnessHash,
            documentsScheduled = ExpectedDocuments,
            documentsCompleted = predictionFiles.Length,
            providerCalls = providerCallsTotal,
            goldReadBeforePredictionFreeze = false,
            predictionMutationAfterFreeze = false,
            frozenAt = DateTimeOffset.UtcNow,
            artifacts = predictionFiles.Select(path => new { path = Path.GetRelativePath(repoRoot, path).Replace('\\', '/'), sha256 = Sha256File(path) }).ToArray(),
        };
        var freezePath = Path.Combine(output, "prediction-freeze-manifest.json");
        await WriteJsonAsync(freezePath, freeze, CancellationToken.None);
        await WriteJsonAsync(Path.Combine(output, "provider-attempt-manifest.json"), new
        {
            schemaVersion = $"{SchemaPrefix}-provider-attempt-manifest-v1",
            benchmark = Benchmark,
            campaignId = CampaignId,
            runConfigurationHash = configHash,
            productionSemanticHash,
            executionHarnessHash,
            providerCalls = providerCallsTotal,
            documents = runs,
            artifacts = Directory.EnumerateFiles(output, "*.v1.json", SearchOption.AllDirectories).Select(path => new { path = Path.GetRelativePath(repoRoot, path).Replace('\\', '/'), sha256 = Sha256File(path) }).OrderBy(item => item.path, StringComparer.Ordinal).ToArray(),
        }, CancellationToken.None);
        await WriteJsonAsync(Path.Combine(output, "production-run-manifest.json"), new
        {
            schemaVersion = $"{SchemaPrefix}-production-run-manifest-v1",
            status = $"{CampaignId}_PREDICTIONS_FROZEN",
            benchmark = Benchmark,
            campaignId = CampaignId,
            runConfigurationHash = configHash,
            productionSemanticHash,
            executionHarnessHash,
            documents = runs,
            providerCalls = providerCallsTotal,
            perAttemptHardTimeoutSeconds = PerAttemptSeconds,
            documentSafetyCeilingSeconds = DocumentSafetySeconds,
            goldReadCount = 0,
            scoringArtifacts = 0,
            predictionFreezeManifestSha256 = Sha256File(freezePath),
        }, CancellationToken.None);
        return 0;
    }

    private static async Task<int> AbortAsync(string output, IReadOnlyList<object> runs, int calls, string configHash, string semanticHash, string harnessHash, string reason, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "production-run-manifest.json"), new
        {
            schemaVersion = $"{SchemaPrefix}-production-run-manifest-v1",
            status = reason,
            benchmark = Benchmark,
            campaignId = CampaignId,
            runConfigurationHash = configHash,
            productionSemanticHash = semanticHash,
            executionHarnessHash = harnessHash,
            documents = runs,
            providerCalls = calls,
            goldReadCount = 0,
            scoringArtifacts = 0,
            predictionMutationAfterFreeze = false,
        }, ct);
        return 2;
    }

    private static async Task<int> BlockAsync(string output, string reason, CancellationToken ct)
    {
        Directory.CreateDirectory(output);
        await WriteJsonAsync(Path.Combine(output, "preflight-failure.v1.json"), new { schemaVersion = $"{SchemaPrefix}-preflight-failure-v1", status = "BLOCKED", campaignId = CampaignId, reason, goldReadCount = 0, providerCalls = 0 }, ct);
        return 2;
    }

    private static SourceEntry[] LoadInventory(string repoRoot)
    {
        var path = Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("documents").EnumerateArray().Select(item => new SourceEntry(
            item.GetProperty("documentId").GetString()!,
            item.GetProperty("sourcePath").GetString() ?? "",
            item.GetProperty("sourceSha256").GetString() ?? "")).ToArray();
    }

    private static ResolvedSource ResolveSource(string repoRoot, SourceEntry item)
    {
        var full = Path.GetFullPath(Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar)));
        var exists = File.Exists(full);
        return new(item.DocumentId, full, item.SourceSha256, exists ? Sha256File(full) : "", exists);
    }

    private static string ComputeV4HarnessHash(string repoRoot)
    {
        var files = new[]
        {
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalDevV4Runner.cs",
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalDevV1BaselineRunner.cs",
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/ProviderHardTimeoutIntegrity.cs",
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/ProviderTimeoutPolicyV2.cs",
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/ProviderObservability.cs",
            "src/DocxHeaderExtractor.DocumentProcessing/Inference/OpenRouterHeaderExtractor.cs",
            "src/DocxHeaderExtractor.Cli/Program.cs",
            "src/DocxHeaderExtractor.Cli/CommandLineOptions.cs",
        };
        return Sha256Text(string.Join("|", files.Select(path => path + ":" + FileHashOrMissing(repoRoot, path))));
    }

    private static int ReadProviderCalls(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("providerCalls", out var value) && value.TryGetInt32(out var calls) ? calls : 0;
    }

    private static void PromoteAtomic(string source, string destination)
    {
        var temporary = destination + ".promoting";
        File.Copy(source, temporary, true);
        File.Move(temporary, destination, true);
    }

    private static string FileHashOrMissing(string repoRoot, string path)
    {
        var full = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? Sha256File(full) : "MISSING";
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Task WriteJsonAsync(string path, object value, CancellationToken ct) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, ct);

    private sealed record SourceEntry(string DocumentId, string SourcePath, string SourceSha256);
    private sealed record ResolvedSource(string DocumentId, string SourcePath, string ExpectedSha256, string ActualSha256, bool Exists);
}
