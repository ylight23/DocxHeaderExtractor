using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Append-only diagnostic checkpoint of one PDF run: the selected source identities and each
/// semantic batch, each line under a stable lane identity. Written, never read back by the run.
/// </summary>
internal sealed class PdfStageCheckpoint : IAsyncDisposable
{
    private readonly string _path;
    private readonly string _documentIdentity;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly object _writeState = new();
    private TaskCompletionSource _writesIdle = CompletedSource();
    private int _activeWrites;
    private bool _acceptWrites = true;
    private int _disposed;

    public PdfStageCheckpoint(string path, string documentIdentity)
    {
        _path = Path.GetFullPath(path);
        _documentIdentity = documentIdentity;
        _writesIdle.TrySetResult();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public Task RecordSemanticBatchAsync(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfBlockDecision> decisions,
        CancellationToken ct,
        PdfLaneExecutionLease? executionLease = null) =>
        AppendAsync("semantic", "batch:" + string.Join(',', decisions.Select(d => d.Id)), "completed", new
        {
            blocks = decisions.Select(d =>
            {
                var block = blocks.FirstOrDefault(block => string.Equals(block.Id, d.Id, StringComparison.Ordinal));
                var lineIds = block?.Lines.Select(PdfLineIdentity.Of).ToArray() ?? [];
                return new
                {
                    id = d.Id,
                    page = block?.Page,
                    // Keep the first source line for old readers. New evaluation joins on every
                    // exact source line, since a reviewed heading may span more than one line.
                    lineId = lineIds.FirstOrDefault(),
                    lineIds,
                    role = d.Role.ToString(),
                    semanticRole = d.SemanticRole.ToString(),
                    sourceSpan = d.ProposedSourceSpan,
                    d.Confidence,
                    d.Reason,
                };
            }),
        }, ct, executionLease);

    /// <summary>
    /// Records the selected source identities before the first semantic provider call. This is
    /// append-only observability; it is never read by selection or execution decisions.
    /// </summary>
    public Task RecordSelectionAsync(
        IReadOnlyList<PdfSelectedSourceIdentity> selected,
        CancellationToken ct,
        PdfLaneExecutionLease? executionLease = null) =>
        AppendAsync("selection", "selected", "completed", new
        {
            selected = selected.Select(item => new
            {
                item.RouteBlockIdDiagnostic,
                item.Page,
                item.SourceLineIds,
                item.SourceText,
                item.SourceSpan,
            }).ToArray(),
        }, ct, executionLease);

    private async Task<bool> AppendAsync(
        string lane,
        string identity,
        string status,
        object payload,
        CancellationToken ct,
        PdfLaneExecutionLease? executionLease = null)
    {
        lock (_writeState)
        {
            if (!_acceptWrites) return false;
            if (_activeWrites++ == 0)
                _writesIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using var operation = executionLease?.TryAdmitOperation();
        if (executionLease is not null && operation is null)
        {
            ExitWrite();
            return false;
        }
        var acquired = false;
        try
        {
            var line = JsonSerializer.Serialize(new { lane, identity = _documentIdentity + ":" + identity, status, completedAt = DateTimeOffset.UtcNow, payload });
            await _write.WaitAsync(ct);
            acquired = true;
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, ct);
            return true;
        }
        finally
        {
            if (acquired) _write.Release();
            ExitWrite();
        }
    }

    /// <summary>Stops late writes and drains only writes already admitted to the checkpoint.</summary>
    public async Task StopAcceptingWritesAndDrainAsync()
    {
        Task? idle;
        lock (_writeState)
        {
            _acceptWrites = false;
            idle = _activeWrites > 0 ? _writesIdle.Task : null;
        }
        if (idle is not null) await idle.ConfigureAwait(false);
    }

    private void ExitWrite()
    {
        lock (_writeState)
        {
            if (--_activeWrites == 0 && !_acceptWrites)
                _writesIdle.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _write.Dispose();
        return ValueTask.CompletedTask;
    }

}
