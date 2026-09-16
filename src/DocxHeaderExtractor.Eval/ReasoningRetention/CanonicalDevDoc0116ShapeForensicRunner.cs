using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.DocumentProcessing.Review;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Offline forensic report for the aborted EXEC_V3 DOC-0116 run.</summary>
public static class CanonicalDevDoc0116ShapeForensicRunner
{
    private const string CampaignRoot = "artifacts/level-accuracy/canonical-dev-v1-exec-v3";
    private const string OutputRoot = "artifacts/execution-integrity/canonical-dev-doc0116-shape-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var campaign = Path.Combine(repoRoot, CampaignRoot.Replace('/', Path.DirectorySeparatorChar));
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        using var runManifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(campaign, "production-run-manifest.json"), ct));
        using var runConfig = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(campaign, "run-configuration.json"), ct));
        var docs = runManifest.RootElement.GetProperty("documents").EnumerateArray()
            .ToDictionary(x => x.GetProperty("documentId").GetString()!, StringComparer.OrdinalIgnoreCase);
        var config = runConfig.RootElement;
        var doc1 = await InspectAsync(campaign, docs["DOC-0001"], true, config, ct);
        var doc116 = await InspectAsync(campaign, docs["DOC-0116"], false, config, ct);
        var outputFiles = new[] { "summary.json", "request-shape.json", "attempt-timeline.json", "doc0001-vs-doc0116.json", "report.md" };

        var summary = new
        {
            schemaVersion = "a99-canonical-dev-doc0116-shape-summary-v1",
            status = "DOC0116_EXECUTION_SHAPE_DIAGNOSED",
            campaignId = "CANONICAL_DEV_V1_EXEC_V3",
            checkpoint = "55dc1d3",
            goldReads = 0,
            providerCalls = 0,
            semanticChanges = false,
            doc0001 = new { status = "COMPLETE", logicalCalls = doc1.LogicalCalls, physicalAttempts = doc1.PhysicalAttempts, largestRequestBytes = doc1.LargestRequestBytes, medianRequestBytes = doc1.MedianRequestBytes, largestEstimatedInputTokens = doc1.LargestTokens, medianEstimatedInputTokens = doc1.MedianTokens, riskBuckets = doc1.RiskBuckets },
            doc0116 = new { status = "DOCUMENT_TIMEOUT", watchdogSeconds = 300, workerWallSeconds = doc116.ElapsedSeconds, logicalCallsPlanned = (int?)null, logicalCallsStarted = 0, physicalAttemptsStarted = 0, requestShape = "UNOBSERVED", blockingStage = "UNKNOWN", observabilityGap = true },
            timeoutHierarchy = new { documentWatchdogSeconds = 300, providerRequestTimeoutSeconds = config.GetProperty("requestTimeoutSeconds").GetInt32(), providerConfiguredRetries = config.GetProperty("transientRequestRetries").GetInt32(), parentWatchdogOwnsProcessTree = true, actualObservedEnforcement = "PER_DOCUMENT_PARENT_WATCHDOG" },
            primaryExecutionBlocker = "OBSERVABILITY_GAP",
            recommendedNextExperiment = "D. OBSERVABILITY_INSTRUMENTATION",
            recommendationReason = "The document watchdog kill is proven, but the blocking logical call and transport phase are absent from frozen EXEC_V3 telemetry."
        };
        var requestShape = new
        {
            campaignId = "CANONICAL_DEV_V1_EXEC_V3",
            sourceOnly = new[] { doc1.SourceShape, doc116.SourceShape },
            completedDoc0001Requests = doc1.RequestShapes,
            doc0116RequestShape = new { exactSerializedPayloadAvailable = false, reason = "No request/attempt artifact was persisted before the document watchdog killed the worker.", estimatedInputTokens = (int?)null, requestBytes = (long?)null, riskBucket = "UNKNOWN" },
            convention = new { tokenEstimator = "ReasoningTokenBudget.EstimateTokens(chars / 3.4)", exactBytesMeaning = "UTF-8 bytes of persisted model input contract; full provider HTTP envelope was not retained by EXEC_V3.", maxOutputTokens = config.GetProperty("maxOutputTokens").GetInt32() }
        };
        var timeline = new { documents = new[] { doc1.AttemptTimeline, doc116.AttemptTimeline }, lastCompletedLogicalCall = doc1.LastCompletedLogicalCall, blockingLogicalCall = (string?)null, blockingStage = "UNKNOWN", observabilityGap = true };
        var comparison = new { doc0001 = doc1.Comparison, doc0116 = doc116.Comparison, comparisonLimit = "DOC-0001 has persisted model-input contracts; DOC-0116 has source-only and watchdog lineage only." };
        await WriteJsonAsync(Path.Combine(output, "summary.json"), summary, ct);
        await WriteJsonAsync(Path.Combine(output, "request-shape.json"), requestShape, ct);
        await WriteJsonAsync(Path.Combine(output, "attempt-timeline.json"), timeline, ct);
        await WriteJsonAsync(Path.Combine(output, "doc0001-vs-doc0116.json"), comparison, ct);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(doc1, doc116, config), ct);
        await WriteJsonAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-canonical-dev-doc0116-shape-manifest-v1",
            status = "DIAGNOSTIC_COMPLETE",
            campaignRoot = CampaignRoot,
            sourceArtifacts = new[] { "production-run-manifest.json", "DOC-0001/prediction.v1.json", "DOC-0001/attempts.v1.json", "DOC-0116/attempt.started.A01-20260916T100206661Z.json", "DOC-0116/failure-20260916T100206661Z.v1.json", "work/DOC-0116/A01/worker-job.json" },
            goldReads = 0,
            providerCalls = 0,
            semanticMutation = false,
            outputFiles = outputFiles.Select(name => new { path = name, sha256 = Sha256File(Path.Combine(output, name)) }).ToArray()
        }, ct);
        return 0;
    }

    private static async Task<DocForensics> InspectAsync(string campaign, JsonElement runDocument, bool completed, JsonElement config, CancellationToken ct)
    {
        var id = runDocument.GetProperty("documentId").GetString()!;
        var docDir = Path.Combine(campaign, id);
        var workerJob = Path.Combine(campaign, "work", id, "A01", "worker-job.json");
        var sourcePath = runDocument.TryGetProperty("sourcePath", out var sourceElement)
            ? sourceElement.GetString()!
            : ReadWorkerSourcePath(workerJob);
        var sourceInfo = new FileInfo(sourcePath);
        var snapshot = new AuthorityDocumentSourceReader().Read(sourcePath);
        var failure = Directory.EnumerateFiles(docDir, "failure*.json").FirstOrDefault();
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;
        if (File.Exists(workerJob))
        {
            using var job = JsonDocument.Parse(await File.ReadAllTextAsync(workerJob, ct));
            start = job.RootElement.GetProperty("started").GetDateTimeOffset();
        }
        if (failure is not null)
        {
            using var f = JsonDocument.Parse(await File.ReadAllTextAsync(failure, ct));
            end = f.RootElement.GetProperty("completed").GetDateTimeOffset();
        }
        var modelHeadingCount = (int?)null;
        var modelNodeCount = (int?)null;
        var requests = new List<object>();
        var logicalCalls = 0;
        var physicalAttempts = 0;
        var lastCompleted = (string?)null;
        if (completed)
        {
            using var prediction = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(docDir, "prediction.v1.json"), ct));
            using var attempts = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(docDir, "attempts.v1.json"), ct));
            var p = prediction.RootElement;
            var a = attempts.RootElement;
            end = p.GetProperty("completed").GetDateTimeOffset();
            modelHeadingCount = p.GetProperty("occurrencePredictions").GetArrayLength();
            modelNodeCount = p.GetProperty("semanticNodes").GetArrayLength();
            physicalAttempts = a.GetProperty("providerCalls").GetInt32();
            var auditRequests = a.GetProperty("requests");
            logicalCalls = auditRequests.GetArrayLength();
            lastCompleted = auditRequests.EnumerateArray().Last().GetProperty("requestId").GetString();
            var contracts = p.GetProperty("parentCandidateContracts");
            var modelRequests = p.GetProperty("modelRequests");
            for (var i = 0; i < Math.Min(contracts.GetArrayLength(), modelRequests.GetArrayLength()); i++)
            {
                var contract = contracts[i].GetString() ?? "";
                using var c = JsonDocument.Parse(contract);
                var chars = contract.Length;
                var tokens = ReasoningTokenBudget.EstimateTokens(chars);
                var bytes = Encoding.UTF8.GetByteCount(contract);
                var candidateCount = modelRequests[i].GetProperty("candidateIds").GetArrayLength();
                var contextUnits = c.RootElement.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array ? blocks.GetArrayLength() : (int?)null;
                requests.Add(new { requestId = modelRequests[i].GetProperty("requestId").GetString(), stage = modelRequests[i].GetProperty("stage").GetString(), requestBytes = bytes, estimatedInputTokens = tokens, maxOutputTokens = config.GetProperty("maxOutputTokens").GetInt32(), candidateCount, contextUnitsIncluded = contextUnits, exactPersistedContract = true, fullProviderEnvelopePersisted = false, responseObserved = modelRequests[i].GetProperty("responseObserved").GetBoolean(), riskBucket = Bucket(tokens) });
            }
        }
        var requestBytes = requests.Select(x => (int)x.GetType().GetProperty("requestBytes")!.GetValue(x)!).OrderBy(x => x).ToArray();
        var requestTokens = requests.Select(x => (int)x.GetType().GetProperty("estimatedInputTokens")!.GetValue(x)!).OrderBy(x => x).ToArray();
        var shape = new { documentId = id, sourcePath, sourceBytes = sourceInfo.Length, sourceSha256 = Sha256File(sourcePath), parserParagraphCount = snapshot.Document.Paragraphs.Count, productionCandidateCountBeforeModel = snapshot.CandidateIndexes.Count, detectedHeadingCountBeforeModel = snapshot.CandidateIndexes.Count, modelSelectedHeadingCount = modelHeadingCount, modelSemanticNodeCount = modelNodeCount, semanticInputNodeCountStatus = completed ? "MODEL_OUTPUT_COUNTS_FROM_FROZEN_PREDICTION" : "UNAVAILABLE_BEFORE_TIMEOUT" };
        var elapsed = start.HasValue && end.HasValue ? (end.Value - start.Value).TotalSeconds : (double?)null;
        return new DocForensics(id, sourceInfo.Length, snapshot.Document.Paragraphs.Count, snapshot.CandidateIndexes.Count, modelHeadingCount, modelNodeCount, start, end, elapsed, logicalCalls, physicalAttempts, lastCompleted, requests, requestBytes.Length == 0 ? null : requestBytes.Max(), requestBytes.Length == 0 ? null : Median(requestBytes), requestTokens.Length == 0 ? null : requestTokens.Max(), requestTokens.Length == 0 ? null : Median(requestTokens), requests.GroupBy(x => (string)x.GetType().GetProperty("riskBucket")!.GetValue(x)!).ToDictionary(x => x.Key, x => x.Count()), shape, new { documentId = id, workerStart = start, workerTermination = end, elapsedSeconds = elapsed, logicalCalls, physicalAttempts, requests, lastCompletedLogicalCall = lastCompleted }, new { sourceBytes = sourceInfo.Length, parserParagraphCount = snapshot.Document.Paragraphs.Count, productionCandidateCountBeforeModel = completed ? snapshot.CandidateIndexes.Count : (int?)null, logicalCalls = completed ? logicalCalls : (int?)null, requestBytes = requestBytes.Length == 0 ? null : (int[]?)requestBytes, estimatedInputTokens = requestTokens.Length == 0 ? null : (int[]?)requestTokens, largestRequestBytes = requestBytes.Length == 0 ? (int?)null : requestBytes.Max(), medianRequestBytes = requestBytes.Length == 0 ? (int?)null : Median(requestBytes) });
    }

    private static string BuildReport(DocForensics doc1, DocForensics doc116, JsonElement config)
    {
        var b = new StringBuilder();
        b.AppendLine("# DOC-0116 EXECUTION SHAPE FORENSIC V1");
        b.AppendLine();
        b.AppendLine("Status: `DOC0116_EXECUTION_SHAPE_DIAGNOSED`");
        b.AppendLine();
        b.AppendLine("## Finding");
        b.AppendLine();
        b.AppendLine($"EXEC_V3 enforced a 300-second **per-document parent watchdog**. DOC-0116 started at `{doc116.Start:O}` and was terminated at `{doc116.End:O}` with `PROCESS_TREE_KILL`. DOC-0001 completed in `{doc1.ElapsedSeconds:0.###}` seconds and made `{doc1.PhysicalAttempts}` provider calls.");
        b.AppendLine();
        b.AppendLine("The exact blocking logical call and transport phase are **UNKNOWN**. EXEC_V3 persisted the worker job before entering the child, but did not persist per-logical-call request/attempt telemetry before timeout. No request bytes, response headers, stream events, parse event, or binding event survived for DOC-0116. This is an `OBSERVABILITY_GAP`, not evidence of a provider stall or oversized request.");
        b.AppendLine();
        b.AppendLine("## Time budget");
        b.AppendLine();
        b.AppendLine("| Layer | Value / observation |");
        b.AppendLine("|---|---|");
        b.AppendLine("| Parent document watchdog | 300 seconds |");
        b.AppendLine($"| Provider request timeout | {config.GetProperty("requestTimeoutSeconds").GetInt32()} seconds |");
        b.AppendLine($"| Provider configured retries | {config.GetProperty("transientRequestRetries").GetInt32()} |");
        b.AppendLine("| Actual terminating layer | parent watchdog, process-tree kill |");
        b.AppendLine($"| DOC-0116 wall time | {doc116.ElapsedSeconds:0.###} seconds |");
        b.AppendLine();
        b.AppendLine("The run proves the document watchdog is coarse relative to a multi-call document, but cannot distinguish request construction, provider wait, response parse, or binding.");
        b.AppendLine();
        b.AppendLine("## Production-input comparison");
        b.AppendLine();
        b.AppendLine("| Metric | DOC-0001 | DOC-0116 |");
        b.AppendLine("|---|---:|---:|");
        b.AppendLine($"| Source bytes | {doc1.SourceBytes} | {doc116.SourceBytes} |");
        b.AppendLine($"| Parser paragraphs | {doc1.ParserParagraphs} | {doc116.ParserParagraphs} |");
        b.AppendLine($"| Candidate/heading inputs before model | {doc1.SourceCandidateCount} | {doc116.SourceCandidateCount} |");
        b.AppendLine($"| Logical provider calls | {doc1.LogicalCalls} | unknown |");
        b.AppendLine($"| Persisted request contracts | {doc1.RequestShapes.Count} | 0 |");
        b.AppendLine($"| Largest persisted contract bytes | {doc1.LargestRequestBytes?.ToString() ?? "unknown"} | unknown |");
        b.AppendLine($"| Largest estimated input tokens | {doc1.LargestTokens?.ToString() ?? "unknown"} | unknown |");
        b.AppendLine();
        b.AppendLine("DOC-0001 measurements are UTF-8 bytes and `ReasoningTokenBudget` estimates of persisted model-input contracts. They are not full provider HTTP-envelope sizes. DOC-0116 has no persisted request contract to measure.");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine();
        b.AppendLine("Primary execution blocker: `OBSERVABILITY_GAP`.");
        b.AppendLine();
        b.AppendLine("Recommended next experiment: **D. OBSERVABILITY_INSTRUMENTATION**. Add append-only per-logical-call events before transport and at request-built, headers-received, body-progress, response-complete, parse-start, parse-complete, bind-start, and bind-complete. This changes execution observability only; it does not change production semantic behavior.");
        b.AppendLine();
        b.AppendLine("No EXEC_V4, provider call, Gold read, scoring, truncation, or request repair was performed.");
        return b.ToString();
    }

    private static int Median(int[] values) => values[values.Length / 2];
    private static string Bucket(int tokens) => tokens switch { < 32_000 => "<32k", < 64_000 => "32k-64k", < 128_000 => "64k-128k", < 256_000 => "128k-256k", < 512_000 => "256k-512k", _ => ">512k" };
    private static string Sha256File(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static string ReadWorkerSourcePath(string path)
    {
        using var job = JsonDocument.Parse(File.ReadAllText(path));
        return job.RootElement.GetProperty("sourcePath").GetString()!;
    }
    private static Task WriteJsonAsync(string path, object value, CancellationToken ct) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, ct);

    private sealed record DocForensics(string DocumentId, long SourceBytes, int ParserParagraphs, int SourceCandidateCount, int? ModelHeadingCount, int? ModelNodeCount, DateTimeOffset? Start, DateTimeOffset? End, double? ElapsedSeconds, int LogicalCalls, int PhysicalAttempts, string? LastCompletedLogicalCall, List<object> RequestShapes, int? LargestRequestBytes, int? MedianRequestBytes, int? LargestTokens, int? MedianTokens, Dictionary<string, int> RiskBuckets, object SourceShape, object AttemptTimeline, object Comparison);
}
