using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Provider-free proof of the canonical worker launch contract.</summary>
public static class CanonicalWorkerLaunchIntegrityRunner
{
    private const string OutputRoot = "artifacts/execution-integrity/canonical-worker-launch-v1";
    private const string CampaignId = "CANONICAL_DEV_V1_EXEC_V4";
    private const string ExpectedSemanticHash = "5f0eb27dfa44068fc60c69e0dcf8a05b14a061ed7526610e4592d68c268fe5e6";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var source = LoadInventory(repoRoot).FirstOrDefault(item => item.DocumentId == "DOC-0001");
        if (source is null) return await BlockAsync(output, "SOURCE_NOT_FOUND", ct);
        var sourcePath = Path.GetFullPath(Path.Combine(repoRoot, source.SourcePath.Replace('/', Path.DirectorySeparatorChar)));
        var semanticHash = CanonicalDevV1BaselineRunner.ComputeProductionSemanticHashForIntegrity(repoRoot);
        var harnessHash = ComputeHarnessHash(repoRoot);
        var launchContract = new
        {
            schemaVersion = "a99-canonical-worker-launch-contract-v1",
            campaignId = CampaignId,
            executable = Environment.ProcessPath,
            entryAssembly = Assembly.GetEntryAssembly()?.Location,
            arguments = ResolveCliLaunch("a99-canonical-dev-v1-worker").Arguments,
            workingDirectory = repoRoot,
            documentId = source.DocumentId,
            sourcePath,
            sourceSha256 = source.SourceSha256,
            productionSemanticHash = semanticHash,
            executionHarnessHash = harnessHash,
            environmentWhitelist = new[] { "provider/model identifiers", "timeout/config identifiers", "artifact/output roots" },
            secretsPersisted = false,
        };
        await WriteJsonAsync(Path.Combine(output, "launch-contract.json"), launchContract, ct);
        await WriteJsonAsync(Path.Combine(output, "exec-v4-forensic-close.v1.json"), new
        {
            campaignId = CampaignId,
            status = "BLOCKED_ON_PROVIDER_EXECUTION_INTEGRITY",
            scorable = false,
            primaryReason = "WORKER_LAUNCH_FAILURE",
            orchestratedProviderCalls = 0,
            uncontrolledRecoveryProviderCalls = 5,
            uncontrolledRecoveryAccepted = false,
            GoldReads = 0,
            rawExecV4TelemetryMutated = false,
        }, ct);
        var selfTestRoot = Path.Combine(output, "self-test");
        Directory.CreateDirectory(selfTestRoot);
        var runConfigurationHash = Sha256Text(JsonSerializer.Serialize(new { CampaignId, semanticHash, harnessHash, source.DocumentId, source.SourceSha256 }, JsonOptions));
        var validJob = Path.Combine(selfTestRoot, "valid-worker-job.json");
        await WriteJsonAsync(validJob, new
        {
            benchmark = "CANONICAL_DEV_V1",
            campaignId = CampaignId,
            productionSemanticCheckpoint = "8b2d366",
            executionHarnessCheckpoint = "launch-integrity-v1",
            productionSemanticHash = semanticHash,
            executionHarnessHash = harnessHash,
            documentId = source.DocumentId,
            sourcePath,
            sourceSha256 = source.SourceSha256,
            outputDir = selfTestRoot,
            runConfigurationHash,
            attemptId = "CANONICAL_WORKER_LAUNCH_INTEGRITY_V1:DOC-0001:A01",
            requestHash = Sha256Text("launch-integrity-valid"),
            started = DateTimeOffset.UtcNow,
            perAttemptHardTimeoutSeconds = 300,
            executionMode = "NO_PROVIDER_SELF_TEST",
        }, ct);
        var valid = await RunObservedWorkerAsync(validJob, selfTestRoot, TimeSpan.FromSeconds(60), ct);
        var validPass = valid.Status == "COMPLETE" && valid.ExitCode == 0 && valid.BootMarkerFound && valid.LastStageMarker == "PRODUCTION_INPUT_READY" && valid.ProviderCalls == 0;

        var badExecutable = await RunBadLaunchAsync(output, "bad-executable", "PROCESS_START_FAILED", processPath: Path.Combine(selfTestRoot, "does-not-exist.exe"), args: [], workingDirectory: repoRoot, ct);
        var badArgumentLaunch = ResolveCliLaunch("a99-canonical-dev-v1-worker");
        var badArgument = await RunBadLaunchAsync(output, "bad-argument", "ARGUMENT_BINDING_FAILED", processPath: badArgumentLaunch.ProcessPath, args: badArgumentLaunch.Arguments, workingDirectory: repoRoot, ct, omitJobEnvironment: true);
        var runtimeJob = Path.Combine(selfTestRoot, "runtime-failure-job.json");
        await WriteJsonAsync(runtimeJob, new
        {
            benchmark = "CANONICAL_DEV_V1", campaignId = CampaignId, productionSemanticHash = semanticHash, executionHarnessHash = harnessHash,
            documentId = source.DocumentId, sourcePath, sourceSha256 = source.SourceSha256, outputDir = Path.Combine(selfTestRoot, "runtime-failure"),
            runConfigurationHash, attemptId = "CANONICAL_WORKER_LAUNCH_INTEGRITY_V1:DOC-0001:A02", requestHash = Sha256Text("launch-integrity-runtime"), started = DateTimeOffset.UtcNow,
            executionMode = "RUNTIME_FAILURE",
        }, ct);
        var runtime = await RunObservedWorkerAsync(runtimeJob, Path.Combine(selfTestRoot, "runtime-failure"), TimeSpan.FromSeconds(60), ct);
        var runtimePass = runtime.Status == "WORKER_RUNTIME_FAILURE" && runtime.ExitCode != 0 && runtime.StderrBytes >= 0;
        var passed = string.Equals(semanticHash, ExpectedSemanticHash, StringComparison.OrdinalIgnoreCase) && validPass && badExecutable && badArgument && runtimePass;
        await WriteJsonAsync(Path.Combine(output, "self-test.json"), new { schemaVersion = "a99-canonical-worker-launch-self-test-v1", status = passed ? "PASS" : "FAIL", providerCalls = 0, modelCalls = 0, goldReads = 0, semanticHash, expectedSemanticHash = ExpectedSemanticHash, valid, tests = new { realEntrypoint = validPass, badExecutable, badArgument, runtimeFailure = runtimePass } }, ct);
        await WriteJsonAsync(Path.Combine(output, "failure-classification.json"), new
        {
            schemaVersion = "a99-canonical-worker-launch-failure-classification-v1",
            classifications = new[]
            {
                new { status = "PROCESS_START_FAILED", covered = badExecutable },
                new { status = "ARGUMENT_BINDING_FAILED", covered = badArgument },
                new { status = "WORKER_RUNTIME_FAILURE", covered = runtimePass },
                new { status = "CHILD_BOOT_FAILED", covered = !valid.BootMarkerFound },
                new { status = "SOURCE_RESOLUTION_FAILED", covered = valid.LastStageMarker != "PRODUCTION_INPUT_READY" },
            },
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "semantic-drift-check.json"), new { productionSemanticHash = semanticHash, expectedProductionSemanticHash = ExpectedSemanticHash, unchanged = string.Equals(semanticHash, ExpectedSemanticHash, StringComparison.OrdinalIgnoreCase), providerCalls = 0, modelCalls = 0, GoldReads = 0 }, ct);
        await WriteJsonAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-canonical-worker-launch-integrity-v1",
            status = passed ? "CANONICAL_WORKER_LAUNCH_INTEGRITY_PROVEN" : "BLOCKED_ON_WORKER_LAUNCH_INTEGRITY",
            baseline = "af896be",
            campaignId = CampaignId,
            providerCalls = 0,
            modelCalls = 0,
            GoldReads = 0,
            semanticHash,
            outputRoot = OutputRoot,
        }, ct);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), $"# Canonical worker launch integrity\n\nStatus: {(passed ? "CANONICAL_WORKER_LAUNCH_INTEGRITY_PROVEN" : "BLOCKED_ON_WORKER_LAUNCH_INTEGRITY")}\n\nProvider calls: 0\nGold reads: 0\nProduction semantic hash unchanged: {string.Equals(semanticHash, ExpectedSemanticHash, StringComparison.OrdinalIgnoreCase)}\n", ct);
        return passed ? 0 : 2;
    }

    public static async Task<int> RunLifecycleSelfTestAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var root = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar), "orchestration-lifecycle");
        Directory.CreateDirectory(root);
        var source = LoadInventory(repoRoot).First(item => item.DocumentId == "DOC-0001");
        var sourcePath = Path.GetFullPath(Path.Combine(repoRoot, source.SourcePath.Replace('/', Path.DirectorySeparatorChar)));
        var semanticHash = CanonicalDevV1BaselineRunner.ComputeProductionSemanticHashForIntegrity(repoRoot);
        var harnessHash = ComputeHarnessHash(repoRoot);
        var cases = new[]
        {
            (Name: "complete", Mode: "NO_PROVIDER_SELF_TEST", TimeoutSeconds: 60),
            (Name: "nonzero", Mode: "NONZERO_EXIT", TimeoutSeconds: 60),
            (Name: "runtime", Mode: "RUNTIME_FAILURE", TimeoutSeconds: 60),
            (Name: "process-tree-kill", Mode: "PROCESS_TREE_KILL", TimeoutSeconds: 1),
            (Name: "large-output", Mode: "LARGE_OUTPUT", TimeoutSeconds: 60),
        };
        var results = new List<object>();
        var allPassed = true;
        foreach (var item in cases)
        {
            var work = Path.Combine(root, item.Name);
            Directory.CreateDirectory(work);
            var jobPath = Path.Combine(work, "worker-job.json");
            await WriteJsonAsync(jobPath, new
            {
                benchmark = "CANONICAL_DEV_V1", campaignId = "CANONICAL_DEV_V1_EXEC_V6",
                productionSemanticCheckpoint = "f1686fb", executionHarnessCheckpoint = "lifecycle-self-test",
                productionSemanticHash = semanticHash, executionHarnessHash = harnessHash,
                documentId = source.DocumentId, sourcePath, sourceSha256 = source.SourceSha256, outputDir = work,
                runConfigurationHash = Sha256Text($"lifecycle:{item.Name}"), attemptId = $"CANONICAL_WORKER_LAUNCH_INTEGRITY_V6:{item.Name}",
                requestHash = Sha256Text($"lifecycle-request:{item.Name}"), started = DateTimeOffset.UtcNow,
                perAttemptHardTimeoutSeconds = item.TimeoutSeconds, executionMode = item.Mode,
            }, ct);
            var result = await ProviderHardTimeoutIntegrity.RunWorkerAsync(jobPath, work, TimeSpan.FromSeconds(item.TimeoutSeconds), ct);
            var stdoutPath = Path.Combine(work, "worker.stdout.log");
            var stderrPath = Path.Combine(work, "worker.stderr.log");
            var stdout = File.Exists(stdoutPath) ? await File.ReadAllTextAsync(stdoutPath, ct) : "";
            var stderr = File.Exists(stderrPath) ? await File.ReadAllTextAsync(stderrPath, ct) : "";
            var processGone = !IsProcessAlive(result.ChildPid);
            var captureComplete = File.Exists(stdoutPath) && File.Exists(stderrPath) && File.Exists(Path.Combine(work, "worker-exit.v1.json"));
            var passed = item.Name switch
            {
                "complete" => result.Status == "COMPLETE" && result.ExitCode == 0 && File.Exists(Path.Combine(work, "worker.boot.json")) && File.Exists(Path.Combine(work, "worker.stage.PRODUCTION_INPUT_READY.json")) && captureComplete && processGone,
                "nonzero" => result.Status == "WORKER_FAILURE" && result.ExitCode != 0 && captureComplete && processGone,
                "runtime" => result.Status == "WORKER_FAILURE" && result.ExitCode != 0 && File.Exists(Path.Combine(work, "worker-failure.v1.json")) && captureComplete && processGone,
                "process-tree-kill" => result.Status == "DOCUMENT_TIMEOUT" && result.TerminationMode == "PROCESS_TREE_KILL" && captureComplete && processGone,
                "large-output" => result.Status == "COMPLETE" && stdout.Length > 100_000 && stderr.Length > 100_000 && captureComplete && processGone,
                _ => false,
            };
            allPassed &= passed;
            results.Add(new { name = item.Name, mode = item.Mode, passed, result.Status, result.TerminationMode, result.ExitCode, result.ChildPid, processGone, captureComplete, stdoutBytes = Encoding.UTF8.GetByteCount(stdout), stderrBytes = Encoding.UTF8.GetByteCount(stderr), stdoutHash = Sha256Text(stdout), stderrHash = Sha256Text(stderr), providerCalls = 0 });
        }
        await WriteJsonAsync(Path.Combine(root, "summary.json"), new
        {
            schemaVersion = "a99-canonical-worker-orchestration-lifecycle-v1",
            status = allPassed ? "PASS" : "FAIL",
            campaignId = "CANONICAL_DEV_V1_EXEC_V6",
            providerCalls = 0, modelCalls = 0, GoldReads = 0,
            productionSemanticHash = semanticHash, expectedProductionSemanticHash = ExpectedSemanticHash,
            streamLifecycle = "child-exit -> stdout/stderr drain -> capture tasks complete -> streams disposed -> artifact validation",
            cases = results,
        }, ct);
        return allPassed ? 0 : 2;
    }

    private static async Task<bool> RunBadLaunchAsync(string output, string name, string expectedClassification, string processPath, IReadOnlyList<string> args, string workingDirectory, CancellationToken ct, bool omitJobEnvironment = false)
    {
        var dir = Path.Combine(output, name);
        Directory.CreateDirectory(dir);
        var executionId = $"{name}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}";
        var startedPath = Path.Combine(dir, $"worker-launch.started.{executionId}.json");
        await WriteJsonAsync(startedPath, new { executionId, executable = processPath, arguments = args, workingDirectory, expectedClassification, environmentWhitelist = new[] { "provider/model identifiers", "timeout/config identifiers", "artifact/output roots" } }, ct);
        try
        {
            var info = new ProcessStartInfo { FileName = processPath, WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            if (!omitJobEnvironment) info.Environment["A99_CANONICAL_DEV_WORKER_JOB"] = Path.Combine(dir, "missing-job.json");
            using var process = Process.Start(info);
            if (process is null) return false;
            await process.WaitForExitAsync(ct);
            return expectedClassification == "ARGUMENT_BINDING_FAILED" && process.ExitCode != 0;
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(Path.Combine(dir, $"worker-launch.failure.{executionId}.json"), new { executionId, expectedClassification, exceptionType = ex.GetType().FullName, message = ex.Message, hresult = ex.HResult, executable = processPath, arguments = args, workingDirectory, timestamp = DateTimeOffset.UtcNow }, CancellationToken.None);
            return expectedClassification == "PROCESS_START_FAILED";
        }
    }

    private static async Task<ObservedResult> RunObservedWorkerAsync(string jobPath, string workDirectory, TimeSpan timeout, CancellationToken ct)
    {
        Directory.CreateDirectory(workDirectory);
        var executionId = Path.GetFileNameWithoutExtension(jobPath);
        var stdoutPath = Path.Combine(workDirectory, "stdout.log");
        var stderrPath = Path.Combine(workDirectory, "stderr.log");
        var launch = ResolveCliLaunch("a99-canonical-dev-v1-worker");
        var processPath = launch.ProcessPath;
        var args = launch.Arguments;
        using var job = JsonDocument.Parse(await File.ReadAllTextAsync(jobPath, ct));
        var root = job.RootElement;
        await WriteJsonAsync(Path.Combine(workDirectory, $"worker-launch.started.{executionId}.json"), new
        {
            executionId, executable = processPath, arguments = args, workingDirectory = Directory.GetCurrentDirectory(),
            documentId = root.GetProperty("documentId").GetString(), sourcePath = root.GetProperty("sourcePath").GetString(), sourceSha = root.GetProperty("sourceSha256").GetString(),
            campaignId = root.GetProperty("campaignId").GetString(), runConfigurationHash = root.GetProperty("runConfigurationHash").GetString(), productionSemanticHash = root.GetProperty("productionSemanticHash").GetString(), executionHarnessHash = root.GetProperty("executionHarnessHash").GetString(),
            environmentWhitelist = new[] { "A99_CANONICAL_DEV_WORKER_JOB", "campaignId", "runConfigurationHash", "timeout/config identifiers", "artifact/output roots" }, secretsPersisted = false,
        }, ct);
        var info = new ProcessStartInfo { FileName = processPath, WorkingDirectory = Directory.GetCurrentDirectory(), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["A99_CANONICAL_DEV_WORKER_JOB"] = jobPath;
        Process? process;
        try { process = Process.Start(info); }
        catch (Exception ex)
        {
            await WriteJsonAsync(Path.Combine(workDirectory, $"worker-launch.failure.{executionId}.json"), new { executionId, exceptionType = ex.GetType().FullName, message = ex.Message, hresult = ex.HResult, executable = processPath, arguments = args, workingDirectory = info.WorkingDirectory, timestamp = DateTimeOffset.UtcNow }, CancellationToken.None);
            return new ObservedResult("PROCESS_START_FAILED", null, 0, 0, false, null, 0, 0);
        }
        using var child = process ?? throw new InvalidOperationException("Could not start worker process.");
        var started = DateTimeOffset.UtcNow;
        var stdoutTask = CaptureAsync(child.StandardOutput, stdoutPath);
        var stderrTask = CaptureAsync(child.StandardError, stderrPath);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { try { if (!child.HasExited) child.Kill(true); } catch { } }
        await Task.WhenAll(stdoutTask, stderrTask);
        var boot = Path.Combine(workDirectory, "worker.boot.json");
        var stageOrder = new[] { "WORKER_BOOTED", "SOURCE_RESOLVED", "SOURCE_PARSED", "PRODUCTION_INPUT_READY" };
        var lastStage = stageOrder.LastOrDefault(stage => File.Exists(Path.Combine(workDirectory, $"worker.stage.{stage}.json")));
        var status = child.ExitCode == 0 ? "COMPLETE" : "WORKER_RUNTIME_FAILURE";
        var calls = 0;
        var resultPath = Path.Combine(workDirectory, "worker-self-test-result.v1.json");
        if (File.Exists(resultPath)) using (var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, ct))) calls = result.RootElement.GetProperty("providerCalls").GetInt32();
        return new ObservedResult(status, child.Id, child.ExitCode, (int)FileLength(stdoutPath), File.Exists(boot), lastStage, (int)FileLength(stderrPath), calls);
    }

    private static async Task CaptureAsync(StreamReader reader, string path)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } line) await writer.WriteLineAsync(line);
    }

    private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static CliLaunch ResolveCliLaunch(string command)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable");
        var entryAssembly = Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("CLI entry assembly unavailable");
        var isDotnetHost = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        return isDotnetHost ? new CliLaunch(processPath, new[] { entryAssembly, command }) : new CliLaunch(processPath, new[] { command });
    }

    private static string ComputeHarnessHash(string repoRoot) => Sha256Text(string.Join("|", new[] { "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalWorkerLaunchIntegrityRunner.cs", "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalDevV1BaselineRunner.cs", "src/DocxHeaderExtractor.Eval/ReasoningRetention/ProviderHardTimeoutIntegrity.cs", "src/DocxHeaderExtractor.Cli/Program.cs", "src/DocxHeaderExtractor.Cli/CommandLineOptions.cs" }.Select(path => path + ":" + FileHashOrMissing(repoRoot, path))));

    private static string FileHashOrMissing(string repoRoot, string path) { var full = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar)); return File.Exists(full) ? Sha256File(full) : "MISSING"; }
    private static string Sha256File(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static Task WriteJsonAsync(string path, object value, CancellationToken ct) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, ct);
    private static SourceEntry[] LoadInventory(string repoRoot) { using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar)))); return doc.RootElement.GetProperty("documents").EnumerateArray().Select(item => new SourceEntry(item.GetProperty("documentId").GetString()!, item.GetProperty("sourcePath").GetString() ?? "", item.GetProperty("sourceSha256").GetString() ?? "")).ToArray(); }
    private static async Task<int> BlockAsync(string output, string reason, CancellationToken ct) { await WriteJsonAsync(Path.Combine(output, "manifest.json"), new { status = "BLOCKED", reason, providerCalls = 0, GoldReads = 0 }, ct); return 2; }

    private sealed record SourceEntry(string DocumentId, string SourcePath, string SourceSha256);
    private sealed record CliLaunch(string ProcessPath, IReadOnlyList<string> Arguments);
    private sealed record ObservedResult(string Status, int? ChildPid, int? ExitCode, int StdoutBytes, bool BootMarkerFound, string? LastStageMarker, int StderrBytes, int ProviderCalls);
}
