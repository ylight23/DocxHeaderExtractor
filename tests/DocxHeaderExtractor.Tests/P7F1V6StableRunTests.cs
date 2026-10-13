using System.Text;
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
            using var p = JsonDocument.Parse(P7F1V6Protocol.MessageText(d.RootElement.GetProperty("messages")[1].GetProperty("content")));
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
        using var payload = JsonDocument.Parse(P7F1V6Protocol.MessageText(body.RootElement.GetProperty("messages")[1].GetProperty("content")));
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
            return P7F1V6Protocol.MessageText(body.RootElement.GetProperty("messages")[1].GetProperty("content"));
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
        using var payload = JsonDocument.Parse(P7F1V6Protocol.MessageText(body.RootElement.GetProperty("messages")[1].GetProperty("content")));
        Assert.Equal(en, payload.RootElement.GetProperty("instruction").GetString());
        // The default is unchanged: the Vietnamese structural instruction.
        using var def = JsonDocument.Parse(C.FirstRequest(A.Arm.Facts, u, "SEG-000", true).Body);
        using var defPayload = JsonDocument.Parse(P7F1V6Protocol.MessageText(def.RootElement.GetProperty("messages")[1].GetProperty("content")));
        Assert.Equal(P7F1V6DirectD45.Instruction, defPayload.RootElement.GetProperty("instruction").GetString());
        var file = Path.Combine(Path.GetTempPath(), "dhx-en-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, bytes);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }

    // ---- D4.12d: parallel units ----

    [Fact]
    public void Budget_UnderConcurrentAdmission_NeverAdmitsMoreThanItsLimits()
    {
        var req = new P7F1V6Protocol.PreparedRequest("{}", 20000, "sha");
        var worst = P7F1V6Direct.AttemptUsd(req.Bytes);
        var budget = new P7F1V6DirectV2.Budget(40, worst * 25.5, C.MaxRequestBytes);
        var admitted = 0;
        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            if (budget.Admit(req) is null) Interlocked.Increment(ref admitted);
        });
        Assert.Equal(budget.Calls, admitted);
        Assert.Equal(25, admitted);                         // the reserve ceiling binds before the call limit of 40
        Assert.True(budget.ReservedUsd <= worst * 25.5 + 1e-9);
        Assert.Equal(375, budget.Refusals.Count);
    }

    [Fact]
    public void Budget_ConsecutiveContractFailures_StillHaltsTheCampaign_WhenReportedFromManyThreads()
    {
        var budget = new P7F1V6DirectV2.Budget(100, 10, C.MaxRequestBytes);
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => budget.NoteContract(false));
        Assert.Equal("CONSECUTIVE_CONTRACT_FAILURES", budget.HaltReason);
        Assert.Equal(8, budget.ContractFailures);
        Assert.NotNull(budget.Admit(new P7F1V6Protocol.PreparedRequest("{}", 1000, "sha")));
    }

    static int inFlight, maxInFlight;

    static A.AgentModel ScriptedModel(bool slow) => (unit, turn, req) =>
    {
        var now = Interlocked.Increment(ref inFlight);
        int seen;
        do { seen = Volatile.Read(ref maxInFlight); } while (now > seen && Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen);
        if (slow) Thread.Sleep(60);
        Interlocked.Decrement(ref inFlight);
        using var d = JsonDocument.Parse(req.Body);
        using var p = JsonDocument.Parse(P7F1V6Protocol.MessageText(d.RootElement.GetProperty("messages")[1].GetProperty("content")));
        var targets = p.RootElement.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
        // A deterministic answer that depends on the unit: select the first target of segment 1.
        var odd = unit.Contains("SEG-001");
        return new A.ModelTurn(new JsonObject
        {
            ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
            ["decisions"] = new JsonArray(targets.Select((a, i) => (JsonNode)(odd && i == 0
                ? new JsonObject { ["alias"] = a, ["decision"] = "SELECT", ["evidenceRefs"] = new JsonArray(a) }
                : new JsonObject { ["alias"] = a, ["decision"] = "NOT_SELECT" })).ToArray()),
        }.ToJsonString(), Array.Empty<A.ToolCall>(), "stop");
    };

    [Fact]
    public void RunArms_InParallel_GivesTheSameUnitsInTheSameOrder_AsOneAfterAnother_AndReallyOverlaps()
    {
        var u = Universe();
        var segs = new[] { "SEG-000", "SEG-001", "SEG-002" };
        IReadOnlyDictionary<string, P7F1V6DirectV2.Budget> Budgets() => C.Arms.ToDictionary(a => a.Arm, _ => new P7F1V6DirectV2.Budget(60, 5, C.MaxRequestBytes));
        var models = C.Arms.ToDictionary(a => a.Arm, _ => ScriptedModel(true));
        maxInFlight = 0;
        var sequential = C.RunArms(u, segs, models, Budgets(), reps: 2, parallelism: 1);
        Assert.Equal(1, maxInFlight);
        maxInFlight = 0;
        var parallel = C.RunArms(u, segs, models, Budgets(), reps: 2, parallelism: 6);
        Assert.True(maxInFlight > 1, "units should overlap");
        Assert.Equal(sequential.Count, parallel.Count);
        static string Key(C.RepUnit x) => x.Rep + "|" + x.Outcome.Unit.UnitId + "|" + x.Outcome.Unit.Status + "|"
            + string.Join(",", x.Outcome.Unit.Decisions.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => d.Key + "=" + d.Value));
        for (int k = 0; k < sequential.Count; k++)
        {
            Assert.Equal(sequential[k].Arm, parallel[k].Arm);
            Assert.Equal(sequential[k].Units.Select(Key), parallel[k].Units.Select(Key));
            Assert.Equal(sequential[k].Budget.Calls, parallel[k].Budget.Calls);
        }
        Assert.Equal(new[] { 1, 1, 1, 2, 2, 2 }, parallel[0].Units.Select(x => x.Rep));   // order: per repetition, per segment
    }

    [Fact]
    public void RunArms_TransportFailures_NeverAdmitMoreThanTheCallLimit_AlsoInParallel()
    {
        var u = Universe();
        var budgets = C.Arms.ToDictionary(a => a.Arm, _ => new P7F1V6DirectV2.Budget(4, 5, C.MaxRequestBytes));
        A.AgentModel model = (unit, turn, req) => throw new P7F1V6DirectV2.ReplyFailure("TRANSPORT:TEST");
        var r = C.RunArms(u, new[] { "SEG-000", "SEG-001", "SEG-002" }, C.Arms.ToDictionary(a => a.Arm, _ => model), budgets, reps: 2, parallelism: 6);
        foreach (var arm in r) Assert.True(arm.Budget.Calls <= 4);
    }

    sealed class SseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content!.ReadAsStringAsync(ct).Result;
            using var d = JsonDocument.Parse(body);
            using var p = JsonDocument.Parse(P7F1V6Protocol.MessageText(d.RootElement.GetProperty("messages")[1].GetProperty("content")));
            var targets = p.RootElement.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
            var content = Bare(targets);
            var prompt = Encoding.UTF8.GetByteCount(body) / 4;
            var sb = new StringBuilder();
            void Data(object o) => sb.Append("data: ").Append(JsonSerializer.Serialize(o)).Append("\n\n");
            Thread.Sleep(20);
            Data(new { id = "g", provider = "Alibaba", model = "qwen/qwen3.7-flash", choices = new[] { new { index = 0, delta = (object)new { content } } } });
            Data(new { id = "g", provider = "Alibaba", model = "qwen/qwen3.7-flash", choices = new[] { new { index = 0, delta = (object)new { }, finish_reason = "stop", native_finish_reason = "stop" } } });
            Data(new { id = "g", provider = "Alibaba", model = "qwen/qwen3.7-flash", choices = Array.Empty<object>(),
                usage = new { prompt_tokens = prompt, completion_tokens = 300, cost = P7F1V6Usage.Cost(prompt, 300), completion_tokens_details = new { reasoning_tokens = 200 } } });
            sb.Append("data: [DONE]\n\n");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream") });
        }
    }

    [Fact]
    public void AuditedModel_UnderConcurrentCalls_KeepsOneLedgerEntryPerCall_WithUniqueSequenceNumbers_AndOrdersBySeq()
    {
        var u = Universe();
        var dir = Path.Combine(Path.GetTempPath(), "dhx-par-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = P7F1V6SafeRawStore.CreateRun(dir);
            var budget = new P7F1V6DirectV2.Budget(100, 5, C.MaxRequestBytes);
            var model = new P7F1V6AgentAuditedModel(new P7F1V6DirectAuditedModel(new P7F1V6OpenRouterTransport(new SseHandler(), TimeSpan.FromSeconds(30)), "test-key", store, budget));
            var requests = new[] { "SEG-000", "SEG-001", "SEG-002" }.Select(s => C.FirstRequest(A.Arm.Auto, u, s)).ToArray();
            Parallel.For(0, 24, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                Assert.Null(budget.Admit(requests[i % 3]));
                var turn = model.Turn("U" + i, 1, requests[i % 3]);
                Assert.NotNull(turn.Content);
            });
            var ledger = model.Ledger;
            Assert.Equal(24, ledger.Count);
            Assert.Equal(Enumerable.Range(1, 24), ledger.Select(e => e.Seq));                  // unique, and returned in call order
            Assert.Equal(24, ledger.Select(e => e.UnitId).Distinct().Count());
            Assert.All(ledger, e => Assert.Equal("PASSED_AUDIT_TO_PARSER", e.Verdict));
            Assert.Equal(24, budget.Calls);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void MainHeadingsInstruction_IsEnglish_NamesNoKindOfLine_AndPinsInAPlan()
    {
        var en = P7F1V6DirectD45.InstructionEnglishMain;
        Assert.Contains("main headings", en);
        Assert.Contains("structure", en);
        foreach (var banned in new[] { "table", "footer", "running", "contents", "clause", "article", "chapter", "title" })
            Assert.DoesNotContain(banned, en, StringComparison.OrdinalIgnoreCase);
        var rel = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        var bytes = T.Serialize(T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1, firstSegments: 2, instruction: en));
        using var d = JsonDocument.Parse(bytes);
        Assert.Equal(en, T.InstructionOf(d.RootElement));
        var file = Path.Combine(Path.GetTempPath(), "dhx-main-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, bytes);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void MainToolsInstruction_SaysStructureToolsAndNoGuessing_NamesNoKindOfLine_AndTheToolRoundsBudgetIsPinned()
    {
        var en = P7F1V6DirectD45.InstructionEnglishMainTools;
        Assert.StartsWith(P7F1V6DirectD45.InstructionEnglishMain.Split((char)10)[0], en);       // the same request first
        Assert.Contains("use the tools", en);
        Assert.Contains("Do not guess", en);
        Assert.Contains("unresolved", en);
        Assert.Contains("frame of the document", en);
        foreach (var banned in new[] { "table", "footer", "running", "contents", "clause", "article", "chapter", "title", "cover" })
            Assert.DoesNotContain(banned, en, StringComparison.OrdinalIgnoreCase);
        var rel = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        var plain = T.Serialize(T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1, firstSegments: 3, instruction: en));
        var full = T.Serialize(T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1, firstSegments: 3, instruction: en, fullToolRounds: true));
        using var a = JsonDocument.Parse(plain);
        using var b = JsonDocument.Parse(full);
        Assert.False(T.ToolRoundsOf(a.RootElement));
        Assert.True(T.ToolRoundsOf(b.RootElement));
        Assert.Equal(3 + 3, a.RootElement.GetProperty("arms")[0].GetProperty("budget").GetProperty("maxCalls").GetInt32());                     // 3 units + 3 spare
        Assert.Equal(3 + 3 * A.MaxToolTurns, b.RootElement.GetProperty("arms")[0].GetProperty("budget").GetProperty("maxCalls").GetInt32());     // 3 units + every unit's tool rounds
        Assert.True(b.RootElement.GetProperty("arms")[0].GetProperty("budget").GetProperty("ceilingUsd").GetDouble() > a.RootElement.GetProperty("arms")[0].GetProperty("budget").GetProperty("ceilingUsd").GetDouble());
        var file = Path.Combine(Path.GetTempPath(), "dhx-tools-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, full);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(full)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }

    // ---- D4.12g: the facts through a mandatory first tool call ----

    static (string Body, JsonElement Root) Req(P7F1V6Protocol.PreparedRequest r)
    {
        var d = JsonDocument.Parse(r.Body);
        return (r.Body, d.RootElement.Clone());
    }

    [Fact]
    public void FactsTool_IsDeclaredOnlyWhenAsked_AndEveryEarlierToolDeclarationIsUnchanged()
    {
        Assert.Equal(P7F1V6DirectAgent.ToolDefinitionsSha256, P7F1V6AgentTools.DefinitionsSha256());
        Assert.Equal(7, P7F1V6AgentTools.Definitions().Count);
        var with = P7F1V6AgentTools.Definitions(true);
        Assert.Equal(8, with.Count);
        Assert.Equal(P7F1V6AgentTools.LineFacts, with[7]!["function"]!["name"]!.GetValue<string>());
        Assert.NotEqual(P7F1V6AgentTools.DefinitionsSha256(), P7F1V6AgentTools.DefinitionsSha256(true));
        for (int i = 0; i < 7; i++) Assert.Equal(P7F1V6AgentTools.Definitions()[i]!.ToJsonString(), with[i]!.ToJsonString());
        // The earlier arms' requests are byte for byte what they were.
        var u = Universe();
        foreach (var arm in new[] { A.Arm.Auto, A.Arm.Prefetch, A.Arm.Geometry, A.Arm.Facts })
            Assert.DoesNotContain(P7F1V6AgentTools.LineFacts, C.FirstRequest(arm, u, "SEG-000").Body);
        Assert.Contains(P7F1V6AgentTools.LineFacts, C.FirstRequest(A.Arm.ToolFacts, u, "SEG-000").Body);
    }

    [Fact]
    public void FactsTool_AnswersARange_WithTheLayoutFactsAndTheRepeatedText_PagesByOffset_AndIsUnknownWithoutTheFlag()
    {
        var u = Universe();
        var plain = new P7F1V6AgentTools(u.Tools, u.LayoutFacts);
        var r0 = plain.Execute("E1.01", P7F1V6AgentTools.LineFacts, "{\"first\":\"" + u.Lines[0].Alias + "\",\"last\":\"" + u.Lines[5].Alias + "\",\"offset\":0}");
        Assert.Equal("UNKNOWN_TOOL", r0.Status);
        var tools = new P7F1V6AgentTools(u.Tools, u.LayoutFacts, factsTool: true);
        var first = u.Lines[10].Alias; var last = u.Lines[69].Alias;                       // 60 lines: more than one call
        var r = tools.Execute("E1.01", P7F1V6AgentTools.LineFacts, "{\"first\":\"" + first + "\",\"last\":\"" + last + "\",\"offset\":0}");
        Assert.Equal("OK", r.Status);
        Assert.True(r.Content.Length <= P7F1V6AgentTools.MaxResultBytes);
        using var doc = JsonDocument.Parse(r.Content);
        var res = doc.RootElement.GetProperty("result");
        var facts = res.GetProperty("facts").EnumerateArray().ToList();
        Assert.True(facts.Count > 0 && facts.Count <= P7F1V6AgentTools.MaxFactLines);
        Assert.Equal(60, res.GetProperty("rangeLines").GetInt32());
        Assert.True(res.GetProperty("hasMore").GetBoolean());
        foreach (var f in facts)
        {
            var alias = f.GetProperty("alias").GetString()!;
            var expected = u.LayoutFacts[alias];
            Assert.Equal(expected["block"]!.ToJsonString(), f.GetProperty("block").GetRawText());
            Assert.Equal(expected["segments"]!.ToJsonString(), f.GetProperty("segments").GetRawText());
        }
        // The next page starts where this one ended and nothing is repeated or skipped.
        var next = res.GetProperty("nextOffset").GetInt32();
        Assert.Equal(facts.Count, next);
        var r2 = tools.Execute("E1.02", P7F1V6AgentTools.LineFacts, "{\"first\":\"" + first + "\",\"last\":\"" + last + "\",\"offset\":" + next + "}");
        using var doc2 = JsonDocument.Parse(r2.Content);
        var facts2 = doc2.RootElement.GetProperty("result").GetProperty("facts").EnumerateArray().Select(x => x.GetProperty("alias").GetString()).ToList();
        Assert.DoesNotContain(facts2, a => facts.Any(f => f.GetProperty("alias").GetString() == a));
        Assert.Equal(u.Lines[10 + next].Alias, facts2[0]);
        // Every alias a result shows counts as observed (Qwen may cite it).
        Assert.All(facts.Select(f => f.GetProperty("alias").GetString()!), a => Assert.Contains(a, tools.ObservedAliases));
        // Bad ranges are refused.
        Assert.Equal("INVALID_ARGUMENT", tools.Execute("E1.03", P7F1V6AgentTools.LineFacts, "{\"first\":\"" + last + "\",\"last\":\"" + first + "\",\"offset\":0}").Status);
        Assert.Equal("INVALID_ARGUMENT", tools.Execute("E1.04", P7F1V6AgentTools.LineFacts, "{\"first\":\"" + first + "\",\"last\":\"" + last + "\"}").Status);
    }

    [Fact]
    public void FactsTool_ShowsTheSameRepeatedTextObservationsThePayloadCarriesInTheFactsArm()
    {
        var u = Universe();
        var (decide, _) = P7F1V6DirectCanary410.Scope(u, "SEG-001");
        var tools = new P7F1V6AgentTools(u.Tools, u.LayoutFacts, factsTool: true);
        var pf = new P7F1V6AgentTools(u.Tools, u.LayoutFacts).Prefetch(decide);
        var listed = pf.Observations["targets"]!.AsArray().Select(t => t!["alias"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        while (offset < decide.Length)
        {
            var r = tools.Execute("E1." + offset, P7F1V6AgentTools.LineFacts, "{\"first\":\"" + decide[0] + "\",\"last\":\"" + decide[^1] + "\",\"offset\":" + offset + "}");
            using var doc = JsonDocument.Parse(r.Content);
            var res = doc.RootElement.GetProperty("result");
            var facts = res.GetProperty("facts").EnumerateArray().ToList();
            foreach (var f in facts) if (f.TryGetProperty("repeatedText", out _)) seen.Add(f.GetProperty("alias").GetString()!);
            offset += facts.Count;
            Assert.True(facts.Count > 0);
        }
        Assert.Equal(listed, seen);   // the tool lists repeated text for exactly the targets the payload observations listed
    }

    static A.AgentUnitOutcome RunToolFacts(P7F1V6Universe u, Func<string, int, A.ModelTurn> reply, out List<string> requests)
    {
        var (decide, ctx) = P7F1V6DirectCanary410.Scope(u, "SEG-001");
        var seen = new List<string>();
        A.AgentModel model = (unit, turn, req) => { lock (seen) seen.Add(req.Body); return reply(req.Body, turn); };
        var outcome = A.RunUnit(A.Arm.ToolFacts, P7F1V6DirectD45.InstructionEnglishAll, u, "SEG-001", decide, ctx, model, new P7F1V6DirectV2.Budget(8, 5, C.MaxRequestBytes), rangeStated: true, jsonObjectAllTurns: true);
        requests = seen;
        return outcome;
    }

    static string TargetRange(string body, string key)
    {
        using var d = JsonDocument.Parse(body);
        using var p = JsonDocument.Parse(P7F1V6Protocol.MessageText(d.RootElement.GetProperty("messages")[1].GetProperty("content")));
        return p.RootElement.GetProperty(key).GetString()!;
    }

    [Fact]
    public void ToolFactsArm_HasNoObservationsInThePayload_ARefusedAnswerWithoutTheFactsCall_AndAcceptsOnceTheFactsHaveBeenRead()
    {
        var u = Universe();
        // No observations in the payload; eight tools declared; the prompt is the TOOLFACTS one.
        var first = C.FirstRequest(A.Arm.ToolFacts, u, "SEG-001", true, P7F1V6DirectD45.InstructionEnglishAll);
        using (var d = JsonDocument.Parse(first.Body))
        using (var p = JsonDocument.Parse(P7F1V6Protocol.MessageText(d.RootElement.GetProperty("messages")[1].GetProperty("content"))))
        {
            Assert.False(p.RootElement.TryGetProperty("observations", out _));
            Assert.Equal(8, d.RootElement.GetProperty("tools").GetArrayLength());
            Assert.Equal(A.ToolFactsPromptRange, d.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        }
        Assert.Contains("first reply must be tool calls to get_line_facts", A.ToolFactsPromptRange);
        Assert.DoesNotContain("get_line_facts", A.FactsPromptRange);

        // An answer with no facts call is refused as a contract failure.
        var refused = RunToolFacts(u, (body, turn) => new A.ModelTurn(Bare(TargetsOf(body)), Array.Empty<A.ToolCall>(), "stop"), out _);
        Assert.Equal("FAILED", refused.Unit.Status);
        Assert.Equal("PROTOCOL:TOOL_FACTS_REQUIRED_NOT_REQUESTED", refused.Unit.Failure);

        // A call to another tool is not the facts call.
        var other = RunToolFacts(u, (body, turn) => turn == 1
            ? new A.ModelTurn(null, new[] { new A.ToolCall("c1", P7F1V6AgentTools.DocumentOverview, "{}") }, "tool_calls")
            : new A.ModelTurn(Bare(TargetsOf(body)), Array.Empty<A.ToolCall>(), "stop"), out _);
        Assert.Equal("PROTOCOL:TOOL_FACTS_REQUIRED_NOT_REQUESTED", other.Unit.Failure);

        // Facts first, then the answer.
        var ok = RunToolFacts(u, (body, turn) => turn == 1
            ? new A.ModelTurn(null, new[] { new A.ToolCall("c1", P7F1V6AgentTools.LineFacts, "{\"first\":\"" + TargetRange(body, "targetFirstAlias") + "\",\"last\":\"" + TargetRange(body, "targetLastAlias") + "\",\"offset\":0}") }, "tool_calls")
            : new A.ModelTurn(Bare(TargetsOf(body)), Array.Empty<A.ToolCall>(), "stop"), out var requests);
        Assert.Equal("COMPLETE", ok.Unit.Status);
        Assert.Equal(2, requests.Count);
        Assert.Contains("rangeLines", requests[1]);                // the facts came back as the tool message
        Assert.True(ok.Evidence.ToolSucceeded >= 1);
        Assert.NotEmpty(ok.Evidence.TargetsShownByTools);
    }

    static IReadOnlyList<string> TargetsOf(string body)
    {
        using var d = JsonDocument.Parse(body);
        using var p = JsonDocument.Parse(P7F1V6Protocol.MessageText(d.RootElement.GetProperty("messages")[1].GetProperty("content")));
        return p.RootElement.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
    }

    [Fact]
    public void ToolFactsPlan_HasOneToolFactsArm_PinsTheEightToolDefinitions_AndVerifies()
    {
        var rel = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        var bytes = T.Serialize(T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1,
            firstSegments: 3, instruction: P7F1V6DirectD45.InstructionEnglishMainTools, fullToolRounds: true, toolFacts: true));
        using var d = JsonDocument.Parse(bytes);
        Assert.True(T.ToolFactsOf(d.RootElement));
        var arms = d.RootElement.GetProperty("arms").EnumerateArray().ToList();
        Assert.Single(arms);
        Assert.Equal("TOOLFACTS", arms[0].GetProperty("arm").GetString());
        Assert.Equal(A.Sha(A.ToolFactsPromptRange), arms[0].GetProperty("promptSha256").GetString());
        Assert.Equal(P7F1V6AgentTools.DefinitionsSha256(true), d.RootElement.GetProperty("tools").GetProperty("definitionsSha256").GetString());
        Assert.Contains(P7F1V6AgentTools.LineFacts, d.RootElement.GetProperty("tools").GetProperty("names").EnumerateArray().Select(x => x.GetString()));
        var file = Path.Combine(Path.GetTempPath(), "dhx-toolfacts-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, bytes);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }

    // ---- D4.12h: the analysis string beside the decisions ----

    static string WithAnalysis(IReadOnlyList<string> targets, object? analysis)
    {
        var o = new JsonObject
        {
            ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
        };
        if (analysis is not null) o["analysis"] = JsonSerializer.SerializeToNode(analysis);
        o["decisions"] = new JsonArray(targets.Select(a => (JsonNode)new JsonObject { ["alias"] = a, ["decision"] = "NOT_SELECT" }).ToArray());
        return o.ToJsonString();
    }

    [Fact]
    public void ParseSelect_TheAnalysisKey_IsAllowedOnlyWhenAsked_IsBoundedAndTyped_AndNeverChangesTheDecisionRules()
    {
        var targets = new[] { "L1:S0", "L2:S0" };
        var shown = targets.Select((a, i) => (a, i)).ToDictionary(x => x.a, x => x.i, StringComparer.Ordinal);
        P7F1V6DirectTwoStage.SelectReply Parse(string reply, bool allowed) =>
            P7F1V6DirectTwoStage.ParseSelect(P7F1V6DirectTwoStage.SelectFormat.Objects, reply, shown, targets, needsEvidenceAllowed: false, analysisAllowed: allowed);
        // Not asked: the key is refused, as every earlier arm always did.
        var off = Parse(WithAnalysis(targets, "why"), false);
        Assert.False(off.Ok);
        Assert.Equal("ROOT_KEYS_INVALID", off.Failure);
        Assert.True(Parse(WithAnalysis(targets, null), false).Ok);
        // Asked: read, kept, and the decisions are the same.
        var on = Parse(WithAnalysis(targets, "The first line is a title because of its block."), true);
        Assert.True(on.Ok);
        Assert.Equal("The first line is a title because of its block.", on.Analysis);
        Assert.Equal(targets.ToDictionary(a => a, _ => "NOT_SELECT"), on.Values.ToDictionary(kv => kv.Key, kv => kv.Value));
        Assert.Null(Parse(WithAnalysis(targets, null), true).Analysis);
        // Bounded and typed.
        Assert.True(Parse(WithAnalysis(targets, new string('x', 6000)), true).Ok);                   // no length limit of its own (owner, D4.12m)
        Assert.Equal("REPLY_OVER_CAP", Parse(WithAnalysis(targets, new string('x', 20000)), true).Failure);   // the whole reply stays within the cap
        Assert.Equal("ANALYSIS_INVALID", Parse(WithAnalysis(targets, 7), true).Failure);
        // Nothing else relaxes: another root key, a missing decision, an extra alias and a key inside a decision are still refused.
        Assert.Equal("ROOT_KEYS_INVALID", Parse(WithAnalysis(targets, "ok").Replace("\"decisions\"", "\"notes\":\"x\",\"decisions\""), true).Failure);
        Assert.Equal("COVERAGE_INCOMPLETE", Parse(WithAnalysis(targets.Take(1).ToArray(), "ok"), true).Failure);
        Assert.Equal("UNEXPECTED_ALIAS", Parse(WithAnalysis(targets.Append("L9:S0").ToArray(), "ok"), true).Failure);
        // reason: allowed on a decision only under this contract, bounded and typed.
        string WithReason(object reason) => WithAnalysis(targets, "ok").Replace("\"decision\":\"NOT_SELECT\"", "\"decision\":\"NOT_SELECT\",\"reason\":" + JsonSerializer.Serialize(reason), StringComparison.Ordinal);
        var withReason = Parse(WithReason("short"), true);
        Assert.True(withReason.Ok);
        Assert.Equal("short", withReason.Reasons!["L1:S0"]);
        Assert.Equal("DECISION_KEYS_INVALID", Parse(WithReason("short").Replace("\"analysis\":\"ok\",", ""), false).Failure);   // not asked: reason is refused too
        Assert.Equal("REASON_INVALID", Parse(WithReason(new string((char)120, P7F1V6DirectTwoStage.MaxReasonChars + 1)), true).Failure);
        Assert.Equal("REASON_INVALID", Parse(WithReason(3), true).Failure);
        Assert.Equal("DECISION_KEYS_INVALID", Parse(WithAnalysis(targets, "ok").Replace("\"decision\":\"NOT_SELECT\"", "\"decision\":\"NOT_SELECT\",\"why\":\"x\""), true).Failure);
    }

    [Fact]
    public void ToolFactsAnalysisPrompt_DiffersFromToolFactsOnlyByTheAnalysisExampleAndTheRule()
    {
        var a = A.ToolFactsPromptRange.Split((char)10).ToList();
        var b = A.ToolFactsAnalysisPromptRange.Split((char)10).ToList();
        Assert.Equal(a.Count, b.Count);
        var changed = a.Zip(b).Where(p => p.First != p.Second).ToList();
        Assert.Equal(2, changed.Count);                                         // the COMPLETE example line (analysis and reason) and the Rules line
        Assert.Contains("\"analysis\":\"<your explanation", changed[0].Second);
        Assert.Contains("\"reason\":\"<why this line is such a heading", changed[0].Second);
        Assert.Contains("Two extra keys are allowed: analysis", changed[1].Second);
        Assert.Contains("Write nothing before or after the JSON object", changed[1].Second);
        Assert.DoesNotContain("analysis", A.ToolFactsPromptRange);
        foreach (var banned in new[] { "table", "footer", "title", "contents", "cover", "clause" })   // the word heading is the task's own word; no kind of line is named
            Assert.DoesNotContain(banned, string.Join(" ", changed.Select(c => c.Second)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolFactsAnalysisArm_KeepsTheAnalysis_WhileTheOtherToolFactsArmStillRefusesTheSameAnswer()
    {
        var u = Universe();
        var (decide, ctx) = P7F1V6DirectCanary410.Scope(u, "SEG-001");
        A.AgentUnitOutcome Run(A.Arm arm)
        {
            A.AgentModel model = (unit, turn, req) =>
            {
                var body = req.Body;
                if (turn == 1)
                    return new A.ModelTurn(null, new[] { new A.ToolCall("c1", P7F1V6AgentTools.LineFacts, "{\"first\":\"" + TargetRange(body, "targetFirstAlias") + "\",\"last\":\"" + TargetRange(body, "targetLastAlias") + "\",\"offset\":0}") }, "tool_calls");
                return new A.ModelTurn(WithAnalysis(TargetsOf(body), "Read the facts; no line is a main heading here."), Array.Empty<A.ToolCall>(), "stop");
            };
            return A.RunUnit(arm, P7F1V6DirectD45.InstructionEnglishMainTools, u, "SEG-001", decide, ctx, model, new P7F1V6DirectV2.Budget(8, 5, C.MaxRequestBytes), rangeStated: true, jsonObjectAllTurns: true);
        }
        var with = Run(A.Arm.ToolFactsAnalysis);
        Assert.Equal("COMPLETE", with.Unit.Status);
        Assert.Equal("Read the facts; no line is a main heading here.", with.Analysis);
        var without = Run(A.Arm.ToolFacts);
        Assert.Equal("FAILED", without.Unit.Status);
        Assert.Equal("PROTOCOL:ROOT_KEYS_INVALID", without.Unit.Failure);
        Assert.Null(without.Analysis);
    }

    [Fact]
    public void ToolFactsAnalysisPlan_HasOneArm_PinsThePrompt_AndVerifies()
    {
        var rel = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        var bytes = T.Serialize(T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1,
            firstSegments: 3, instruction: P7F1V6DirectD45.InstructionEnglishMainTools, fullToolRounds: true, toolFactsAnalysis: true));
        using var d = JsonDocument.Parse(bytes);
        Assert.True(T.ToolFactsAnalysisOf(d.RootElement));
        var arms = d.RootElement.GetProperty("arms").EnumerateArray().ToList();
        Assert.Single(arms);
        Assert.Equal("TOOLFACTS_ANALYSIS", arms[0].GetProperty("arm").GetString());
        Assert.Equal(A.Sha(A.ToolFactsAnalysisPromptRange), arms[0].GetProperty("promptSha256").GetString());
        Assert.Equal(P7F1V6AgentTools.DefinitionsSha256(true), d.RootElement.GetProperty("tools").GetProperty("definitionsSha256").GetString());
        var file = Path.Combine(Path.GetTempPath(), "dhx-analysis-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, bytes);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void MainPlainInstruction_IsTheOwnersThreeSentencesVerbatim()
    {
        Assert.Equal(
            "Extract the main headings of the document: the headings that carry its structure (its outline).\n" +
            "When you are unsure about how a line relates to other lines, use the tools to check that relationship.\n" +
            "Do not guess: if the evidence does not settle a line, say that it is unresolved instead of choosing.",
            P7F1V6DirectD45.InstructionEnglishMainPlain);
        Assert.DoesNotContain("name or open", P7F1V6DirectD45.InstructionEnglishMainPlain);
        Assert.DoesNotContain("how it looks", P7F1V6DirectD45.InstructionEnglishMainPlain);
    }

    // ---- D4.12j: layout facts V2 ----

    [Fact]
    public void LayoutFactsV2_AddTheRelativeLeftEdgeAndFontSize_V1IsUnchanged_AndTheVersionSurvivesResegmentation()
    {
        var u = Universe();
        Assert.Equal(1, u.FactsVersion);
        Assert.All(u.LayoutFacts.Values, f => { Assert.False(f.ContainsKey("leftEdge")); Assert.False(f.ContainsKey("fontSizeVsPageMedianPt")); });
        var v2 = u.WithFactsVersion(2);
        Assert.Equal(2, v2.FactsVersion);
        Assert.Equal(2, v2.WithSegmentation(P7F1V6Universe.LayoutSegmentation).FactsVersion);
        Assert.Equal(1, v2.WithSegmentation(P7F1V6Universe.LayoutSegmentation).WithFactsVersion(1).FactsVersion);
        foreach (var l in u.Lines)
        {
            var a = u.LayoutFacts[l.Alias]; var b = v2.LayoutFacts[l.Alias];
            // Every V1 field is the same in V2.
            foreach (var kv in a) Assert.Equal(kv.Value!.ToJsonString(), b[kv.Key]!.ToJsonString());
        }
        // On every page the common edge is shared by the most lines: no line's own edge is shared by more lines than the common edge.
        foreach (var page in v2.Lines.GroupBy(l => l.Page))
        {
            var withEdge = page.Select(l => v2.LayoutFacts[l.Alias]).Where(f => f.ContainsKey("leftEdge")).ToList();
            if (withEdge.Count == 0) continue;
            var common = withEdge.Max(f => (int)f["leftEdge"]!["pageCommonEdgeLines"]!);
            Assert.All(withEdge, f => Assert.True((int)f["leftEdgeSharedBy"]! + 1 <= common));
            Assert.Contains(withEdge, f => Math.Abs((double)f["leftEdge"]!["offsetFromPageCommonPt"]!) <= P7F1V6LayoutFacts.LeftEdgeTolerancePt);
        }
        Assert.Contains("offsetFromPageCommonPt", P7F1V6LayoutFacts.DescriptionOf(2));
        Assert.DoesNotContain("offsetFromPageCommonPt", P7F1V6LayoutFacts.DescriptionOf(1));
        // The tool describes the version it serves.
        var tools = new P7F1V6AgentTools(v2.Tools, v2.LayoutFacts, factsTool: true, factsVersion: 2);
        var r = tools.Execute("E1.01", P7F1V6AgentTools.LineFacts, "{\"first\":\"" + v2.Lines[0].Alias + "\",\"last\":\"" + v2.Lines[3].Alias + "\",\"offset\":0}");
        Assert.Contains(P7F1V6LayoutFacts.VersionV2, Encoding.UTF8.GetString(r.Content));
        Assert.Contains("fontSizeVsPageMedianPt", Encoding.UTF8.GetString(r.Content));
    }

    [Fact]
    public void FactsV2Plan_PinsTheVersion_ChangesOnlyTheFacts_AndVerifies()
    {
        var rel = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        Dictionary<string, object?> Pinned(int v) => T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1,
            firstSegments: 3, instruction: P7F1V6DirectD45.InstructionEnglishMainPlain, fullToolRounds: true, toolFactsAnalysis: true, factsVersion: v);
        var one = T.Serialize(Pinned(1)); var two = T.Serialize(Pinned(2));
        using var a = JsonDocument.Parse(one); using var b = JsonDocument.Parse(two);
        Assert.Equal(1, T.FactsVersionOf(a.RootElement));
        Assert.Equal(2, T.FactsVersionOf(b.RootElement));
        // The first requests of TOOLFACTS carry no facts (they come from the tool), so they are the same; the plan differs by the pinned version.
        Assert.Equal(a.RootElement.GetProperty("arms")[0].GetProperty("requestPlanSha256").GetString(), b.RootElement.GetProperty("arms")[0].GetProperty("requestPlanSha256").GetString());
        var file = Path.Combine(Path.GetTempPath(), "dhx-v2-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, two);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(two)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void ToolFactsAnalysisV2Prompt_DiffersOnlyByTheFactsSentence_WhichDescribesTheTwoNewMeasurements_AndThePlanPinsIt()
    {
        var a = A.ToolFactsAnalysisPromptRange.Split((char)10).ToList();
        var b = A.ToolFactsAnalysisV2PromptRange.Split((char)10).ToList();
        Assert.Equal(a.Count, b.Count);
        var changed = a.Zip(b).Where(p => p.First != p.Second).ToList();
        Assert.Single(changed);
        Assert.StartsWith("Facts:", changed[0].Second);
        Assert.Contains("the edge that the most lines of its page share", changed[0].Second);
        Assert.Contains("median font size of its page", changed[0].Second);
        foreach (var banned in new[] { "table", "footer", "title", "contents", "body", "margin" })
            Assert.DoesNotContain(banned, changed[0].Second, StringComparison.OrdinalIgnoreCase);
        Assert.True(A.UsesFactsTool(A.Arm.ToolFactsAnalysisV2));
        Assert.True(A.AllowsAnalysis(A.Arm.ToolFactsAnalysisV2));
        var rel = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.Throws<ArgumentException>(() => T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1,
            firstSegments: 2, instruction: P7F1V6DirectD45.InstructionEnglishMainPlain, fullToolRounds: true, toolFactsAnalysis: true, factsVersion: 1, promptFactsV2: true));
        var bytes = T.Serialize(T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1,
            firstSegments: 2, instruction: P7F1V6DirectD45.InstructionEnglishMainPlain, fullToolRounds: true, toolFactsAnalysis: true, factsVersion: 2, promptFactsV2: true));
        using var d = JsonDocument.Parse(bytes);
        Assert.True(T.PromptFactsV2Of(d.RootElement));
        Assert.Equal("TOOLFACTS_ANALYSIS_V2", d.RootElement.GetProperty("arms")[0].GetProperty("arm").GetString());
        Assert.Equal(A.Sha(A.ToolFactsAnalysisV2PromptRange), d.RootElement.GetProperty("arms")[0].GetProperty("promptSha256").GetString());
        var file = Path.Combine(Path.GetTempPath(), "dhx-pv2-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, bytes);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }

    // ---- D4.12l: no layout in the request, the document style table, facts V3 ----

    [Fact]
    public void FactsV3_KeepV2_AddTheLinesLayoutAndItsStyleRank_ConsistentWithTheDocumentStyleTable()
    {
        var u = Universe();
        var v2 = u.WithFactsVersion(2); var v3 = u.WithFactsVersion(3);
        var table = new P7F1V6AgentTools(v3.Tools, v3.LayoutFacts, factsVersion: 3).DocumentStyles();
        var styles = table["styles"]!.AsArray();
        Assert.True(styles.Count > 0);
        // Largest first, bold before regular at the same size, ranks 1..n.
        for (int k = 0; k < styles.Count; k++) Assert.Equal(k + 1, (int)styles[k]!["rank"]!);
        for (int k = 1; k < styles.Count; k++)
        {
            var a = (double)styles[k - 1]!["sizePt"]!; var b = (double)styles[k]!["sizePt"]!;
            Assert.True(a > b || (a == b && (bool)styles[k - 1]!["bold"]! && !(bool)styles[k]!["bold"]!));
        }
        foreach (var l in u.Lines)
        {
            var f2 = v2.LayoutFacts[l.Alias]; var f3 = v3.LayoutFacts[l.Alias];
            foreach (var kv in f2) Assert.Equal(kv.Value!.ToJsonString(), f3[kv.Key]!.ToJsonString());
            if (f3["style"] is JsonObject st)
            {
                var rank = (int)st["rankInDocument"]!;
                if (rank <= styles.Count)
                {
                    Assert.Equal((double)styles[rank - 1]!["sizePt"]!, (double)st["sizePt"]!);
                    Assert.Equal((bool)styles[rank - 1]!["bold"]!, (bool)st["bold"]!);
                    Assert.Equal((int)styles[rank - 1]!["lines"]!, (int)st["linesWithThisStyleInDocument"]!);
                }
            }
        }
        Assert.Contains(v3.LayoutFacts.Values, f => f.ContainsKey("layout") && f.ContainsKey("style"));
        Assert.Equal(u.Lines.Count, (int)table["documentLines"]!);
    }

    [Fact]
    public void V3Request_ShowsNoLayout_CarriesTheDocumentStyles_AndItsPromptSaysSo()
    {
        var u = Universe().WithFactsVersion(3);
        var r = C.FirstRequest(A.Arm.ToolFactsAnalysisV3, u, "SEG-001", true, P7F1V6DirectD45.InstructionEnglishMainPlain);
        using var d = JsonDocument.Parse(r.Body);
        using var p = JsonDocument.Parse(P7F1V6Protocol.MessageText(d.RootElement.GetProperty("messages")[1].GetProperty("content")));
        foreach (var part in new[] { "targets", "context" })
            foreach (var item in p.RootElement.GetProperty(part).EnumerateArray())
                Assert.Equal(new[] { "alias", "page", "text" }, item.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.True(p.RootElement.TryGetProperty("documentStyles", out var ds));
        Assert.True(ds.GetProperty("styles").GetArrayLength() > 0);
        Assert.False(p.RootElement.TryGetProperty("observations", out _));
        Assert.Equal(8, d.RootElement.GetProperty("tools").GetArrayLength());
        var prompt = d.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Equal(A.ToolFactsAnalysisV3PromptRange, prompt);
        Assert.Contains("this request shows no layout", prompt);
        Assert.Contains("Document styles:", prompt);
        Assert.Contains("its style against the whole document", prompt);
        Assert.DoesNotContain("Each line has alias, page, text and layout.", prompt);
        var added = A.ToolFactsAnalysisV3PromptRange.Split((char)10).Except(A.ToolFactsAnalysisV2PromptRange.Split((char)10)).ToList();
        foreach (var banned in new[] { "table of", "footer", "title", "contents", "cover page", "clause" })
            Assert.DoesNotContain(banned, string.Join(" ", added), StringComparison.OrdinalIgnoreCase);
        // The earlier arms still show the layout.
        using var d2 = JsonDocument.Parse(C.FirstRequest(A.Arm.ToolFactsAnalysisV2, u, "SEG-001", true).Body);
        using var p2 = JsonDocument.Parse(P7F1V6Protocol.MessageText(d2.RootElement.GetProperty("messages")[1].GetProperty("content")));
        Assert.Contains(p2.RootElement.GetProperty("targets").EnumerateArray(), x => x.TryGetProperty("layout", out _));
        Assert.False(p2.RootElement.TryGetProperty("documentStyles", out _));
    }

    [Fact]
    public void V3Plan_NeedsFactsV3_PinsTheArm_AndVerifies()
    {
        var rel = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        Dictionary<string, object?> Pinned(int v) => T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1,
            firstSegments: 2, instruction: P7F1V6DirectD45.InstructionEnglishMainPlain, fullToolRounds: true, toolFactsAnalysis: true, factsVersion: v, hideLayout: true);
        Assert.Throws<ArgumentException>(() => Pinned(2));
        var bytes = T.Serialize(Pinned(3));
        using var d = JsonDocument.Parse(bytes);
        Assert.True(T.HideLayoutOf(d.RootElement));
        Assert.Equal(3, T.FactsVersionOf(d.RootElement));
        Assert.Equal("TOOLFACTS_ANALYSIS_V3", d.RootElement.GetProperty("arms")[0].GetProperty("arm").GetString());
        Assert.Equal(A.Sha(A.ToolFactsAnalysisV3PromptRange), d.RootElement.GetProperty("arms")[0].GetProperty("promptSha256").GetString());
        var file = Path.Combine(Path.GetTempPath(), "dhx-v3-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, bytes);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }

    // ---- D4.12m: transport retry and one repair turn ----

    static string FactsCall(string body) =>
        "{\"first\":\"" + TargetRange(body, "targetFirstAlias") + "\",\"last\":\"" + TargetRange(body, "targetLastAlias") + "\",\"offset\":0}";

    static (A.AgentUnitOutcome Outcome, List<string> Bodies, P7F1V6DirectV2.Budget Budget) RunV3(Func<string, int, int, A.ModelTurn> reply, bool repair, int retries)
    {
        var u = Universe().WithFactsVersion(3);
        var (decide, ctx) = P7F1V6DirectCanary410.Scope(u, "SEG-001");
        var bodies = new List<string>();
        var n = 0;
        A.AgentModel model = (unit, turn, req) => { bodies.Add(req.Body); n++; return reply(req.Body, turn, n); };
        var budget = new P7F1V6DirectV2.Budget(20, 5, C.MaxRequestBytes);
        var o = A.RunUnit(A.Arm.ToolFactsAnalysisV3, P7F1V6DirectD45.InstructionEnglishMainPlain, u, "SEG-001", decide, ctx, model, budget,
            rangeStated: true, jsonObjectAllTurns: true, transportRetries: retries, repairTurn: repair, retryDelayMs: 0);
        return (o, bodies, budget);
    }

    static A.ModelTurn FactsTurn(string body) => new(null, new[] { new A.ToolCall("f" + Guid.NewGuid().ToString("N")[..6], P7F1V6AgentTools.LineFacts, FactsCall(body)) }, "tool_calls");
    static A.ModelTurn Answer(string body) => new(WithAnalysis(TargetsOf(body), "ok"), Array.Empty<A.ToolCall>(), "stop");

    [Fact]
    public void Repair_AProseAnswer_IsSentBackOnceWithTheRefusal_AndTheCorrectedAnswerIsAccepted()
    {
        var (o, bodies, _) = RunV3((body, turn, n) => n == 1 ? FactsTurn(body)
            : n == 2 ? new A.ModelTurn("Here is my analysis.\n" + WithAnalysis(TargetsOf(body), "x"), Array.Empty<A.ToolCall>(), "stop")
            : Answer(body), repair: true, retries: 0);
        Assert.Equal("COMPLETE", o.Unit.Status);
        Assert.Equal("PROTOCOL:REPLY_NOT_JSON", o.RepairedFrom);
        Assert.Equal(3, bodies.Count);
        Assert.Contains("Your answer was refused (PROTOCOL:REPLY_NOT_JSON)", bodies[2]);
        Assert.Contains("Here is my analysis.", bodies[2]);                      // the refused reply is in the conversation, unchanged
        // Without the flag the same replies fail as before.
        var (off, offBodies, _) = RunV3((body, turn, n) => n == 1 ? FactsTurn(body)
            : new A.ModelTurn("Here is my analysis.\n" + WithAnalysis(TargetsOf(body), "x"), Array.Empty<A.ToolCall>(), "stop"), repair: false, retries: 0);
        Assert.Equal("PROTOCOL:REPLY_NOT_JSON", off.Unit.Failure);
        Assert.Null(off.RepairedFrom);
        Assert.Equal(2, offBodies.Count);
    }

    [Fact]
    public void Repair_IsUsedAtMostOnce_ASecondRefusalFailsTheUnit_AndIsRecorded()
    {
        var (o, bodies, _) = RunV3((body, turn, n) => n == 1 ? FactsTurn(body) : new A.ModelTurn("not json", Array.Empty<A.ToolCall>(), "stop"), repair: true, retries: 0);
        Assert.Equal("FAILED", o.Unit.Status);
        Assert.Equal("PROTOCOL:REPLY_NOT_JSON", o.Unit.Failure);
        Assert.Equal("PROTOCOL:REPLY_NOT_JSON", o.RepairedFrom);
        Assert.Equal(3, bodies.Count);
    }

    [Fact]
    public void Repair_MissingFacts_GetsATurnToCallTheTool_ThenTheAnswer()
    {
        var (o, bodies, _) = RunV3((body, turn, n) => n == 1 ? Answer(body) : n == 2 ? FactsTurn(body) : Answer(body), repair: true, retries: 0);
        Assert.Equal("COMPLETE", o.Unit.Status);
        Assert.Equal("PROTOCOL:TOOL_FACTS_REQUIRED_NOT_REQUESTED", o.RepairedFrom);
        Assert.Equal(3, bodies.Count);
        Assert.Contains("Call get_line_facts now", bodies[1]);
        using var last = JsonDocument.Parse(bodies[2]);
        Assert.Equal("none", last.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public void Repair_AToolCallAfterTheLastRound_IsAnsweredAsNotRun_ThenTheAnswerIsAsked()
    {
        var (o, bodies, _) = RunV3((body, turn, n) => n <= 4 ? FactsTurn(body) : Answer(body), repair: true, retries: 0);
        Assert.Equal("COMPLETE", o.Unit.Status);
        Assert.Equal("PROTOCOL:TOOL_CALL_AFTER_TOOL_CAP", o.RepairedFrom);
        Assert.Equal(5, bodies.Count);
        Assert.Contains("TOOL_ROUNDS_EXHAUSTED", bodies[4]);
        Assert.Contains("no tool rounds are left", bodies[4]);
        using var last = JsonDocument.Parse(bodies[4]);
        Assert.Equal("none", last.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public void Repair_EvidenceIdsInsteadOfAliases_AreNamedInTheRepairMessage()
    {
        Assert.Contains("never evidence ids such as E1.01", A.RepairMessage("PROTOCOL:EVIDENCE_NOT_SHOWN"));
        Assert.Contains("for every alias from targetFirstAlias to targetLastAlias", A.RepairMessage("PROTOCOL:COVERAGE_INCOMPLETE"));
        foreach (var code in new[] { "PROTOCOL:REPLY_NOT_JSON", "PROTOCOL:EVIDENCE_NOT_SHOWN", "PROTOCOL:TOOL_CALL_AFTER_TOOL_CAP", "PROTOCOL:TOOL_FACTS_REQUIRED_NOT_REQUESTED", "PROTOCOL:X" })
            foreach (var banned in new[] { "heading", "title", "table", "contents" })
                Assert.DoesNotContain(banned, A.RepairMessage(code), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransportRetry_ResendsTheSameBytesOnAProviderFailure_AdmittedByTheBudget_ButNotABoundaryMismatch()
    {
        var (o, bodies, budget) = RunV3((body, turn, n) => n == 1 ? throw new P7F1V6DirectV2.ReplyFailure("TRANSPORT:STREAM_ERROR")
            : n == 2 ? FactsTurn(body) : Answer(body), repair: false, retries: 1);
        Assert.Equal("COMPLETE", o.Unit.Status);
        Assert.Equal(1, o.TransportRetries);
        Assert.Equal(bodies[0], bodies[1]);                                      // the same bytes again
        Assert.Equal(3, budget.Calls);
        var (once, _, _) = RunV3((body, turn, n) => n <= 2 ? throw new P7F1V6DirectV2.ReplyFailure("TRANSPORT:STREAM_ERROR") : Answer(body), repair: false, retries: 1);
        Assert.Equal("CALL:TRANSPORT:STREAM_ERROR", once.Unit.Failure);           // one retry only
        var (boundary, b2, _) = RunV3((body, turn, n) => n == 1 ? throw new P7F1V6DirectV2.ReplyFailure("TRANSPORT:BOUNDARY_SHA_MISMATCH") : Answer(body), repair: false, retries: 1);
        Assert.Equal("CALL:TRANSPORT:BOUNDARY_SHA_MISMATCH", boundary.Unit.Failure);
        Assert.Single(b2);
        var (off, _, _) = RunV3((body, turn, n) => n == 1 ? throw new P7F1V6DirectV2.ReplyFailure("TRANSPORT:STREAM_ERROR") : Answer(body), repair: false, retries: 0);
        Assert.Equal("CALL:TRANSPORT:STREAM_ERROR", off.Unit.Failure);
    }

    [Fact]
    public void FailureHandlingPlan_PinsRetriesAndRepair_AddsSpareCalls_AndVerifies()
    {
        var rel = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        Dictionary<string, object?> Pinned(int r, bool rep) => T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1,
            firstSegments: 2, instruction: P7F1V6DirectD45.InstructionEnglishMainPlain, fullToolRounds: true, toolFactsAnalysis: true, factsVersion: 3, hideLayout: true, transportRetries: r, repairTurn: rep);
        using var a = JsonDocument.Parse(T.Serialize(Pinned(0, false)));
        var bytes = T.Serialize(Pinned(1, true));
        using var b = JsonDocument.Parse(bytes);
        Assert.False(a.RootElement.TryGetProperty("failureHandling", out _));
        Assert.Equal(1, T.RetriesOf(b.RootElement));
        Assert.True(T.RepairOf(b.RootElement));
        Assert.Equal(a.RootElement.GetProperty("arms")[0].GetProperty("budget").GetProperty("maxCalls").GetInt32() + 2 * 3,
            b.RootElement.GetProperty("arms")[0].GetProperty("budget").GetProperty("maxCalls").GetInt32());     // 2 units, 3 spare calls each
        var file = Path.Combine(Path.GetTempPath(), "dhx-fix-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, bytes);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Repair_ARefusalThatIsRepairedDoesNotCountTowardTheEarlyStop_ButAFinalFailureDoes()
    {
        var u = Universe().WithFactsVersion(3);
        var (decide, ctx) = P7F1V6DirectCanary410.Scope(u, "SEG-001");
        var budget = new P7F1V6DirectV2.Budget(60, 5, C.MaxRequestBytes);
        // Three units in a row whose first answer is refused and whose repair succeeds: no halt.
        for (int k = 0; k < 3; k++)
        {
            var n = 0;
            A.AgentModel model = (unit, turn, req) => { n++; return n == 1 ? FactsTurn(req.Body) : n == 2 ? new A.ModelTurn("prose " + WithAnalysis(TargetsOf(req.Body), "x"), Array.Empty<A.ToolCall>(), "stop") : Answer(req.Body); };
            var o = A.RunUnit(A.Arm.ToolFactsAnalysisV3, P7F1V6DirectD45.InstructionEnglishMainPlain, u, "SEG-001", decide, ctx, model, budget, rangeStated: true, jsonObjectAllTurns: true, repairTurn: true, retryDelayMs: 0);
            Assert.Equal("COMPLETE", o.Unit.Status);
        }
        Assert.Null(budget.HaltReason);
        Assert.Equal(0, budget.ContractFailures);
        // Three units that fail even after their repair: the campaign stops.
        for (int k = 0; k < 3; k++)
        {
            var n = 0;
            A.AgentModel model = (unit, turn, req) => { n++; return n == 1 ? FactsTurn(req.Body) : new A.ModelTurn("prose", Array.Empty<A.ToolCall>(), "stop"); };
            A.RunUnit(A.Arm.ToolFactsAnalysisV3, P7F1V6DirectD45.InstructionEnglishMainPlain, u, "SEG-001", decide, ctx, model, budget, rangeStated: true, jsonObjectAllTurns: true, repairTurn: true, retryDelayMs: 0);
        }
        Assert.Equal("CONSECUTIVE_CONTRACT_FAILURES", budget.HaltReason);
        Assert.Equal(3, budget.ContractFailures);
        Assert.Contains("must be a string", A.RepairMessage("PROTOCOL:ANALYSIS_INVALID"));
        Assert.Contains("Shorten analysis", A.RepairMessage("PROTOCOL:REPLY_OVER_CAP"));
    }

    [Fact]
    public void Repair_CoverageIncomplete_NamesTheMissingAliases_AndSaysEverySegmentIsATarget()
    {
        var msg = A.RepairMessage("PROTOCOL:COVERAGE_INCOMPLETE", new[] { "L0430:S1", "L0430:S2", "L0431:S1" });
        Assert.Contains("missing decisions for: L0430:S1, L0430:S2, L0431:S1.", msg);
        Assert.Contains("each segment of a visual line", msg);
        // Long lists are cut and counted; other failures keep their message.
        var many = Enumerable.Range(1, A.MaxMissingNamed + 5).Select(i => $"L{i:0000}:S0").ToArray();
        Assert.Contains("and 5 more", A.RepairMessage("PROTOCOL:COVERAGE_INCOMPLETE", many));
        Assert.Equal(A.RepairMessage("PROTOCOL:REPLY_NOT_JSON"), A.RepairMessage("PROTOCOL:REPLY_NOT_JSON", new[] { "L1:S0" }));
        Assert.Equal(A.RepairMessage("PROTOCOL:COVERAGE_INCOMPLETE"), A.RepairMessage("PROTOCOL:COVERAGE_INCOMPLETE", null));

        // In the loop: an answer that leaves out the last target gets a repair message naming exactly that alias.
        string? missingAlias = null;
        var (o, bodies, _) = RunV3((body, turn, n) =>
        {
            if (n == 1) return FactsTurn(body);
            var t = TargetsOf(body);
            missingAlias = t[^1];
            return n == 2 ? new A.ModelTurn(WithAnalysis(t.Take(t.Count - 1).ToArray(), "x"), Array.Empty<A.ToolCall>(), "stop") : Answer(body);
        }, repair: true, retries: 0);
        Assert.Equal("COMPLETE", o.Unit.Status);
        Assert.Equal("PROTOCOL:COVERAGE_INCOMPLETE", o.RepairedFrom);
        Assert.Contains("missing decisions for: " + missingAlias + ".", bodies[2]);
    }

    [Fact]
    public void MainRepresentativeInstruction_IsTheOwnersFourSentencesVerbatim()
    {
        Assert.Equal(
            "Extract the main headings of the document: the headings that carry its structure (its outline).\n" +
            "The result is a list of the document's main headings in which each heading appears once: for each heading, select the line where the heading itself stands, at the start of the part it names, as its representative. A line that only lists, repeats or points to a heading that stands elsewhere, for example in a table of contents, is not that heading's representative and is not selected.\n" +
            "When you are unsure about how a line relates to other lines, use the tools to check that relationship.\n" +
            "Do not guess: if the evidence does not settle a line, say that it is unresolved instead of choosing.",
            P7F1V6DirectD45.InstructionEnglishMainRepresentative);
        var lines = P7F1V6DirectD45.InstructionEnglishMainRepresentative.Split((char)10);
        Assert.Equal(4, lines.Length);
        var plain = P7F1V6DirectD45.InstructionEnglishMainPlain.Split((char)10);
        Assert.Equal(plain[0], lines[0]);
        Assert.Equal(plain[1], lines[2]);
        Assert.Equal(plain[2], lines[3]);
    }

    [Fact]
    public void MainRepresentativeLinesInstruction_DiffersOnlyBySelectingAllLinesOfAHeading()
    {
        var a = P7F1V6DirectD45.InstructionEnglishMainRepresentative.Split((char)10);
        var b = P7F1V6DirectD45.InstructionEnglishMainRepresentativeLines.Split((char)10);
        Assert.Equal(a.Length, b.Length);
        Assert.Equal(new[] { 1 }, Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]));
        Assert.Equal(a[1].Replace("select the line where the heading itself stands,", "select the lines where the heading itself stands (all lines of a heading that runs over several lines),"), b[1]);
    }

    [Fact]
    public void V3_AnAliasShownInTheDocumentStyleTable_MayBeCitedAsEvidence()
    {
        var u = Universe().WithFactsVersion(3);
        var (decide, ctx) = P7F1V6DirectCanary410.Scope(u, "SEG-001");
        var shown = decide.Concat(ctx.Select(i => u.Lines[i].Alias)).ToHashSet(StringComparer.Ordinal);
        var styleLine = new P7F1V6AgentTools(u.Tools, u.LayoutFacts, factsVersion: 3).DocumentStyles()["styles"]!.AsArray()
            .SelectMany(s => s!["firstLines"]!.AsArray().Select(l => l!["alias"]!.GetValue<string>())).First(a => !shown.Contains(a));
        var n = 0;
        A.AgentModel model = (unit, turn, req) =>
        {
            n++;
            if (n == 1) return FactsTurn(req.Body);
            var t = TargetsOf(req.Body);
            return new A.ModelTurn(new JsonObject
            {
                ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
                ["decisions"] = new JsonArray(t.Select((a, i) => (JsonNode)(i == 0
                    ? new JsonObject { ["alias"] = a, ["decision"] = "SELECT", ["evidenceRefs"] = new JsonArray(styleLine) }
                    : new JsonObject { ["alias"] = a, ["decision"] = "NOT_SELECT" })).ToArray()),
            }.ToJsonString(), Array.Empty<A.ToolCall>(), "stop");
        };
        var o = A.RunUnit(A.Arm.ToolFactsAnalysisV3, P7F1V6DirectD45.InstructionEnglishMainRepresentativeLines, u, "SEG-001", decide, ctx, model,
            new P7F1V6DirectV2.Budget(10, 5, C.MaxRequestBytes), rangeStated: true, jsonObjectAllTurns: true, retryDelayMs: 0);
        Assert.Equal("COMPLETE", o.Unit.Status);
    }

    static A.AgentUnitOutcome RunRepairs(int failuresBeforeGood, int maxRepairs, int budgetCalls)
    {
        var u = Universe().WithFactsVersion(3);
        var (decide, ctx) = P7F1V6DirectCanary410.Scope(u, "SEG-001");
        var n = 0;
        A.AgentModel model = (unit, turn, req) =>
        {
            n++;
            if (n == 1) return FactsTurn(req.Body);
            return n - 1 <= failuresBeforeGood ? new A.ModelTurn("prose, not json", Array.Empty<A.ToolCall>(), "stop") : Answer(req.Body);
        };
        return A.RunUnit(A.Arm.ToolFactsAnalysisV3, P7F1V6DirectD45.InstructionEnglishMainRepresentativeLines, u, "SEG-001", decide, ctx, model,
            new P7F1V6DirectV2.Budget(budgetCalls, 5, C.MaxRequestBytes), rangeStated: true, jsonObjectAllTurns: true, repairTurn: true, retryDelayMs: 0, maxRepairs: maxRepairs);
    }

    [Fact]
    public void Repairs_WithNoLimitOfTheirOwn_KeepGoingUntilTheAnswerIsValid_AndTheArmCallLimitStopsAUnitThatNeverRecovers()
    {
        var ok = RunRepairs(failuresBeforeGood: 4, maxRepairs: -1, budgetCalls: 40);
        Assert.Equal("COMPLETE", ok.Unit.Status);
        Assert.Equal(4, ok.RepairsUsed);
        Assert.Equal("PROTOCOL:REPLY_NOT_JSON", ok.RepairedFrom);
        // The default is still one repair.
        var one = RunRepairs(failuresBeforeGood: 4, maxRepairs: 1, budgetCalls: 40);
        Assert.Equal("FAILED", one.Unit.Status);
        Assert.Equal(1, one.RepairsUsed);
        // A unit that never recovers is stopped by the call limit, not by a loop of its own.
        var never = RunRepairs(failuresBeforeGood: 1000, maxRepairs: -1, budgetCalls: 12);
        Assert.Equal("INCOMPLETE", never.Unit.Status);
        Assert.StartsWith("BUDGET:", never.Unit.IncompleteReason ?? never.Unit.Failure);
    }

    [Fact]
    public void UnlimitedRepairPlan_PinsMaxRepairs_AndVerifies_WhileAOneRepairPlanKeepsItsEarlierShape()
    {
        var rel = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        Dictionary<string, object?> Pinned(int r, int m) => T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1,
            firstSegments: 2, instruction: P7F1V6DirectD45.InstructionEnglishMainRepresentativeLines, fullToolRounds: true, toolFactsAnalysis: true, factsVersion: 3, hideLayout: true,
            transportRetries: r, repairTurn: true, maxRepairs: m);
        using var one = JsonDocument.Parse(T.Serialize(Pinned(1, 1)));
        Assert.False(one.RootElement.GetProperty("failureHandling").TryGetProperty("maxRepairs", out _));
        Assert.Equal(1, T.MaxRepairsOf(one.RootElement));
        var bytes = T.Serialize(Pinned(3, -1));
        using var all = JsonDocument.Parse(bytes);
        Assert.Equal(-1, T.MaxRepairsOf(all.RootElement));
        Assert.Equal(3, T.RetriesOf(all.RootElement));
        var file = Path.Combine(Path.GetTempPath(), "dhx-all-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, bytes);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }

    // ---- D4.12q: prompt-cache markers ----

    [Fact]
    public void CacheMarkers_ChangeOnlyTheContentShape_TheTextIsTheSame_AndTheDefaultIsUnchanged()
    {
        var u = Universe().WithFactsVersion(3);
        var plain = C.FirstRequest(A.Arm.ToolFactsAnalysisV3, u, "SEG-001", true, P7F1V6DirectD45.InstructionEnglishMainRepresentativeLines);
        var marked = C.FirstRequest(A.Arm.ToolFactsAnalysisV3, u, "SEG-001", true, P7F1V6DirectD45.InstructionEnglishMainRepresentativeLines, cacheMarkers: true);
        using var a = JsonDocument.Parse(plain.Body);
        using var b = JsonDocument.Parse(marked.Body);
        for (int i = 0; i < 2; i++)
        {
            var pa = a.RootElement.GetProperty("messages")[i].GetProperty("content");
            var pb = b.RootElement.GetProperty("messages")[i].GetProperty("content");
            Assert.Equal(JsonValueKind.String, pa.ValueKind);
            Assert.Equal(JsonValueKind.Array, pb.ValueKind);
            Assert.Equal(1, pb.GetArrayLength());
            Assert.Equal("text", pb[0].GetProperty("type").GetString());
            Assert.Equal("ephemeral", pb[0].GetProperty("cache_control").GetProperty("type").GetString());
            Assert.Equal(pa.GetString(), P7F1V6Protocol.MessageText(pb));
        }
        // Everything else in the body is the same.
        var na = JsonNode.Parse(plain.Body)!.AsObject(); var nb = JsonNode.Parse(marked.Body)!.AsObject();
        na.Remove("messages"); nb.Remove("messages");
        Assert.Equal(na.ToJsonString(), nb.ToJsonString());
        Assert.NotEqual(plain.Sha256, marked.Sha256);
    }

    static int Marks(JsonElement body) => body.GetProperty("messages").EnumerateArray()
        .Count(m => m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array && c.EnumerateArray().Any(b => b.TryGetProperty("cache_control", out _)));

    [Fact]
    public void CacheMarkers_AThirdMarkGoesOnTheLastHistoryMessageWithText_NeverMoreThanThree_AndTheHistoryIsNotChanged()
    {
        var history = new JsonArray(
            new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "c1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "get_line_facts", ["arguments"] = "{}" } }) },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "c1", ["content"] = "{\"result\":1}" },
            new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "c2", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "get_line_facts", ["arguments"] = "{}" } }) },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "c2", ["content"] = "{\"result\":2}" });
        var before = history.ToJsonString();
        using var d = JsonDocument.Parse(A.Build("S", "{}", history, "auto", cacheMarkers: true, cacheHistoryMark: true).Body);
        var msgs = d.RootElement.GetProperty("messages");
        Assert.Equal(3, Marks(d.RootElement));
        Assert.Equal(JsonValueKind.String, msgs[3].GetProperty("content").ValueKind);            // the earlier tool result is not marked
        Assert.Equal(JsonValueKind.Array, msgs[5].GetProperty("content").ValueKind);             // the last one is
        Assert.Equal("{\"result\":2}", P7F1V6Protocol.MessageText(msgs[5].GetProperty("content")));
        Assert.Equal(before, history.ToJsonString());
        // A repair message after an answer is the last text message: it carries the third mark.
        history.Add(new JsonObject { ["role"] = "assistant", ["content"] = "answer" });
        history.Add(new JsonObject { ["role"] = "user", ["content"] = "Your answer was refused." });
        using var r = JsonDocument.Parse(A.Build("S", "{}", history, "none", cacheMarkers: true, cacheHistoryMark: true).Body);
        Assert.Equal(3, Marks(r.RootElement));
        Assert.Equal(JsonValueKind.Array, r.RootElement.GetProperty("messages")[7].GetProperty("content").ValueKind);
        // No history: two marks. No markers asked: none.
        using var first = JsonDocument.Parse(A.Build("S", "{}", new JsonArray(), "auto", cacheMarkers: true).Body);
        Assert.Equal(2, Marks(first.RootElement));
        using var off = JsonDocument.Parse(A.Build("S", "{}", history, "auto").Body);
        Assert.Equal(0, Marks(off.RootElement));
        // The default (D4.12s) is two marks, whatever the history.
        using var two = JsonDocument.Parse(A.Build("S", "{}", history, "auto", cacheMarkers: true).Body);
        Assert.Equal(2, Marks(two.RootElement));
    }

    [Fact]
    public void CacheMarkers_TheAuditedSenderReadsAMarkedRequest_AndThePlanPinsTheFlag()
    {
        var u = Universe().WithFactsVersion(3);
        var dir = Path.Combine(Path.GetTempPath(), "dhx-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = P7F1V6SafeRawStore.CreateRun(dir);
            var budget = new P7F1V6DirectV2.Budget(10, 5, C.MaxRequestBytes);
            var model = new P7F1V6AgentAuditedModel(new P7F1V6DirectAuditedModel(new P7F1V6OpenRouterTransport(new SseHandler(), TimeSpan.FromSeconds(30)), "test-key", store, budget));
            var req = C.FirstRequest(A.Arm.Auto, u, "SEG-001", cacheMarkers: true);
            Assert.Null(budget.Admit(req));
            var turn = model.Turn("U1", 1, req);
            Assert.NotNull(turn.Content);
            Assert.Equal("SEGMENT", model.Ledger[0].Stage);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }

        var rel = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
        var path = Path.Combine(TestRepository.Root(), rel.Replace('/', Path.DirectorySeparatorChar));
        Dictionary<string, object?> Pinned(bool cache) => T.PinnedValues(path, "TEST_COMMIT", P7F1V6Universe.LayoutSegmentation, true, true, unwrapFence: true, singleArm: true, factsUse: true, reps: 1,
            firstSegments: 2, instruction: P7F1V6DirectD45.InstructionEnglishMainRepresentativeLines, fullToolRounds: true, toolFactsAnalysis: true, factsVersion: 3, hideLayout: true,
            transportRetries: 3, repairTurn: true, maxRepairs: -1, promptCache: cache);
        using var off = JsonDocument.Parse(T.Serialize(Pinned(false)));
        Assert.False(T.PromptCacheOf(off.RootElement));
        var bytes = T.Serialize(Pinned(true));
        using var on = JsonDocument.Parse(bytes);
        Assert.True(T.PromptCacheOf(on.RootElement));
        Assert.NotEqual(off.RootElement.GetProperty("arms")[0].GetProperty("requestPlanSha256").GetString(), on.RootElement.GetProperty("arms")[0].GetProperty("requestPlanSha256").GetString());
        var file = Path.Combine(Path.GetTempPath(), "dhx-cacheplan-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, bytes);
            Assert.Empty(T.Verify(file, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT", path));
        }
        finally { File.Delete(file); }
    }
}
