using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.V5Qualification.P7;
using F = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectTwoStage.SelectFormat;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.9 (offline): the two-stage protocol (SELECT membership, then GROUP), with the object (B) and compact (C) Stage 1
/// formats. Every model here is scripted, including adversarial ones; these tests check the protocol and the C# side only.</summary>
public sealed class P7F1V6DirectD49Tests
{
    const string Task = "Trích xuất tất cả heading";

    static string A(int i) => $"L{i:0000}:S0";
    static int Idx(string alias) => int.Parse(alias.AsSpan(1, 4));
    static List<P7F1V6Direct.Line> Lines(int n) => Enumerable.Range(0, n).Select(i => new P7F1V6Direct.Line(A(i), 1 + i / 40, $"Dòng {i}")).ToList();
    static Dictionary<string, int> Shown(IEnumerable<int> idx) => idx.ToDictionary(A, i => i);
    static P7F1V6DirectV2.Budget Gen() => new(10_000, 1_000, 2_000_000);

    static JsonElement Payload(P7F1V6Protocol.PreparedRequest req)
    {
        using var doc = JsonDocument.Parse(req.Body);
        using var p = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return p.RootElement.Clone();
    }

    static string[] Targets(P7F1V6Protocol.PreparedRequest req) => Payload(req).GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
    static string Stage(P7F1V6Protocol.PreparedRequest req) => Payload(req).GetProperty("stage").GetString()!;

    /// <summary>A compact Stage 1 reply: blocks of 8 codes with start and boundary aliases, evidence for every S.</summary>
    static string Compact(IReadOnlyList<string> targets, ISet<int> select, Action<JsonObject>? tamper = null, ISet<int>? unresolved = null)
    {
        var codes = targets.Select(a => unresolved?.Contains(Idx(a)) == true ? 'U' : select.Contains(Idx(a)) ? 'S' : 'N').ToArray();
        var blocks = new JsonArray();
        for (int s = 0; s < targets.Count; s += 8)
        {
            var len = Math.Min(8, targets.Count - s);
            blocks.Add(new JsonObject { ["start"] = s, ["firstAlias"] = targets[s], ["lastAlias"] = targets[s + len - 1], ["codes"] = new string(codes, s, len) });
        }
        var evidence = new JsonArray();
        for (int i = 0; i < codes.Length; i++)
            if (codes[i] == 'S') evidence.Add(new JsonObject { ["targetIndex"] = i, ["evidenceRefs"] = new JsonArray(targets[i]) });
        var o = new JsonObject
        {
            ["protocolVersion"] = P7F1V6DirectTwoStage.SelectCompactVersion,
            ["status"] = codes.Contains('U') ? "INCOMPLETE" : "COMPLETE",
            ["targetCount"] = targets.Count,
            ["blocks"] = blocks,
            ["selectedEvidence"] = evidence,
        };
        if (codes.Contains('U')) o["incompleteReason"] = "thiếu bằng chứng";
        tamper?.Invoke(o);
        return o.ToJsonString();
    }

    static string Objects(IReadOnlyList<string> targets, ISet<int> select, Action<JsonObject>? tamper = null)
    {
        var o = new JsonObject
        {
            ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion,
            ["status"] = "COMPLETE",
            ["decisions"] = new JsonArray(targets.Select(a => (JsonNode)(select.Contains(Idx(a))
                ? new JsonObject { ["alias"] = a, ["decision"] = "SELECT", ["evidenceRefs"] = new JsonArray(a) }
                : new JsonObject { ["alias"] = a, ["decision"] = "NOT_SELECT" })).ToArray()),
        };
        tamper?.Invoke(o);
        return o.ToJsonString();
    }

    static string Group(IEnumerable<int[]> groups, IEnumerable<(int, string)>? reviews = null, string status = "COMPLETE")
    {
        var o = new JsonObject
        {
            ["protocolVersion"] = P7F1V6DirectTwoStage.GroupVersion,
            ["status"] = status,
            ["groups"] = new JsonArray(groups.Select(g => (JsonNode)new JsonObject { ["parts"] = new JsonArray(g.Select(i => (JsonNode)A(i)).ToArray()) }).ToArray()),
        };
        if (reviews is not null) o["reviewRequests"] = new JsonArray(reviews.Select(r => (JsonNode)new JsonObject { ["alias"] = A(r.Item1), ["reason"] = r.Item2 }).ToArray());
        if (status == "INCOMPLETE") o["incompleteReason"] = "không đủ bằng chứng";
        return o.ToJsonString();
    }

    static readonly string[] T48 = Enumerable.Range(0, 48).Select(A).ToArray();
    static readonly Dictionary<string, int> S48 = Shown(Enumerable.Range(0, 52));

    // ---------------- Stage 1, compact ----------------

    [Fact]
    public void Compact_FortyEightTargets_EachWithItsOwnCode_IsAccepted()
    {
        var r = P7F1V6DirectTwoStage.ParseSelect(F.Compact, Compact(T48, new HashSet<int> { 2, 3, 40 }), S48, T48);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal(48, r.Values.Count);
        Assert.Equal(3, r.Values.Values.Count(v => v == "SELECT"));
        Assert.Equal(45, r.Values.Values.Count(v => v == "NOT_SELECT"));
    }

    public static IEnumerable<object[]> CompactTampering()
    {
        object[] Case(Action<JsonObject> t, string code) => new object[] { t, code };
        yield return Case(o => o["blocks"]!.AsArray().RemoveAt(5), "BLOCK_COUNT_INVALID");
        yield return Case(o => o["blocks"]![1]!["codes"] = "NNNNNNN", "BLOCK_LENGTH_INVALID");
        yield return Case(o => o["blocks"]![1]!["start"] = 0, "BLOCK_START_INVALID");
        yield return Case(o => o["blocks"]![2]!["firstAlias"] = A(17), "BLOCK_BOUNDARY_MISMATCH");
        yield return Case(o => o["targetCount"] = 49, "TARGET_COUNT_MISMATCH");
        yield return Case(o => o["blocks"]![0]!["codes"] = "NNXNNNNN", "CODE_INVALID");
        yield return Case(o => o["selectedEvidence"]!.AsArray().RemoveAt(0), "SELECT_WITHOUT_EVIDENCE");
        yield return Case(o => o["selectedEvidence"]!.AsArray().Add(new JsonObject { ["targetIndex"] = 7, ["evidenceRefs"] = new JsonArray(A(7)) }), "EVIDENCE_FOR_NOT_SELECT");
        yield return Case(o => o["selectedEvidence"]!.AsArray().Add(new JsonObject { ["targetIndex"] = 48, ["evidenceRefs"] = new JsonArray(A(1)) }), "EVIDENCE_INDEX_INVALID");
        yield return Case(o => o["selectedEvidence"]![0]!["evidenceRefs"] = new JsonArray("L9999:S0"), "EVIDENCE_NOT_SHOWN");
        yield return Case(o => o["blocks"]![0]!["codes"] = "NNSUNNNN", "UNRESOLVED_ONLY_IN_INCOMPLETE");
        yield return Case(o => o["headings"] = new JsonArray(), "ROOT_KEYS_INVALID");
    }

    [Theory]
    [MemberData(nameof(CompactTampering))]
    public void Compact_Tampered_IsRefused(Action<JsonObject> tamper, string code)
    {
        var r = P7F1V6DirectTwoStage.ParseSelect(F.Compact, Compact(T48, new HashSet<int> { 2, 3, 40 }, tamper), S48, T48);
        Assert.False(r.Ok);
        Assert.Equal(code, r.Failure);
    }

    [Fact]
    public void Compact_IncompleteWithU_IsAccepted_AndKeepsU()
    {
        var r = P7F1V6DirectTwoStage.ParseSelect(F.Compact, Compact(T48, new HashSet<int> { 2 }, unresolved: new HashSet<int> { 9 }), S48, T48);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal("UNRESOLVED", r.Values[A(9)]);
    }

    // ---------------- Stage 1, objects ----------------

    [Fact]
    public void Objects_Full_IsAccepted_HeadingsAndScopeErrorsAreRefused()
    {
        Assert.True(P7F1V6DirectTwoStage.ParseSelect(F.Objects, Objects(T48, new HashSet<int> { 2 }), S48, T48).Ok);
        Assert.Equal("ROOT_KEYS_INVALID", P7F1V6DirectTwoStage.ParseSelect(F.Objects, Objects(T48, new HashSet<int> { 2 }, o => o["headings"] = new JsonArray()), S48, T48).Failure);
        Assert.Equal("UNEXPECTED_ALIAS", P7F1V6DirectTwoStage.ParseSelect(F.Objects,
            Objects(T48.Append(A(49)).ToArray(), new HashSet<int>()), S48, T48).Failure);
        Assert.Equal("COVERAGE_INCOMPLETE", P7F1V6DirectTwoStage.ParseSelect(F.Objects, Objects(T48.Skip(1).ToArray(), new HashSet<int>()), S48, T48).Failure);
        Assert.Equal("EVIDENCE_REFS_INVALID", P7F1V6DirectTwoStage.ParseSelect(F.Objects, Objects(T48, new HashSet<int> { 2 },
            o => o["decisions"]![2]!.AsObject().Remove("evidenceRefs")), S48, T48).Failure);
    }

    // ---------------- Stage 2, group ----------------

    static readonly HashSet<string> Sel = new[] { 3, 9, 10, 25 }.Select(A).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Group_RulesAreEnforced()
    {
        var shown = Shown(Enumerable.Range(0, 30));
        var targets = new[] { A(9), A(10) };
        Assert.True(P7F1V6DirectTwoStage.ParseGroup(Group(new[] { new[] { 9, 10 } }), shown, targets, Sel).Ok);
        Assert.Equal("GROUP_PART_NOT_SELECTED", P7F1V6DirectTwoStage.ParseGroup(Group(new[] { new[] { 9, 10, 11 } }), shown, targets, Sel).Failure);
        Assert.Equal("TARGET_NOT_GROUPED", P7F1V6DirectTwoStage.ParseGroup(Group(new[] { new[] { 9 } }), shown, targets, Sel).Failure);
        Assert.Equal("TARGET_IN_TWO_GROUPS", P7F1V6DirectTwoStage.ParseGroup(Group(new[] { new[] { 9, 10 }, new[] { 10 } }), shown, targets, Sel).Failure);
        Assert.Equal("GROUP_WITHOUT_TARGET", P7F1V6DirectTwoStage.ParseGroup(Group(new[] { new[] { 9, 10 }, new[] { 25 } }), shown, targets, Sel).Failure);
        var many = Enumerable.Range(0, 9).ToHashSet();
        var allSel = many.Select(A).ToHashSet(StringComparer.Ordinal);
        Assert.Equal("GROUP_PARTS_OVER_CAP", P7F1V6DirectTwoStage.ParseGroup(Group(new[] { many.ToArray() }), shown, many.Select(A).ToArray(), allSel).Failure);
    }

    [Fact]
    public void Group_ReviewRequest_IsRecordedOnly()
    {
        var r = P7F1V6DirectTwoStage.ParseGroup(Group(new[] { new[] { 9, 10 } }, new[] { (11, "có thể là phần của heading") }),
            Shown(Enumerable.Range(0, 30)), new[] { A(9), A(10) }, Sel);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal(A(11), Assert.Single(r.ReviewRequests).Alias);
    }

    // ---------------- requests ----------------

    [Fact]
    public void Requests_CompactNumbersTargets_GroupMarksSelectedContext()
    {
        var lines = Lines(20);
        var sel = BuildSelect(F.Compact, lines);
        var p = Payload(sel);
        Assert.Equal(Enumerable.Range(0, 10), p.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("i").GetInt32()));
        Assert.Equal(P7F1V6DirectTwoStage.SelectCompactVersion, p.GetProperty("responseVersion").GetString());
        Assert.False(Payload(BuildSelect(F.Objects, lines)).GetProperty("targets")[0].TryGetProperty("i", out _));

        var g = P7F1V6DirectTwoStage.BuildGroupRequest(Task, "sha", "GROUP-000", 1, new[] { A(9) }, lines.Take(12).ToArray(),
            new HashSet<string> { A(9), A(10) }, Array.Empty<P7F1V6DirectV2.Retrieval>());
        var gp = Payload(g);
        Assert.Equal("GROUP", gp.GetProperty("stage").GetString());
        var ctx = gp.GetProperty("context").EnumerateArray().ToDictionary(x => x.GetProperty("alias").GetString()!, x => x.GetProperty("selected").GetBoolean());
        Assert.True(ctx[A(10)]);
        Assert.False(ctx[A(11)]);
        Assert.Throws<ArgumentException>(() => P7F1V6DirectTwoStage.BuildGroupRequest(Task, "sha", "GROUP-000", 1, new[] { A(11) }, lines,
            new HashSet<string> { A(9) }, Array.Empty<P7F1V6DirectV2.Retrieval>()));
    }

    static P7F1V6Protocol.PreparedRequest BuildSelect(F format, List<P7F1V6Direct.Line> lines) =>
        P7F1V6DirectTwoStage.BuildSelectRequest(format, Task, "sha", "SEGMENT", "SEG-000", 1, Enumerable.Range(0, 10).Select(A).ToArray(),
            lines.Take(12).ToArray(), Array.Empty<P7F1V6DirectV2.Retrieval>());

    // ---------------- whole document ----------------

    /// <summary>Selects 9, 10 (one heading across the segment boundary) and 25; GROUP returns those two headings.</summary>
    static P7F1V6DirectV2.ReplyModel Script(F format, Func<string, string>? group = null) => (u, r, req) =>
    {
        if (Stage(req) == "GROUP")
        {
            if (group is not null) return group(u);
            var t = Targets(req).Select(Idx).ToHashSet();
            var gs = new List<int[]>();
            if (t.Contains(9) || t.Contains(10)) gs.Add(new[] { 9, 10 });
            if (t.Contains(25)) gs.Add(new[] { 25 });
            return Group(gs);
        }
        var sel = new HashSet<int> { 9, 10, 25 };
        return format == F.Compact ? Compact(Targets(req), sel) : Objects(Targets(req), sel);
    };

    [Theory]
    [InlineData("Compact")]
    [InlineData("Objects")]
    public void TwoStage_Completes_AndKeepsTheModelsCrossSegmentHeading(string formatName)
    {
        var format = Enum.Parse<F>(formatName);
        var o = P7F1V6DirectTwoStage.Run(format, Task, "sha", Lines(40), Script(format), Gen(), overlap: 2, perUnit: 10);
        Assert.Equal("COMPLETE", o.Run.Status);
        Assert.Equal("DONE", o.Grouping);
        Assert.Equal(P7F1V6DirectV2.AssuranceFinalSelectionReviewed, o.Run.Assurance);
        Assert.Contains(o.Run.Headings, h => h.SequenceEqual(new[] { A(9), A(10) }));
        Assert.Contains(o.Run.Headings, h => h.SequenceEqual(new[] { A(25) }));
        Assert.Equal(new[] { "SEGMENT", "FINALIZE", "GROUP" }, o.Run.Units.Select(u => u.Stage).Distinct());
    }

    [Fact]
    public void TwoStage_ReviewRequest_LeavesTheRunIncomplete_AndChangesNothing()
    {
        var o = P7F1V6DirectTwoStage.Run(F.Compact, Task, "sha", Lines(40),
            Script(F.Compact, _ => Group(new[] { new[] { 9, 10 }, new[] { 25 } }, new[] { (11, "có thể thuộc heading") })), Gen(), overlap: 2, perUnit: 10);
        Assert.Equal("INCOMPLETE", o.Run.Status);
        Assert.Equal("REVIEW_REQUESTED", o.Grouping);
        Assert.Equal("NOT_SELECT", o.Run.Decisions[A(11)]);
        Assert.Equal(A(11), Assert.Single(o.ReviewRequests).Alias);
    }

    [Fact]
    public void TwoStage_GroupThatUsesANotSelectedLine_IsRefused_NotAdmitted()
    {
        var o = P7F1V6DirectTwoStage.Run(F.Compact, Task, "sha", Lines(40),
            Script(F.Compact, _ => Group(new[] { new[] { 9, 10, 11 }, new[] { 25 } })), Gen(), overlap: 2, perUnit: 10);
        Assert.Equal("INCOMPLETE", o.Run.Status);
        Assert.Equal("NOT_DONE", o.Grouping);
        Assert.Equal("PROTOCOL:GROUP_PART_NOT_SELECTED", o.Run.Units.Single(u => u.Stage == "GROUP").Failure);
        Assert.Equal("NOT_SELECT", o.Run.Decisions[A(11)]);
    }

    [Fact]
    public void TwoStage_BudgetShortForGroup_IsIncomplete_WithNoHeadings()
    {
        // 4 segments + 1 finalize = 5 calls; GROUP is refused.
        var o = P7F1V6DirectTwoStage.Run(F.Compact, Task, "sha", Lines(40), Script(F.Compact), new P7F1V6DirectV2.Budget(5, 100, 2_000_000), overlap: 2, perUnit: 10);
        Assert.Equal("INCOMPLETE", o.Run.Status);
        Assert.Equal("NOT_DONE", o.Grouping);
        Assert.Equal("BUDGET:CALL_LIMIT", o.Run.Units.Single(u => u.Stage == "GROUP").Failure);
        Assert.Empty(o.Run.Headings);
    }

    [Fact]
    public void TwoStage_BudgetShortForFinalize_IsIncomplete_AndGroupIsNotReached()
    {
        var o = P7F1V6DirectTwoStage.Run(F.Compact, Task, "sha", Lines(40), Script(F.Compact), new P7F1V6DirectV2.Budget(4, 100, 2_000_000), overlap: 2, perUnit: 10);
        Assert.Equal("INCOMPLETE", o.Run.Status);
        Assert.Equal("NOT_DONE", o.Run.Finalization);
        Assert.Equal("NOT_REACHED", o.Grouping);
    }

    [Fact]
    public void TwoStage_MissingCodes_AreNeverReadAsNotSelect()
    {
        // The scripted model drops the last block of every reply: the unit fails and its targets stay UNRESOLVED.
        var o = P7F1V6DirectTwoStage.Run(F.Compact, Task, "sha", Lines(40), (u, r, req) =>
            Compact(Targets(req), new HashSet<int>(), j => j["blocks"]!.AsArray().RemoveAt(j["blocks"]!.AsArray().Count - 1)),
            Gen(), overlap: 2, perUnit: 10);
        Assert.Equal("INCOMPLETE", o.Run.Status);
        Assert.Equal("PROTOCOL:BLOCK_COUNT_INVALID", o.Run.Units[0].Failure);
        Assert.All(o.Run.Units[0].Decisions.Values, v => Assert.Equal("UNRESOLVED", v));
    }

    // ---------------- a selection contrary to the instruction is kept and scored ----------------

    [Fact]
    public void TwoStage_TableOfContentsSelection_IsKept_AndCountsAsAnError()
    {
        var root = TestRepository.Root();
        var gold = P7F1V6DirectD45.ReadGoldPin(root);
        var u = P7F1V6Universe.Rebuild(root, P7F1V6DirectD45.Document, gold.SourceSha256, out var why) ?? throw new InvalidOperationException(why);
        using var g = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, gold.Path)));
        var claims = g.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray()
            .Select(c => c.GetProperty("sourceParts").EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!).ToList()).ToList();
        var atoms = claims.SelectMany(c => c).Append("L0035:S0").ToHashSet(StringComparer.Ordinal);   // L0035:S0 is a table-of-contents line
        var o = P7F1V6DirectTwoStage.Run(F.Compact, P7F1V6DirectD45.Instruction, u, (unit, r, req) =>
        {
            var t = Targets(req);
            if (Stage(req) == "GROUP")
            {
                var owned = t.ToHashSet(StringComparer.Ordinal);
                var gs = claims.Where(c => c.Any(owned.Contains)).Select(c => (JsonNode)new JsonObject { ["parts"] = new JsonArray(c.Select(a => (JsonNode)a).ToArray()) }).ToList();
                if (owned.Contains("L0035:S0")) gs.Add(new JsonObject { ["parts"] = new JsonArray("L0035:S0") });
                return new JsonObject { ["protocolVersion"] = P7F1V6DirectTwoStage.GroupVersion, ["status"] = "COMPLETE", ["groups"] = new JsonArray(gs.ToArray()) }.ToJsonString();
            }
            var sel = t.Select((a, i) => (a, i)).Where(x => atoms.Contains(x.a)).Select(x => x.i).ToHashSet();
            return CompactByPosition(t, sel);
        }, Gen());

        Assert.Equal("COMPLETE", o.Run.Status);
        Assert.Equal("SELECT", o.Run.Decisions["L0035:S0"]);
        var s = P7F1V6DirectScorer.Score(root, P7F1V6DirectD45.Registry, P7F1V6DirectD45.AuthorityId, P7F1V6DirectScorer.RunEvidence.From(u, o.Run));
        Assert.True(s.Official, s.Reason);
        Assert.Equal(1, s.AtomMembership!.FalsePositive);
        Assert.Equal(1, s.HeadingOccurrence!.FalsePositive);
    }

    static string CompactByPosition(IReadOnlyList<string> targets, ISet<int> selectedPositions)
    {
        var codes = targets.Select((_, i) => selectedPositions.Contains(i) ? 'S' : 'N').ToArray();
        var blocks = new JsonArray();
        for (int s = 0; s < targets.Count; s += 8)
        {
            var len = Math.Min(8, targets.Count - s);
            blocks.Add(new JsonObject { ["start"] = s, ["firstAlias"] = targets[s], ["lastAlias"] = targets[s + len - 1], ["codes"] = new string(codes, s, len) });
        }
        return new JsonObject
        {
            ["protocolVersion"] = P7F1V6DirectTwoStage.SelectCompactVersion, ["status"] = "COMPLETE", ["targetCount"] = targets.Count, ["blocks"] = blocks,
            ["selectedEvidence"] = new JsonArray(selectedPositions.OrderBy(i => i).Select(i => (JsonNode)new JsonObject { ["targetIndex"] = i, ["evidenceRefs"] = new JsonArray(targets[i]) }).ToArray()),
        }.ToJsonString();
    }

    // ---------------- prompts ----------------

    static readonly string[] SemanticVocabulary =
    {
        "table of contents", "toc", "contents", "navigation", "index", "caption", "figure", "footnote", "header", "footer", "page number",
        "front matter", "appendix", "chapter", "section", "clause", "article", "title", "bold", "font", "size", "numbered", "numbering",
        "bullet", "indent", "prominent", "important", "main", "major", "primary", "level", "body", "rfc",
    };

    static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    {
        "<alias from targets>", "<alias from targets or context>", "<selected alias from targets or context>", "<alias>", "<why>",
        "<literal text>", "<question to the user>", "<reading>",
    };

    static readonly HashSet<string> Enums = new(StringComparer.Ordinal)
    {
        P7F1V6DirectTwoStage.SelectObjectsVersion, P7F1V6DirectTwoStage.SelectCompactVersion, P7F1V6DirectTwoStage.GroupVersion,
        "COMPLETE", "INCOMPLETE", "NEEDS_EVIDENCE", "CLARIFICATION_REQUIRED", "SELECT", "NOT_SELECT", "UNRESOLVED", "RANGE", "SEARCH",
    };

    static IEnumerable<string> Strings(JsonNode? n) => n switch
    {
        JsonObject o => o.SelectMany(p => Strings(p.Value)),
        JsonArray a => a.SelectMany(Strings),
        JsonValue v when v.TryGetValue<string>(out var s) => new[] { s },
        _ => Array.Empty<string>(),
    };

    [Fact]
    public void Prompts_ArePinned_ProtocolOnly_AndTheirSkeletonsHoldNoContent()
    {
        Assert.Equal(P7F1V6DirectTwoStage.SelectObjectsPromptSha256, P7F1V6DirectTwoStage.Sha(P7F1V6DirectTwoStage.SelectObjectsPrompt));
        Assert.Equal(P7F1V6DirectTwoStage.SelectCompactPromptSha256, P7F1V6DirectTwoStage.Sha(P7F1V6DirectTwoStage.SelectCompactPrompt));
        Assert.Equal(P7F1V6DirectTwoStage.GroupPromptSha256, P7F1V6DirectTwoStage.Sha(P7F1V6DirectTwoStage.GroupPrompt));
        foreach (var prompt in new[] { P7F1V6DirectTwoStage.SelectObjectsPrompt, P7F1V6DirectTwoStage.SelectCompactPrompt, P7F1V6DirectTwoStage.GroupPrompt })
        {
            var found = SemanticVocabulary.Where(w => Regex.IsMatch(prompt, @"\b" + Regex.Escape(w) + @"\b", RegexOptions.IgnoreCase)).ToList();
            Assert.True(found.Count == 0, "semantic vocabulary: " + string.Join(", ", found));
            Assert.Contains("it alone defines what to select", prompt, StringComparison.Ordinal);
            foreach (var s in prompt.Split('\n').Where(l => l.StartsWith("{", StringComparison.Ordinal)))
                foreach (var v in Strings(JsonNode.Parse(s)))
                    Assert.True(Placeholders.Contains(v) || Enums.Contains(v) || Regex.IsMatch(v, "^[SNU]+$"), $"skeleton value '{v}'");
        }
    }

    [Fact]
    public void Prompts_EverySkeleton_ParsesOnceFilledIn()
    {
        var shown = Shown(Enumerable.Range(0, 12));
        var targets = Enumerable.Range(0, 12).Select(A).ToArray();
        foreach (var line in P7F1V6DirectTwoStage.SelectCompactPrompt.Split('\n').Where(l => l.StartsWith("{", StringComparison.Ordinal)))
        {
            var node = JsonNode.Parse(line)!.AsObject();
            var status = node["status"]!.GetValue<string>();
            if (status is "COMPLETE" or "INCOMPLETE")
            {
                var n = node["targetCount"]!.GetValue<int>();
                var t = targets.Take(n).ToArray();
                foreach (var b in node["blocks"]!.AsArray())
                {
                    var s = b!["start"]!.GetValue<int>(); var len = b["codes"]!.GetValue<string>().Length;
                    b["firstAlias"] = t[s]; b["lastAlias"] = t[s + len - 1];
                }
                foreach (var e in node["selectedEvidence"]!.AsArray()) e!["evidenceRefs"] = new JsonArray(t[0]);
                if (status == "INCOMPLETE") node["incompleteReason"] = "x";
                var r = P7F1V6DirectTwoStage.ParseSelect(F.Compact, node.ToJsonString(), shown, t);
                Assert.True(r.Ok, status + ": " + r.Failure);
            }
        }
    }
}
