using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D3.1b (offline): the envelope cap is a second, separate budget. It is checked before any model call, and the body
/// that is measured is the body that is stored and the body that a transport must send.</summary>
public sealed class P7F1V6D31bTests
{
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static IReadOnlyList<P7F1V6Hier.Cand> Four() => new P7F1V6Hier.Cand[]
    {
        new("L0001:S0", 1, "Intro"), new("L0002:S0", 1, "Body"), new("L0003:S0", 1, "Intro"), new("L0004:S0", 2, "Summary"),
    };

    static P7F1V6Hier.Options Opts() => new("Trích xuất các mục có tiêu đề", "src-sha", ShardSize: 2, Context: 1, FanIn: 3, PassUpCap: 8,
        SourceUniverseSha256: "universe-sha", ParserVersion: "parser-v1", EvidenceStoreVersion: "store-v1");

    static JsonElement Root(string text) => JsonDocument.Parse(text).RootElement;

    /// <summary>Interpretation answered; every node answered by a plain confirmation. Used only where a node must run.</summary>
    static string Honest(string nodeId, P7F1V6Hier.Kind kind, string payload)
    {
        var r = Root(payload);
        if (kind == P7F1V6Hier.Kind.Interpretation)
            return JsonSerializer.Serialize(new { protocolVersion = "P7_F1_V6_INTERPRET_V1", requestId = "INTERPRET", status = "COMPLETE",
                interpretation = "Task: " + r.GetProperty("originalUserInstruction").GetString() }, Json);
        if (kind == P7F1V6Hier.Kind.Local)
            return $"{{\"protocolVersion\":\"{P7F1V6Hier.NodeVersion}\",\"nodeId\":\"{nodeId}\",\"status\":\"COMPLETE\",\"items\":[" +
                string.Join(",", r.GetProperty("owned").EnumerateArray().Select(o => $"{{\"id\":\"{o.GetProperty("id").GetString()}\",\"decision\":\"UNRESOLVED\"}}")) + "]}";
        var ids = (kind == P7F1V6Hier.Kind.Reconcile ? r.GetProperty("owned") : r.GetProperty("shown")).EnumerateArray()
            .Select(o => $"\"{o.GetProperty("id").GetString()}\"");
        return $"{{\"protocolVersion\":\"{P7F1V6Hier.NodeVersion}\",\"nodeId\":\"{nodeId}\",\"status\":\"COMPLETE\",\"confirmed\":[{string.Join(",", ids)}]}}";
    }

    [Fact] public void The_default_envelope_cap_is_147456_bytes_and_the_payload_cap_stays_131072()
    {
        Assert.Equal(147456, Opts().MaxEnvelopeBytes);
        Assert.Equal(147456, P7F1V6Protocol.EnvelopeCapBytes);
        Assert.Equal(131072, P7F1V6Protocol.PayloadCapBytes);
        Assert.Equal(16384, P7F1V6Protocol.VisibleResponseCapBytes);
    }

    [Fact] public void An_envelope_over_its_cap_stops_the_run_before_any_model_call()
    {
        int calls = 0;
        var run = P7F1V6Hier.Run(Four(), Opts() with { MaxEnvelopeBytes = 1024 }, (n, k, p) =>
        {
            calls++;
            throw new InvalidOperationException("the model must not be called when the envelope is over its cap");
        });
        Assert.Equal("STOPPED_ENVELOPE_OVER_BUDGET:INTERPRET", run.RunStatus);
        Assert.Equal(0, calls);
        Assert.True(run.Summary.MaxEnvelopeBytes > 1024);
    }

    [Fact] public void A_node_envelope_over_its_cap_stops_at_that_node_while_its_payload_stays_under_the_payload_cap()
    {
        // Measure the two requests of a normal run, then put the cap between them: the interpretation fits, the node request does not.
        string? interpPayload = null, localPayload = null;
        P7F1V6Hier.Run(Four(), Opts(), (n, k, p) =>
        {
            if (k == P7F1V6Hier.Kind.Interpretation) interpPayload = p;
            if (k == P7F1V6Hier.Kind.Local) localPayload ??= p;
            return Honest(n, k, p);
        });
        var interpBytes = P7F1V6Protocol.Prepare(P7F1V6Protocol.InterpretPrompt, interpPayload!, "alibaba", 32768).Bytes;
        var nodeBytes = P7F1V6Protocol.Prepare(P7F1V6Protocol.SystemPrompt(P7F1V6Hier.Kind.Local), localPayload!, "alibaba", 32768).Bytes;
        Assert.True(interpBytes < nodeBytes, "the fixture needs an interpretation request smaller than a node request");

        int calls = 0;
        var run = P7F1V6Hier.Run(Four(), Opts() with { MaxEnvelopeBytes = interpBytes + 1 }, (n, k, p) =>
        {
            calls++;
            return k == P7F1V6Hier.Kind.Interpretation ? Honest(n, k, p) : throw new InvalidOperationException("no node may be called");
        });
        Assert.Equal("STOPPED_ENVELOPE_OVER_BUDGET:L0-S0000", run.RunStatus);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(run.Nodes, x => x.NodeId == "L0-S0000");
        Assert.True(Encoding.UTF8.GetByteCount(localPayload!) < P7F1V6Protocol.PayloadCapBytes, "the node payload itself is under the payload cap");
    }

    [Fact] public void Payloads_of_a_normal_run_are_under_the_payload_cap_and_the_envelope_is_larger_than_each_of_them()
    {
        var payloadBytes = new List<int>();
        var run = P7F1V6Hier.Run(Four(), Opts(), (n, k, p) => { payloadBytes.Add(Encoding.UTF8.GetByteCount(p)); return Honest(n, k, p); });
        Assert.DoesNotContain("STOPPED", run.RunStatus, StringComparison.Ordinal);
        Assert.All(payloadBytes, b => Assert.True(b < P7F1V6Protocol.PayloadCapBytes));
        Assert.True(run.Summary.MaxEnvelopeBytes > payloadBytes.Max(), "the envelope is larger than every payload it carries");
        Assert.True(run.Summary.MaxEnvelopeBytes < P7F1V6Protocol.EnvelopeCapBytes);
    }

    [Fact] public void The_prepared_body_is_the_envelope_and_the_transport_guard_rejects_any_change_to_it()
    {
        var prepared = P7F1V6Protocol.Prepare(P7F1V6Protocol.SystemPrompt(P7F1V6Hier.Kind.Local), "{\"owned\":[]}", "alibaba", 32768);
        Assert.Equal(P7F1V6Protocol.Envelope(P7F1V6Protocol.SystemPrompt(P7F1V6Hier.Kind.Local), "{\"owned\":[]}", "alibaba", 32768), prepared.Body);
        Assert.Equal(Encoding.UTF8.GetByteCount(prepared.Body), prepared.Bytes);
        Assert.True(P7F1V6Protocol.VerifyTransportBody(prepared.Body, prepared.Sha256));
        Assert.False(P7F1V6Protocol.VerifyTransportBody(prepared.Body + " ", prepared.Sha256));
        Assert.False(P7F1V6Protocol.VerifyTransportBody(prepared.Body.Replace("\"temperature\":0", "\"temperature\":1"), prepared.Sha256));
    }

    [Fact] public void The_exact_interpretation_body_that_was_measured_is_stored_under_its_hash()
    {
        string? payload = null;
        var run = P7F1V6Hier.Run(Four(), Opts(), (n, k, p) => { if (k == P7F1V6Hier.Kind.Interpretation) payload = p; return Honest(n, k, p); });
        Assert.NotNull(payload);
        var prepared = P7F1V6Protocol.Prepare(P7F1V6Protocol.InterpretPrompt, payload!, "alibaba", 32768);
        Assert.True(run.Raw.Items.ContainsKey(prepared.Sha256), "the stored body must be the body that was measured");
        Assert.True(run.Raw.Verify());
    }

    [Fact] public void The_manifest_pins_the_three_caps()
    {
        var m = Root(JsonSerializer.Serialize(P7F1V6Protocol.Manifest("alibaba", 32768)));
        Assert.Equal(131072, m.GetProperty("payloadCapBytes").GetInt32());
        Assert.Equal(147456, m.GetProperty("envelopeCapBytes").GetInt32());
        Assert.Equal(16384, m.GetProperty("visibleResponseCapBytes").GetInt32());
    }
}
