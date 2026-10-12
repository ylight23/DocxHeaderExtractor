using System.Text.Encodings.Web;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// The mock end-to-end path (D3.5a). It runs the same dispatch, audit and parser path as a live run, but the port is a mock: it answers
/// from scripted decisions wrapped in the chat response shape, with usage, and never opens a connection. A mock run is labelled MOCK and
/// can only reach a mock port (enforced in <see cref="P7F1V6Runner.RunState"/>).
/// The scripted decisions are a protocol exercise. They are not evidence of Qwen's behaviour.
/// </summary>
internal static class P7F1V6MockRun
{
    // Unicode text stays as UTF-8 in every body the mock writes: an escaped character would be refused by the parser.
    static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed class MockPort : P7F1V6Runner.ISendPort
    {
        public bool IsMock => true;
        public int Calls { get; private set; }
        public List<P7F1V6Protocol.PreparedRequest> Requests { get; } = new();

        public P7F1V6OpenRouterTransport.SendResult Send(P7F1V6Protocol.PreparedRequest request)
        {
            Calls++;
            Requests.Add(request);
            using var doc = JsonDocument.Parse(request.Body);
            var user = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            var content = P7F1V6ScriptedResponder.Respond(user);
            var body = JsonSerializer.Serialize(new
            {
                id = "mock", @object = "chat.completion",
                choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
                usage = new { prompt_tokens = Math.Max(1, request.Bytes / 8), completion_tokens = 200,
                    completion_tokens_details = new { reasoning_tokens = 20 } },
            }, Options);
            return new P7F1V6OpenRouterTransport.SendResult("HTTP_OK", 200, body, request.Sha256, request.Bytes, 1);
        }
    }

    /// <summary>Runs the protocol over the candidates through the mock port. Returns the run and the run state (attempts, reserve, stop reason).</summary>
    public static (P7F1V6Hier.RunResult Run, P7F1V6Runner.RunState State, MockPort Port) Run(IReadOnlyList<P7F1V6Hier.Cand> cands,
        P7F1V6Hier.Options options, P7F1V6Preflight.Result preflight)
    {
        if (preflight.Mode != P7F1V6Preflight.RunMode.Mock) throw new InvalidOperationException("MOCK_RUN_REQUIRES_MOCK_PREFLIGHT");
        var state = new P7F1V6Runner.RunState(preflight);
        var port = new MockPort();
        var run = P7F1V6Hier.Run(cands, options, (P7F1V6Hier.PreparedNodeModel)((n, k, p, r) => state.Dispatch(r, port) ?? ""));
        return (run, state, port);
    }
}

/// <summary>Scripted decisions for the protocol exercise: local decides by alias parity, reconcile and synthesis and global confirm what they
/// were shown, interpretation restates the instruction. Used only by the mock path and the C0 replay.</summary>
internal static class P7F1V6ScriptedResponder
{
    static readonly JsonSerializerOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Respond(string userPayload)
    {
        using var doc = JsonDocument.Parse(userPayload);
        var r = doc.RootElement;
        var kind = r.TryGetProperty("kind", out var kp) ? kp.GetString() : "Interpretation";
        var nodeId = r.TryGetProperty("nodeId", out var np) ? np.GetString() : "INTERPRET";
        if (kind == "Interpretation")
            return JsonSerializer.Serialize(new { protocolVersion = "P7_F1_V6_INTERPRET_V1", requestId = "INTERPRET", status = "COMPLETE",
                interpretation = "Task: " + r.GetProperty("originalUserInstruction").GetString() }, Options);
        if (kind == "Local")
        {
            var items = r.GetProperty("owned").EnumerateArray().Select(o =>
            {
                var a = o.GetProperty("sourceAlias").GetString()!;
                var even = int.Parse(a[1..5]) % 2 == 0;
                return new { id = o.GetProperty("id").GetString(), decision = even ? "SELECT" : "NOT_SELECT", evidenceRefs = new[] { a } };
            }).ToArray();
            return JsonSerializer.Serialize(new { protocolVersion = P7F1V6Hier.NodeVersion, nodeId, status = "COMPLETE", items }, Options);
        }
        var ids = (kind == "Reconcile" ? r.GetProperty("owned") : r.GetProperty("shown")).EnumerateArray()
            .Select(o => o.GetProperty("id").GetString()).ToArray();
        return JsonSerializer.Serialize(new { protocolVersion = P7F1V6Hier.NodeVersion, nodeId, status = "COMPLETE", confirmed = ids }, Options);
    }
}
