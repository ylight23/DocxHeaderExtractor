using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.3 (offline): the DIRECT-F1 V2 contract (status, UNRESOLVED, clarification, retrieval, multipart headings), the
/// document driver with its resolve pass, and the scorer on real multipart Gold. Every model here is scripted: these tests check the protocol
/// and the C# side only. They are not evidence of what Qwen does.</summary>
public sealed class P7F1V6DirectD43Tests : IDisposable
{
    const string Registry = "eval/a99-closed-loop/gold-current/registry.v1.json";
    const string Instruction = "Trích xuất các heading chính";
    readonly string scratch = Path.Combine(Path.GetTempPath(), "dhx-d43-" + Guid.NewGuid().ToString("N"));

    public P7F1V6DirectD43Tests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
    }

    // ---------------- helpers ----------------

    static string A(int i) => $"L{i:0000}:S0";

    static List<P7F1V6Direct.Line> Lines(int n, Func<int, string>? text = null) =>
        Enumerable.Range(0, n).Select(i => new P7F1V6Direct.Line(A(i), 1, text?.Invoke(i) ?? $"Dòng {i}")).ToList();

    static Dictionary<string, int> Order(IEnumerable<int> shown) => shown.ToDictionary(A, i => i);

    static string Decisions(string status, IEnumerable<(int I, string V)> ds, IEnumerable<int[]>? headings = null, string? reason = null)
    {
        var o = new Dictionary<string, object?>
        {
            ["protocolVersion"] = P7F1V6DirectV2.ResponseVersion,
            ["status"] = status,
            ["decisions"] = ds.Select(d => new { alias = A(d.I), decision = d.V, evidenceRefs = new[] { A(d.I) } }).ToArray(),
            ["headings"] = (headings ?? Array.Empty<int[]>()).Select(h => new { parts = h.Select(A).ToArray() }).ToArray(),
        };
        if (reason is not null) o["incompleteReason"] = reason;
        return JsonSerializer.Serialize(o);
    }

    static string Needs(params object[] retrieve) =>
        JsonSerializer.Serialize(new { protocolVersion = P7F1V6DirectV2.ResponseVersion, status = "NEEDS_EVIDENCE", retrieve });

    static string Clarify(params string[] interpretations) =>
        JsonSerializer.Serialize(new { protocolVersion = P7F1V6DirectV2.ResponseVersion, status = "CLARIFICATION_REQUIRED",
            clarification = new { question = "Heading chính nghĩa là cấp nào?", interpretations } });

    static JsonElement Payload(P7F1V6Protocol.PreparedRequest req)
    {
        using var doc = JsonDocument.Parse(req.Body);
        using var p = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return p.RootElement.Clone();
    }

    static string[] Decide(P7F1V6Protocol.PreparedRequest req) => Payload(req).GetProperty("decide").EnumerateArray().Select(x => x.GetString()!).ToArray();

    static string[] Shown(P7F1V6Protocol.PreparedRequest req) =>
        Payload(req).GetProperty("lines").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();

    static int Idx(string alias) => int.Parse(alias.AsSpan(1, 4));

    /// <summary>A scripted model: decides every alias in decide NOT_SELECT unless the selector says SELECT; groups SELECTs by the grouping.</summary>
    static string AllNot(P7F1V6Protocol.PreparedRequest req) =>
        Decisions("COMPLETE", Decide(req).Select(a => (Idx(a), "NOT_SELECT")));

    // ---------------- reply contract ----------------

    [Fact]
    public void Reply_CompleteWithAMultipartHeading_IsAccepted()
    {
        var r = P7F1V6DirectV2.Parse(Decisions("COMPLETE", new[] { (0, "SELECT"), (1, "SELECT"), (2, "NOT_SELECT") }, new[] { new[] { 0, 1 } }),
            Order(Enumerable.Range(0, 6)), new[] { A(0), A(1), A(2) });
        Assert.True(r.Ok, r.Failure);
        Assert.Single(r.Headings);
        Assert.Equal(new[] { A(0), A(1) }, r.Headings[0]);
    }

    [Fact]
    public void Reply_HeadingMayIncludeAShownAtomOwnedByTheNextSegment()
    {
        var r = P7F1V6DirectV2.Parse(Decisions("COMPLETE", new[] { (0, "NOT_SELECT"), (1, "NOT_SELECT"), (2, "SELECT") }, new[] { new[] { 2, 3 } }),
            Order(Enumerable.Range(0, 6)), new[] { A(0), A(1), A(2) });
        Assert.True(r.Ok, r.Failure);
    }

    [Fact]
    public void Reply_IncompleteWithUnresolved_IsAccepted_AndKeepsUnresolved()
    {
        var r = P7F1V6DirectV2.Parse(Decisions("INCOMPLETE", new[] { (0, "UNRESOLVED"), (1, "NOT_SELECT") }, reason: "cần xem mục lục"),
            Order(Enumerable.Range(0, 4)), new[] { A(0), A(1) });
        Assert.True(r.Ok, r.Failure);
        Assert.Equal("UNRESOLVED", r.Decisions[0].Value);
    }

    [Fact]
    public void Reply_Clarification_IsAccepted()
    {
        var r = P7F1V6DirectV2.Parse(Clarify("Chỉ cấp chương", "Chương và mục"), Order(Enumerable.Range(0, 2)), new[] { A(0) });
        Assert.True(r.Ok, r.Failure);
        Assert.Equal(2, r.Clarification!.Interpretations.Count);
    }

    [Fact]
    public void Reply_NeedsEvidence_IsAccepted()
    {
        var r = P7F1V6DirectV2.Parse(Needs(new { kind = "SEARCH", text = "Mục lục" }, new { kind = "RANGE", fromAlias = A(0), toAlias = A(5) }),
            Order(Enumerable.Range(0, 2)), new[] { A(0) });
        Assert.True(r.Ok, r.Failure);
        Assert.Equal(2, r.Retrieve.Count);
    }

    public static IEnumerable<object[]> BadReplies()
    {
        string D(string status, (int, string)[] ds, int[][]? hs = null, string? reason = null) => Decisions(status, ds, hs, reason);
        yield return new object[] { D("COMPLETE", new[] { (0, "UNRESOLVED"), (1, "NOT_SELECT") }), "UNRESOLVED_ONLY_IN_INCOMPLETE" };
        yield return new object[] { D("INCOMPLETE", new[] { (0, "UNRESOLVED"), (1, "NOT_SELECT") }), "INCOMPLETE_REASON_MISSING" };
        yield return new object[] { D("INCOMPLETE", new[] { (0, "NOT_SELECT"), (1, "NOT_SELECT") }, reason: "x"), "INCOMPLETE_WITHOUT_UNRESOLVED" };
        yield return new object[] { D("COMPLETE", new[] { (0, "SELECT"), (1, "NOT_SELECT") }), "SELECT_WITHOUT_HEADING" };
        yield return new object[] { D("COMPLETE", new[] { (0, "SELECT"), (1, "NOT_SELECT") }, new[] { new[] { 0, 1 } }), "HEADING_PART_NOT_SELECTED" };
        yield return new object[] { D("COMPLETE", new[] { (0, "SELECT"), (1, "SELECT") }, new[] { new[] { 1, 0 } }), "HEADING_PARTS_NOT_IN_DOCUMENT_ORDER" };
        yield return new object[] { D("COMPLETE", new[] { (0, "SELECT"), (1, "NOT_SELECT") }, new[] { new[] { 0 }, new[] { 2, 3 } }), "HEADING_WITHOUT_OWNED_SELECT" };
        yield return new object[] { D("COMPLETE", new[] { (0, "SELECT"), (1, "NOT_SELECT") }, new[] { new[] { 0, 9 } }), "HEADING_PART_NOT_SHOWN" };
        yield return new object[] { D("COMPLETE", new[] { (0, "SELECT"), (1, "SELECT") }, new[] { new[] { 0, 1 }, new[] { 1 } }), "ALIAS_IN_TWO_HEADINGS" };
        yield return new object[] { D("COMPLETE", new[] { (0, "NOT_SELECT") }), "COVERAGE_INCOMPLETE" };
        yield return new object[] { D("COMPLETE", new[] { (0, "NOT_SELECT"), (1, "NOT_SELECT"), (2, "NOT_SELECT") }), "UNEXPECTED_ALIAS" };
        yield return new object[] { Clarify("chỉ một cách hiểu"), "CLARIFICATION_INTERPRETATIONS_INVALID" };
        yield return new object[] { "{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V2\",\"status\":\"CLARIFICATION_REQUIRED\",\"clarification\":{\"question\":\"q\",\"interpretations\":[\"a\",\"b\"]},\"decisions\":[{\"alias\":\"L0000:S0\",\"decision\":\"NOT_SELECT\",\"evidenceRefs\":[\"L0000:S0\"]}]}", "CLARIFICATION_WITH_OTHER_FIELDS" };
        yield return new object[] { Needs(Enumerable.Range(0, 5).Select(i => (object)new { kind = "SEARCH", text = "ab" + i }).ToArray()), "RETRIEVE_INVALID" };
        yield return new object[] { Needs(new { kind = "SEARCH", text = "a" }), "RETRIEVE_SEARCH_INVALID" };
        yield return new object[] { Needs(new { kind = "SUMMARY", text = "ab" }), "RETRIEVE_KIND_INVALID" };
        yield return new object[] { "{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V2\",\"status\":\"NEEDS_EVIDENCE\",\"retrieve\":[{\"kind\":\"SEARCH\",\"text\":\"ab\"}],\"decisions\":[{\"alias\":\"L0000:S0\",\"decision\":\"NOT_SELECT\",\"evidenceRefs\":[\"L0000:S0\"]}]}", "NEEDS_EVIDENCE_WITH_OTHER_FIELDS" };
        yield return new object[] { "{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V2\",\"status\":\"COMPLETE\",\"decisions\":[{\"alias\":\"L0000:S0\",\"decision\":\"SELECT\",\"evidenceRefs\":[\"L0000:S0\"],\"level\":1}],\"headings\":[{\"parts\":[\"L0000:S0\"]}]}", "DECISION_KEYS_INVALID" };
        yield return new object[] { "{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V2\",\"status\":\"COMPLETE\",\"decisions\":[],\"parentId\":\"x\"}", "ROOT_KEYS_INVALID" };
        yield return new object[] { "{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V2\",\"status\":\"DONE\"}", "STATUS_INVALID" };
    }

    [Theory]
    [MemberData(nameof(BadReplies))]
    public void Reply_Malformed_IsRefused(string reply, string code)
    {
        var r = P7F1V6DirectV2.Parse(reply, Order(Enumerable.Range(0, 4)), new[] { A(0), A(1) });
        Assert.False(r.Ok);
        Assert.Equal(code, r.Failure);
    }

    // ---------------- driver ----------------

    [Fact]
    public void Retrieval_Search_AddsFarLinesToTheNextRequest()
    {
        var lines = Lines(100, i => i == 80 ? "Mục lục đặc biệt" : $"Dòng {i}");
        var seen = new List<P7F1V6Protocol.PreparedRequest>();
        var run = P7F1V6DirectV2.Run(Instruction, "sha", lines, (u, round, req) =>
        {
            seen.Add(req);
            if (u == "SEG-000" && round == 1) return Needs(new { kind = "SEARCH", text = "đặc biệt" }, new { kind = "RANGE", fromAlias = "L9999:S0", toAlias = A(3) });
            return AllNot(req);
        }, overlap: 2, perUnit: 10);

        Assert.Equal("COMPLETE", run.Status);
        var seg0 = run.Units[0];
        Assert.Equal(2, seg0.Calls);
        Assert.DoesNotContain(A(80), Shown(seg0.Requests[0]));
        Assert.Contains(A(80), Shown(seg0.Requests[1]));
        Assert.Equal("OK", seg0.Retrievals[0].Status);
        Assert.Equal(new[] { A(80) }, seg0.Retrievals[0].Aliases);
        Assert.Equal("ALIAS_UNKNOWN", seg0.Retrievals[1].Status);
        Assert.Equal(2, Payload(seg0.Requests[1]).GetProperty("retrievals").GetArrayLength());
        Assert.False(Payload(seg0.Requests[0]).GetProperty("retrievals").EnumerateArray().Any());
    }

    [Fact]
    public void Retrieval_RangeIsCappedAndReported()
    {
        var lines = Lines(200);
        var (found, log) = P7F1V6DirectV2.Serve(new P7F1V6DirectV2.RetrieveRequest("RANGE", A(0), A(150), null), lines,
            lines.Select((l, i) => (l.Alias, i)).ToDictionary(x => x.Alias, x => x.i));
        Assert.Equal(P7F1V6DirectV2.MaxRangeLines, found.Count);
        Assert.Equal("TRUNCATED", log.Status);
    }

    [Fact]
    public void Retrieval_RoundsExhausted_LeavesAliasesUnresolved_NeverNotSelect()
    {
        var lines = Lines(20);
        var run = P7F1V6DirectV2.Run(Instruction, "sha", lines, (u, round, req) =>
            u == "SEG-000" || u.StartsWith("RESOLVE", StringComparison.Ordinal) ? Needs(new { kind = "SEARCH", text = "Dòng" }) : AllNot(req),
            overlap: 2, perUnit: 10);

        Assert.Equal("INCOMPLETE", run.Status);
        Assert.Equal("RETRIEVE_AFTER_LAST_ROUND", run.Units[0].Failure);
        Assert.Equal(P7F1V6DirectV2.MaxRounds, run.Units[0].Calls);
        Assert.Equal(Enumerable.Range(0, 10).Select(A), run.Unresolved);
        Assert.All(Enumerable.Range(0, 10), i => Assert.Equal("UNRESOLVED", run.Decisions[A(i)]));
        Assert.Contains(run.Units, u => u.UnitId == "RESOLVE-000");
    }

    [Fact]
    public void Clarification_StopsTheRunAtOnce()
    {
        var lines = Lines(50);
        var run = P7F1V6DirectV2.Run(Instruction, "sha", lines, (u, round, req) => Clarify("Chỉ cấp chương", "Chương và mục"), perUnit: 10);
        Assert.Equal("CLARIFICATION_REQUIRED", run.Status);
        Assert.Equal(1, run.Calls);
        Assert.Single(run.Units);
        Assert.Equal(new[] { "Chỉ cấp chương", "Chương và mục" }, run.Clarification!.Interpretations);
        Assert.Empty(run.Decisions);
    }

    [Fact]
    public void ProtocolFailure_LeavesAliasesUnresolved_AndTheResolvePassDecidesThem()
    {
        var lines = Lines(20);
        var run = P7F1V6DirectV2.Run(Instruction, "sha", lines, (u, round, req) => u == "SEG-001" ? "not json" : AllNot(req), overlap: 2, perUnit: 10);
        Assert.Equal("FAILED", run.Units[1].Status);
        Assert.Equal("PROTOCOL:REPLY_NOT_JSON", run.Units[1].Failure);
        var resolve = Assert.Single(run.Units, u => u.UnitId == "RESOLVE-000");
        Assert.Equal(Enumerable.Range(10, 10).Select(A), Decide(resolve.Requests[0]));
        Assert.Equal("COMPLETE", run.Status);
    }

    /// <summary>Segment 0 owns L0009, segment 1 owns L0010; one heading spans both.</summary>
    static P7F1V6DirectV2.ReplyModel CrossBoundary(bool secondAgrees, Func<P7F1V6Protocol.PreparedRequest, string>? resolve = null) => (u, round, req) =>
    {
        var d = Decide(req);
        if (u == "SEG-000") return Decisions("COMPLETE", d.Select(a => (Idx(a), Idx(a) == 9 ? "SELECT" : "NOT_SELECT")), new[] { new[] { 9, 10 } });
        if (u == "SEG-001")
            return secondAgrees
                ? Decisions("COMPLETE", d.Select(a => (Idx(a), Idx(a) == 10 ? "SELECT" : "NOT_SELECT")), new[] { new[] { 9, 10 } })
                : Decisions("COMPLETE", d.Select(a => (Idx(a), "NOT_SELECT")));
        return resolve!(req);
    };

    [Fact]
    public void MultipartHeading_AcrossTheSegmentBoundary_IsAssembledFromBothOwners()
    {
        var run = P7F1V6DirectV2.Run(Instruction, "sha", Lines(20), CrossBoundary(secondAgrees: true), overlap: 2, perUnit: 10);
        Assert.Equal("COMPLETE", run.Status);
        Assert.Equal(2, run.Calls);
        var h = Assert.Single(run.Headings);
        Assert.Equal(new[] { A(9), A(10) }, h);
    }

    [Fact]
    public void ConflictingOwners_GoToTheResolvePass_WhichDecides()
    {
        var run = P7F1V6DirectV2.Run(Instruction, "sha", Lines(20), CrossBoundary(secondAgrees: false,
            req => Decisions("COMPLETE", Decide(req).Select(a => (Idx(a), "SELECT")), new[] { new[] { 9, 10 } })), overlap: 2, perUnit: 10);
        var resolve = Assert.Single(run.Units, u => u.UnitId == "RESOLVE-000");
        Assert.Equal(new[] { A(9), A(10) }, Decide(resolve.Requests[0]));
        Assert.Equal("COMPLETE", run.Status);
        Assert.Equal(new[] { A(9), A(10) }, Assert.Single(run.Headings));
    }

    [Fact]
    public void ConflictingOwners_UnresolvedByTheResolvePass_StayOpen()
    {
        var run = P7F1V6DirectV2.Run(Instruction, "sha", Lines(20), CrossBoundary(secondAgrees: false,
            req => Decisions("INCOMPLETE", Decide(req).Select(a => (Idx(a), "UNRESOLVED")), reason: "không đủ bằng chứng")), overlap: 2, perUnit: 10);
        Assert.Equal("INCOMPLETE", run.Status);
        Assert.Equal(new[] { A(9), A(10) }, run.Unresolved);
        Assert.Empty(run.Headings);
    }

    [Fact]
    public void CallBudget_LeavesTheRestUnresolved()
    {
        var run = P7F1V6DirectV2.Run(Instruction, "sha", Lines(30), (u, r, req) => AllNot(req), perUnit: 10, maxCalls: 1);
        Assert.Equal(1, run.Calls);
        Assert.Equal("INCOMPLETE", run.Status);
        Assert.Equal("CALL_BUDGET_EXHAUSTED", run.Units[1].Failure);
        Assert.Equal(20, run.Unresolved.Count);
    }

    [Fact]
    public void Instruction_IsCarriedVerbatim_WithTheSameEvidence_ForDifferentTasks()
    {
        // Plumbing only: three instructions over the same lines produce requests that differ in the instruction and nothing else.
        var lines = Lines(12);
        var tasks = new[] { "Trích xuất tất cả heading", "Trích xuất các heading chính", "Chỉ lấy tiêu đề cấp chương" };
        var bodies = tasks.Select(t =>
        {
            P7F1V6Protocol.PreparedRequest? first = null;
            P7F1V6DirectV2.Run(t, "sha", lines, (u, r, req) => { first ??= req; return AllNot(req); }, perUnit: 12);
            return Payload(first!);
        }).ToArray();
        for (int k = 0; k < tasks.Length; k++)
        {
            Assert.Equal(tasks[k], bodies[k].GetProperty("instruction").GetString());
            Assert.Equal(bodies[0].GetProperty("lines").GetRawText(), bodies[k].GetProperty("lines").GetRawText());
            Assert.Equal(bodies[0].GetProperty("decide").GetRawText(), bodies[k].GetProperty("decide").GetRawText());
        }
    }

    [Fact]
    public void ReplyPage_OfWorstCaseDecisionsAndHeadings_FitsTheCap()
    {
        int aliasChars = 8;
        int n = P7F1V6DirectV2.DecisionsPerReply(aliasChars);
        var alias = new string('A', aliasChars);
        var reply = JsonSerializer.Serialize(new
        {
            protocolVersion = P7F1V6DirectV2.ResponseVersion,
            status = "INCOMPLETE",
            decisions = Enumerable.Range(0, n).Select(_ => new { alias, decision = "UNRESOLVED", evidenceRefs = Enumerable.Repeat(alias, P7F1V6DirectV2.MaxEvidenceRefs) }),
            headings = Enumerable.Range(0, n).Select(_ => new { parts = new[] { alias } }),
            incompleteReason = new string('x', P7F1V6DirectV2.MaxFreeTextChars),
        });
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(reply) <= P7F1V6Direct.VisibleResponseCapBytes);
    }

    [Fact]
    public void Retrieval_ControlCharactersAndLongAliases_AreRefused()
    {
        var order = Order(Enumerable.Range(0, 2));
        Assert.Equal("RETRIEVE_SEARCH_INVALID", P7F1V6DirectV2.Parse(Needs(new { kind = "SEARCH", text = "ab\u0001" }), order, new[] { A(0) }).Failure);
        Assert.Equal("RETRIEVE_RANGE_INVALID", P7F1V6DirectV2.Parse(Needs(new { kind = "RANGE", fromAlias = new string('L', 65), toAlias = A(1) }), order, new[] { A(0) }).Failure);
    }

    [Fact]
    public void ResolveContext_IsCappedForScatteredAliases()
    {
        var decide = Enumerable.Range(0, 80).Select(i => i * 30).ToArray();
        var ctx = P7F1V6DirectV2.ResolveContext(decide, 3000, 20).ToList();
        Assert.True(ctx.Count <= P7F1V6DirectV2.MaxResolveContextLines, $"{ctx.Count} lines");
        Assert.All(decide, i => Assert.Contains(i, ctx));
    }

    [Fact]
    public void WorstCaseBound_CoversEveryRequestOfAMaximalRun()
    {
        // A scripted model that asks for the most evidence it can every round, then leaves everything unresolved, so the resolve pass runs on
        // every alias. Every request it causes must fit the bound.
        var lines = Lines(400, i => i % 7 == 0 ? $"Mục \"{i}\" — tiêu đề dài {new string('ữ', 40)}" : $"Dòng {i}");
        var bound = P7F1V6DirectV2.WorstCase(Instruction, "sha", lines, overlap: 20, perUnit: 40);
        var segMax = 0; var resMax = 0;
        var run = P7F1V6DirectV2.Run(Instruction, "sha", lines, (u, round, req) =>
        {
            if (u.StartsWith("SEG", StringComparison.Ordinal)) segMax = Math.Max(segMax, req.Bytes); else resMax = Math.Max(resMax, req.Bytes);
            if (round < P7F1V6DirectV2.MaxRounds)
                return Needs(new { kind = "SEARCH", text = "Mục" }, new { kind = "SEARCH", text = "tiêu đề" },
                    new { kind = "RANGE", fromAlias = A(0), toAlias = A(399) }, new { kind = "SEARCH", text = "Dòng" });
            return Decisions("INCOMPLETE", Decide(req).Select(a => (Idx(a), "UNRESOLVED")), reason: "thiếu bằng chứng");
        }, overlap: 20, perUnit: 40);

        Assert.True(run.Calls <= bound.WorstCalls, $"{run.Calls} > {bound.WorstCalls}");
        Assert.True(segMax <= bound.WorstSegmentRequestBytes, $"segment {segMax} > {bound.WorstSegmentRequestBytes}");
        Assert.True(resMax <= bound.WorstResolveRequestBytes, $"resolve {resMax} > {bound.WorstResolveRequestBytes}");
        Assert.True(resMax > 0);
        Assert.Equal("INCOMPLETE", run.Status);
    }

    // ---------------- scorer on real Gold ----------------

    sealed record GoldFacts(string Source, string Universe, List<List<string>> Claims);

    static GoldFacts ReadGold(string id, string? root = null)
    {
        root ??= TestRepository.Root();
        using var reg = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, Registry)));
        var a = reg.RootElement.GetProperty("authorities").EnumerateArray().First(x => x.GetProperty("authorityId").GetString() == id);
        using var g = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, a.GetProperty("canonicalGoldPath").GetString()!)));
        var claims = g.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().Select(c =>
            c.TryGetProperty("sourceParts", out var ps)
                ? ps.EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!).ToList()
                : new List<string> { c.GetProperty("sourceAlias").GetString()! }).ToList();
        var universe = g.RootElement.GetProperty("occurrence").TryGetProperty("sourceUniverseSha256", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "";
        return new GoldFacts(a.GetProperty("sourceSha256").GetString()!, universe, claims);
    }

    static P7F1V6DirectScorer.RunEvidence Perfect(GoldFacts f, IEnumerable<IReadOnlyList<string>>? headings = null, IEnumerable<string>? unresolved = null,
        IEnumerable<string>? universe = null, string? source = null, string? universeSha = null)
    {
        var hs = (headings ?? f.Claims).ToList();
        return new(source ?? f.Source, universeSha ?? f.Universe, (universe ?? f.Claims.SelectMany(c => c).Append("ZZZZ:S0").Distinct()).ToList(),
            hs.SelectMany(h => h).ToList(), hs, (unresolved ?? Array.Empty<string>()).ToList(), Array.Empty<string>(), Array.Empty<string>());
    }

    [Fact]
    public void Scorer_Src089_MultipartGold_IsReadFromSourceParts_AndScoresBothMetrics()
    {
        var f = ReadGold("SRC-089");
        Assert.Contains(f.Claims, c => c.SequenceEqual(new[] { "L0006:S0", "L0007:S0", "L0008:S0" }));
        var r = P7F1V6DirectScorer.Score(TestRepository.Root(), Registry, "SRC-089", Perfect(f));
        Assert.True(r.Scored, r.Reason);
        Assert.Equal(1.0, r.AtomMembership!.F1);
        Assert.Equal(1.0, r.HeadingOccurrence!.F1);
        Assert.Equal($"{2 * f.Claims.Count}/{2 * f.Claims.Count}", r.HeadingOccurrence.F1Exact);
    }

    [Fact]
    public void Scorer_ThreeAtomsOfOneHeading_AreOneOccurrence_NotThree()
    {
        var f = ReadGold("SRC-089");
        // Same atoms, but every atom reported as its own heading: atom membership is perfect, heading occurrence is not.
        var split = f.Claims.SelectMany(c => c).Select(a => (IReadOnlyList<string>)new[] { a }).ToList();
        var r = P7F1V6DirectScorer.Score(TestRepository.Root(), Registry, "SRC-089", Perfect(f, split));
        Assert.True(r.Scored, r.Reason);
        Assert.Equal(1.0, r.AtomMembership!.F1);
        var single = f.Claims.Count(c => c.Count == 1);
        var multi = f.Claims.Count - single;
        var atomsInMulti = f.Claims.Where(c => c.Count > 1).Sum(c => c.Count);
        Assert.Equal(single, r.HeadingOccurrence!.TruePositive);
        Assert.Equal(atomsInMulti, r.HeadingOccurrence.FalsePositive);
        Assert.Equal(multi, r.HeadingOccurrence.FalseNegative);
        Assert.True(r.HeadingOccurrence.F1 < 1.0);
    }

    [Fact]
    public void Scorer_VerbatimTextParts_MakeHeadingOccurrenceNotComparable()
    {
        var f = ReadGold("SRC-044");
        var r = P7F1V6DirectScorer.Score(TestRepository.Root(), Registry, "SRC-044", Perfect(f));
        Assert.True(r.Scored, r.Reason);
        Assert.Null(r.HeadingOccurrence);
        Assert.StartsWith("VERBATIM_TEXT_PARTS:", r.HeadingOccurrenceNotComparable, StringComparison.Ordinal);
        Assert.Contains("ATOM_MEMBERSHIP_COUNTS_A_VERBATIM_TEXT_PART_AS_ITS_WHOLE_ATOM", r.Notes);
        Assert.NotNull(r.AtomMembership);
    }

    [Fact]
    public void Scorer_UnstatedSelectionMode_MakesHeadingOccurrenceNotComparable()
    {
        var f = ReadGold("DOC-0001");
        var r = P7F1V6DirectScorer.Score(TestRepository.Root(), Registry, "DOC-0001", Perfect(f));
        Assert.True(r.Scored, r.Reason);
        Assert.Null(r.HeadingOccurrence);
        Assert.StartsWith("SELECTION_MODE_NOT_STATED:", r.HeadingOccurrenceNotComparable, StringComparison.Ordinal);
    }

    [Fact]
    public void Scorer_EveryRegisteredGold_IsScoredOrRefusedWithoutThrowing()
    {
        using var reg = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRepository.Root(), Registry)));
        foreach (var a in reg.RootElement.GetProperty("authorities").EnumerateArray())
        {
            var id = a.GetProperty("authorityId").GetString()!;
            var evaluable = a.GetProperty("semanticClaimsEvaluable").GetBoolean() && a.GetProperty("occurrenceEvaluable").GetBoolean();
            var f = evaluable ? ReadGold(id) : null;
            var ev = f is null
                ? new P7F1V6DirectScorer.RunEvidence(a.GetProperty("sourceSha256").GetString()!, "", new[] { "X" }, Array.Empty<string>(),
                    Array.Empty<IReadOnlyList<string>>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
                : Perfect(f);
            var r = P7F1V6DirectScorer.Score(TestRepository.Root(), Registry, id, ev);
            if (evaluable)
            {
                Assert.True(r.Scored, id + ": " + r.Reason);
                Assert.Equal(1.0, r.AtomMembership!.F1);
                if (r.HeadingOccurrence is not null) Assert.Equal(1.0, r.HeadingOccurrence.F1);
                else Assert.NotNull(r.HeadingOccurrenceNotComparable);
            }
            else
            {
                Assert.False(r.Scored, id);
                Assert.NotNull(r.Reason);
            }
        }
    }

    [Fact]
    public void Scorer_RefusesRunEvidenceThatDoesNotMatch()
    {
        var f = ReadGold("SRC-089");
        var root = TestRepository.Root();
        string? Reason(P7F1V6DirectScorer.RunEvidence e) => P7F1V6DirectScorer.Score(root, Registry, "SRC-089", e).Reason;
        Assert.Equal("SOURCE_SHA_MISMATCH", Reason(Perfect(f, source: new string('0', 64))));
        Assert.Equal("UNIVERSE_SHA_MISMATCH", Reason(Perfect(f, universeSha: new string('0', 64))));
        Assert.Equal("RUN_HAS_UNRESOLVED:1", Reason(Perfect(f, unresolved: new[] { "L0001:S0" })));
        Assert.Equal("GOLD_OUTSIDE_UNIVERSE", Reason(Perfect(f, universe: f.Claims.SelectMany(c => c).Skip(1).Distinct())));
        var outside = f.Claims.Append(new List<string> { "QQQQ:S0" }).Select(c => (IReadOnlyList<string>)c).ToList();
        Assert.StartsWith("PREDICTED_OUTSIDE_UNIVERSE:QQQQ:S0", Reason(Perfect(f, outside)), StringComparison.Ordinal);
        var good = Perfect(f);
        Assert.Equal("PREDICTION_INCONSISTENT", Reason(good with { SelectedAtoms = good.SelectedAtoms.Skip(1).ToList() }));
        Assert.Equal("COVERAGE_INCOMPLETE", Reason(good with { CoverageMissing = new[] { "L0001:S0" } }));
        Assert.Equal("NO_APPROVED_GOLD_FOR_AUTHORITY", P7F1V6DirectScorer.Score(root, Registry, "DOC-0179", good).Reason);
    }

    string CopyGold(string id)
    {
        var src = Path.Combine(TestRepository.Root(), "eval", "a99-closed-loop", "gold-current");
        var dst = Path.Combine(scratch, "repo-" + id, "eval", "a99-closed-loop", "gold-current");
        Directory.CreateDirectory(Path.Combine(dst, "documents"));
        File.Copy(Path.Combine(src, "registry.v1.json"), Path.Combine(dst, "registry.v1.json"));
        File.Copy(Path.Combine(src, "documents", id + ".gold.v1.json"), Path.Combine(dst, "documents", id + ".gold.v1.json"));
        return Path.Combine(scratch, "repo-" + id);
    }

    /// <summary>Edits a scratch copy of a Gold through its JSON tree, then writes the new hash into the scratch registry.</summary>
    static void Rehash(string root, string id, Action<System.Text.Json.Nodes.JsonNode> edit)
    {
        var path = Path.Combine(root, "eval", "a99-closed-loop", "gold-current", "documents", id + ".gold.v1.json");
        var before = File.ReadAllBytes(path);
        var oldSha = Convert.ToHexStringLower(SHA256.HashData(before));
        var node = System.Text.Json.Nodes.JsonNode.Parse(before)!;
        edit(node);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var newSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var reg = Path.Combine(root, Registry);
        File.WriteAllText(reg, File.ReadAllText(reg).Replace(oldSha, newSha));
    }

    [Fact]
    public void Scorer_GoldBytesThatDoNotMatchTheRegistry_AreRefused()
    {
        var root = CopyGold("SRC-089");
        var path = Path.Combine(root, "eval", "a99-closed-loop", "gold-current", "documents", "SRC-089.gold.v1.json");
        File.AppendAllText(path, " ");
        Assert.Equal("GOLD_SHA_MISMATCH", P7F1V6DirectScorer.Score(root, Registry, "SRC-089", Perfect(ReadGold("SRC-089"))).Reason);
    }

    [Fact]
    public void Scorer_UnknownClaimShape_IsAControlledRefusal()
    {
        var root = CopyGold("SRC-089");
        Rehash(root, "SRC-089", n =>
        {
            var claim = n["semantic"]!["claims"]![0]!.AsObject();
            var parts = claim["sourceParts"]!;
            claim.Remove("sourceParts");
            claim["sourcePartz"] = parts;
        });
        var r = P7F1V6DirectScorer.Score(root, Registry, "SRC-089", Perfect(ReadGold("SRC-089")));
        Assert.False(r.Scored);
        Assert.Equal("GOLD_CLAIM_SHAPE_UNKNOWN", r.Reason);
    }

    [Fact]
    public void Scorer_OccurrenceAndSemanticUnitsThatDiffer_AreRefused()
    {
        var root = CopyGold("SRC-089");
        // Change one part of the first occurrence claim only; the semantic claims stay as they were.
        Rehash(root, "SRC-089", n => n["occurrence"]!["claims"]![0]!["sourceParts"]![0]!["sourceAlias"] = "L0099:S0");
        var r = P7F1V6DirectScorer.Score(root, Registry, "SRC-089", Perfect(ReadGold("SRC-089")));
        Assert.Equal("UNIT_MISMATCH_OCCURRENCE_VS_SEMANTIC", r.Reason);
    }

    [Fact]
    public void Scorer_AGoldPathOutsideTheAuthorityFolder_IsRefused()
    {
        var root = CopyGold("SRC-089");
        var reg = Path.Combine(root, Registry);
        File.WriteAllText(reg, File.ReadAllText(reg).Replace("eval/a99-closed-loop/gold-current/documents/SRC-089.gold.v1.json", "eval/a99-closed-loop/gold-current/registry.v1.json"));
        Assert.Equal("GOLD_PATH_OUTSIDE_AUTHORITY_FOLDER", P7F1V6DirectScorer.Score(root, Registry, "SRC-089", Perfect(ReadGold("SRC-089"))).Reason);
    }
}
