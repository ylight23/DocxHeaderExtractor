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

    static P7F1V6DirectV2.Budget Gen() => new(10_000, 1_000, 2_000_000);

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
        var run = P7F1V6DirectV2.Run(Instruction, "sha", lines, budget: Gen(), finalize: false, model: (u, round, req) =>
        {
            seen.Add(req);
            if (u == "SEG-000" && round == 1) return Needs(new { kind = "SEARCH", text = "đặc biệt" }, new { kind = "RANGE", fromAlias = "L9999:S0", toAlias = A(3) });
            return AllNot(req);
        }, overlap: 2, perUnit: 10);

        Assert.Equal("NOT_FINALIZED", run.Status);
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
        var run = P7F1V6DirectV2.Run(Instruction, "sha", lines, budget: Gen(), finalize: false, model: (u, round, req) =>
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
        var run = P7F1V6DirectV2.Run(Instruction, "sha", lines, budget: Gen(), finalize: false, model: (u, round, req) => Clarify("Chỉ cấp chương", "Chương và mục"), perUnit: 10);
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
        var run = P7F1V6DirectV2.Run(Instruction, "sha", lines, budget: Gen(), finalize: false, model: (u, round, req) => u == "SEG-001" ? "not json" : AllNot(req), overlap: 2, perUnit: 10);
        Assert.Equal("FAILED", run.Units[1].Status);
        Assert.Equal("PROTOCOL:REPLY_NOT_JSON", run.Units[1].Failure);
        var resolve = Assert.Single(run.Units, u => u.UnitId == "RESOLVE-000");
        Assert.Equal(Enumerable.Range(10, 10).Select(A), Decide(resolve.Requests[0]));
        Assert.Equal("NOT_FINALIZED", run.Status);
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
        var run = P7F1V6DirectV2.Run(Instruction, "sha", Lines(20), CrossBoundary(secondAgrees: true), Gen(), overlap: 2, perUnit: 10, finalize: false);
        Assert.Equal("NOT_FINALIZED", run.Status);
        Assert.Equal(2, run.Calls);
        var h = Assert.Single(run.Headings);
        Assert.Equal(new[] { A(9), A(10) }, h);
    }

    [Fact]
    public void ConflictingOwners_GoToTheResolvePass_WhichDecides()
    {
        var run = P7F1V6DirectV2.Run(Instruction, "sha", Lines(20), CrossBoundary(secondAgrees: false,
            req => Decisions("COMPLETE", Decide(req).Select(a => (Idx(a), "SELECT")), new[] { new[] { 9, 10 } })), Gen(), overlap: 2, perUnit: 10, finalize: false);
        var resolve = Assert.Single(run.Units, u => u.UnitId == "RESOLVE-000");
        Assert.Equal(new[] { A(9), A(10) }, Decide(resolve.Requests[0]));
        Assert.Equal("NOT_FINALIZED", run.Status);
        Assert.Equal(new[] { A(9), A(10) }, Assert.Single(run.Headings));
    }

    [Fact]
    public void ConflictingOwners_UnresolvedByTheResolvePass_StayOpen()
    {
        var run = P7F1V6DirectV2.Run(Instruction, "sha", Lines(20), CrossBoundary(secondAgrees: false,
            req => Decisions("INCOMPLETE", Decide(req).Select(a => (Idx(a), "UNRESOLVED")), reason: "không đủ bằng chứng")), Gen(), overlap: 2, perUnit: 10, finalize: false);
        Assert.Equal("INCOMPLETE", run.Status);
        Assert.Equal(new[] { A(9), A(10) }, run.Unresolved);
        Assert.Empty(run.Headings);
    }

    [Fact]
    public void CallBudget_LeavesTheRestUnresolved()
    {
        var run = P7F1V6DirectV2.Run(Instruction, "sha", Lines(30), (u, r, req) => AllNot(req), new P7F1V6DirectV2.Budget(1, 100, 2_000_000), perUnit: 10, finalize: false);
        Assert.Equal(1, run.Calls);
        Assert.Equal("INCOMPLETE", run.Status);
        Assert.Equal("BUDGET:CALL_LIMIT", run.Units[1].Failure);
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
            P7F1V6DirectV2.Run(t, "sha", lines, (u, r, req) => { first ??= req; return AllNot(req); }, Gen(), perUnit: 12, finalize: false);
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
        var run = P7F1V6DirectV2.Run(Instruction, "sha", lines, budget: Gen(), finalize: false, model: (u, round, req) =>
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

    // ---------------- scorer on real Gold, with universes rebuilt from the pinned PDFs ----------------

    sealed record GoldFacts(string SourcePath, string SourceSha, List<List<string>> Claims);

    static readonly Dictionary<string, P7F1V6Universe> Universes = new();
    static readonly object UniverseLock = new();

    static GoldFacts ReadGold(string id)
    {
        var root = TestRepository.Root();
        using var reg = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, Registry)));
        var a = reg.RootElement.GetProperty("authorities").EnumerateArray().First(x => x.GetProperty("authorityId").GetString() == id);
        using var g = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, a.GetProperty("canonicalGoldPath").GetString()!)));
        var claims = g.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().Select(c =>
            c.TryGetProperty("sourceParts", out var ps)
                ? ps.EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!).ToList()
                : new List<string> { c.GetProperty("sourceAlias").GetString()! }).ToList();
        return new GoldFacts(g.RootElement.GetProperty("source").GetProperty("sourcePath").GetString()!, a.GetProperty("sourceSha256").GetString()!, claims);
    }

    static P7F1V6Universe Universe(string id)
    {
        lock (UniverseLock)
        {
            if (Universes.TryGetValue(id, out var u)) return u;
            var f = ReadGold(id);
            u = P7F1V6Universe.Rebuild(TestRepository.Root(), f.SourcePath, f.SourceSha, out var failure) ?? throw new InvalidOperationException(failure);
            Universes[id] = u;
            return u;
        }
    }

    /// <summary>A run that decides every alias of the universe: the given headings SELECT, everything else NOT_SELECT.</summary>
    static P7F1V6DirectScorer.RunEvidence RunOn(P7F1V6Universe u, IEnumerable<IReadOnlyList<string>> headings, Action<Dictionary<string, string>>? edit = null)
    {
        var hs = headings.ToList();
        var selected = hs.SelectMany(h => h).ToHashSet(StringComparer.Ordinal);
        var decisions = u.Aliases.ToDictionary(a => a, a => selected.Contains(a) ? "SELECT" : "NOT_SELECT", StringComparer.Ordinal);
        edit?.Invoke(decisions);
        return new(u, decisions, hs, "COMPLETE", "DONE", P7F1V6DirectV2.AssuranceFinalSelectionReviewed);
    }

    static P7F1V6DirectScorer.RunEvidence Perfect(string id) => RunOn(Universe(id), ReadGold(id).Claims);

    [Fact]
    public void Universe_IsRebuiltFromThePinnedPdf_AndMatchesTheGold()
    {
        var u = Universe("SRC-089");
        Assert.Equal(581, u.Aliases.Count);
        Assert.Equal("d45b4a027d4da6133c852f7ee470365d08fbf8f9a47c25aff9afd074278ec8cc", u.UniverseSha256);
        Assert.Equal(ReadGold("SRC-089").SourceSha, u.SourceSha256);
        Assert.Null(P7F1V6Universe.Rebuild(TestRepository.Root(), ReadGold("SRC-089").SourcePath, new string('0', 64), out var failure));
        Assert.StartsWith("SOURCE_GATE:", failure, StringComparison.Ordinal);
        Assert.Null(P7F1V6Universe.Rebuild(TestRepository.Root(), "bench/01-style-chuan.docx", ReadGold("DOC-0001").SourceSha, out failure));
        Assert.Equal("UNIVERSE_REBUILD_PDF_ONLY", failure);
    }

    [Fact]
    public void Scorer_Src089_MultipartGold_IsReadFromSourceParts_AndScoresBothMetrics()
    {
        var claims = ReadGold("SRC-089").Claims;
        Assert.Contains(claims, c => c.SequenceEqual(new[] { "L0006:S0", "L0007:S0", "L0008:S0" }));
        var r = P7F1V6DirectScorer.Score(TestRepository.Root(), Registry, "SRC-089", Perfect("SRC-089"));
        Assert.True(r.Scored, r.Reason);
        Assert.Equal(1.0, r.AtomMembership!.F1);
        Assert.Equal(1.0, r.HeadingOccurrence!.F1);
        Assert.Equal($"{2 * claims.Count}/{2 * claims.Count}", r.HeadingOccurrence.F1Exact);
    }

    [Fact]
    public void Scorer_ThreeAtomsOfOneHeading_AreOneOccurrence_NotThree()
    {
        var claims = ReadGold("SRC-089").Claims;
        var split = claims.SelectMany(c => c).Select(a => (IReadOnlyList<string>)new[] { a }).ToList();
        var r = P7F1V6DirectScorer.Score(TestRepository.Root(), Registry, "SRC-089", RunOn(Universe("SRC-089"), split));
        Assert.True(r.Scored, r.Reason);
        Assert.Equal(1.0, r.AtomMembership!.F1);
        Assert.Equal(claims.Count(c => c.Count == 1), r.HeadingOccurrence!.TruePositive);
        Assert.Equal(claims.Where(c => c.Count > 1).Sum(c => c.Count), r.HeadingOccurrence.FalsePositive);
        Assert.Equal(claims.Count(c => c.Count > 1), r.HeadingOccurrence.FalseNegative);
        Assert.True(r.HeadingOccurrence.F1 < 1.0);
    }

    [Fact]
    public void Scorer_VerbatimTextParts_MakeHeadingOccurrenceNotComparable()
    {
        var r = P7F1V6DirectScorer.Score(TestRepository.Root(), Registry, "SRC-044", Perfect("SRC-044"));
        Assert.True(r.Scored, r.Reason);
        Assert.Null(r.HeadingOccurrence);
        Assert.StartsWith("VERBATIM_TEXT_PARTS:", r.HeadingOccurrenceNotComparable, StringComparison.Ordinal);
        Assert.Contains("ATOM_MEMBERSHIP_COUNTS_A_VERBATIM_TEXT_PART_AS_ITS_WHOLE_ATOM", r.Notes);
        Assert.NotNull(r.AtomMembership);
    }

    [Fact]
    public void Scorer_EveryRegisteredGold_IsScoredOrRefusedWithoutThrowing()
    {
        using var reg = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRepository.Root(), Registry)));
        foreach (var a in reg.RootElement.GetProperty("authorities").EnumerateArray())
        {
            var id = a.GetProperty("authorityId").GetString()!;
            var evaluable = a.GetProperty("semanticClaimsEvaluable").GetBoolean() && a.GetProperty("occurrenceEvaluable").GetBoolean();
            if (!evaluable)
            {
                var refused = P7F1V6DirectScorer.Score(TestRepository.Root(), Registry, id, Perfect("SRC-089"));
                Assert.False(refused.Scored, id);
                Assert.NotNull(refused.Reason);
                continue;
            }
            var f = ReadGold(id);
            if (!f.SourcePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                // A DOCX universe cannot be rebuilt by the PDF parser, so a DOCX Gold cannot be scored by this path at all.
                Assert.Null(P7F1V6Universe.Rebuild(TestRepository.Root(), f.SourcePath, f.SourceSha, out var why));
                Assert.Equal("UNIVERSE_REBUILD_PDF_ONLY", why);
                continue;
            }
            var r = P7F1V6DirectScorer.Score(TestRepository.Root(), Registry, id, Perfect(id));
            Assert.True(r.Scored, id + ": " + r.Reason);
            Assert.Equal(1.0, r.AtomMembership!.F1);
            if (r.HeadingOccurrence is not null) Assert.Equal(1.0, r.HeadingOccurrence.F1);
            else Assert.NotNull(r.HeadingOccurrenceNotComparable);
        }
    }

    [Fact]
    public void Scorer_RefusesRunEvidenceThatDoesNotMatch()
    {
        var root = TestRepository.Root();
        var u = Universe("SRC-089");
        var claims = ReadGold("SRC-089").Claims.Select(c => (IReadOnlyList<string>)c).ToList();
        string? Reason(P7F1V6DirectScorer.RunEvidence e, string id = "SRC-089") => P7F1V6DirectScorer.Score(root, Registry, id, e).Reason;

        Assert.Equal("SOURCE_SHA_MISMATCH", Reason(RunOn(Universe("SRC-095"), Array.Empty<IReadOnlyList<string>>())));
        Assert.Equal("RUN_HAS_UNRESOLVED:1", Reason(RunOn(u, claims, d => d["L0001:S0"] = "UNRESOLVED")));
        Assert.Equal("COVERAGE_INCOMPLETE:missing=1,unexpected=0", Reason(RunOn(u, claims, d => d.Remove("L0001:S0"))));
        Assert.Equal("COVERAGE_INCOMPLETE:missing=0,unexpected=1", Reason(RunOn(u, claims, d => d["QQQQ:S0"] = "NOT_SELECT")));
        Assert.Equal("DECISION_VALUE_INVALID", Reason(RunOn(u, claims, d => d["L0001:S0"] = "MAYBE")));
        Assert.StartsWith("PREDICTED_OUTSIDE_UNIVERSE:QQQQ:S0", Reason(new P7F1V6DirectScorer.RunEvidence(u, RunOn(u, claims).Decisions,
            claims.Append(new[] { "QQQQ:S0" }).ToList(), "COMPLETE", "DONE", P7F1V6DirectV2.AssuranceFinalSelectionReviewed)), StringComparison.Ordinal);
        Assert.Equal("PREDICTION_INCONSISTENT", Reason(RunOn(u, claims, d => d[claims[0][0]] = "NOT_SELECT")));
        Assert.Equal("NO_APPROVED_GOLD_FOR_AUTHORITY", Reason(RunOn(u, claims), "DOC-0179"));
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
        File.AppendAllText(Path.Combine(root, "eval", "a99-closed-loop", "gold-current", "documents", "SRC-089.gold.v1.json"), " ");
        Assert.Equal("GOLD_SHA_MISMATCH", P7F1V6DirectScorer.Score(root, Registry, "SRC-089", Perfect("SRC-089")).Reason);
    }

    [Fact]
    public void Scorer_AGoldWhoseUniverseDiffersFromTheRebuiltOne_IsRefused()
    {
        var root = CopyGold("SRC-089");
        Rehash(root, "SRC-089", n => n["occurrence"]!["sourceUniverseSha256"] = new string('a', 64));
        Assert.Equal("UNIVERSE_SHA_MISMATCH", P7F1V6DirectScorer.Score(root, Registry, "SRC-089", Perfect("SRC-089")).Reason);
    }

    [Fact]
    public void Scorer_UnstatedSelectionMode_MakesHeadingOccurrenceNotComparable()
    {
        var root = CopyGold("SRC-089");
        Rehash(root, "SRC-089", n => n["semantic"]!["claims"]![0]!["sourceParts"]![0]!["selectionMode"] = null);
        var r = P7F1V6DirectScorer.Score(root, Registry, "SRC-089", Perfect("SRC-089"));
        Assert.True(r.Scored, r.Reason);
        Assert.Null(r.HeadingOccurrence);
        Assert.Equal("SELECTION_MODE_NOT_STATED:1", r.HeadingOccurrenceNotComparable);
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
        var r = P7F1V6DirectScorer.Score(root, Registry, "SRC-089", Perfect("SRC-089"));
        Assert.False(r.Scored);
        Assert.Equal("GOLD_CLAIM_SHAPE_UNKNOWN", r.Reason);
    }

    [Fact]
    public void Scorer_OccurrenceAndSemanticUnitsThatDiffer_AreRefused()
    {
        var root = CopyGold("SRC-089");
        Rehash(root, "SRC-089", n => n["occurrence"]!["claims"]![0]!["sourceParts"]![0]!["sourceAlias"] = "L0099:S0");
        Assert.Equal("UNIT_MISMATCH_OCCURRENCE_VS_SEMANTIC", P7F1V6DirectScorer.Score(root, Registry, "SRC-089", Perfect("SRC-089")).Reason);
    }

    [Fact]
    public void Scorer_AGoldPathOutsideTheAuthorityFolder_IsRefused()
    {
        var root = CopyGold("SRC-089");
        var reg = Path.Combine(root, Registry);
        File.WriteAllText(reg, File.ReadAllText(reg).Replace("eval/a99-closed-loop/gold-current/documents/SRC-089.gold.v1.json", "eval/a99-closed-loop/gold-current/registry.v1.json"));
        Assert.Equal("GOLD_PATH_OUTSIDE_AUTHORITY_FOLDER", P7F1V6DirectScorer.Score(root, Registry, "SRC-089", Perfect("SRC-089")).Reason);
    }
}

