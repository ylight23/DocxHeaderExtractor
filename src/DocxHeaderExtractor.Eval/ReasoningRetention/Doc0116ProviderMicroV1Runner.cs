using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>One-document, non-scorable provider execution forensic run.</summary>
public static class Doc0116ProviderMicroV1Runner
{
    private const string ExperimentId = "DOC0116_PROVIDER_MICRO_V1";
    private const string DocumentId = "DOC-0116";
    private const string ExpectedProductionSemanticHash = "5f0eb27dfa44068fc60c69e0dcf8a05b14a061ed7526610e4592d68c268fe5e6";
    private const int TimeoutSeconds = 300;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken cancellationToken = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, "artifacts", "execution-integrity", "doc0116-provider-micro-v1");
        Directory.CreateDirectory(output);
        var source = LoadSource(repoRoot);
        var productionSemanticHash = CanonicalDevV1BaselineRunner.ComputeProductionSemanticHashForIntegrity(repoRoot);
        var executionHarnessHash = CanonicalDevV1BaselineRunner.ComputeExecutionHarnessHashForIntegrity(repoRoot);
        if (!string.Equals(productionSemanticHash, ExpectedProductionSemanticHash, StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(Path.Combine(output, "summary.json"), new { status = "PRODUCTION_SEMANTIC_DRIFT", providerCalls = 0, goldReads = 0, productionSemanticHash }, cancellationToken);
            return 2;
        }
        var remote = RemoteInferenceOptions.FromEnvironment("openrouter");
        var runConfig = new
        {
            schemaVersion = "doc0116-provider-micro-v1-run-configuration-v1",
            experimentId = ExperimentId,
            documentId = DocumentId,
            productionSemanticCheckpoint = "40a0d5f",
            executionObservabilityCheckpoint = "3e1f8f6",
            productionSemanticHash,
            executionHarnessHash,
            provider = "OpenRouter",
            model = remote.Model,
            endpoint = remote.Endpoint.ToString(),
            temperature = "pipeline/provider default; not exposed",
            topP = "pipeline/provider default; not exposed",
            maxOutputTokens = remote.MaxOutputTokens,
            contextSize = remote.ContextSize,
            requestTimeoutSeconds = remote.RequestTimeoutSeconds,
            transientRequestRetries = remote.TransientRequestRetries,
            missingIdRetries = remote.MissingIdRetries,
            timeoutSeconds = TimeoutSeconds,
            retryPolicy = "EXECUTION_V1_DOCUMENT_WATCHDOG; NO MICRO-RUN RETRY",
            sourcePath = source.SourcePath,
            sourceSha256 = source.SourceSha256,
            promptHash = "PIPELINE_OWNED_PROMPTS_NOT_EXPOSED_AS_SINGLE_TEMPLATE",
            candidateGenerationHash = HashRelative(repoRoot, "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/DocxAuthorityPipeline.cs"),
            parserHash = HashRelative(repoRoot, "src/DocxHeaderExtractor.DocumentProcessing/OpenXmlLayer/OpenXmlDocumentSource.cs"),
            bindingHash = HashRelative(repoRoot, "src/DocxHeaderExtractor.DocumentProcessing/Authority/RouteOccurrenceTraceBuilder.cs"),
            goldReadsBeforePredictionFreeze = 0,
            historicalReadsBeforePredictionFreeze = 0,
        };
        var runConfigHash = Sha256Text(JsonSerializer.Serialize(runConfig, JsonOptions));
        var runConfigPath = Path.Combine(output, "run-configuration.json");
        if (File.Exists(runConfigPath))
        {
            await WriteJsonAsync(Path.Combine(output, "summary.json"), new { status = "DOC0116_REAL_PROVIDER_MICRO_V1_INVALID", reason = "RUN_CONFIGURATION_ALREADY_EXISTS; MICRO-RUN_NOT_RERUN", providerCalls = 0, goldReads = 0 }, cancellationToken);
            return 2;
        }
        await WriteJsonAsync(runConfigPath, runConfig, cancellationToken);
        await WriteJsonAsync(Path.Combine(output, "preflight.json"), new
        {
            status = "READY_FOR_ONE_REAL_PROVIDER_DOCUMENT",
            experimentId = ExperimentId,
            documentId = DocumentId,
            productionSemanticHash,
            executionHarnessHash,
            sourceSha256 = source.SourceSha256,
            timeoutSeconds = TimeoutSeconds,
            providerCallsBeforeRun = 0,
            goldReads = 0,
            noScoring = true,
        }, cancellationToken);

        var started = DateTimeOffset.UtcNow;
        var workDir = Path.Combine(output, "work", DocumentId, "A01");
        Directory.CreateDirectory(workDir);
        var requestHash = Sha256Text(JsonSerializer.Serialize(new { experimentId = ExperimentId, DocumentId, source.SourceSha256, runConfigHash }, JsonOptions));
        var jobPath = Path.Combine(workDir, "worker-job.json");
        await WriteJsonAsync(jobPath, new
        {
            benchmark = ExperimentId,
            campaignId = ExperimentId,
            productionSemanticHash,
            executionHarnessHash,
            documentId = DocumentId,
            sourcePath = source.SourcePath,
            sourceSha256 = source.SourceSha256,
            outputDir = workDir,
            runConfigurationHash = runConfigHash,
            attemptId = $"{ExperimentId}:{DocumentId}:A01:{started:yyyyMMddTHHmmssfffZ}",
            requestHash,
            started,
        }, cancellationToken);
        await WriteJsonAsync(Path.Combine(output, "provider-attempt-manifest.json"), new
        {
            schemaVersion = "doc0116-provider-micro-v1-attempt-manifest-v1",
            experimentId = ExperimentId,
            documentId = DocumentId,
            attempt = "A01",
            started,
            runConfigurationHash = runConfigHash,
            timeoutSeconds = TimeoutSeconds,
            retry = 0,
            goldReads = 0,
            providerCallsBeforeRun = 0,
        }, cancellationToken);

        var watchdog = await ProviderHardTimeoutIntegrity.RunWorkerAsync(jobPath, workDir, TimeSpan.FromSeconds(TimeoutSeconds), cancellationToken);
        var completed = DateTimeOffset.UtcNow;
        var telemetry = ReadTelemetry(workDir);
        var classification = Classify(watchdog, telemetry, completed - started);
        await WriteJsonAsync(Path.Combine(output, "timing-audit.json"), BuildTimingAudit(telemetry, watchdog), CancellationToken.None);
        await WriteJsonAsync(Path.Combine(output, "logical-call-timeline.json"), telemetry.LogicalCalls, CancellationToken.None);
        await WriteJsonAsync(Path.Combine(output, "request-shape.json"), telemetry.RequestShapes, CancellationToken.None);
        await WriteJsonAsync(Path.Combine(output, "transport-timeline.json"), telemetry.TransportEvents, CancellationToken.None);
        await WriteJsonAsync(Path.Combine(output, "provider-attempt-manifest.json"), new
        {
            schemaVersion = "doc0116-provider-micro-v1-attempt-manifest-v1",
            experimentId = ExperimentId,
            documentId = DocumentId,
            attempt = "A01",
            started,
            completed,
            runConfigurationHash = runConfigHash,
            timeoutSeconds = TimeoutSeconds,
            retry = 0,
            watchdogStatus = watchdog.Status,
            terminationMode = watchdog.TerminationMode,
            providerCalls = telemetry.LogicalCalls.Count,
            physicalAttempts = telemetry.Attempts.Count,
            attempts = telemetry.Attempts,
        }, CancellationToken.None);
        await WriteJsonAsync(Path.Combine(output, "classification.json"), classification, CancellationToken.None);
        var status = watchdog.Status == "COMPLETE" ? "DOC0116_REAL_PROVIDER_MICRO_V1_COMPLETE" : "DOC0116_REAL_PROVIDER_MICRO_V1_COMPLETE";
        var summary = new
        {
            schemaVersion = "doc0116-provider-micro-v1-summary-v1",
            status,
            experimentId = ExperimentId,
            documentId = DocumentId,
            nonScorable = true,
            productionSemanticHash,
            executionHarnessHash,
            sourceSha256 = source.SourceSha256,
            watchdogStatus = watchdog.Status,
            terminationMode = watchdog.TerminationMode,
            totalWallTimeMs = (completed - started).TotalMilliseconds,
            logicalCalls = telemetry.LogicalCalls.Count,
            physicalAttempts = telemetry.Attempts.Count,
            primaryClassification = classification.primaryClassification,
            providerCalls = telemetry.LogicalCalls.Count,
            goldReads = 0,
            scoring = false,
        };
        await WriteJsonAsync(Path.Combine(output, "summary.json"), summary, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(summary, telemetry, classification), CancellationToken.None);
        await WriteJsonAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "doc0116-provider-micro-v1-manifest-v1",
            status,
            experimentId = ExperimentId,
            documentId = DocumentId,
            runConfigurationHash = runConfigHash,
            productionSemanticHash,
            executionHarnessHash,
            sourceSha256 = source.SourceSha256,
            providerCalls = telemetry.LogicalCalls.Count,
            goldReads = 0,
            noScoring = true,
            artifacts = Directory.GetFiles(output, "*", SearchOption.TopDirectoryOnly).Select(path => new { path = Path.GetFileName(path), sha256 = HashFile(path) }).Where(x => x.path != "manifest.json").ToArray(),
        }, CancellationToken.None);
        return 0;
    }

    /// <summary>
    /// Rebuilds only derived forensic artifacts from the already persisted micro-run.
    /// This method never constructs a provider and never replays a request.
    /// </summary>
    public static async Task<int> RunOfflineReclassificationAsync(string repoRoot, CancellationToken cancellationToken = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, "artifacts", "execution-integrity", "doc0116-provider-micro-v1");
        var workDir = Path.Combine(output, "work", DocumentId, "A01");
        var telemetryDir = Path.Combine(workDir, "telemetry");
        if (!File.Exists(Path.Combine(output, "summary.json")) || !Directory.Exists(telemetryDir))
        {
            await WriteJsonAsync(Path.Combine(output, "offline-reclassification.json"), new
            {
                status = "RECLASSIFICATION_BLOCKED",
                reason = "PERSISTED_MICRO_RUN_ARTIFACTS_NOT_FOUND",
                providerCalls = 0,
                goldReads = 0,
            }, cancellationToken);
            return 2;
        }

        var priorClassificationPath = Path.Combine(output, "classification.json");
        var priorClassificationHash = File.Exists(priorClassificationPath) ? HashFile(priorClassificationPath) : null;
        var telemetry = ReadTelemetry(workDir);
        using var attemptManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "provider-attempt-manifest.json")));
        var manifestRoot = attemptManifest.RootElement;
        var watchdogStatus = manifestRoot.GetProperty("watchdogStatus").GetString() ?? "UNKNOWN";
        var terminationMode = manifestRoot.GetProperty("terminationMode").GetString() ?? "UNKNOWN";
        var elapsedMs = 0d;
        using (var summaryDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "summary.json"))))
        {
            if (summaryDocument.RootElement.TryGetProperty("totalWallTimeMs", out var elapsedElement))
                elapsedMs = elapsedElement.GetDouble();
        }

        var startedAt = manifestRoot.GetProperty("started").GetDateTimeOffset();
        var completedAt = manifestRoot.GetProperty("completed").GetDateTimeOffset();
        var watchdog = new ProviderHardTimeoutIntegrity.WatchdogResult(
            Mode: "DOC0116_PROVIDER_MICRO_V1_OFFLINE_RECLASSIFICATION",
            ChildPid: 0,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            Status: watchdogStatus,
            TerminationMode: terminationMode,
            ExitCode: null,
            Marker: null,
            Stdout: string.Empty,
            Stderr: string.Empty);
        var classification = Classify(watchdog, telemetry, TimeSpan.FromMilliseconds(elapsedMs));
        await WriteJsonAsync(Path.Combine(output, "classification.json"), classification, CancellationToken.None);
        await WriteJsonAsync(Path.Combine(output, "timing-audit.json"), BuildTimingAudit(telemetry, watchdog), CancellationToken.None);
        await WriteJsonAsync(Path.Combine(output, "offline-reclassification.json"), new
        {
            schemaVersion = "doc0116-provider-micro-v1-offline-reclassification-v1",
            status = "OFFLINE_RECLASSIFICATION_COMPLETE",
            reason = "CALL_SCOPED_FORENSIC_CORRECTION_ONLY",
            previousClassificationSha256 = priorClassificationHash,
            blockingCallOrdinal = telemetry.LogicalCalls.Count,
            providerCalls = 0,
            goldReads = 0,
            historicalReads = 0,
            rawTelemetryMutated = false,
            classification = classification.primaryClassification,
        }, CancellationToken.None);

        var summaryPath = Path.Combine(output, "summary.json");
        using (var summaryDocument = JsonDocument.Parse(File.ReadAllText(summaryPath)))
        {
            var old = summaryDocument.RootElement;
            await WriteJsonAsync(summaryPath, new
            {
                schemaVersion = old.GetProperty("schemaVersion").GetString(),
                status = old.GetProperty("status").GetString(),
                experimentId = old.GetProperty("experimentId").GetString(),
                documentId = old.GetProperty("documentId").GetString(),
                nonScorable = true,
                productionSemanticHash = old.GetProperty("productionSemanticHash").GetString(),
                executionHarnessHash = old.GetProperty("executionHarnessHash").GetString(),
                sourceSha256 = old.GetProperty("sourceSha256").GetString(),
                watchdogStatus,
                terminationMode,
                totalWallTimeMs = old.GetProperty("totalWallTimeMs").GetDouble(),
                logicalCalls = telemetry.LogicalCalls.Count,
                physicalAttempts = telemetry.Attempts.Count,
                primaryClassification = classification.primaryClassification,
                blockingCallOrdinal = telemetry.LogicalCalls.Count,
                blockingCallStartOffsetMs = classification.blockingCallStartOffsetMs,
                completedCallsBeforeBlocking = classification.completedCallsBeforeBlocking,
                sumCompletedCallDurationMs = classification.sumCompletedCallDurationMs,
                medianCompletedCallMs = classification.medianCompletedCallMs,
                p95CompletedCallMs = classification.p95CompletedCallMs,
                providerCalls = telemetry.LogicalCalls.Count,
                goldReads = 0,
                historicalReads = 0,
                scoring = false,
                reclassifiedOffline = true,
            }, CancellationToken.None);
        }

        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(
            new { }, telemetry, classification), CancellationToken.None);
        await WriteManifestAsync(output, cancellationToken);
        return 0;
    }

    private static SourceInfo LoadSource(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "eval", "a99-dataset", "document-inventory.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var item = doc.RootElement.GetProperty("documents").EnumerateArray().Single(x => x.GetProperty("documentId").GetString() == DocumentId);
        var relative = item.GetProperty("sourcePath").GetString()!.Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.Combine(repoRoot, relative);
        var sha = HashFile(full);
        var expected = item.GetProperty("sourceSha256").GetString()!;
        if (!string.Equals(sha, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("DOC-0116 source SHA mismatch.");
        return new SourceInfo(full, sha);
    }

    private static TelemetrySnapshot ReadTelemetry(string workDir)
    {
        var dir = Path.Combine(workDir, "telemetry");
        var logical = Directory.Exists(dir) ? Directory.GetFiles(dir, "logical-call.started.*.json").Select(ReadJson).OrderBy(x => x.GetProperty("callOrdinal").GetInt32()).ToArray() : [];
        var attempts = Directory.Exists(dir) ? Directory.GetFiles(dir, "attempt.started.*.json").Select(ReadJson).OrderBy(x => x.GetProperty("callOrdinal").GetInt32()).ToArray() : [];
        var events = ReadJsonLines(Path.Combine(dir, "events.jsonl"));
        var heartbeats = ReadJsonLines(Path.Combine(dir, "heartbeats.jsonl"));
        return new TelemetrySnapshot(
            logical.Select(x => (object)new { callOrdinal = x.GetProperty("callOrdinal").GetInt32(), logicalCallId = x.GetProperty("logicalCallId").GetString(), stage = x.GetProperty("stage").GetString(), requestBytes = x.GetProperty("requestBytes").GetInt32(), estimatedInputTokens = x.GetProperty("estimatedInputTokens").GetInt32(), candidateCount = x.GetProperty("candidateCount").GetInt32(), contextItemCount = x.GetProperty("contextItemCount").GetInt32(), contextCharacterCount = x.GetProperty("contextCharacterCount").GetInt32(), requestHash = x.GetProperty("requestHash").GetString(), timestamp = x.GetProperty("timestamp").GetString() }).ToArray(),
            attempts.Select(x => (object)new { attemptId = x.GetProperty("attemptId").GetString(), callOrdinal = x.GetProperty("callOrdinal").GetInt32(), requestBytes = x.GetProperty("requestBytes").GetInt32(), estimatedInputTokens = x.GetProperty("estimatedInputTokens").GetInt32(), requestHash = x.GetProperty("requestHash").GetString(), timestamp = x.GetProperty("timestamp").GetString() }).ToArray(),
            events,
            heartbeats);
    }

    private static ClassificationResult Classify(ProviderHardTimeoutIntegrity.WatchdogResult watchdog, TelemetrySnapshot telemetry, TimeSpan elapsed)
    {
        var timing = BuildTimingAudit(telemetry, watchdog);
        var blockingOrdinal = telemetry.LogicalCalls.Count;
        var scopedEvents = telemetry.Events.Where(x => GetInt32(x, "callOrdinal") == blockingOrdinal).ToArray();
        var scopedHeartbeats = telemetry.Heartbeats.Where(x => GetInt32(x, "callOrdinal") == blockingOrdinal).ToArray();
        var latest = scopedEvents.LastOrDefault();
        var type = latest.ValueKind == JsonValueKind.Undefined ? null : GetString(latest, "eventType");
        var primary = watchdog.Status == "COMPLETE" ? "H. OTHER_OBSERVED_STAGE" : timing.documentBudgetExhaustionEvidence ? "G. DOCUMENT_BUDGET_EXHAUSTED_BY_MULTIPLE_HEALTHY_CALLS" : type switch
        {
            "RESPONSE_HEADERS_RECEIVED" => "D. PROVIDER_RESPONSE_STREAM_STALL",
            "RESPONSE_BODY_COMPLETE" => "E. RESPONSE_PARSE_STALL",
            "PARSE_STARTED" => "E. RESPONSE_PARSE_STALL",
            "BIND_STARTED" => "F. RESPONSE_BIND_STALL",
            "TRANSPORT_START" or "NOT_OBSERVABLE_WITH_CURRENT_TRANSPORT" => "I. STILL_UNOBSERVABLE",
            _ => "I. STILL_UNOBSERVABLE",
        };
        var heartbeat = scopedHeartbeats.LastOrDefault();
        var lastTransport = GetString(latest, "milestone")
            ?? (heartbeat.ValueKind == JsonValueKind.Undefined ? null : GetString(heartbeat, "lastTransportMilestone"))
            ?? type;
        return new ClassificationResult
        {
            primaryClassification = primary,
            watchdogStatus = watchdog.Status,
            terminationMode = watchdog.TerminationMode,
            elapsedMs = elapsed.TotalMilliseconds,
            blockingCallStartOffsetMs = timing.blockingCallStartOffsetMs,
            completedCallsBeforeBlocking = timing.completedCallsBeforeBlocking,
            sumCompletedCallDurationMs = timing.sumCompletedCallDurationMs,
            medianCompletedCallMs = timing.medianCompletedCallMs,
            p95CompletedCallMs = timing.p95CompletedCallMs,
            documentBudgetExhaustionEvidence = timing.documentBudgetExhaustionEvidence,
            blockingLogicalCall = telemetry.LogicalCalls.LastOrDefault(),
            blockingAttempt = telemetry.Attempts.LastOrDefault(),
            lastTransportMilestone = lastTransport,
            lastHeartbeat = heartbeat.ValueKind == JsonValueKind.Undefined ? null : heartbeat,
            responseHeadersReceived = scopedEvents.Any(x => GetString(x, "eventType") == "RESPONSE_HEADERS_RECEIVED") ? "YES" : "NO",
            firstResponseByte = scopedEvents.Any(x => GetString(x, "eventType") == "FIRST_RESPONSE_BYTE") ? "YES/NO_AS_OBSERVED" : "NO",
            responseBodyComplete = scopedEvents.Any(x => GetString(x, "eventType") == "RESPONSE_BODY_COMPLETE") ? "YES" : "NO",
            parseStarted = scopedEvents.Any(x => GetString(x, "eventType") == "PARSE_STARTED"),
            bindStarted = scopedEvents.Any(x => GetString(x, "eventType") == "BIND_STARTED"),
            nextExperiment = "PER_CALL_TIMEOUT_EXPERIMENT",
        };
    }

    private static string BuildReport(object summary, TelemetrySnapshot telemetry, ClassificationResult classification) => $"# DOC-0116 provider micro-run\n\nStatus: DOC0116_REAL_PROVIDER_MICRO_V1_COMPLETE\n\nPrimary classification: {classification.primaryClassification}\n\n- Logical calls: {telemetry.LogicalCalls.Count}\n- Physical attempts: {telemetry.Attempts.Count}\n- Provider calls: {telemetry.LogicalCalls.Count}\n- Completed calls before watchdog: {classification.completedCallsBeforeBlocking}\n- Blocking call start offset: {classification.blockingCallStartOffsetMs:0.###} ms\n- Blocking call elapsed before kill: {Math.Max(0, classification.elapsedMs - classification.blockingCallStartOffsetMs):0.###} ms\n- Sum completed-call durations: {classification.sumCompletedCallDurationMs:0.###} ms\n- Median completed-call latency: {classification.medianCompletedCallMs:0.###} ms\n- P95 completed-call latency: {classification.p95CompletedCallMs:0.###} ms\n- Document-budget exhaustion evidence: {classification.documentBudgetExhaustionEvidence}\n- Gold reads: 0\n- Non-scorable forensic run: true\n\nThe run uses the 300-second process-tree watchdog. Call-scoped transport evidence is in `classification.json`; the complete offline timing reconstruction is in `timing-audit.json`.\n";

    private static TimingAudit BuildTimingAudit(TelemetrySnapshot telemetry, ProviderHardTimeoutIntegrity.WatchdogResult watchdog)
    {
        var timings = telemetry.Events
            .GroupBy(x => GetInt32(x, "callOrdinal"))
            .Where(x => x.Key > 0)
            .OrderBy(x => x.Key)
            .Select(group =>
            {
                var events = group.OrderBy(x => GetDateTimeOffset(x, "timestamp")).ToArray();
                var start = GetDateTimeOffset(events[0], "timestamp");
                var terminal = events.LastOrDefault(x => GetString(x, "eventType") is "LOGICAL_CALL_COMPLETED" or "LOGICAL_CALL_FAILED" or "LOGICAL_CALL_CANCELLED");
                var end = terminal.ValueKind == JsonValueKind.Undefined ? (DateTimeOffset?)null : GetDateTimeOffset(terminal, "timestamp");
                return new CallTiming
                {
                    callOrdinal = group.Key,
                    startedAt = start,
                    completedAt = end,
                    startOffsetMs = (start - watchdog.StartedAt).TotalMilliseconds,
                    durationMs = end.HasValue ? (end.Value - start).TotalMilliseconds : null,
                    terminalEvent = terminal.ValueKind == JsonValueKind.Undefined ? null : GetString(terminal, "eventType"),
                    lastEvent = GetString(events[^1], "eventType"),
                    lastTransportMilestone = GetString(events[^1], "milestone"),
                };
            })
            .ToArray();
        var completed = timings.Where(x => x.durationMs.HasValue).Select(x => x.durationMs!.Value).OrderBy(x => x).ToArray();
        var median = completed.Length == 0 ? 0 : completed[(completed.Length - 1) / 2];
        var p95 = completed.Length == 0 ? 0 : completed[Math.Min(completed.Length - 1, Math.Max(0, (int)Math.Ceiling(completed.Length * 0.95) - 1))];
        var blocking = timings.OrderBy(x => x.callOrdinal).LastOrDefault();
        var blockingOffset = blocking?.startOffsetMs ?? 0;
        var documentElapsed = Math.Max(0, (watchdog.CompletedAt - watchdog.StartedAt).TotalMilliseconds);
        return new TimingAudit
        {
            schemaVersion = "doc0116-provider-micro-v1-timing-audit-v1",
            documentStartedAt = watchdog.StartedAt,
            watchdogCompletedAt = watchdog.CompletedAt,
            watchdogBudgetMs = documentElapsed,
            completedCallsBeforeBlocking = completed.Length,
            blockingCallOrdinal = blocking?.callOrdinal ?? 0,
            blockingCallStartOffsetMs = blockingOffset,
            blockingCallElapsedBeforeKillMs = blocking is null ? 0 : Math.Max(0, (watchdog.CompletedAt - blocking.startedAt).TotalMilliseconds),
            sumCompletedCallDurationMs = completed.Sum(),
            medianCompletedCallMs = median,
            p95CompletedCallMs = p95,
            documentBudgetExhaustionEvidence = watchdog.Status == "DOCUMENT_TIMEOUT" && completed.Length >= 2 && blockingOffset >= documentElapsed * 0.95,
            calls = timings,
        };
    }

    private static async Task WriteManifestAsync(string output, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(output, "manifest.json");
        var telemetryDir = Path.Combine(output, "work", DocumentId, "A01", "telemetry");
        var providerCallCount = Directory.Exists(telemetryDir)
            ? Directory.GetFiles(telemetryDir, "logical-call.started.*.json").Length
            : 0;
        await WriteJsonAsync(manifestPath, new
        {
            schemaVersion = "doc0116-provider-micro-v1-manifest-v1",
            status = "DOC0116_REAL_PROVIDER_MICRO_V1_COMPLETE",
            experimentId = ExperimentId,
            documentId = DocumentId,
            productionSemanticHash = ExpectedProductionSemanticHash,
            providerCalls = providerCallCount,
            goldReads = 0,
            historicalReads = 0,
            noScoring = true,
            offlineReclassification = true,
            artifacts = Directory.GetFiles(output, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new { path = Path.GetFileName(path), sha256 = HashFile(path) })
                .Where(x => x.path != "manifest.json")
                .ToArray(),
        }, cancellationToken);
    }

    private static int GetInt32(JsonElement element, string propertyName) => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
    private static string? GetString(JsonElement element, string propertyName) => element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static DateTimeOffset GetDateTimeOffset(JsonElement element, string propertyName) => element.GetProperty(propertyName).GetDateTimeOffset();
    private static JsonElement ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    private static JsonElement[] ReadJsonLines(string path) => File.Exists(path) ? File.ReadAllLines(path).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => JsonDocument.Parse(x).RootElement.Clone()).ToArray() : [];
    private static string HashRelative(string repoRoot, string relative) => HashFile(Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), ct);

    private sealed record SourceInfo(string SourcePath, string SourceSha256);
    private sealed record TelemetrySnapshot(IReadOnlyList<object> LogicalCalls, IReadOnlyList<object> Attempts, JsonElement[] Events, JsonElement[] Heartbeats)
    {
        public object[] RequestShapes => LogicalCalls.ToArray();
        public JsonElement[] TransportEvents => Events;
    }

    private sealed record ClassificationResult
    {
        public required string primaryClassification { get; init; }
        public required string watchdogStatus { get; init; }
        public required string terminationMode { get; init; }
        public required double elapsedMs { get; init; }
        public required double blockingCallStartOffsetMs { get; init; }
        public required int completedCallsBeforeBlocking { get; init; }
        public required double sumCompletedCallDurationMs { get; init; }
        public required double medianCompletedCallMs { get; init; }
        public required double p95CompletedCallMs { get; init; }
        public required bool documentBudgetExhaustionEvidence { get; init; }
        public object? blockingLogicalCall { get; init; }
        public object? blockingAttempt { get; init; }
        public string? lastTransportMilestone { get; init; }
        public JsonElement? lastHeartbeat { get; init; }
        public required string responseHeadersReceived { get; init; }
        public required string firstResponseByte { get; init; }
        public required string responseBodyComplete { get; init; }
        public required bool parseStarted { get; init; }
        public required bool bindStarted { get; init; }
        public required string nextExperiment { get; init; }
    }

    private sealed record CallTiming
    {
        public required int callOrdinal { get; init; }
        public required DateTimeOffset startedAt { get; init; }
        public DateTimeOffset? completedAt { get; init; }
        public required double startOffsetMs { get; init; }
        public double? durationMs { get; init; }
        public string? terminalEvent { get; init; }
        public string? lastEvent { get; init; }
        public string? lastTransportMilestone { get; init; }
    }

    private sealed record TimingAudit
    {
        public required string schemaVersion { get; init; }
        public required DateTimeOffset documentStartedAt { get; init; }
        public required DateTimeOffset watchdogCompletedAt { get; init; }
        public required double watchdogBudgetMs { get; init; }
        public required int completedCallsBeforeBlocking { get; init; }
        public required int blockingCallOrdinal { get; init; }
        public required double blockingCallStartOffsetMs { get; init; }
        public required double blockingCallElapsedBeforeKillMs { get; init; }
        public required double sumCompletedCallDurationMs { get; init; }
        public required double medianCompletedCallMs { get; init; }
        public required double p95CompletedCallMs { get; init; }
        public required bool documentBudgetExhaustionEvidence { get; init; }
        public required IReadOnlyList<CallTiming> calls { get; init; }
    }
}
