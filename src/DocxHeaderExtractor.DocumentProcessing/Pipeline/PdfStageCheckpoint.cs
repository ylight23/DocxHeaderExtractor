using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Append-only diagnostic checkpoint. Each line has a stable lane identity so a resumed visual
/// schedule can skip a region that already produced a durable outcome.
/// </summary>
internal sealed class PdfStageCheckpoint : IAsyncDisposable
{
    private readonly string _path;
    private readonly string _documentIdentity;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly object _writeState = new();
    private TaskCompletionSource _writesIdle = CompletedSource();
    private readonly HashSet<string> _completedVisualRegions = new(StringComparer.Ordinal);
    private readonly List<PdfVisualRecoveryTrace> _completedVisualTraces = [];
    private readonly Dictionary<string, (PdfBlockRole Role, double Confidence, string Reason, PdfSemanticRole SemanticRole, TextOffsetSpan? ProposedSourceSpan)> _semanticDecisions = new(StringComparer.Ordinal);
    private int _activeWrites;
    private bool _acceptWrites = true;
    private int _disposed;

    public PdfStageCheckpoint(string path, bool resume, string documentIdentity)
    {
        _path = Path.GetFullPath(path);
        _documentIdentity = documentIdentity;
        _writesIdle.TrySetResult();
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
                        var semanticRole = block.TryGetProperty("semanticRole", out var semanticRoleProperty) &&
                                           Enum.TryParse<PdfSemanticRole>(semanticRoleProperty.GetString(), true, out var parsedSemanticRole)
                            ? parsedSemanticRole
                            : PdfSemanticRole.Unknown;
                        TextOffsetSpan? proposedSourceSpan = null;
                        if (block.TryGetProperty("sourceSpan", out var sourceSpan) &&
                            sourceSpan.ValueKind == JsonValueKind.Object &&
                            sourceSpan.TryGetProperty("start", out var start) && start.TryGetInt32(out var startValue) &&
                            sourceSpan.TryGetProperty("end", out var end) && end.TryGetInt32(out var endValue))
                            proposedSourceSpan = new TextOffsetSpan(startValue, endValue);
                        if (!string.IsNullOrWhiteSpace(id) && Enum.TryParse<PdfBlockRole>(role, true, out var parsedRole))
                            _semanticDecisions[id] = (parsedRole, confidence, reason, semanticRole, proposedSourceSpan);
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

    public bool TryGetSemanticDecision(string blockId, out (PdfBlockRole Role, double Confidence, string Reason, PdfSemanticRole SemanticRole, TextOffsetSpan? ProposedSourceSpan) decision) =>
        _semanticDecisions.TryGetValue(blockId, out decision);

    /// <summary>
    /// A3 (partial-result preservation): spans actually resolved and durably recorded so far, re-read
    /// from this checkpoint's own file rather than tracked live in memory. The span lane's work
    /// continues in the background past its deadline (<see cref="PdfLaneExecution"/> does not await a
    /// timed-out task before returning), so nothing in-process ever holds a live, complete picture of
    /// "what finished" - only the file each completed batch was durably appended to does. Called only
    /// once a caller has already decided the lane timed out and needs to know what survived; never
    /// polled during normal execution.
    /// </summary>
    public IReadOnlyDictionary<string, TextOffsetSpan> ReadCompletedSpanResolutions()
    {
        var resolved = new Dictionary<string, TextOffsetSpan>(StringComparer.Ordinal);
        if (!File.Exists(_path)) return resolved;

        foreach (var line in File.ReadLines(_path))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                if (!json.RootElement.TryGetProperty("lane", out var lane) || lane.GetString() != "span") continue;
                if (!json.RootElement.TryGetProperty("payload", out var payload) ||
                    !payload.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array) continue;

                foreach (var block in blocks.EnumerateArray())
                {
                    var id = block.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
                    var isResolved = block.TryGetProperty("resolved", out var resolvedProperty) && resolvedProperty.ValueKind == JsonValueKind.True;
                    if (string.IsNullOrWhiteSpace(id) || !isResolved) continue;
                    if (!block.TryGetProperty("start", out var startProperty) || !startProperty.TryGetInt32(out var start)) continue;
                    if (!block.TryGetProperty("end", out var endProperty) || !endProperty.TryGetInt32(out var end)) continue;
                    resolved[id] = new TextOffsetSpan(start, end);
                }
            }
            catch (JsonException)
            {
                // A torn final line - the background lane may still be appending - is skipped, not
                // faulted. Earlier completed batches on prior lines remain usable.
            }
        }

        return resolved;
    }

    public Task RecordSemanticBatchAsync(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfBlockDecision> decisions,
        CancellationToken ct) =>
        AppendAsync("semantic", "batch:" + string.Join(',', decisions.Select(d => d.Id)), "completed", new
        {
            blocks = decisions.Select(d =>
            {
                var block = blocks.FirstOrDefault(candidate => string.Equals(candidate.Id, d.Id, StringComparison.Ordinal));
                var lineIds = block?.Lines.Select(PdfCandidateProvenance.LineId).ToArray() ?? [];
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
        }, ct);

    /// <summary>
    /// Records the selected source identities before the first semantic provider call. This is
    /// append-only observability; it is never read by selection or execution decisions.
    /// </summary>
    public Task RecordSelectionAsync(
        IReadOnlyList<PdfSelectedSourceIdentity> selected,
        CancellationToken ct) =>
        AppendAsync("selection", "selected", "completed", new
        {
            selected = selected.Select(item => new
            {
                item.CandidateIdDiagnostic,
                item.Page,
                item.SourceLineIds,
                item.SourceText,
                item.SourceSpan,
            }).ToArray(),
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
        IReadOnlyList<(string Id, int Page, string? LineId, IReadOnlyList<string> LineIds, TextOffsetSpan? Span)> resolutions,
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
                    lineIds = r.LineIds,
                    resolved = r.Span is not null,
                    spanOutcome = SpanOutcome(r.Span, failureClass),
                    start = r.Span?.Start,
                    end = r.Span?.End,
                }),
            }, ct);

    /// <summary>Persists the downstream decision chain without making it an execution input.</summary>
    public Task RecordDownstreamProvenanceAsync(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfBlockDecision> decisions,
        IReadOnlyList<PdfCandidateStageTrace> traces,
        IReadOnlySet<string> groundedIds,
        IReadOnlySet<string> emittedIds,
        IReadOnlyList<PdfSemanticClusterDecision> clusterDecisions,
        CancellationToken ct)
    {
        var decisionById = decisions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var traceById = traces.ToDictionary(item => item.Id, StringComparer.Ordinal);
        return AppendAsync("downstream", "provenance", "completed", new
        {
            clusterDecisions,
            occurrences = blocks.Select(block =>
            {
                decisionById.TryGetValue(block.Id, out var decision);
                traceById.TryGetValue(block.Id, out var trace);
                var sourceLineIds = block.Lines.Select(PdfCandidateProvenance.LineId).ToArray();
                return new
                {
                    sourceIdentity = new
                    {
                        page = block.Page,
                        sourceLineIds,
                        sourceSpan = decision?.HeadingSpan,
                    },
                    candidateIdDiagnostic = block.Id,
                    semanticRole = decision?.Role.ToString(),
                    semanticReason = decision?.Reason,
                    spanProposal = decision?.HeadingSpan,
                    spanOutcome = decision?.HeadingSpan is not null ? "RESOLVED" : "NO_PROPOSAL",
                    validatorStatus = trace?.ValidationStatus,
                    validatorReason = trace?.Reason,
                    groundingStatus = groundedIds.Contains(block.Id) ? "GROUNDED" : "NOT_GROUNDED",
                    outputStatus = emittedIds.Contains(block.Id) ? "EMITTED" : "NOT_EMITTED",
                };
            }).ToArray(),
        }, ct);
    }

    public async Task RecordVisualRegionAsync(PdfVisualRecoveryTrace trace, CancellationToken ct)
    {
        await AppendAsync("visual", trace.RegionId, "completed", trace, ct);
        lock (_completedVisualRegions) _completedVisualRegions.Add(trace.RegionId);
    }

    private async Task AppendAsync(string lane, string identity, string status, object payload, CancellationToken ct)
    {
        lock (_writeState)
        {
            if (!_acceptWrites) return;
            if (_activeWrites++ == 0)
                _writesIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var acquired = false;
        try
        {
            var line = JsonSerializer.Serialize(new { lane, identity = _documentIdentity + ":" + identity, status, completedAt = DateTimeOffset.UtcNow, payload });
            await _write.WaitAsync(ct);
            acquired = true;
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, ct);
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

    private static string SpanOutcome(TextOffsetSpan? span, string? failureClass) =>
        span is not null ? "RESOLVED" : failureClass switch
        {
            "semantic_batch_timeout" => "BATCH_TIMEOUT",
            "semantic_request_timeout" => "REQUEST_TIMEOUT",
            "semantic_lane_timeout" => "LANE_DEADLINE",
            null => "NO_PROPOSAL",
            _ => "BATCH_EXCEPTION",
        };

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
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
