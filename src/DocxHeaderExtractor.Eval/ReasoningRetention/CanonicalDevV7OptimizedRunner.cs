using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// The first optimized production campaign. This runner intentionally owns its cohort,
/// output root, hashes, and lineage instead of reusing the historical V4/V5/V6 runner.
/// </summary>
public static class CanonicalDevV7OptimizedRunner
{
    private const string Benchmark = "CANONICAL_DEV_V1";
    private const string CampaignId = "CANONICAL_DEV_V1_EXEC_V7_OPTIMIZED";
    private const string OutputRoot = "artifacts/level-accuracy/canonical-dev-v1-exec-v7-optimized";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string CodeCheckpoint = "PRE_V7_RUNNER_TELEMETRY_CLOSURE";
    private const int PerAttemptSeconds = 300;
    private const int DocumentSafetySeconds = 7200;
    private const int RoleTargetTokens = 5000;
    private const int RoleCap = 32;
    private const int SpanTargetTokens = 5000;
    private const int SpanCap = 16;
    private const int V6Doc0116Responses = 352;

    private static readonly string[] CohortIds = ["DOC-0001", "DOC-0116"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            return await BlockAsync(output, "V7_OUTPUT_ALREADY_EXISTS", ct);
        Directory.CreateDirectory(output);

        var inventory = LoadInventory(repoRoot);
        var sources = CohortIds.Select(id => inventory.FirstOrDefault(item => item.DocumentId == id))
            .Select(item => item is null ? null : ResolveSource(repoRoot, item))
            .ToArray();
        if (sources.Any(item => item is null))
            return await BlockAsync(output, "V7_SOURCE_COHORT_NOT_FOUND", ct);
        var resolved = sources!.Cast<ResolvedSource>().ToArray();
        if (resolved.Any(item => !item.Exists || !string.Equals(item.ActualSha256, item.ExpectedSha256, StringComparison.OrdinalIgnoreCase)))
        {
            await WriteJsonAsync(Path.Combine(output, "production-run-manifest.json"), new
            {
                schemaVersion = "a99-canonical-dev-v1-exec-v7-production-run-v1",
                status = "BLOCKED_ON_SOURCE_LINEAGE",
                campaignId = CampaignId,
                documents = resolved,
                providerCalls = 0,
                goldReads = 0,
            }, ct);
            return 2;
        }

        var perAttempt = ProviderTimeoutPolicyV2.ResolvePerAttemptHardTimeout();
        var safety = ProviderTimeoutPolicyV2.ResolveDocumentSafetyCeiling();
        if (perAttempt.TotalSeconds != PerAttemptSeconds || safety.TotalSeconds != DocumentSafetySeconds)
            return await BlockAsync(output, "V7_TIMEOUT_POLICY_NOT_FROZEN", ct);

        var remote = RemoteInferenceOptions.FromEnvironment("openrouter");
        remote.RequestTimeoutSeconds = PerAttemptSeconds;
        var productionSemanticHash = ComputeProductionSemanticHash(repoRoot);
        var executionHarnessHash = ComputeExecutionHarnessHash(repoRoot);
        var sourceUniverseHash = Sha256Text(JsonSerializer.Serialize(
            resolved.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
                .Select(item => new { item.DocumentId, item.SourcePath, item.ExpectedSha256 }), JsonOptions));
        var configuration = new
        {
            schemaVersion = "a99-canonical-dev-v1-exec-v7-run-configuration-v1",
            benchmark = Benchmark,
            campaignId = CampaignId,
            codeCheckpoint = CodeCheckpoint,
            productionSemanticHash,
            executionHarnessHash,
            sourceUniverseHash,
            provider = "OpenRouter",
            model = remote.Model,
            endpoint = remote.Endpoint.ToString(),
            contextSize = remote.ContextSize,
            maxOutputTokens = remote.MaxOutputTokens,
            requestTimeoutSeconds = PerAttemptSeconds,
            perAttemptHardTimeoutSeconds = PerAttemptSeconds,
            documentSafetyCeilingSeconds = DocumentSafetySeconds,
            roleTargetTokens = RoleTargetTokens,
            roleCap = RoleCap,
            spanTargetTokens = SpanTargetTokens,
            spanCap = SpanCap,
            transientRequestRetries = remote.TransientRequestRetries,
            missingIdRetries = remote.MissingIdRetries,
            maxParallelRequests = remote.MaxParallelRequests,
            promptHash = "PIPELINE_OWNED_PROMPTS_NOT_EXPOSED_AS_SINGLE_TEMPLATE",
            candidateGenerationHash = FileHashOrMissing(repoRoot, "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/DocxAuthorityPipeline.cs"),
            parserHash = FileHashOrMissing(repoRoot, "src/DocxHeaderExtractor.DocumentProcessing/OpenXmlLayer/OpenXmlDocumentSource.cs"),
            bindingHash = FileHashOrMissing(repoRoot, "src/DocxHeaderExtractor.DocumentProcessing/Authority/RouteOccurrenceTraceBuilder.cs"),
            documents = resolved.Select(item => new { item.DocumentId, item.SourcePath, item.ExpectedSha256 }).ToArray(),
            predictionReuse = false,
            goldReadsBeforePredictionFreeze = 0,
            scoring = false,
        };
        var configPath = Path.Combine(output, "run-configuration.json");
        await WriteJsonAsync(configPath, configuration, CancellationToken.None);
        var runConfigurationHash = Sha256File(configPath);

        await WriteJsonAsync(Path.Combine(output, "preflight.v1.json"), new
        {
            schemaVersion = "a99-canonical-dev-v1-exec-v7-preflight-v1",
            status = "FROZEN_BEFORE_PROVIDER_CALL_1",
            benchmark = Benchmark,
            campaignId = CampaignId,
            codeCheckpoint = CodeCheckpoint,
            productionSemanticHash,
            executionHarnessHash,
            sourceUniverseHash,
            runConfigurationHash,
            documentIds = CohortIds,
            documentsScheduled = CohortIds.Length,
            perAttemptHardTimeoutSeconds = PerAttemptSeconds,
            documentSafetyCeilingSeconds = DocumentSafetySeconds,
            roleTargetTokens = RoleTargetTokens,
            roleCap = RoleCap,
            spanTargetTokens = SpanTargetTokens,
            spanCap = SpanCap,
            goldReads = 0,
            historicalReads = 0,
            scoring = false,
            predictionReuse = false,
            frozenAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        await WriteJsonAsync(Path.Combine(output, "production-run-manifest.json"), new
        {
            schemaVersion = "a99-canonical-dev-v1-exec-v7-production-run-v1",
            status = "PRODUCTION_RUN_STARTED",
            benchmark = Benchmark,
            campaignId = CampaignId,
            productionSemanticHash,
            executionHarnessHash,
            sourceUniverseHash,
            runConfigurationHash,
            documents = resolved.Select(item => new { item.DocumentId, status = "SCHEDULED", sourceSha256 = item.ExpectedSha256 }).ToArray(),
            providerCalls = 0,
            goldReads = 0,
            scoring = false,
        }, CancellationToken.None);

        var runs = new List<object>();
        var totalProviderCalls = 0;
        foreach (var source in resolved)
        {
            if (ct.IsCancellationRequested)
                return await AbortAsync(output, runs, totalProviderCalls, runConfigurationHash, productionSemanticHash, executionHarnessHash, "CAMPAIGN_CANCELLED", ct);

            var docDir = Path.Combine(output, source.DocumentId);
            Directory.CreateDirectory(docDir);
            var started = DateTimeOffset.UtcNow;
            var attemptId = $"{CampaignId}:{source.DocumentId}:A01:{started:yyyyMMddTHHmmssfffZ}";
            var requestHash = Sha256Text(JsonSerializer.Serialize(new { source.DocumentId, source.ExpectedSha256, runConfigurationHash }, JsonOptions));
            await WriteJsonAsync(Path.Combine(docDir, "attempt.started.v1.json"), new
            {
                schemaVersion = "a99-canonical-dev-v1-exec-v7-attempt-start-v1",
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
                runConfigurationHash,
                goldReads = 0,
                scoring = false,
            }, CancellationToken.None);

            var workDir = Path.Combine(output, "work", source.DocumentId, "A01");
            Directory.CreateDirectory(workDir);
            var jobPath = Path.Combine(workDir, "worker-job.json");
            await WriteJsonAsync(jobPath, new
            {
                benchmark = Benchmark,
                campaignId = CampaignId,
                productionSemanticCheckpoint = CodeCheckpoint,
                executionHarnessCheckpoint = CodeCheckpoint,
                productionSemanticHash,
                executionHarnessHash,
                documentId = source.DocumentId,
                sourcePath = source.SourcePath,
                sourceSha256 = source.ExpectedSha256,
                outputDir = workDir,
                runConfigurationHash,
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
                    schemaVersion = "a99-canonical-dev-v1-exec-v7-failure-v1",
                    benchmark = Benchmark,
                    campaignId = CampaignId,
                    documentId = source.DocumentId,
                    attemptId,
                    requestHash,
                    status = watchdog.Status,
                    terminationMode = watchdog.TerminationMode,
                    childPid = watchdog.ChildPid,
                    started,
                    completed = watchdog.CompletedAt,
                    goldReads = 0,
                }, CancellationToken.None);
                runs.Add(new { documentId = source.DocumentId, status = watchdog.Status, providerCalls = 0 });
                return await AbortAsync(output, runs, totalProviderCalls, runConfigurationHash, productionSemanticHash, executionHarnessHash, "BLOCKED_ON_PROVIDER_EXECUTION_INTEGRITY", ct);
            }

            var workerPrediction = Path.Combine(workDir, "worker-prediction.v1.json");
            var workerAttempts = Path.Combine(workDir, "worker-attempts.v1.json");
            if (!File.Exists(workerPrediction) || !File.Exists(workerAttempts))
                return await AbortAsync(output, runs, totalProviderCalls, runConfigurationHash, productionSemanticHash, executionHarnessHash, "MISSING_TELEMETRY_BEARING_WORKER_ARTIFACTS", ct);
            PromoteAtomic(workerPrediction, Path.Combine(docDir, "prediction.v1.json"));
            PromoteAtomic(workerAttempts, Path.Combine(docDir, "attempts.v1.json"));
            var promotedPrediction = Path.Combine(docDir, "prediction.v1.json");
            var promotedAttempts = Path.Combine(docDir, "attempts.v1.json");
            if (!TelemetryRoundTripMatches(promotedPrediction, promotedAttempts, out var telemetryFailure))
                return await AbortAsync(output, runs, totalProviderCalls, runConfigurationHash, productionSemanticHash, executionHarnessHash, $"PROMOTED_BATCH_TELEMETRY_INVALID:{telemetryFailure}", ct);

            var calls = ReadInt(promotedAttempts, "providerCalls");
            totalProviderCalls += calls;
            runs.Add(new
            {
                documentId = source.DocumentId,
                status = "COMPLETE",
                providerCalls = calls,
                responseCount = ReadInt(promotedAttempts, "responseCount"),
                elapsedMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds,
            });

            if (source.DocumentId == "DOC-0116")
                await WritePerformanceAuditAsync(output, docDir, runs, runConfigurationHash, productionSemanticHash, executionHarnessHash, source, CancellationToken.None);
        }

        await WriteJsonAsync(Path.Combine(output, "prediction-freeze-manifest.v1.json"), new
        {
            schemaVersion = "a99-canonical-dev-v1-exec-v7-prediction-freeze-v1",
            status = "PHASE_1_PREDICTIONS_FROZEN_BEFORE_SCORING",
            campaignId = CampaignId,
            documentsScheduled = CohortIds,
            documentsCompleted = runs.Count,
            providerCalls = totalProviderCalls,
            goldReads = 0,
            scoring = false,
            artifacts = Directory.EnumerateFiles(output, "*.v1.json", SearchOption.AllDirectories)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .Select(item => new { path = Path.GetRelativePath(repoRoot, item).Replace('\\', '/'), sha256 = Sha256File(item) })
                .ToArray(),
        }, CancellationToken.None);
        await WriteJsonAsync(Path.Combine(output, "production-run-manifest.json"), new
        {
            schemaVersion = "a99-canonical-dev-v1-exec-v7-production-run-v1",
            status = "CANONICAL_DEV_V1_EXEC_V7_PHASE_1_COMPLETE",
            benchmark = Benchmark,
            campaignId = CampaignId,
            productionSemanticHash,
            executionHarnessHash,
            sourceUniverseHash,
            runConfigurationHash,
            documents = runs,
            providerCalls = totalProviderCalls,
            goldReads = 0,
            scoring = false,
        }, CancellationToken.None);
        return 0;
    }

    private static async Task WritePerformanceAuditAsync(string output, string docDir, IReadOnlyList<object> runs,
        string runConfigurationHash, string productionSemanticHash,
        string executionHarnessHash, ResolvedSource source, CancellationToken ct)
    {
        var attemptsPath = Path.Combine(docDir, "attempts.v1.json");
        var telemetry = ReadRequiredObject(attemptsPath, "batchTelemetry");
        var documentProviderCalls = ReadInt(attemptsPath, "providerCalls");
        var documentResponseCount = ReadInt(attemptsPath, "responseCount");
        var metrics = CalculatePerformanceAuditMetrics(documentProviderCalls, documentResponseCount, telemetry);
        await WriteJsonAsync(Path.Combine(output, "v7-performance-audit.v1.json"), new
        {
            schemaVersion = "a99-canonical-dev-v1-exec-v7-performance-audit-v1",
            status = "DOC_0116_COMPLETE_PERFORMANCE_AUDIT",
            campaignId = CampaignId,
            documentId = source.DocumentId,
            sourceSha256 = source.ExpectedSha256,
            v6Baseline = new { totalResponses = V6Doc0116Responses, source = "canonical-dev-v1-exec-v6/partial-forensic-snapshot-v2" },
            v7 = new
            {
                sourceParagraphCount = ReadTelemetryInt(telemetry, "sourceParagraphCount"),
                roleInputBlockCount = ReadTelemetryInt(telemetry, "roleInputBlockCount"),
                roleBatchCount = ReadTelemetryInt(telemetry, "roleBatchCount"),
                roleProviderCalls = ReadTelemetryInt(telemetry, "roleProviderCalls"),
                roleInputTokensTotal = ReadTelemetryInt(telemetry, "roleInputTokensTotal"),
                roleLargestBatchBlocks = ReadTelemetryInt(telemetry, "roleLargestBatchBlocks"),
                roleLargestBatchTokens = ReadTelemetryInt(telemetry, "roleLargestBatchTokens"),
                headingLikeAfterRole = ReadTelemetryInt(telemetry, "headingLikeAfterRole"),
                spanBatchCount = ReadTelemetryInt(telemetry, "spanBatchCount"),
                spanProviderCalls = ReadTelemetryInt(telemetry, "spanProviderCalls"),
                spanInputTokensTotal = ReadTelemetryInt(telemetry, "spanInputTokensTotal"),
                hierarchyInputCount = ReadTelemetryInt(telemetry, "hierarchyInputCount"),
                hierarchyProviderCalls = ReadTelemetryInt(telemetry, "hierarchyProviderCalls"),
                totalProviderCalls = metrics.DocumentProviderCalls,
                totalResponses = metrics.DocumentResponseCount,
                elapsedMs = ReadTelemetryLong(telemetry, "elapsedMs"),
                additionalProviderCallsObserved = metrics.AdditionalProviderCallsObserved,
                attemptTimeouts = 0,
                providerFailures = 0,
            },
            reductions = new
            {
                responseReduction = V6Doc0116Responses - metrics.DocumentResponseCount,
                responseReductionRatio = V6Doc0116Responses == 0 ? 0d : 1d - (double)metrics.DocumentResponseCount / V6Doc0116Responses,
                providerCallReduction = V6Doc0116Responses - metrics.DocumentProviderCalls,
                providerCallReductionRatio = V6Doc0116Responses == 0 ? 0d : 1d - (double)metrics.DocumentProviderCalls / V6Doc0116Responses,
            },
            noGold = true,
            scoring = false,
            providerCalls = metrics.DocumentProviderCalls,
            runConfigurationHash,
            productionSemanticHash,
            executionHarnessHash,
            documentRuns = runs,
        }, ct);
    }

    internal static PerformanceAuditMetrics CalculatePerformanceAuditMetrics(
        int documentProviderCalls, int documentResponseCount, JsonElement telemetry)
    {
        var baseProviderCalls = ReadTelemetryInt(telemetry, "roleProviderCalls") +
                                ReadTelemetryInt(telemetry, "spanProviderCalls") +
                                ReadTelemetryInt(telemetry, "hierarchyProviderCalls");
        return new PerformanceAuditMetrics(
            documentProviderCalls,
            documentResponseCount,
            baseProviderCalls,
            Math.Max(0, documentProviderCalls - baseProviderCalls));
    }

    private static bool TelemetryRoundTripMatches(string predictionPath, string attemptsPath, out string failure)
    {
        failure = "";
        try
        {
            using var prediction = JsonDocument.Parse(File.ReadAllText(predictionPath));
            using var attempts = JsonDocument.Parse(File.ReadAllText(attemptsPath));
            if (!prediction.RootElement.TryGetProperty("batchTelemetry", out var p) || p.ValueKind == JsonValueKind.Null)
            { failure = "prediction.batchTelemetry missing"; return false; }
            if (!attempts.RootElement.TryGetProperty("batchTelemetry", out var a) || a.ValueKind == JsonValueKind.Null)
            { failure = "attempts.batchTelemetry missing"; return false; }
            if (p.GetProperty("totalProviderCalls").GetInt32() != attempts.RootElement.GetProperty("providerCalls").GetInt32())
            { failure = "totalProviderCalls mismatch"; return false; }
            if (p.GetProperty("totalResponses").GetInt32() != attempts.RootElement.GetProperty("responseCount").GetInt32())
            { failure = "totalResponses mismatch"; return false; }
            foreach (var property in new[] { "sourceParagraphCount", "roleInputBlockCount", "roleBatchCount", "roleProviderCalls", "roleInputTokensTotal", "roleLargestBatchBlocks", "roleLargestBatchTokens", "headingLikeAfterRole", "spanBatchCount", "spanProviderCalls", "spanInputTokensTotal", "hierarchyInputCount", "hierarchyProviderCalls", "totalProviderCalls", "totalResponses", "elapsedMs" })
            {
                if (!p.GetProperty(property).ToString().Equals(a.GetProperty(property).ToString(), StringComparison.Ordinal))
                { failure = $"telemetry field mismatch: {property}"; return false; }
            }
            return true;
        }
        catch (Exception ex) { failure = ex.Message; return false; }
    }

    private static JsonElement ReadRequiredObject(string path, string property)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Missing object {property} in {path}.");
        return value.Clone();
    }

    private static int ReadInt(string path, string property)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;
    }

    private static int ReadTelemetryInt(JsonElement telemetry, string property) => telemetry.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static long ReadTelemetryLong(JsonElement telemetry, string property) => telemetry.TryGetProperty(property, out var value) && value.TryGetInt64(out var result) ? result : 0L;

    private static async Task<int> AbortAsync(string output, IReadOnlyList<object> runs, int providerCalls, string configHash, string semanticHash, string harnessHash, string reason, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "production-run-manifest.json"), new
        {
            schemaVersion = "a99-canonical-dev-v1-exec-v7-production-run-v1",
            status = "BLOCKED_ON_PROVIDER_EXECUTION_INTEGRITY",
            reason,
            benchmark = Benchmark,
            campaignId = CampaignId,
            productionSemanticHash = semanticHash,
            executionHarnessHash = harnessHash,
            runConfigurationHash = configHash,
            documents = runs,
            providerCalls,
            goldReads = 0,
            scoring = false,
            predictionFrozen = false,
        }, CancellationToken.None);
        return 2;
    }

    private static async Task<int> BlockAsync(string output, string reason, CancellationToken ct)
    {
        Directory.CreateDirectory(output);
        await WriteJsonAsync(Path.Combine(output, "failure.v1.json"), new
        {
            schemaVersion = "a99-canonical-dev-v1-exec-v7-failure-v1",
            status = "BLOCKED",
            reason,
            campaignId = CampaignId,
            providerCalls = 0,
            goldReads = 0,
        }, ct);
        return 2;
    }

    private static SourceEntry[] LoadInventory(string repoRoot)
    {
        var path = Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("documents").EnumerateArray()
            .Select(item => new SourceEntry(item.GetProperty("documentId").GetString()!, item.GetProperty("sourcePath").GetString() ?? "", item.GetProperty("sourceSha256").GetString() ?? ""))
            .ToArray();
    }

    private static ResolvedSource ResolveSource(string repoRoot, SourceEntry item)
    {
        var full = Path.GetFullPath(Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar)));
        var exists = File.Exists(full);
        return new(item.DocumentId, full, item.SourceSha256, exists ? Sha256File(full) : "", exists);
    }

    private static string ComputeProductionSemanticHash(string repoRoot)
    {
        var files = new[]
        {
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/AuthorityExtractionPipeline.cs",
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/DocxAuthorityPipeline.cs",
            "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfBlockAnalyst.cs",
            "src/DocxHeaderExtractor.DocumentProcessing/Authority/RouteExecutionAudit.cs",
            "src/DocxHeaderExtractor.DocumentProcessing/OpenXmlLayer/OpenXmlDocumentSource.cs",
            "src/DocxHeaderExtractor.DocumentProcessing/Authority/RouteOccurrenceTraceBuilder.cs",
        };
        return Sha256Text(string.Join("|", files.Select(path => path + ":" + FileHashOrMissing(repoRoot, path))));
    }

    private static string ComputeExecutionHarnessHash(string repoRoot)
    {
        var files = new[]
        {
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalDevV7OptimizedRunner.cs",
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalDevV1BaselineRunner.cs",
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/ProviderHardTimeoutIntegrity.cs",
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/ProviderTimeoutPolicyV2.cs",
            "src/DocxHeaderExtractor.Infrastructure/AI/OpenRouterHeaderExtractor.cs",
            "src/DocxHeaderExtractor.Cli/Program.cs",
            "src/DocxHeaderExtractor.Cli/CommandLineOptions.cs",
        };
        return Sha256Text(string.Join("|", files.Select(path => path + ":" + FileHashOrMissing(repoRoot, path))));
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

    internal sealed record PerformanceAuditMetrics(
        int DocumentProviderCalls,
        int DocumentResponseCount,
        int BaseProviderCalls,
        int AdditionalProviderCallsObserved);
}
