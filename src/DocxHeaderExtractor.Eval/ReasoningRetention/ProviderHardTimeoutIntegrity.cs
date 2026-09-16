using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Process-isolated watchdog used by benchmark execution. It deliberately does not rely on a
/// provider or SDK honoring a CancellationToken: the parent owns the deadline and can terminate
/// the complete child process tree.
/// </summary>
public static class ProviderHardTimeoutIntegrity
{
    public static async Task<WatchdogResult> RunChildAsync(
        string mode,
        string workDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workDirectory);
        var markerPath = Path.Combine(workDirectory, "child-marker.json");
        var launch = ResolveCliLaunch("a99-provider-hard-timeout-child");
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.ProcessPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in launch.Arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["A99_HARD_TIMEOUT_CHILD_MODE"] = mode;
        startInfo.Environment["A99_HARD_TIMEOUT_CHILD_MARKER"] = markerPath;

        using var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start isolated child process.");
        var started = DateTimeOffset.UtcNow;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var timedOut = false;
        var interrupted = false;
        try
        {
            await child.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            interrupted = true;
        }

        if (timedOut || interrupted)
        {
            try
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The child exited between HasExited and Kill; the final state below is authoritative.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Preserve the watchdog result; the parent still records the failed attempt.
            }
            try { await child.WaitForExitAsync(CancellationToken.None); } catch { }
        }

        var completed = DateTimeOffset.UtcNow;
        return new WatchdogResult(
            mode,
            child.Id,
            started,
            completed,
            timedOut ? "DOCUMENT_TIMEOUT" : interrupted ? "CAMPAIGN_CANCELLED" : "COMPLETE",
            timedOut || interrupted ? "PROCESS_TREE_KILL" : "NONE",
            child.HasExited ? child.ExitCode : null,
            File.Exists(markerPath) ? JsonDocument.Parse(await File.ReadAllTextAsync(markerPath)).RootElement.Clone() : null,
            await stdout,
            await stderr);
    }

    /// <summary>
    /// Isolated timeout for one physical provider attempt. Unlike the legacy document watchdog,
    /// a timeout here is classified as ATTEMPT_TIMEOUT and has no shared document deadline.
    /// </summary>
    public static async Task<WatchdogResult> RunAttemptAsync(
        string mode,
        string workDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workDirectory);
        var markerPath = Path.Combine(workDirectory, "attempt-marker.json");
        var launch = ResolveCliLaunch("a99-provider-hard-timeout-child");
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.ProcessPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in launch.Arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["A99_HARD_TIMEOUT_CHILD_MODE"] = mode;
        startInfo.Environment["A99_HARD_TIMEOUT_CHILD_MARKER"] = markerPath;

        using var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start isolated attempt process.");
        var started = DateTimeOffset.UtcNow;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var timedOut = false;
        var interrupted = false;
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { timedOut = true; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { interrupted = true; }
        if (timedOut || interrupted)
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            try { await child.WaitForExitAsync(CancellationToken.None); } catch { }
        }
        var completed = DateTimeOffset.UtcNow;
        return new WatchdogResult(
            "provider-attempt",
            child.Id,
            started,
            completed,
            timedOut ? "ATTEMPT_TIMEOUT" : interrupted ? "CAMPAIGN_CANCELLED" : child.ExitCode == 0 ? "COMPLETE" : "ATTEMPT_FAILURE",
            timedOut || interrupted ? "PROCESS_TREE_KILL" : "NONE",
            child.HasExited ? child.ExitCode : null,
            File.Exists(markerPath) ? JsonDocument.Parse(await File.ReadAllTextAsync(markerPath)).RootElement.Clone() : null,
            await stdout,
            await stderr);
    }

    public static async Task<WatchdogResult> RunWorkerAsync(
        string workerJobPath,
        string workDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workDirectory);
        var launch = ResolveCliLaunch("a99-canonical-dev-v1-worker");
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.ProcessPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in launch.Arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["A99_CANONICAL_DEV_WORKER_JOB"] = workerJobPath;
        await WriteLaunchMetadataAsync(workDirectory, workerJobPath, launch, startInfo.WorkingDirectory);
        Process? startedChild;
        try
        {
            startedChild = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(workDirectory, "worker-launch.failure.json"), JsonSerializer.Serialize(new
            {
                classification = "PROCESS_START_FAILED",
                exceptionType = ex.GetType().FullName,
                message = ex.Message,
                hresult = ex.HResult,
                executable = launch.ProcessPath,
                arguments = launch.Arguments,
                workingDirectory = startInfo.WorkingDirectory,
                timestamp = DateTimeOffset.UtcNow,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return new WatchdogResult("production-worker", -1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "PROCESS_START_FAILED", "NONE", null, null, "", ex.ToString());
        }
        using var child = startedChild ?? throw new InvalidOperationException("Could not start isolated production worker.");
        var started = DateTimeOffset.UtcNow;
        var stdoutPath = Path.Combine(workDirectory, "worker.stdout.log");
        var stderrPath = Path.Combine(workDirectory, "worker.stderr.log");
        var stdout = CaptureStreamAsync(child.StandardOutput, stdoutPath);
        var stderr = CaptureStreamAsync(child.StandardError, stderrPath);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var timedOut = false;
        var interrupted = false;
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { timedOut = true; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { interrupted = true; }
        if (timedOut || interrupted)
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            try { await child.WaitForExitAsync(CancellationToken.None); } catch { }
        }
        var result = new WatchdogResult(
            "production-worker",
            child.Id,
            started,
            DateTimeOffset.UtcNow,
            timedOut ? "DOCUMENT_TIMEOUT" : interrupted ? "CAMPAIGN_CANCELLED" : child.ExitCode == 0 ? "COMPLETE" : "WORKER_FAILURE",
            timedOut || interrupted ? "PROCESS_TREE_KILL" : "NONE",
            child.HasExited ? child.ExitCode : null,
            null,
            await stdout,
            await stderr);
        await File.WriteAllTextAsync(Path.Combine(workDirectory, "worker-exit.v1.json"), JsonSerializer.Serialize(new
        {
            status = result.Status,
            terminationMode = result.TerminationMode,
            childPid = result.ChildPid,
            exitCode = result.ExitCode,
            startedAt = result.StartedAt,
            completedAt = result.CompletedAt,
            stdoutBytes = File.Exists(stdoutPath) ? new FileInfo(stdoutPath).Length : 0,
            stderrBytes = File.Exists(stderrPath) ? new FileInfo(stderrPath).Length : 0,
            bootMarkerFound = File.Exists(Path.Combine(workDirectory, "worker.boot.json")),
            lastStageMarker = new[] { "WORKER_BOOTED", "SOURCE_RESOLVED", "SOURCE_PARSED", "PRODUCTION_INPUT_READY" }
                .LastOrDefault(stage => File.Exists(Path.Combine(workDirectory, $"worker.stage.{stage}.json"))),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    private static async Task<string> CaptureStreamAsync(StreamReader reader, string path)
    {
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
        {
            while (await reader.ReadLineAsync() is { } line) await writer.WriteLineAsync(line);
        }
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
    }

    private static async Task WriteLaunchMetadataAsync(string workDirectory, string jobPath, CliLaunch launch, string? workingDirectory)
    {
        string? documentId = null;
        string? campaignId = null;
        string? runConfigurationHash = null;
        string? sourceSha256 = null;
        try
        {
            using var job = JsonDocument.Parse(await File.ReadAllTextAsync(jobPath));
            var root = job.RootElement;
            documentId = root.TryGetProperty("documentId", out var d) ? d.GetString() : null;
            campaignId = root.TryGetProperty("campaignId", out var c) ? c.GetString() : null;
            runConfigurationHash = root.TryGetProperty("runConfigurationHash", out var h) ? h.GetString() : null;
            sourceSha256 = root.TryGetProperty("sourceSha256", out var s) ? s.GetString() : null;
        }
        catch { /* launch metadata remains useful even when the job cannot be parsed */ }
        await File.WriteAllTextAsync(Path.Combine(workDirectory, "worker-launch.started.json"), JsonSerializer.Serialize(new
        {
            processStarted = false,
            executable = launch.ProcessPath,
            arguments = launch.Arguments,
            workingDirectory,
            documentId,
            campaignId,
            runConfigurationHash,
            sourceSha256,
            environmentVariables = new[] { "A99_CANONICAL_DEV_WORKER_JOB" },
            secretsPersisted = false,
            timestamp = DateTimeOffset.UtcNow,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task<int> RunSelfTestAsync(string repoRoot, CancellationToken cancellationToken = default)
    {
        var output = Path.Combine(repoRoot, "artifacts", "execution-integrity", "provider-hard-timeout-v1");
        Directory.CreateDirectory(output);
        var normal = await RunChildAsync("normal", Path.Combine(output, "normal"), TimeSpan.FromSeconds(5), cancellationToken);
        var hanging = await RunChildAsync("hang", Path.Combine(output, "hang"), TimeSpan.FromSeconds(2), cancellationToken);
        var partial = await RunChildAsync("partial-hang", Path.Combine(output, "partial"), TimeSpan.FromSeconds(2), cancellationToken);
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        interrupt.CancelAfter(TimeSpan.FromMilliseconds(250));
        var campaignInterrupt = await RunChildAsync("hang", Path.Combine(output, "campaign-interrupt"), TimeSpan.FromSeconds(5), interrupt.Token);

        var passed = normal.Status == "COMPLETE" && normal.TerminationMode == "NONE" &&
            hanging.Status == "DOCUMENT_TIMEOUT" && hanging.TerminationMode == "PROCESS_TREE_KILL" &&
            partial.Status == "DOCUMENT_TIMEOUT" && partial.TerminationMode == "PROCESS_TREE_KILL" &&
            campaignInterrupt.Status == "CAMPAIGN_CANCELLED" && campaignInterrupt.TerminationMode == "PROCESS_TREE_KILL" &&
            HasMarker(output, "normal", "COMPLETE") &&
            !HasMarker(output, "hang", "COMPLETE") &&
            HasMarker(output, "partial", "PARTIAL") &&
            !HasMarker(output, "partial", "COMPLETE");

        await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "provider-hard-timeout-integrity-v1",
            status = passed ? "PROVIDER_HARD_TIMEOUT_INTEGRITY_PROVEN" : "FAILED",
            providerCalls = 0,
            normal,
            hanging,
            partial,
            campaignInterrupt,
        }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        return passed ? 0 : 2;
    }

    public static int RunChildMode()
    {
        var mode = Environment.GetEnvironmentVariable("A99_HARD_TIMEOUT_CHILD_MODE") ?? "hang";
        var markerPath = Environment.GetEnvironmentVariable("A99_HARD_TIMEOUT_CHILD_MARKER")
            ?? throw new InvalidOperationException("Missing child marker path.");
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        if (mode == "normal")
        {
            File.WriteAllText(markerPath, JsonSerializer.Serialize(new { status = "COMPLETE" }));
            return 0;
        }
        if (mode == "healthy-delay")
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(150));
            File.WriteAllText(markerPath, JsonSerializer.Serialize(new { status = "COMPLETE" }));
            return 0;
        }
        if (mode == "partial-hang")
            File.WriteAllText(markerPath, JsonSerializer.Serialize(new { status = "PARTIAL" }));
        while (true) Thread.Sleep(TimeSpan.FromSeconds(1));
    }

    private static bool HasMarker(string root, string child, string status)
    {
        var path = Path.Combine(root, child, "child-marker.json");
        return File.Exists(path) && JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("status").GetString() == status;
    }

    private static CliLaunch ResolveCliLaunch(string command)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current process path is unavailable.");
        var entryAssembly = Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("CLI entry assembly is unavailable.");
        var executableName = Path.GetFileNameWithoutExtension(processPath);
        var isDotnetHost = string.Equals(executableName, "dotnet", StringComparison.OrdinalIgnoreCase);
        return isDotnetHost
            ? new CliLaunch(processPath, new[] { entryAssembly, command })
            : new CliLaunch(processPath, new[] { command });
    }

    private sealed record CliLaunch(string ProcessPath, IReadOnlyList<string> Arguments);

    public sealed record WatchdogResult(
        string Mode,
        int ChildPid,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        string Status,
        string TerminationMode,
        int? ExitCode,
        JsonElement? Marker,
        string Stdout,
        string Stderr);
}
