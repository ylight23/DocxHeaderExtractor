using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Append-only diagnostic checkpoint. Each line has a stable lane identity so a resumed visual
/// schedule can skip a region that already produced a durable outcome.
/// </summary>
internal sealed class PdfStageCheckpoint : IAsyncDisposable
{
    private readonly string _path;
    private readonly string _documentIdentity;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly HashSet<string> _completedVisualRegions = new(StringComparer.Ordinal);
    private readonly List<PdfVisualRecoveryTrace> _completedVisualTraces = [];
    private readonly Dictionary<string, (PdfBlockRole Role, double Confidence, string Reason)> _semanticDecisions = new(StringComparer.Ordinal);

    public PdfStageCheckpoint(string path, bool resume, string documentIdentity)
    {
        _path = Path.GetFullPath(path);
        _documentIdentity = documentIdentity;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (!resume || !File.Exists(_path)) return;

        foreach (var line in File.ReadLines(_path))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                if (!json.RootElement.TryGetProperty("lane", out var lane) ||
                    !json.RootElement.TryGetProperty("identity", out var identity))
                    continue;

                var laneName = lane.GetString();
                var checkpointIdentity = identity.GetString();
                if (string.Equals(laneName, "visual", StringComparison.Ordinal) &&
                    TryUnprefix(checkpointIdentity, out var rawIdentity))
                {
                    _completedVisualRegions.Add(rawIdentity);
                    if (json.RootElement.TryGetProperty("payload", out var payload))
                    {
                        var trace = payload.Deserialize<PdfVisualRecoveryTrace>();
                        if (trace is not null) _completedVisualTraces.Add(trace);
                    }
                }
                else if (string.Equals(laneName, "semantic", StringComparison.Ordinal) &&
                         TryUnprefix(checkpointIdentity, out _) &&
                         json.RootElement.TryGetProperty("payload", out var semanticPayload) &&
                         semanticPayload.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in blocks.EnumerateArray())
                    {
                        var id = block.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
                        var role = block.TryGetProperty("role", out var roleProperty) ? roleProperty.GetString() : null;
                        var confidence = block.TryGetProperty("confidence", out var confidenceProperty) && confidenceProperty.TryGetDouble(out var value) ? value : 0;
                        var reason = block.TryGetProperty("reason", out var reasonProperty) ? reasonProperty.GetString() ?? "checkpoint" : "checkpoint";
                        if (!string.IsNullOrWhiteSpace(id) && Enum.TryParse<PdfBlockRole>(role, true, out var parsedRole))
                            _semanticDecisions[id] = (parsedRole, confidence, reason);
                    }
                }
            }
            catch (JsonException)
            {
                // A torn final line is ignored; earlier completed work remains reusable.
            }
        }
    }

    public IReadOnlySet<string> CompletedVisualRegions => _completedVisualRegions;
    public IReadOnlyList<PdfVisualRecoveryTrace> CompletedVisualTraces => _completedVisualTraces;

    public bool TryGetSemanticDecision(string blockId, out (PdfBlockRole Role, double Confidence, string Reason) decision) =>
        _semanticDecisions.TryGetValue(blockId, out decision);

    public Task RecordSemanticBatchAsync(IReadOnlyList<PdfBlockDecision> decisions, CancellationToken ct) =>
        AppendAsync("semantic", "batch:" + string.Join(',', decisions.Select(d => d.Id)), "completed", new
        {
            blocks = decisions.Select(d => new { id = d.Id, role = d.Role.ToString(), d.Confidence, d.Reason }),
        }, ct);

    /// <summary>
    /// One span-resolution batch as it actually ended. A heading cannot validate without a resolved
    /// span, and until now a batch that resolved nothing - or threw and was swallowed - left no trace
    /// at all, so a span-lane failure and a healthy run produced identical artifacts.
    /// <para>
    /// Blocks are identified by source authority as well as candidate id: candidate ids are
    /// discovery-order and shift between revisions, so they can address a block within this run but
    /// must never be the identity a later comparison relies on.
    /// </para>
    /// </summary>
    public Task RecordSpanBatchAsync(
        IReadOnlyList<(string Id, int Page, string? LineId, TextOffsetSpan? Span)> resolutions,
        string? failureClass,
        CancellationToken ct) =>
        AppendAsync("span", "batch:" + string.Join(',', resolutions.Select(r => r.Id)),
            failureClass is null ? "completed" : "failed", new
            {
                failureClass,
                blocks = resolutions.Select(r => new
                {
                    id = r.Id,
                    page = r.Page,
                    lineId = r.LineId,
                    resolved = r.Span is not null,
                    start = r.Span?.Start,
                    end = r.Span?.End,
                }),
            }, ct);

    public async Task RecordVisualRegionAsync(PdfVisualRecoveryTrace trace, CancellationToken ct)
    {
        await AppendAsync("visual", trace.RegionId, "completed", trace, ct);
        lock (_completedVisualRegions) _completedVisualRegions.Add(trace.RegionId);
    }

    private async Task AppendAsync(string lane, string identity, string status, object payload, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(new { lane, identity = _documentIdentity + ":" + identity, status, completedAt = DateTimeOffset.UtcNow, payload });
        await _write.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, ct);
        }
        finally
        {
            _write.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _write.Dispose();
        return ValueTask.CompletedTask;
    }

    private bool TryUnprefix(string? identity, out string rawIdentity)
    {
        var prefix = _documentIdentity + ":";
        if (!string.IsNullOrWhiteSpace(identity) && identity.StartsWith(prefix, StringComparison.Ordinal))
        {
            rawIdentity = identity[prefix.Length..];
            return true;
        }
        rawIdentity = "";
        return false;
    }
}
