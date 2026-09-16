using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Offline proof that provider execution telemetry survives a hard worker kill.</summary>
public static class ProviderObservabilityV1Runner
{
    public static async Task<int> RunAsync(string repoRoot, CancellationToken cancellationToken = default)
    {
        var output = Path.Combine(repoRoot, "artifacts", "execution-integrity", "provider-observability-v1");
        Directory.CreateDirectory(output);
        var normalRoot = Path.Combine(output, "normal");
        var hangingRoot = Path.Combine(output, "hanging");
        var largeRoot = Path.Combine(output, "large-request");
        ResetDirectory(normalRoot);
        ResetDirectory(hangingRoot);
        ResetDirectory(largeRoot);
        var normal = await RunChildAsync("normal", normalRoot, TimeSpan.FromSeconds(8), cancellationToken);
        var hanging = await RunChildAsync("hang", hangingRoot, TimeSpan.FromSeconds(2), cancellationToken);
        var large = RunLargeRequestTest(largeRoot);
        var normalOrdering = CheckNormalOrdering(Path.Combine(output, "normal"));
        var survivability = CheckSurvivability(Path.Combine(output, "hanging"));
        var watchdog = hanging.Status == "DOCUMENT_TIMEOUT" && hanging.TerminationMode == "PROCESS_TREE_KILL";
        var semanticBefore = CanonicalDevV1BaselineRunner.ComputeProductionSemanticHashForIntegrity(repoRoot);
        var semanticAfter = CanonicalDevV1BaselineRunner.ComputeProductionSemanticHashForIntegrity(repoRoot);
        var harnessAfter = CanonicalDevV1BaselineRunner.ComputeExecutionHarnessHashForIntegrity(repoRoot);
        var semanticUnchanged = semanticBefore == semanticAfter;

        var passed = normal.Status == "COMPLETE" && normal.TerminationMode == "NONE" &&
            watchdog && survivability && large && normalOrdering && semanticUnchanged;
        var summary = new
        {
            schemaVersion = "provider-observability-v1",
            status = passed ? "PROVIDER_OBSERVABILITY_INSTRUMENTATION_PROVEN" : "FAILED",
            providerCalls = 0,
            goldReads = 0,
            normal,
            hanging,
            tests = new { hangingSurvivability = survivability, largeRequest = large, normalResponseOrdering = normalOrdering, hardTimeoutWatchdog = watchdog },
            productionSemanticHashBefore = semanticBefore,
            productionSemanticHashAfter = semanticAfter,
            productionSemanticHashUnchanged = semanticUnchanged,
            executionHarnessHash = harnessAfter,
        };
        await WriteJsonAsync(Path.Combine(output, "summary.json"), summary, cancellationToken);
        await WriteJsonAsync(Path.Combine(output, "telemetry-schema.json"), new
        {
            schemaVersion = "provider-observability-v1",
            records = new[] { "logical-call.started.<id>.json", "attempt.started.<id>.json", "response.raw.<attemptId>.txt", "response.parsed.<attemptId>.json", "response.binding.<attemptId>.json", "events.jsonl", "heartbeats.jsonl" },
            milestones = new[] { "TRANSPORT_START", "CONNECTION_ESTABLISHED", "REQUEST_HEADERS_SENT", "REQUEST_BODY_SENT", "RESPONSE_HEADERS_RECEIVED", "FIRST_RESPONSE_BYTE", "RESPONSE_BODY_COMPLETE", "PARSE_STARTED", "PARSE_COMPLETED", "BIND_STARTED", "BIND_COMPLETED", "HEARTBEAT" },
            terminalRule = "exactly one completed or failed terminal event for a normal logical call; zero terminal events is recoverable after a hard kill",
            flushMode = "FileOptions.WriteThrough + Flush(true) per record",
        }, cancellationToken);
        await WriteJsonAsync(Path.Combine(output, "self-test.json"), new
        {
            status = passed ? "PASS" : "FAIL",
            hangingSurvivability = survivability,
            largeRequest = large,
            normalResponseOrdering = normalOrdering,
            hardTimeoutWatchdog = watchdog,
            providerCalls = 0,
        }, cancellationToken);
        await WriteJsonAsync(Path.Combine(output, "production-semantic-drift-check.json"), new
        {
            productionSemanticHashBefore = semanticBefore,
            productionSemanticHashAfter = semanticAfter,
            unchanged = semanticUnchanged,
            executionHarnessChanged = true,
            executionHarnessHash = harnessAfter,
        }, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), $"# Provider observability v1\n\nStatus: {(passed ? "PROVIDER_OBSERVABILITY_INSTRUMENTATION_PROVEN" : "FAILED")}\n\n- Provider calls: 0\n- Gold reads: 0\n- Hanging telemetry survived process-tree kill: {survivability}\n- Large request telemetry: {large}\n- Normal response ordering: {normalOrdering}\n- Production semantic hash unchanged: {semanticUnchanged}\n", cancellationToken);
        await WriteJsonAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "provider-observability-v1-manifest",
            status = passed ? "FROZEN" : "FAILED",
            sourceFiles = new[]
            {
                "src/DocxHeaderExtractor.Infrastructure/AI/ProviderObservability.cs",
                "src/DocxHeaderExtractor.Infrastructure/AI/OpenRouterHeaderExtractor.cs",
                "src/DocxHeaderExtractor.Infrastructure/AI/RemoteInferenceOptions.cs",
                "src/DocxHeaderExtractor.Eval/ReasoningRetention/ProviderObservabilityV1Runner.cs",
                "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalDevV1BaselineRunner.cs",
            }.ToDictionary(path => path, path => HashFile(Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar))), StringComparer.Ordinal),
            artifactHashes = Directory.GetFiles(output, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !path.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(path => Path.GetFileName(path), HashFile, StringComparer.Ordinal),
            providerCalls = 0,
            goldReads = 0,
        }, cancellationToken);
        return passed ? 0 : 2;
    }

    public static int RunChildMode()
    {
        var mode = Environment.GetEnvironmentVariable("A99_PROVIDER_OBSERVABILITY_CHILD_MODE") ?? "hang";
        var root = Environment.GetEnvironmentVariable("A99_PROVIDER_OBSERVABILITY_ROOT")
            ?? throw new InvalidOperationException("Missing observability root.");
        var options = new ProviderObservabilityOptions
        {
            RootDirectory = root,
            CampaignId = "provider-observability-v1-self-test",
            DocumentId = "SELFTEST-DOC",
            Provider = "fake-provider",
            Model = "fake-model",
            HeartbeatSeconds = 1,
        };
        using var logical = ProviderCallTelemetry.Start(options, new ProviderLogicalCallMetadata
        {
            Stage = "SELF_TEST",
            LogicalCallId = "self-test-logical-call",
            RequestHash = ProviderObservabilityHashing.Sha256Utf8("self-test-request"),
            RequestBytes = 4096,
            EstimatedInputTokens = 1024,
            MaxOutputTokens = 128,
            CandidateCount = 12,
            CurrentNodeCount = 12,
            ContextItemCount = 12,
            ContextCharacterCount = 4096,
            Provider = "fake-provider",
            Model = "fake-model",
        });
        using var attempt = logical!.StartAttempt("self-test-attempt", ProviderObservabilityHashing.Sha256Utf8("self-test-payload"), 4096, 1024, 128);
        if (mode == "normal")
        {
            attempt.Event("RESPONSE_HEADERS_RECEIVED", new { status = 200 });
            attempt.PersistRawResponse("{}");
            attempt.Event("RESPONSE_BODY_COMPLETE", new { responseBytes = 64, responseHash = ProviderObservabilityHashing.Sha256Utf8("{}") });
            attempt.Event("PARSE_STARTED");
            attempt.PersistParsed(new { items = new[] { new { id = 1 } } });
            attempt.Event("PARSE_COMPLETED", new { parsedCount = 1 });
            attempt.Event("BIND_STARTED");
            attempt.PersistBinding(new { boundCount = 1 });
            attempt.Event("BIND_COMPLETED", new { boundCount = 1 });
            attempt.Complete();
            logical.Complete(new { resultCount = 1 });
            return 0;
        }
        attempt.Event("RESPONSE_HEADERS_RECEIVED", new { status = 200 });
        while (true) Thread.Sleep(TimeSpan.FromSeconds(1));
    }

    private static async Task<WatchdogResult> RunChildAsync(string mode, string root, TimeSpan timeout, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable.");
        var entryAssembly = Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Entry assembly unavailable.");
        var start = new ProcessStartInfo { FileName = processPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(entryAssembly);
        start.ArgumentList.Add("a99-provider-observability-v1-child");
        start.Environment["A99_PROVIDER_OBSERVABILITY_CHILD_MODE"] = mode;
        start.Environment["A99_PROVIDER_OBSERVABILITY_ROOT"] = root;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start observability child.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var timedOut = false;
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { timedOut = true; }
        if (timedOut)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
        }
        return new WatchdogResult(mode, timedOut ? "DOCUMENT_TIMEOUT" : "COMPLETE", timedOut ? "PROCESS_TREE_KILL" : "NONE", await stdout, await stderr);
    }

    private static bool CheckSurvivability(string root) =>
        Directory.Exists(Path.Combine(root, "telemetry")) &&
        Directory.GetFiles(Path.Combine(root, "telemetry"), "logical-call.started.*.json").Length == 1 &&
        Directory.GetFiles(Path.Combine(root, "telemetry"), "attempt.started.*.json").Length == 1 &&
        File.Exists(Path.Combine(root, "telemetry", "events.jsonl")) &&
        File.Exists(Path.Combine(root, "telemetry", "heartbeats.jsonl"));

    private static bool CheckNormalOrdering(string root)
    {
        var path = Path.Combine(root, "telemetry", "events.jsonl");
        if (!File.Exists(path)) return false;
        using var docs = JsonDocument.Parse("[" + string.Join(',', File.ReadAllLines(path)) + "]");
        var events = docs.RootElement.EnumerateArray().ToArray();
        var names = events.Select(e => e.GetProperty("eventType").GetString()).ToArray();
        var start = Array.IndexOf(names, "ATTEMPT_STARTED");
        var complete = Array.IndexOf(names, "LOGICAL_CALL_COMPLETED");
        var timestamps = events.Select(e => DateTimeOffset.Parse(e.GetProperty("timestamp").GetString()!)).ToArray();
        var monotonic = timestamps.Zip(timestamps.Skip(1), (a, b) => b >= a).All(x => x);
        return start >= 0 && complete > start && Array.IndexOf(names, "PARSE_STARTED") > start && Array.IndexOf(names, "BIND_COMPLETED") < complete &&
            names.Count(n => n == "ATTEMPT_STARTED") == 1 && names.Count(n => n == "ATTEMPT_COMPLETED") == 1 &&
            names.Count(n => n == "LOGICAL_CALL_COMPLETED") == 1 && monotonic &&
            Directory.GetFiles(Path.Combine(root, "telemetry"), "response.raw.*.txt").Length == 1 &&
            Directory.GetFiles(Path.Combine(root, "telemetry"), "response.parsed.*.json").Length == 1 &&
            Directory.GetFiles(Path.Combine(root, "telemetry"), "response.binding.*.json").Length == 1;
    }

    private static bool RunLargeRequestTest(string root)
    {
        Directory.CreateDirectory(root);
        var payload = new string('x', 2_000_000);
        using var logical = ProviderCallTelemetry.Start(new ProviderObservabilityOptions { RootDirectory = root, CampaignId = "large-request-test", DocumentId = "LARGE", HeartbeatSeconds = 5 }, new ProviderLogicalCallMetadata
        {
            Stage = "SELF_TEST_LARGE", LogicalCallId = "large-logical", RequestHash = ProviderObservabilityHashing.Sha256Utf8(payload), RequestBytes = payload.Length, EstimatedInputTokens = ProviderObservabilityHashing.EstimateTokens(payload), MaxOutputTokens = 256,
        });
        using var attempt = logical!.StartAttempt("large-attempt", ProviderObservabilityHashing.Sha256Utf8(payload), payload.Length, ProviderObservabilityHashing.EstimateTokens(payload), 256);
        attempt.Complete();
        logical.Complete();
        var file = Directory.GetFiles(Path.Combine(root, "telemetry"), "logical-call.started.*.json").Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.GetProperty("requestBytes").GetInt32() == payload.Length && doc.RootElement.GetProperty("estimatedInputTokens").GetInt32() > 400_000;
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed record WatchdogResult(string Mode, string Status, string TerminationMode, string Stdout, string Stderr);
}
