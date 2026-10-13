using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;
using A = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectAgent;
using C = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectRepeatCanary;
using T = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectOwnerTrial;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.12 (offline): the single-fence unwrap, the repetition vote, and a plan that pins repetitions and the unwrap.</summary>
public sealed class P7F1V6StableRunTests
{
    const string Obj = "{\"a\":1}";

    [Theory]
    [InlineData("```json\n{\"a\":1}\n```", true)]
    [InlineData("```\n{\"a\":1}\n```", true)]
    [InlineData("  \n```json\n{\"a\":1}\n```  \n", true)]
    [InlineData("```JSON\n{\"a\":1}\n```", false)]
    [InlineData("```python\n{\"a\":1}\n```", false)]
    [InlineData("Here is the answer:\n```json\n{\"a\":1}\n```", false)]
    [InlineData("```json\n{\"a\":1}\n```\nHope that helps", false)]
    [InlineData("```json\n{\"a\":1}\n```\n```json\n{\"b\":2}\n```", false)]
    [InlineData("```json\n[1,2]\n```", false)]
    [InlineData("```json\nnot json\n```", false)]
    [InlineData("```json {\"a\":1}```", false)]
    [InlineData("{\"a\":1}", false)]
    [InlineData("```json\n{\"a\":1}", false)]
    [InlineData("```json\n```", false)]
    [InlineData("", false)]
    public void TryUnwrap_TakesOffExactlyOneFenceAroundOneObject(string reply, bool expected)
    {
        Assert.Equal(expected, P7F1V6ReplyFence.TryUnwrap(reply, out var inner));
        if (expected) Assert.Equal(Obj, inner); else Assert.Equal("", inner);
    }

    static IReadOnlySet<string> S(params string[] a) => a.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Vote_SelectsOnlyWhatMoreThanHalfSelected_ReportsTheRestAsUncertain_AndNeedsThreeCompleteReps()
    {
        var order = new[] { "L1", "L2", "L3", "L4" };
        var r = P7F1V6Vote.Reduce(new[] { S("L1", "L2"), S("L1", "L3"), S("L1", "L2", "L4"), S("L1"), S("L1", "L2") }, order);
        Assert.True(r.Enough);
        Assert.Equal(new[] { "L1", "L2" }, r.Selected);                    // 5/5 and 3/5
        Assert.Equal(new[] { "L3", "L4" }, r.Uncertain);                   // 1/5 each
        Assert.Equal(3, r.Votes["L2"]);
        // An exact tie is not a majority.
        var tie = P7F1V6Vote.Reduce(new[] { S("L1"), S("L1"), S(), S() }, order);
        Assert.Empty(tie.Selected);
        Assert.Equal(new[] { "L1" }, tie.Uncertain);
        // Too few complete repetitions: no result at all.
        var few = P7F1V6Vote.Reduce(new[] { S("L1"), S("L1") }, order);
        Assert.False(few.Enough);
        Assert.Empty(few.Selected);
        Assert.Empty(few.Uncertain);
        Assert.Empty(P7F1V6Vote.Reduce(Array.Empty<IReadOnlySet<string>>(), order).Selected);
    }

    // ---- the agent ----

    static P7F1V6Universe Universe()
    {
        var rel = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        return P7F1V6Universe.Rebuild(TestRepository.Root(), rel, sha, out var why) ?? throw new InvalidOperationException(why);
    }

    static A.AgentUnitOutcome Run(P7F1V6Universe u, bool unwrap, Func<IReadOnlyList<string>, string> answer)
    {
        var (decide, ctx) = P7F1V6DirectCanary410.Scope(u, "SEG-000");
        A.AgentModel model = (unit, turn, req) =>
        {
            using var d = JsonDocument.Parse(req.Body);
            using var p = JsonDocument.Parse(d.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var targets = p.RootElement.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
            return new A.ModelTurn(answer(targets), Array.Empty<A.ToolCall>(), "stop");
        };
        return A.RunUnit(A.Arm.Auto, P7F1V6DirectD45.Instruction, u, "SEG-000", decide, ctx, model, new P7F1V6DirectV2.Budget(4, 1.0, C.MaxRequestBytes),
            rangeStated: true, unwrapSingleFence: unwrap);
    }

    static string Bare(IReadOnlyList<string> targets) => new JsonObject
    {
        ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
        ["decisions"] = new JsonArray(targets.Select(a => (JsonNode)new JsonObject { ["alias"] = a, ["decision"] = "NOT_SELECT" }).ToArray()),
    }.ToJsonString();

    [Fact]
    public void FencedAnswer_IsRefusedByDefault_AndReadWhenTheUnwrapIsOnAndFlagged()
    {
        var u = Universe();
        string Fenced(IReadOnlyList<string> t) => "```json\n" + Bare(t) + "\n```";
        var strict = Run(u, unwrap: false, Fenced);
        Assert.Equal("FAILED", strict.Unit.Status);
        Assert.Equal("PROTOCOL:REPLY_NOT_JSON", strict.Unit.Failure);
        Assert.False(strict.FenceUnwrapped);
        var lenient = Run(u, unwrap: true, Fenced);
        Assert.Equal("COMPLETE", lenient.Unit.Status);
        Assert.True(lenient.FenceUnwrapped);
        var bare = Run(u, unwrap: true, Bare);
        Assert.Equal("COMPLETE", bare.Unit.Status);
        Assert.False(bare.FenceUnwrapped);
    }

    [Fact]
    public void Unwrap_DoesNotRelaxTheParser_ProseAroundTheFenceAndABadObjectInsideStillFail()
    {
        var u = Universe();
        Assert.Equal("PROTOCOL:REPLY_NOT_JSON", Run(u, true, t => "Sure!\n```json\n" + Bare(t) + "\n```").Unit.Failure);
        // Inside the fence the answer is held to the same contract: a decision for an alias that is not a target is refused as before.
        var extra = Run(u, true, t => "```json\n" + new JsonObject
        {
            ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
            ["decisions"] = new JsonArray(t.Append("L9999:S0").Select(a => (JsonNode)new JsonObject { ["alias"] = a, ["decision"] = "NOT_SELECT" }).ToArray()),
        }.ToJsonString() + "\n```");
        Assert.Equal("FAILED", extra.Unit.Status);
        Assert.StartsWith("PROTOCOL:", extra.Unit.Failure);
        Assert.NotEqual("PROTOCOL:REPLY_NOT_JSON", extra.Unit.Failure);
    }

    // ---- the plan ----

    [Fact]
    public void StablePlan_PinsRepetitionsAndTheUnwrap_AndTheBudgetFollowsTheRepetitions()
    {
        var rel = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        var basePlan = T.Serialize(T.PinnedValues(path, "TEST_COMMIT"));
        var stable = T.Serialize(T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, reps: 9, unwrapFence: true));
        using var b = JsonDocument.Parse(basePlan);
        using var s = JsonDocument.Parse(stable);
        Assert.Equal(C.Reps, T.RepsOf(b.RootElement));
        Assert.False(T.FenceUnwrapOf(b.RootElement));
        Assert.Equal(9, T.RepsOf(s.RootElement));
        Assert.True(T.FenceUnwrapOf(s.RootElement));
        Assert.True(T.GeometryArmOf(s.RootElement));
        var segs = s.RootElement.GetProperty("segments").GetArrayLength();
        var units = segs * 9;
        foreach (var arm in s.RootElement.GetProperty("arms").EnumerateArray())
            Assert.Equal(units + Math.Max(3, units / 5), arm.GetProperty("budget").GetProperty("maxCalls").GetInt32());
        var file = Path.Combine(Path.GetTempPath(), "dhx-stable-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, stable);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(stable)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }
}
