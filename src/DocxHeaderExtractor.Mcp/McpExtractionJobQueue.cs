using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace DocxHeaderExtractor.Mcp;

/// <summary>
/// Creates a durable job snapshot and launches the extractor in a detached worker process.
/// LM Studio may tear down a stdio MCP process after a tool call; the long-running extraction
/// therefore must not be owned by that request/host lifetime.
/// </summary>
public sealed class McpExtractionJobQueue
{
    private const int MaxTrackedJobs = 32;
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromMinutes(30);

    private readonly McpPathPolicy _paths;
    private readonly McpJobStore _store;
    private readonly ILogger<McpExtractionJobQueue> _logger;

    public McpExtractionJobQueue(McpPathPolicy paths, McpJobStore store, ILogger<McpExtractionJobQueue> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public McpJobStartResult Start(string inputPath)
    {
        CleanupExpired();
        if (_store.CountActiveOrRecent() >= MaxTrackedJobs)
            throw new InvalidOperationException(
                $"Đã đạt giới hạn {MaxTrackedJobs} job đang được theo dõi. Hãy đợi job cũ hoàn tất.");

        var resolved = _paths.ResolveReadableDocument(inputPath);
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var state = new McpJobStatusResult(id, "Queued", Path.GetFileName(resolved), now, null, null, null, null, 15);
        _store.Save(state);

        try
        {
            var worker = StartDetachedWorker(id, resolved);
            _logger.LogInformation("Started detached MCP worker {JobId} as process {ProcessId}.", id, worker.Id);
            _ = ObserveDetachedWorkerAsync(worker);
        }
        catch (Exception ex)
        {
            _store.Save(state with
            {
                State = "Failed", CompletedAt = DateTimeOffset.UtcNow,
                Error = "Không khởi động được worker MCP: " + Safe(ex.Message),
                RecommendedPollSeconds = 0,
            });
            throw new InvalidOperationException("Không khởi động được worker MCP.", ex);
        }

        return new McpJobStartResult(id, "Queued", state.File, 15,
            "Đã nhận job. Worker chạy độc lập; gọi get_docx_extraction_result sau khoảng 15 giây.");
    }

    public McpJobStatusResult Get(string jobId)
    {
        CleanupExpired();
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64)
            throw new ArgumentException("jobId không hợp lệ.", nameof(jobId));
        return _store.Load(jobId)
               ?? throw new KeyNotFoundException(
                   "Không tìm thấy jobId. 30 phút là thời gian giữ kết quả sau khi hoàn tất; " +
                   "job có thể đã bị tiến trình worker cũ hủy hoặc đã hết hạn.");
    }

    private Process StartDetachedWorker(string id, string resolvedPath)
    {
        var entry = Assembly.GetEntryAssembly()?.Location;
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(entry) || string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("Không xác định được runtime MCP để khởi động worker.");

        if (!OperatingSystem.IsWindows())
            return StartUnixWorker(processPath, entry, id, resolvedPath);

        // LM Studio may terminate the whole stdio process tree. `start` creates a detached
        // Windows process outside that short-lived launcher tree.
        var quote = (string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
        var command = $"/d /c start \"\" /b {quote(processPath)} {quote(entry)} " +
                      $"--worker {quote(id)} {quote(resolvedPath)}";
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = command,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        return Process.Start(start)
               ?? throw new InvalidOperationException("Process.Start trả về null.");
    }

    private Process StartUnixWorker(
        string processPath, string entry, string id, string resolvedPath)
    {
        // Do not use a shell here. ArgumentList preserves paths and job ids exactly and avoids
        // shell injection as well as the Windows-only `cmd.exe /c start` dependency.
        var start = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            // stderr is inherited so a detached worker cannot block on an unread stderr pipe.
            RedirectStandardError = false,
        };
        start.ArgumentList.Add(entry);
        start.ArgumentList.Add("--worker");
        start.ArgumentList.Add(id);
        start.ArgumentList.Add(resolvedPath);
        return Process.Start(start)
               ?? throw new InvalidOperationException("Process.Start trả về null.");
    }

    private async Task ObserveDetachedWorkerAsync(Process worker)
    {
        try
        {
            var stdout = worker.StartInfo.RedirectStandardOutput
                ? worker.StandardOutput.ReadToEndAsync()
                : Task.FromResult(string.Empty);
            await worker.WaitForExitAsync().ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Detached MCP worker observation ended with an error.");
        }
        finally
        {
            worker.Dispose();
        }
    }

    private void CleanupExpired() => _store.DeleteExpired(DateTimeOffset.UtcNow - CompletedRetention);

    internal static string Safe(string message)
    {
        var oneLine = message.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 1_000 ? oneLine : oneLine[..1_000] + "…";
    }
}
