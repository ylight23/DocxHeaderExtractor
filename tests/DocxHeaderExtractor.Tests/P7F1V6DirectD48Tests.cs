using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.V6Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.8 (offline): request V3 separates the lines to decide (targets) from the lines shown only as evidence (context). Every
/// model here is scripted, including an adversarial one that decides context lines; these tests check the protocol and the C# side only.</summary>
public sealed class P7F1V6DirectD48Tests
{
    const string Task = "Trích xuất tất cả heading";

    static string A(int i) => $"L{i:0000}:S0";
    static int Idx(string alias) => int.Parse(alias.AsSpan(1, 4));
    static List<P7F1V6Direct.Line> Lines(int n) => Enumerable.Range(0, n).Select(i => new P7F1V6Direct.Line(A(i), 1 + i / 40, $"Dòng {i}")).ToList();
    static P7F1V6DirectV2.Budget Gen() => new(10_000, 1_000, 2_000_000);

    static JsonElement Payload(P7F1V6Protocol.PreparedRequest req)
    {
        using var doc = JsonDocument.Parse(req.Body);
        using var p = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return p.RootElement.Clone();
    }

    static string[] Targets(P7F1V6Protocol.PreparedRequest req) => Payload(req).GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
    static string[] Context(P7F1V6Protocol.PreparedRequest req) => Payload(req).GetProperty("context").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
    static string Stage(P7F1V6Protocol.PreparedRequest req) => Payload(req).GetProperty("stage").GetString()!;

    static string Complete(IEnumerable<string> decide, ISet<int> select, IEnumerable<int[]>? groups = null)
    {
        var d = decide.ToArray();
        var hs = groups?.ToList() ?? d.Where(a => select.Contains(Idx(a))).Select(a => new[] { Idx(a) }).ToList();
        return JsonSerializer.Serialize(new
        {
            protocolVersion = P7F1V6DirectV2.ResponseVersion,
            status = "COMPLETE",
            decisions = d.Select(a => new { alias = a, decision = select.Contains(Idx(a)) ? "SELECT" : "NOT_SELECT", evidenceRefs = new[] { a } }),
            headings = hs.Select(h => new { parts = h.Select(A).ToArray() }),
        });
    }

    /// <summary>Checks the V3 shape of one request: targets and context disjoint, every alias once in the whole payload, no V2 fields.</summary>
    static void AssertV3(P7F1V6Protocol.PreparedRequest req)
    {
        var p = Payload(req);
        Assert.Equal(P7F1V6DirectV2.RequestVersion, p.GetProperty("protocolVersion").GetString());
        foreach (var gone in new[] { "decide", "lines", "currentHeadings" }) Assert.False(p.TryGetProperty(gone, out _), "V2 field " + gone);
        var t = Targets(req); var c = Context(req);
        Assert.NotEmpty(t);
        Assert.Empty(t.Intersect(c));
        var raw = p.GetRawText();
        foreach (var a in t.Concat(c))
            Assert.Equal(1, Regex.Matches(raw, Regex.Escape("\"" + a + "\"")).Count);
    }

    // ---------------- request shape in every stage ----------------

    [Fact]
    public void Segment_TargetsAreTheCore_ContextIsTheOverlap()
    {
        var requests = new List<P7F1V6Protocol.PreparedRequest>();
        P7F1V6DirectV2.Run(Task, "sha", Lines(30), (u, r, req) => { requests.Add(req); return Complete(Targets(req), new HashSet<int>()); },
            Gen(), overlap: 2, perUnit: 10);
        Assert.All(requests, AssertV3);
        var seg1 = requests.First(r => Payload(r).GetProperty("unitId").GetString() == "SEG-001");
        Assert.Equal(Enumerable.Range(10, 10).Select(A), Targets(seg1));
        Assert.Equal(new[] { A(8), A(9), A(20), A(21) }, Context(seg1));
    }

    [Fact]
    public void Retrieval_AddsToContext_AndNeverChangesTargets()
    {
        var lines = Lines(100);
        var seen = new List<P7F1V6Protocol.PreparedRequest>();
        P7F1V6DirectV2.Run(Task, "sha", lines, (u, r, req) =>
        {
            if (u == "SEG-000") seen.Add(req);
            if (u == "SEG-000" && r == 1) return JsonSerializer.Serialize(new { protocolVersion = P7F1V6DirectV2.ResponseVersion, status = "NEEDS_EVIDENCE",
                retrieve = new[] { new { kind = "RANGE", fromAlias = A(50), toAlias = A(52) } } });
            return Complete(Targets(req), new HashSet<int>());
        }, Gen(), overlap: 2, perUnit: 10);

        Assert.Equal(2, seen.Count);
        Assert.All(seen, AssertV3);
        Assert.Equal(Targets(seen[0]), Targets(seen[1]));
        Assert.DoesNotContain(A(51), Context(seen[0]));
        Assert.Contains(A(51), Context(seen[1]));
        var log = Payload(seen[1]).GetProperty("retrievals")[0];
        Assert.Equal(3, log.GetProperty("linesReturned").GetInt32());
        Assert.False(log.TryGetProperty("aliases", out _));
    }

    [Fact]
    public void Resolve_AndFinalize_UseTheSameSplit_AndFinalizeMarksCurrentGroups()
    {
        var requests = new List<P7F1V6Protocol.PreparedRequest>();
        var run = P7F1V6DirectV2.Run(Task, "sha", Lines(20), (u, r, req) =>
        {
            requests.Add(req);
            var t = Targets(req);
            if (u == "SEG-000") return Complete(t, new HashSet<int> { 9 }, new[] { new[] { 9, 10 } });
            if (u == "SEG-001") return Complete(t, new HashSet<int>());                         // the owners disagree: resolve runs
            if (Stage(req) == "RESOLVE") return Complete(t, new HashSet<int> { 9, 10 }, new[] { new[] { 9, 10 } });
            return Complete(t, new HashSet<int> { 9, 10 }, new[] { new[] { 9, 10 } });           // FINALIZE keeps the group
        }, Gen(), overlap: 2, perUnit: 10);

        Assert.All(requests, AssertV3);
        var resolve = requests.Single(r => Stage(r) == "RESOLVE");
        Assert.Equal(new[] { A(9), A(10) }, Targets(resolve));
        var final = requests.Single(r => Stage(r) == "FINALIZE");
        Assert.Equal(new[] { A(9), A(10) }, Targets(final));
        var groups = Payload(final).GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("currentGroup").GetString()).Distinct().ToList();
        Assert.Equal(new[] { "G000" }, groups);
        Assert.Equal("COMPLETE", run.Status);
        Assert.Equal(new[] { A(9), A(10) }, Assert.Single(run.Headings));
    }

    [Fact]
    public void MultipartHeading_MayUseAContextAlias()
    {
        var run = P7F1V6DirectV2.Run(Task, "sha", Lines(20), (u, r, req) =>
        {
            var t = Targets(req);
            if (Stage(req) == "SEGMENT" && u == "SEG-000") return Complete(t, new HashSet<int> { 9 }, new[] { new[] { 9, 10 } });  // 10 is context here
            if (Stage(req) == "SEGMENT") return Complete(t, new HashSet<int> { 10 }, new[] { new[] { 9, 10 } });                   // 9 is context here
            return Complete(t, new HashSet<int> { 9, 10 }, new[] { new[] { 9, 10 } });
        }, Gen(), overlap: 2, perUnit: 10);
        Assert.Equal("COMPLETE", run.Status);
        Assert.Equal(new[] { A(9), A(10) }, Assert.Single(run.Headings));
    }

    // ---------------- adversarial and coverage ----------------

    [Fact]
    public void Adversarial_DecidingContextLines_IsRefused_NotTrimmed()
    {
        // The scripted model decides every target AND every context line. The parser must refuse the reply; it must not drop the extra
        // decisions and keep the rest.
        var budget = Gen();
        var run = P7F1V6DirectV2.Run(Task, "sha", Lines(40), (u, r, req) =>
            Complete(Targets(req).Concat(Context(req)), new HashSet<int> { 3, 12 }), budget, overlap: 2, perUnit: 10, finalize: false);

        var seg0 = run.Units.First(u => u.UnitId == "SEG-000");
        Assert.Equal("FAILED", seg0.Status);
        Assert.Equal("PROTOCOL:UNEXPECTED_ALIAS", seg0.Failure);
        Assert.All(seg0.Decisions.Values, v => Assert.Equal("UNRESOLVED", v));
        Assert.Equal("UNRESOLVED", run.Decisions[A(3)]);
        Assert.Equal("CONSECUTIVE_CONTRACT_FAILURES", budget.HaltReason);
        Assert.Equal(3, budget.Calls);
        Assert.Equal("INCOMPLETE", run.Status);
    }

    [Fact]
    public void Adversarial_DecidingAContextLineInstead_IsRefused()
    {
        var reply = (P7F1V6Protocol.PreparedRequest req) =>
        {
            var t = Targets(req).ToList();
            var c = Context(req);
            t[^1] = c[0];                       // swaps the last target for a context line: same count, wrong scope
            return Complete(t, new HashSet<int>());
        };
        var run = P7F1V6DirectV2.Run(Task, "sha", Lines(20), (u, r, req) => reply(req), Gen(), overlap: 2, perUnit: 10, finalize: false);
        Assert.Equal("PROTOCOL:UNEXPECTED_ALIAS", run.Units[0].Failure);
    }

    [Fact]
    public void MissingATarget_IsCoverageIncomplete()
    {
        var run = P7F1V6DirectV2.Run(Task, "sha", Lines(20), (u, r, req) => Complete(Targets(req).Skip(1), new HashSet<int>()),
            Gen(), overlap: 2, perUnit: 10, finalize: false);
        Assert.Equal("PROTOCOL:COVERAGE_INCOMPLETE", run.Units[0].Failure);
        Assert.Equal("UNRESOLVED", run.Decisions[A(0)]);
    }

    [Fact]
    public void BuildRequest_RefusesARepeatedLine_OrATargetNotShown()
    {
        var lines = Lines(3);
        Assert.Throws<ArgumentException>(() => P7F1V6DirectV2.BuildRequest(Task, "sha", "SEGMENT", "SEG-000", 1, new[] { A(0) },
            lines.Append(lines[0]).ToList(), Array.Empty<P7F1V6DirectV2.Retrieval>()));
        Assert.Throws<ArgumentException>(() => P7F1V6DirectV2.BuildRequest(Task, "sha", "SEGMENT", "SEG-000", 1, new[] { A(7) },
            lines, Array.Empty<P7F1V6DirectV2.Retrieval>()));
        Assert.Throws<ArgumentException>(() => P7F1V6DirectV2.BuildRequest(Task, "sha", "SEGMENT", "SEG-000", 1, new[] { A(0), A(0) },
            lines, Array.Empty<P7F1V6DirectV2.Retrieval>()));
    }

    [Fact]
    public void Prompt_V24_ExplainsTargetsAndContext_AndIsPinned()
    {
        var p = P7F1V6DirectV2.SystemPrompt;
        Assert.Equal("P7_F1_V6_DIRECT_PROMPT_V2_4", P7F1V6DirectV2.PromptVersion);
        Assert.Equal(P7F1V6DirectV2.SystemPromptSha256, P7F1V6DirectV2.ComputedSystemPromptSha256());
        Assert.Contains("NO decision for any alias in context", p, StringComparison.Ordinal);
        Assert.Contains("targets never change", p, StringComparison.Ordinal);
        Assert.DoesNotContain("decide (", p, StringComparison.Ordinal);
    }
}
