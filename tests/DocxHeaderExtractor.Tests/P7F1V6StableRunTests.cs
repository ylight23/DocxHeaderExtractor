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
        using var p = JsonDocument.Parse(d.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
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
            using var p = JsonDocument.Parse(d.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
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
        using var p = JsonDocument.Parse(d.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return p.RootElement.GetProperty(key).GetString()!;
    }

    [Fact]
    public void ToolFactsArm_HasNoObservationsInThePayload_ARefusedAnswerWithoutTheFactsCall_AndAcceptsOnceTheFactsHaveBeenRead()
    {
        var u = Universe();
        // No observations in the payload; eight tools declared; the prompt is the TOOLFACTS one.
        var first = C.FirstRequest(A.Arm.ToolFacts, u, "SEG-001", true, P7F1V6DirectD45.InstructionEnglishAll);
        using (var d = JsonDocument.Parse(first.Body))
        using (var p = JsonDocument.Parse(d.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!))
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
        using var p = JsonDocument.Parse(d.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
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
        Assert.Equal("ANALYSIS_INVALID", Parse(WithAnalysis(targets, new string('x', P7F1V6DirectTwoStage.MaxAnalysisChars + 1)), true).Failure);
        Assert.True(Parse(WithAnalysis(targets, new string('x', P7F1V6DirectTwoStage.MaxAnalysisChars)), true).Ok);
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
}
