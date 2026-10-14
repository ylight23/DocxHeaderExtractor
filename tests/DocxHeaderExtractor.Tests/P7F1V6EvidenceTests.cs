using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V6Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Issue #9 D1 (review round 2): interpretation completeness, partial lines and span reading, evidence completeness,
/// exact byte caps and the raw store. Scripted models only; offline.
/// </summary>
public sealed class P7F1V6EvidenceTests
{
    const string Rfc = "todo10_8/heading_corpus_100/07_system_generated/094_RFC9113_HTTP_2.pdf";
    const string Instr = "Trích xuất các heading chính";

    static (P7F1QEvidenceTools Legacy, P7F1V6Tools Tools) Load()
    {
        var parsed = PdfSourceAdapter.BuildWithDetails(TestRepository.Path(Rfc));
        var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        var legacy = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["O1"] = parsed.Snapshot.Atoms[0].Alias }, toolSet: 2);
        return (legacy, new P7F1V6Tools(legacy.SourceSha256, legacy.SourceAliasUniverseSha256, legacy.EvidenceStoreSha256, legacy.AllLines));
    }

    static string Interp(string req, string status = "COMPLETE") =>
        $"{{\"protocolVersion\":\"P7_F1_V6_INTERPRET_V1\",\"requestId\":\"{req}\",\"status\":\"{status}\",\"interpretation\":\"Scripted.\"}}";

    static string Respond(string payload, Func<string, string> value)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var req = root.GetProperty("requestId").GetString();
        var items = root.TryGetProperty("subjects", out var s) ? s : root.GetProperty("candidates");
        var decisions = items.EnumerateArray().Select(x =>
        {
            var a = x.GetProperty("sourceAlias").GetString()!;
            return $"{{\"id\":\"{x.GetProperty("id").GetString()}\",\"decision\":\"{value(a)}\",\"evidenceRefs\":[\"{a}\"]}}";
        });
        return $"{{\"protocolVersion\":\"P7_F1_V6_DECISIONS_V1\",\"requestId\":\"{req}\",\"status\":\"COMPLETE\",\"decisions\":[{string.Join(",", decisions)}]}}";
    }

    static string Even(string alias) => int.Parse(alias[1..5]) % 2 == 0 ? "SELECT" : "NOT_SELECT";

    static P7F1V6Driver.Pack PageOf(IReadOnlyList<F1QSourceLine> lines, int page) =>
        new($"pack-p{page}", lines.Where(l => l.Page == page).Select(l => (l.Alias, l.Page!.Value, l.Text)).ToArray());

    /// <summary>A long single line on page 1, first in reading order, that cannot fit in one result.</summary>
    static F1QSourceLine LongLine(int chars) => new("L9990:S0", -1, 1, new string('q', chars), null, null);

    static JsonElement Res(P7F1V6Tools.Result r) => JsonDocument.Parse(r.Content).RootElement.GetProperty("result");

    [Fact] public void An_INCOMPLETE_interpretation_is_not_treated_as_finished_and_no_decision_pack_runs()
    {
        var (legacy, tools) = Load();
        P7F1V6Driver.ScriptedModel model = (phase, req, payload, results) => phase == "interpret"
            ? P7F1V6Driver.Turn.Final(Interp(req, "INCOMPLETE")) : P7F1V6Driver.Turn.Final(Respond(payload, Even));
        var run = P7F1V6Driver.Run(new(Instr, tools, legacy.SourceSha256, [PageOf(legacy.AllLines, 1)]), model);
        Assert.Equal("INTERPRETATION_INCOMPLETE", run.RunStatus);
        Assert.Empty(run.Passes);
        Assert.Null(run.Resolution);
    }

    [Fact] public void A_long_line_is_read_to_the_end_with_read_line_span_and_then_it_is_valid_evidence()
    {
        var (legacy, _) = Load();
        var huge = LongLine(40_000);
        var lines = legacy.AllLines.Append(huge).ToArray();
        var tools = new P7F1V6Tools(legacy.SourceSha256, legacy.SourceAliasUniverseSha256, legacy.EvidenceStoreSha256, lines);
        var pack = new P7F1V6Driver.Pack("pack-1", [(huge.Alias, 1, huge.Text)]);
        int readOffset = -1;
        P7F1V6Driver.ScriptedModel model = (phase, req, payload, results) =>
        {
            if (phase == "interpret") return P7F1V6Driver.Turn.Final(Interp(req));
            if (phase != "pack-1") return P7F1V6Driver.Turn.Final(Respond(payload, Even));
            if (results.Count == 0) return P7F1V6Driver.Turn.Tool("read_line_span", $"{{\"sourceAlias\":\"{huge.Alias}\",\"offset\":0,\"limit\":20000}}");
            var last = JsonDocument.Parse(results[^1]).RootElement.GetProperty("result");
            if (last.GetProperty("hasMore").GetBoolean())
            {
                readOffset = last.GetProperty("nextOffset").GetInt32();
                return P7F1V6Driver.Turn.Tool("read_line_span", $"{{\"sourceAlias\":\"{huge.Alias}\",\"offset\":{readOffset},\"limit\":20000}}");
            }
            return P7F1V6Driver.Turn.Final(Respond(payload, _ => "SELECT"));
        };
        var run = P7F1V6Driver.Run(new(Instr, tools, legacy.SourceSha256, [pack], MaxToolRounds: 6), model);
        Assert.True(readOffset > 0, "the line had to be continued");
        Assert.DoesNotContain(huge.Alias, run.Ledger.IncompleteEvidence);          // fully read now
        Assert.Equal("SELECT", run.Passes.First(p => p.Name == "pack-1").Decisions[run.AliasById[huge.Alias]].Value);
        Assert.Null(run.Passes.First(p => p.Name == "pack-1").FailureCode);
        Assert.Equal("RESOLVED", run.RunStatus);
    }

    [Fact] public void A_decision_on_a_line_read_only_in_part_is_rejected_as_SUBJECT_EVIDENCE_INCOMPLETE()
    {
        var (legacy, _) = Load();
        var huge = LongLine(40_000);
        var lines = legacy.AllLines.Append(huge).ToArray();
        var tools = new P7F1V6Tools(legacy.SourceSha256, legacy.SourceAliasUniverseSha256, legacy.EvidenceStoreSha256, lines);
        var pack = new P7F1V6Driver.Pack("pack-1", [(huge.Alias, 1, huge.Text)]);
        P7F1V6Driver.ScriptedModel model = (phase, req, payload, results) =>
        {
            if (phase == "interpret") return P7F1V6Driver.Turn.Final(Interp(req));
            if (phase == "pack-1") return results.Count == 0
                ? P7F1V6Driver.Turn.Tool("get_page_text", "{\"page\":1,\"offset\":0,\"limit\":1}")       // only the first part is read
                : P7F1V6Driver.Turn.Final(Respond(payload, _ => "SELECT"));                                // and a decision is made anyway
            return P7F1V6Driver.Turn.Final(Respond(payload, Even));
        };
        var run = P7F1V6Driver.Run(new(Instr, tools, legacy.SourceSha256, [pack]), model);
        Assert.Equal("SUBJECT_EVIDENCE_INCOMPLETE", run.Passes.First(p => p.Name == "pack-1").FailureCode);
        Assert.Equal("PASS_FAILED", run.Status!.Assessment);
        Assert.Contains(huge.Alias, run.Ledger.IncompleteEvidence);
    }

    [Fact] public void Evidence_read_only_in_part_makes_a_decided_document_EVIDENCE_INCOMPLETE_not_RESOLVED()
    {
        var (legacy, _) = Load();
        var huge = LongLine(40_000);          // on page 1, not a candidate: the model reads a part of it and decides everything else
        var lines = legacy.AllLines.Append(huge).ToArray();
        var tools = new P7F1V6Tools(legacy.SourceSha256, legacy.SourceAliasUniverseSha256, legacy.EvidenceStoreSha256, lines);
        P7F1V6Driver.ScriptedModel model = (phase, req, payload, results) =>
        {
            if (phase == "interpret") return P7F1V6Driver.Turn.Final(Interp(req));
            if (phase == "pack-1" && results.Count == 0) return P7F1V6Driver.Turn.Tool("get_page_text", "{\"page\":1,\"offset\":0,\"limit\":1}");
            return P7F1V6Driver.Turn.Final(Respond(payload, Even));
        };
        var run = P7F1V6Driver.Run(new(Instr, tools, legacy.SourceSha256, [PageOf(legacy.AllLines, 1)]), model);
        Assert.Equal("EVIDENCE_INCOMPLETE", run.RunStatus);
        Assert.True(run.Status!.CoverageComplete);
        Assert.True(run.Status.ResolvedComplete == false);
        Assert.False(run.Status.EvidenceComplete);
        Assert.Contains(huge.Alias, run.Ledger.IncompleteEvidence);
    }

    [Fact] public void read_line_span_refuses_a_gap_and_a_line_outside_the_granted_scope()
    {
        var (legacy, _) = Load();
        var huge = LongLine(40_000);
        var t = new P7F1V6Tools(legacy.SourceSha256, legacy.SourceAliasUniverseSha256, legacy.EvidenceStoreSha256, legacy.AllLines.Append(huge).ToArray(), new HashSet<int> { 2 });
        Assert.Equal("SCOPE_DENIED", t.ReadLineSpan("E.1", "REQ", huge.Alias, 0, 100).ErrorCode);
        var t2 = new P7F1V6Tools(legacy.SourceSha256, legacy.SourceAliasUniverseSha256, legacy.EvidenceStoreSha256, legacy.AllLines.Append(huge).ToArray());
        Assert.Equal("SPAN_GAP", t2.ReadLineSpan("E.2", "REQ", huge.Alias, 500, 100).ErrorCode);       // nothing read yet: offset 500 is a gap
        Assert.Equal("ALIAS_NOT_FOUND", t2.ReadLineSpan("E.3", "REQ", "L9999:S9", 0, 100).ErrorCode);
    }

    [Fact] public void Every_returned_body_is_within_the_cap_measured_exactly_and_every_transcript_hash_resolves_in_the_raw_store()
    {
        var (legacy, tools) = Load();
        var lines = legacy.AllLines.Append(LongLine(40_000)).ToArray();
        var t = new P7F1V6Tools(legacy.SourceSha256, legacy.SourceAliasUniverseSha256, legacy.EvidenceStoreSha256, lines);
        var pages = t.Pages.ToArray();
        foreach (var pg in pages)
        {
            var r = t.GetPageText("E.x", "REQ", pg, 0, 50);
            Assert.True(r.Content.Length <= P7F1V6Tools.MaxResultBytes);
        }
        var run = P7F1V6Driver.Run(new(Instr, tools, legacy.SourceSha256, [PageOf(legacy.AllLines, 1), PageOf(legacy.AllLines, 2)], RawDir: null),
            (phase, req, payload, results) => phase == "interpret" ? P7F1V6Driver.Turn.Final(Interp(req)) : P7F1V6Driver.Turn.Final(Respond(payload, Even)));
        Assert.True(run.Raw.Verify());
        var hashes = run.Transcript.Select(e => JsonDocument.Parse(JsonSerializer.Serialize(e)).RootElement)
            .Where(e => e.GetProperty("data").TryGetProperty("sha256", out _))
            .Select(e => e.GetProperty("data").GetProperty("sha256").GetString()!).ToArray();
        Assert.NotEmpty(hashes);
        Assert.All(hashes, h => Assert.True(run.Raw.Items.ContainsKey(h), $"raw item {h} missing"));
        var payloads = run.Transcript.Count(e => JsonSerializer.Serialize(e).Contains("\"kind\":\"PAYLOAD\"", StringComparison.Ordinal));
        Assert.Equal(run.Passes.Count + 1, payloads);     // one payload per pass plus the interpretation
    }
}
