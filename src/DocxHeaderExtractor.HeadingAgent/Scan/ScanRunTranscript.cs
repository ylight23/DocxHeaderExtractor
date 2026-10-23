using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.Agents;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>
/// Every model call of one scan run, as one JSON line: the request body exactly as it was sent, the raw reply (reasoning, content, tool calls,
/// finish reason) and the request's SHA-256. A reply the parser refuses is recorded too, because the parser runs after this. The file holds the
/// document's text (the request bodies), so the folder is marked CONTAINS_DOCUMENT_TEXT. No header and no API key is written. A line that cannot be
/// written, or a call that threw, is counted in the run's status file: an incomplete transcript is not evidence for the cause of a decision.
/// </summary>
internal sealed class ScanRunTranscript : IDisposable
{
    public const string Marker = "CONTAINS_DOCUMENT_TEXT.txt";
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly object gate = new();
    readonly string folder;
    readonly StreamWriter? writer;
    int recorded, failedWrites, threwCalls;
    string? firstError;

    public ScanRunTranscript(string folder, string runId)
    {
        this.folder = folder;
        RunId = runId;
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, Marker), "This folder holds request bodies and raw replies: they contain the document's text. Do not publish it.\n", Encoding.UTF8);
            writer = new StreamWriter(Path.Combine(folder, $"run-{runId}.jsonl"), append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            failedWrites++;
            firstError = e.GetType().Name + ": " + e.Message;
        }
    }

    public string RunId { get; }

    /// <summary>Runs one model call and records its request and reply. A call that throws is recorded with its error, then rethrown.</summary>
    public ModelTurn Record(string unit, int turn, PreparedRequest request, Func<ModelTurn> call)
    {
        ModelTurn reply;
        try { reply = call(); }
        catch (Exception e)
        {
            lock (gate) threwCalls++;
            Write(new { run = RunId, unit, turn, requestSha256 = request.Sha256, request = request.Body, error = e.GetType().Name + ": " + e.Message });
            throw;
        }
        Write(new
        {
            run = RunId, unit, turn, at = DateTimeOffset.UtcNow.ToString("O"), requestSha256 = request.Sha256, requestBytes = request.Bytes,
            precedingSelected = AgentSelector.PrecedingSelectedAliasOf(request.Body), request = request.Body,
            finishReason = reply.FinishReason, reasoning = reply.Reasoning, content = reply.Content,
            toolCalls = reply.ToolCalls.Select(c => new { name = c.Name, arguments = c.Arguments }).ToArray(),
        });
        return reply;
    }

    void Write(object line)
    {
        lock (gate)
        {
            if (writer is null) { failedWrites++; return; }
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(line, Json));
                recorded++;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException)
            {
                failedWrites++;
                firstError ??= e.GetType().Name + ": " + e.Message;
            }
        }
    }

    /// <summary>The status of the transcript, written beside it: how many calls were recorded, how many writes or calls failed.</summary>
    public string Status
    {
        get
        {
            lock (gate)
            {
                var complete = failedWrites == 0 && threwCalls == 0;
                return (complete ? "TRANSCRIPT_COMPLETE" : "TRANSCRIPT_INCOMPLETE") + $" recorded={recorded} failedWrites={failedWrites} threwCalls={threwCalls}"
                    + (firstError is null ? "" : " firstError=" + firstError);
            }
        }
    }

    public void Dispose()
    {
        string status;
        lock (gate)
        {
            try { writer?.Dispose(); } catch (IOException) { failedWrites++; }
            status = Status;
        }
        try { File.WriteAllText(Path.Combine(folder, $"run-{RunId}.status.txt"), status + "\n", Encoding.UTF8); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The status could not be kept beside the transcript: say so on the console, so an incomplete record is never taken as complete.
            Console.Error.WriteLine("TRANSCRIPT_STATUS_NOT_WRITTEN " + e.GetType().Name + " " + status);
        }
    }
}
