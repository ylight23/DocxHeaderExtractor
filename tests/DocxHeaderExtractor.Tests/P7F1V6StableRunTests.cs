using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;
using A = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectAgent;
using C = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectRepeatCanary;
using T = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectOwnerTrial;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.12 (offline): the single-fence unwrap and a plan that pins it.</summary>
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
    public void UnwrapPlan_PinsTheUnwrap_AndVerifies()
    {
        var rel = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        var basePlan = T.Serialize(T.PinnedValues(path, "TEST_COMMIT"));
        var plan = T.Serialize(T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true));
        using var b = JsonDocument.Parse(basePlan);
        using var s = JsonDocument.Parse(plan);
        Assert.False(T.FenceUnwrapOf(b.RootElement));
        Assert.True(T.FenceUnwrapOf(s.RootElement));
        Assert.Equal(C.Reps, s.RootElement.GetProperty("repetitions").GetProperty("perArmAndSegment").GetInt32());
        var file = Path.Combine(Path.GetTempPath(), "dhx-unwrap-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, plan);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(plan)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void SingleArmPlan_HasOnlyGeometry_WhoseRequestCarriesEveryObservation_AndVerifies()
    {
        var rel = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        var bytes = T.Serialize(T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true));
        using var d = JsonDocument.Parse(bytes);
        Assert.True(T.SingleArmOf(d.RootElement));
        var arms = d.RootElement.GetProperty("arms").EnumerateArray().ToList();
        Assert.Single(arms);
        Assert.Equal("GEOMETRY", arms[0].GetProperty("arm").GetString());
        var u = T.Load(path, T.Sha256Of(path), P7F1V6Universe.LayoutSegmentation);
        using var body = JsonDocument.Parse(C.FirstRequest(A.Arm.Geometry, u, "SEG-001", true).Body);
        using var payload = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        var obs = payload.RootElement.GetProperty("observations");
        Assert.True(obs.TryGetProperty("targets", out _));          // repeated-text observations
        Assert.True(obs.TryGetProperty("layoutFacts", out _));      // layout block, segments, shared left edge
        var file = Path.Combine(Path.GetTempPath(), "dhx-single-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, bytes);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void FactsArm_IsTheGeometryRequestPlusOneProceduralSentence_AndNamesNoKindOfLine()
    {
        var rel = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        var u = T.Load(path, T.Sha256Of(path), P7F1V6Universe.LayoutSegmentation);
        string Payload(A.Arm arm)
        {
            using var body = JsonDocument.Parse(C.FirstRequest(arm, u, "SEG-001", true).Body);
            return body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        }
        Assert.Equal(Payload(A.Arm.Geometry), Payload(A.Arm.Facts));       // the same observations, byte for byte
        var geometryLines = A.GeometryPromptRange.Split((char)10).ToList();
        var factsLines = A.FactsPromptRange.Split((char)10).ToList();
        var added = factsLines.Except(geometryLines).ToList();
        Assert.Single(added);
        Assert.StartsWith("Use the observations:", added[0]);
        Assert.Empty(geometryLines.Except(factsLines));
        foreach (var banned in new[] { "table", "heading", "header", "footer", "title", "column", "cell", "contents" })
            Assert.DoesNotContain(banned, added[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Use the observations:", A.GeometryPromptRange);
    }

    [Fact]
    public void FactsPlan_HasOneFactsArm_OneRepetition_AndVerifies()
    {
        var rel = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        var bytes = T.Serialize(T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1));
        using var d = JsonDocument.Parse(bytes);
        Assert.True(T.FactsUseOf(d.RootElement));
        Assert.Equal(1, T.RepsOf(d.RootElement));
        var arms = d.RootElement.GetProperty("arms").EnumerateArray().ToList();
        Assert.Single(arms);
        Assert.Equal("FACTS", arms[0].GetProperty("arm").GetString());
        var segs = d.RootElement.GetProperty("segments").GetArrayLength();
        Assert.Equal(segs + Math.Max(3, segs / 5), arms[0].GetProperty("budget").GetProperty("maxCalls").GetInt32());
        Assert.Equal(A.Sha(A.FactsPromptRange), arms[0].GetProperty("promptSha256").GetString());
        var file = Path.Combine(Path.GetTempPath(), "dhx-facts-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, bytes);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void FirstSegmentsPlan_RunsOnlyTheLeadingSegments_NamesTheCorpus_AndVerifies()
    {
        var rel = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(T.InPublicCorpus(path));
        Assert.False(T.InPublicCorpus(Path.Combine(Path.GetTempPath(), "x.pdf")));
        var bytes = T.Serialize(T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1, firstSegments: 4));
        using var d = JsonDocument.Parse(bytes);
        Assert.Equal(4, T.FirstSegmentsOf(d.RootElement));
        Assert.Equal(new[] { "SEG-000", "SEG-001", "SEG-002", "SEG-003" }, d.RootElement.GetProperty("segments").EnumerateArray().Select(x => x.GetString()));
        Assert.StartsWith("A document of the repository's public heading corpus", d.RootElement.GetProperty("documentPublicity").GetString());
        Assert.True(d.RootElement.GetProperty("ownerSupplied").GetProperty("publicCorpus").GetBoolean());
        var file = Path.Combine(Path.GetTempPath(), "dhx-first-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, bytes);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void EnglishInstructionPlan_PinsTheInstruction_ItReachesTheRequest_AndVerifies()
    {
        var rel = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        var en = P7F1V6DirectD45.InstructionEnglishAll;
        Assert.Equal("Extract all headings", en);
        var bytes = T.Serialize(T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1, firstSegments: 2, instruction: en));
        using var d = JsonDocument.Parse(bytes);
        Assert.Equal(en, T.InstructionOf(d.RootElement));
        Assert.Equal(P7F1V6Core.InstructionSha256(en), d.RootElement.GetProperty("instructionSha256").GetString());
        var u = T.Load(path, T.Sha256Of(path), P7F1V6Universe.LayoutSegmentation);
        using var body = JsonDocument.Parse(C.FirstRequest(A.Arm.Facts, u, "SEG-000", true, en).Body);
        using var payload = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Assert.Equal(en, payload.RootElement.GetProperty("instruction").GetString());
        // The default is unchanged: the Vietnamese structural instruction.
        using var def = JsonDocument.Parse(C.FirstRequest(A.Arm.Facts, u, "SEG-000", true).Body);
        using var defPayload = JsonDocument.Parse(def.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Assert.Equal(P7F1V6DirectD45.Instruction, defPayload.RootElement.GetProperty("instruction").GetString());
        var file = Path.Combine(Path.GetTempPath(), "dhx-en-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, bytes);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }
}
