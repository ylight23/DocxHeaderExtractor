using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.4 (offline): prompt V2.1, the runtime budget, document-level finalization, and the task-conditioned plumbing for
/// tasks A/B/C. Every model here is scripted: these tests show what C# does with replies. They are not evidence of what Qwen does.</summary>
public sealed class P7F1V6DirectD44Tests
{
    const string TaskA = "Trích xuất tất cả heading";
    const string TaskB = "Trích xuất các heading chính";
    const string TaskC = "Chỉ trích xuất các heading cấp cao nhất của tài liệu";

    static string A(int i) => $"L{i:0000}:S0";
    static int Idx(string alias) => int.Parse(alias.AsSpan(1, 4));

    static List<P7F1V6Direct.Line> Lines(int n) =>
        Enumerable.Range(0, n).Select(i => new P7F1V6Direct.Line(A(i), 1 + i / 40, $"Dòng {i}")).ToList();

    static P7F1V6DirectV2.Budget Gen() => new(10_000, 1_000, 2_000_000);

    static JsonElement Payload(P7F1V6Protocol.PreparedRequest req)
    {
        using var doc = JsonDocument.Parse(req.Body);
        using var p = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return p.RootElement.Clone();
    }

    static string[] Decide(P7F1V6Protocol.PreparedRequest req) => Payload(req).GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
    static string[] Shown(P7F1V6Protocol.PreparedRequest req) => Payload(req).GetProperty("targets").EnumerateArray().Concat(Payload(req).GetProperty("context").EnumerateArray()).Select(x => x.GetProperty("alias").GetString()!).ToArray();
    static string Stage(P7F1V6Protocol.PreparedRequest req) => Payload(req).GetProperty("stage").GetString()!;

    /// <summary>A COMPLETE reply: the aliases in select are SELECT, each its own heading unless groups say otherwise; the rest NOT_SELECT.</summary>
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

    static string Incomplete(IEnumerable<string> decide) => JsonSerializer.Serialize(new
    {
        protocolVersion = P7F1V6DirectV2.ResponseVersion,
        status = "INCOMPLETE",
        decisions = decide.Select(a => new { alias = a, decision = "UNRESOLVED", evidenceRefs = new[] { a } }),
        headings = Array.Empty<object>(),
        incompleteReason = "không đủ bằng chứng toàn tài liệu",
    });

    // ---------------- prompt V2.1 ----------------

    [Fact]
    public void Prompt_IsPinned()
    {
        Assert.Equal(P7F1V6DirectV2.SystemPromptSha256, P7F1V6DirectV2.ComputedSystemPromptSha256());
    }

    [Fact]
    public void Prompt_EveryStatusSkeleton_ParsesWithTheParser_OnceFilledIn()
    {
        var skeletons = P7F1V6DirectV2.SystemPrompt.Split('\n').Where(l => l.StartsWith("{\"protocolVersion\"", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, skeletons.Count);
        var shown = Enumerable.Range(0, 4).ToDictionary(A, i => i);
        var fills = new Dictionary<string, (string[] Values, string[] Decide)>
        {
            ["COMPLETE"] = (new[] { A(0), A(0), A(1), A(0), A(2) }, new[] { A(0), A(1) }),
            ["INCOMPLETE"] = (new[] { A(0), A(0), "thiếu bằng chứng" }, new[] { A(0) }),
            ["NEEDS_EVIDENCE"] = (new[] { A(0), A(3), "Mục lục" }, new[] { A(0) }),
            ["CLARIFICATION_REQUIRED"] = (new[] { "Ý bạn là gì?", "cách hiểu 1", "cách hiểu 2" }, new[] { A(0) }),
        };
        foreach (var s in skeletons)
        {
            var status = JsonNode.Parse(s)!["status"]!.GetValue<string>();
            var (values, decide) = fills[status];
            int k = 0;
            var filled = Regex.Replace(s, "<[^>]+>", _ => values[k++]);
            Assert.Equal(values.Length, k);
            var r = P7F1V6DirectV2.Parse(filled, shown, decide);
            Assert.True(r.Ok, status + ": " + r.Failure);
            Assert.Equal(status, r.Status);
        }
        Assert.Equal(new[] { "COMPLETE", "INCOMPLETE", "NEEDS_EVIDENCE", "CLARIFICATION_REQUIRED" },
            skeletons.Select(s => JsonNode.Parse(s)!["status"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void Prompt_NamesTheDecisionKey_AndCarriesNoTaxonomy()
    {
        var p = P7F1V6DirectV2.SystemPrompt;
        Assert.Contains("\"alias\"", p, StringComparison.Ordinal);
        foreach (var word in new[] { "table of contents", "toc", "caption", "form", "chapter", "section", "appendix", "title page", "bold", "font", "numbered" })
            Assert.DoesNotMatch(new Regex(@"\b" + Regex.Escape(word) + @"\b", RegexOptions.IgnoreCase), p);
    }

    [Fact]
    public void Prompt_TheRealCanaryReplyShape_StillFailsUnderV2()
    {
        // The shape Qwen returned in canary T1 ("line" as the decision key) must fail as a contract failure, never be renamed.
        var reply = "{\"protocolVersion\":\"P7_F1_V6_DIRECT_RESPONSE_V2\",\"status\":\"COMPLETE\",\"decisions\":[{\"line\":\"L0000:S0\",\"decision\":\"NOT_SELECT\",\"evidenceRefs\":[\"L0000:S0\"]}],\"headings\":[]}";
        var r = P7F1V6DirectV2.Parse(reply, new Dictionary<string, int> { [A(0)] = 0 }, new[] { A(0) });
        Assert.Equal("DECISION_KEYS_INVALID", r.Failure);
    }

    // ---------------- runtime budget ----------------

    [Fact]
    public void Budget_RejectsInvalidSettings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new P7F1V6DirectV2.Budget(0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new P7F1V6DirectV2.Budget(1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new P7F1V6DirectV2.Budget(1, 1, 0));
    }

    [Fact]
    public void Budget_CallLimit_StopsCallsAndLeavesTheRestUnresolved()
    {
        int modelCalls = 0;
        var run = P7F1V6DirectV2.Run(TaskB, "sha", Lines(30), (u, r, req) => { modelCalls++; return Complete(Decide(req), new HashSet<int>()); },
            new P7F1V6DirectV2.Budget(2, 100, 2_000_000), perUnit: 10);
        Assert.Equal(2, modelCalls);
        Assert.Equal("INCOMPLETE", run.Status);
        Assert.Equal("BUDGET:CALL_LIMIT", run.Units[2].Failure);
        Assert.Equal(Enumerable.Range(20, 10).Select(A), run.Unresolved);
        Assert.Contains("CALL_LIMIT", run.BudgetRefusals);
    }

    [Fact]
    public void Budget_ByteCap_SendsNothing()
    {
        int modelCalls = 0;
        var run = P7F1V6DirectV2.Run(TaskB, "sha", Lines(10), (u, r, req) => { modelCalls++; return ""; },
            new P7F1V6DirectV2.Budget(100, 100, 500), perUnit: 10);
        Assert.Equal(0, modelCalls);
        Assert.Equal("INCOMPLETE", run.Status);
        Assert.Equal("BUDGET:REQUEST_OVER_BYTE_CAP", run.Units[0].Failure);
        Assert.All(run.Decisions.Values, v => Assert.Equal("UNRESOLVED", v));
    }

    [Fact]
    public void Budget_TokenBudget_IsCheckedOnBytes()
    {
        var b = new P7F1V6DirectV2.Budget(100, 100, 2_000_000, promptTokenBudget: 500);
        var req = P7F1V6DirectV2.BuildRequest(TaskB, "sha", "SEGMENT", "SEG-000", 1, new[] { A(0) }, Lines(1), Array.Empty<P7F1V6DirectV2.Retrieval>());
        Assert.Equal("REQUEST_OVER_TOKEN_BUDGET", b.Admit(req));
        Assert.Equal(0, b.Calls);
    }

    [Fact]
    public void Budget_Reserve_IsTakenBeforeEachCall_AndRefusedOverTheCeiling()
    {
        var req = P7F1V6DirectV2.BuildRequest(TaskB, "sha", "SEGMENT", "SEG-000", 1, new[] { A(0) }, Lines(1), Array.Empty<P7F1V6DirectV2.Retrieval>());
        var one = P7F1V6Direct.AttemptUsd(req.Bytes);
        var b = new P7F1V6DirectV2.Budget(100, one * 1.5, 2_000_000);
        Assert.Null(b.Admit(req));
        Assert.Equal(one, b.ReservedUsd, 12);
        Assert.Equal("RESERVE_OVER_CEILING", b.Admit(req));
        Assert.Equal(1, b.Calls);
    }

    // ---------------- finalization ----------------

    /// <summary>Three segments of ten; segments select L0003, L0015 and L0027 each as a heading.</summary>
    static readonly HashSet<int> Local = new() { 3, 15, 27 };

    [Fact]
    public void Finalization_ReviewsConsistentLocalChoices_AgainstTheWholeDocument()
    {
        P7F1V6Protocol.PreparedRequest? final = null;
        var run = P7F1V6DirectV2.Run(TaskB, "sha", Lines(30), (u, r, req) =>
        {
            if (Stage(req) != "FINALIZE") return Complete(Decide(req), Local);
            final = req;
            return Complete(Decide(req), new HashSet<int> { 3, 27 });   // the scripted finalizer drops L0015
        }, Gen(), overlap: 2, perUnit: 10);

        Assert.Equal("COMPLETE", run.Status);
        Assert.Equal("DONE", run.Finalization);
        Assert.Equal(new[] { A(3), A(15), A(27) }, Decide(final!));
        Assert.Contains(A(3), Shown(final!));
        Assert.Contains(A(27), Shown(final!));
        Assert.Equal(3, Payload(final!).GetProperty("targets").EnumerateArray().Concat(Payload(final!).GetProperty("context").EnumerateArray())
            .Where(x => x.TryGetProperty("currentGroup", out _)).Select(x => x.GetProperty("currentGroup").GetString()).Distinct().Count());
        Assert.Equal("NOT_SELECT", run.Decisions[A(15)]);
        Assert.Equal(new[] { new[] { A(3) }, new[] { A(27) } }, run.Headings.Select(h => h.ToArray()));
    }

    [Fact]
    public void Finalization_MayRegroupAtomsIntoOneHeading()
    {
        var run = P7F1V6DirectV2.Run(TaskB, "sha", Lines(20), (u, r, req) =>
            Stage(req) == "FINALIZE"
                ? Complete(Decide(req), new HashSet<int> { 5, 6 }, new[] { new[] { 5, 6 } })
                : Complete(Decide(req), new HashSet<int> { 5, 6 }), Gen(), overlap: 2, perUnit: 10);
        Assert.Equal("COMPLETE", run.Status);
        Assert.Equal(new[] { A(5), A(6) }, Assert.Single(run.Headings));
    }

    [Fact]
    public void Finalization_Incomplete_LeavesTheRunIncomplete_AndKeepsUnresolved()
    {
        var run = P7F1V6DirectV2.Run(TaskB, "sha", Lines(30), (u, r, req) =>
            Stage(req) == "FINALIZE" ? Incomplete(Decide(req)) : Complete(Decide(req), Local), Gen(), overlap: 2, perUnit: 10);
        Assert.Equal("INCOMPLETE", run.Status);
        Assert.Equal("NOT_DONE", run.Finalization);
        Assert.All(Local, i => Assert.Equal("UNRESOLVED", run.Decisions[A(i)]));
        Assert.Empty(run.Headings);
    }

    [Fact]
    public void Finalization_RefusedByTheBudget_LeavesTheRunIncomplete()
    {
        var run = P7F1V6DirectV2.Run(TaskB, "sha", Lines(30), (u, r, req) => Complete(Decide(req), Local),
            new P7F1V6DirectV2.Budget(3, 100, 2_000_000), overlap: 2, perUnit: 10);
        Assert.Equal(3, run.Calls);
        Assert.Equal("INCOMPLETE", run.Status);
        Assert.Equal("NOT_DONE", run.Finalization);
        Assert.Equal("BUDGET:CALL_LIMIT", run.Units.Single(u => u.Stage == "FINALIZE").Failure);
    }

    [Fact]
    public void Finalization_WithNothingSelected_IsNotCalled()
    {
        var run = P7F1V6DirectV2.Run(TaskB, "sha", Lines(30), (u, r, req) => Complete(Decide(req), new HashSet<int>()), Gen(), perUnit: 10);
        Assert.Equal("NOT_FINALIZED", run.Status);
        Assert.Equal("NO_CANDIDATES", run.Finalization);
        Assert.Equal(P7F1V6DirectV2.AssuranceSegmentComplete, run.Assurance);
        Assert.Equal(3, run.Calls);
    }

    [Fact]
    public void Finalization_OutlineOverTheCap_IsIncomplete_NotComplete()
    {
        var n = P7F1V6DirectV2.MaxFinalizeOutlineLines + 50;
        var all = Enumerable.Range(0, n).ToHashSet();
        var run = P7F1V6DirectV2.Run(TaskA, "sha", Lines(n), (u, r, req) => Complete(Decide(req), all), Gen(), overlap: 0, perUnit: 50);
        Assert.Equal("OUTLINE_OVER_CAP", run.Finalization);
        Assert.Equal("INCOMPLETE", run.Status);
        Assert.DoesNotContain(run.Units, u => u.Stage == "FINALIZE");
    }

    [Fact]
    public void Finalization_CanAskForEvidence()
    {
        var run = P7F1V6DirectV2.Run(TaskB, "sha", Lines(30), (u, r, req) =>
        {
            if (Stage(req) != "FINALIZE") return Complete(Decide(req), Local);
            if (r == 1) return JsonSerializer.Serialize(new { protocolVersion = P7F1V6DirectV2.ResponseVersion, status = "NEEDS_EVIDENCE",
                retrieve = new[] { new { kind = "RANGE", fromAlias = A(10), toAlias = A(20) } } });
            Assert.Contains(A(12), Shown(req));
            return Complete(Decide(req), Local);
        }, Gen(), overlap: 2, perUnit: 10);
        Assert.Equal("COMPLETE", run.Status);
        Assert.Equal(2, run.Units.Single(u => u.Stage == "FINALIZE").Calls);
    }

    [Fact]
    public void Finalization_Disabled_IsNeverReportedComplete()
    {
        var run = P7F1V6DirectV2.Run(TaskB, "sha", Lines(30), (u, r, req) => Complete(Decide(req), Local), Gen(), perUnit: 10, finalize: false);
        Assert.Equal("NOT_FINALIZED", run.Status);
        Assert.Equal("DISABLED", run.Finalization);
    }

    [Fact]
    public void Finalization_Clarification_StopsTheRun()
    {
        var run = P7F1V6DirectV2.Run(TaskB, "sha", Lines(30), (u, r, req) => Stage(req) == "FINALIZE"
            ? JsonSerializer.Serialize(new { protocolVersion = P7F1V6DirectV2.ResponseVersion, status = "CLARIFICATION_REQUIRED",
                clarification = new { question = "Chính theo nghĩa nào?", interpretations = new[] { "cấp 1", "cấp 1 và 2" } } })
            : Complete(Decide(req), Local), Gen(), perUnit: 10);
        Assert.Equal("CLARIFICATION_REQUIRED", run.Status);
        Assert.Empty(run.Decisions);
    }

    // ---------------- tasks A/B/C: C# carries the instruction and nothing else ----------------

    static JsonObject WithoutInstruction(P7F1V6Protocol.PreparedRequest req)
    {
        var o = JsonNode.Parse(Payload(req).GetRawText())!.AsObject();
        o.Remove("instruction");
        o.Remove("instructionSha256");
        return o;
    }

    [Fact]
    public void Tasks_WithAnInstructionBlindModel_GiveIdenticalRequestsAndResults()
    {
        // If C# read the instruction anywhere, an instruction-blind model would still see different requests or produce different results.
        var lines = Lines(30);
        var runs = new[] { TaskA, TaskB, TaskC }.Select(t =>
        {
            var requests = new List<P7F1V6Protocol.PreparedRequest>();
            var run = P7F1V6DirectV2.Run(t, "sha", lines, (u, r, req) => { requests.Add(req); return Complete(Decide(req), Local); }, Gen(), overlap: 2, perUnit: 10);
            return (Task: t, Run: run, Requests: requests);
        }).ToArray();
        foreach (var x in runs)
        {
            Assert.Equal(runs[0].Requests.Count, x.Requests.Count);
            for (int k = 0; k < x.Requests.Count; k++)
            {
                Assert.Equal(x.Task, Payload(x.Requests[k]).GetProperty("instruction").GetString());
                Assert.Equal(WithoutInstruction(runs[0].Requests[k]).ToJsonString(), WithoutInstruction(x.Requests[k]).ToJsonString());
            }
            Assert.Equal(runs[0].Run.Decisions.OrderBy(kv => kv.Key).ToArray(), x.Run.Decisions.OrderBy(kv => kv.Key).ToArray());
            Assert.Equal(runs[0].Run.Status, x.Run.Status);
        }
    }

    [Fact]
    public void Tasks_WithAnInstructionAwareModel_FollowTheModelsPath()
    {
        // A scripted model that reads the instruction: A keeps every local choice, B keeps the first and last at finalization, C asks for
        // clarification. The three runs take three paths, and the only input that differs is the instruction.
        string Script(string unit, int round, P7F1V6Protocol.PreparedRequest req)
        {
            var task = Payload(req).GetProperty("instruction").GetString();
            if (task == TaskC) return JsonSerializer.Serialize(new { protocolVersion = P7F1V6DirectV2.ResponseVersion, status = "CLARIFICATION_REQUIRED",
                clarification = new { question = "Cấp cao nhất là cấp nào?", interpretations = new[] { "chỉ cấp 1", "cấp 1 và phụ lục" } } });
            if (Stage(req) == "FINALIZE" && task == TaskB) return Complete(Decide(req), new HashSet<int> { 3, 27 });
            return Complete(Decide(req), Local);
        }
        var a = P7F1V6DirectV2.Run(TaskA, "sha", Lines(30), Script, Gen(), overlap: 2, perUnit: 10);
        var b = P7F1V6DirectV2.Run(TaskB, "sha", Lines(30), Script, Gen(), overlap: 2, perUnit: 10);
        var c = P7F1V6DirectV2.Run(TaskC, "sha", Lines(30), Script, Gen(), overlap: 2, perUnit: 10);

        Assert.Equal(3, a.Headings.Count);
        Assert.Equal(2, b.Headings.Count);
        Assert.Equal("CLARIFICATION_REQUIRED", c.Status);
        Assert.Equal(1, c.Calls);
        Assert.Equal(new[] { "COMPLETE", "COMPLETE", "CLARIFICATION_REQUIRED" }, new[] { a.Status, b.Status, c.Status });
    }

    [Fact]
    public void Tasks_UnitSizes_HaveRepliesWithinTheCap()
    {
        foreach (var size in new[] { 12, 24, 48, 88 })
        {
            var b = P7F1V6DirectV2.WorstCase(TaskB, "sha", Lines(300), perUnit: size);
            Assert.True(b.ReplyBoundWithinCap, $"{size}: {b.ReplyBoundBytes}");
            Assert.Equal((300 + size - 1) / size, b.Segments);
            Assert.True(b.MinCalls <= b.WorstCalls);
        }
        Assert.False(P7F1V6DirectV2.WorstCase(TaskB, "sha", Lines(300), perUnit: 200).ReplyBoundWithinCap);
    }
}
