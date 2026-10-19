using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V6Qualification.P7;
using A = DocxHeaderExtractor.V6Qualification.P7.P7F1V6DirectAgent;
using C = DocxHeaderExtractor.V6Qualification.P7.P7F1V6DirectRepeatCanary;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.11i (offline): the stated target range, segments chosen by fixed rules, five repetitions, one document per plan.
/// Scripted models and fake HTTP only. No network.</summary>
public sealed class P7F1V6DirectD411iTests : IDisposable
{
    const string Key = "sk-or-TEST-not-a-real-key-d411i-0000";
    readonly string scratch = Path.Combine(Path.GetTempPath(), "dhx-d411i-" + Guid.NewGuid().ToString("N"));

    public P7F1V6DirectD411iTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static readonly Dictionary<string, Lazy<P7F1V6Universe>> Universes = C.Docs.ToDictionary(kv => kv.Key, kv => new Lazy<P7F1V6Universe>(() =>
        P7F1V6Universe.Rebuild(TestRepository.Root(), kv.Value.RelativePath, P7F1V6DirectD45.ReadGoldPin(TestRepository.Root(), kv.Key).SourceSha256, out var f)
        ?? throw new InvalidOperationException(f)));

    static P7F1V6Universe U(string authority) => Universes[authority].Value;
    static P7F1V6Universe U95 => U("SRC-095");

    static JsonElement PayloadOf(P7F1V6Protocol.PreparedRequest r)
    {
        using var d = JsonDocument.Parse(r.Body);
        using var p = JsonDocument.Parse(d.RootElement.GetProperty("messages")[1].GetProperty("content").Text()!);
        return p.RootElement.Clone();
    }

    // ================= the stated range =================

    // The D4.11h first requests (plan a46eaeba), before the range was stated.
    static readonly Dictionary<(string Arm, string Segment), string> D411hFirst = new()
    {
        [("CONTROL", "SEG-001")] = "1b1e2fa465415bdd78dff500c21a52b5bcc27c3afe2029b7da31d53b546cd05f",
        [("CONTROL", "SEG-008")] = "88c464b808795b08e1d6862160df83660ac82b37c8477ad0100dc7c6b0235ca6",
        [("PREFETCH", "SEG-001")] = "c674ffbed6d2c1cdeb1edb46f5d0180ac39c2732f83b4ad2d90a48e6d740d2b0",
        [("PREFETCH", "SEG-008")] = "a3818f9c31c7f57cf3a3a3fd19b8f73bc40edaf2c5d4a3ad42c7e458eb8cbfb5",
    };

    [Theory]
    [InlineData("CONTROL", "SEG-001")]
    [InlineData("CONTROL", "SEG-008")]
    [InlineData("PREFETCH", "SEG-001")]
    [InlineData("PREFETCH", "SEG-008")]
    public void TheStatedRange_IsTwoFieldsAndOneSentence_AndNothingElse(string arm, string segment)
    {
        var mode = arm == "CONTROL" ? A.Arm.Auto : A.Arm.Prefetch;
        var u = U95;
        var withRange = C.FirstRequest(mode, u, segment);
        var (decide, _) = P7F1V6DirectCanary410.Scope(u, segment);
        using var body = JsonDocument.Parse(withRange.Body);
        var payloadText = body.RootElement.GetProperty("messages")[1].GetProperty("content").Text()!;
        var fields = $"\"targetFirstAlias\":\"{decide[0]}\",\"targetLastAlias\":\"{decide[^1]}\",";
        Assert.Contains(fields, payloadText);
        Assert.Equal(1, Regex_Count(payloadText, "targetFirstAlias"));
        // Directly after targetCount.
        Assert.Contains($"\"targetCount\":{decide.Length}," + fields, payloadText);
        var names = PayloadOf(withRange).EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "instruction", "instructionSha256", "documentSha256", "stage", "unitId", "targetCount", "targetFirstAlias", "targetLastAlias", "targets", "context" },
            names.Take(10));
        // Take the two fields and the sentence out again and the D4.11h request comes back, byte for byte.
        var oldPrompt = A.Prompt(mode, rangeStated: true).Replace(A.RangeSentenceText, "");
        Assert.Equal(A.Prompt(mode), oldPrompt);
        var rebuilt = A.Build(oldPrompt, payloadText.Replace(fields, ""), new JsonArray(), "auto");
        Assert.Equal(D411hFirst[(arm, segment)], rebuilt.Sha256);
        Assert.NotEqual(D411hFirst[(arm, segment)], withRange.Sha256);
    }

    static int Regex_Count(string s, string needle) => System.Text.RegularExpressions.Regex.Matches(s, System.Text.RegularExpressions.Regex.Escape(needle)).Count;

    [Fact]
    public void TheStatedRangePrompt_SaysWhichLinesAreDecidedAndWhichAreNever_AndIsPinned()
    {
        foreach (var arm in new[] { A.Arm.Auto, A.Arm.Prefetch })
        {
            var p = A.Prompt(arm, rangeStated: true);
            Assert.Contains("targetFirstAlias and targetLastAlias are the first and last of the targets", p);
            Assert.Contains("the context lines before the first and after the last are never decided", p);
            Assert.Equal(2, Regex_Count(p, "targetFirstAlias"));            // named twice in the one sentence
        }
        Assert.Equal(A.AutoPromptRangeSha256, A.Sha(A.AutoPromptRange));
        Assert.Equal(A.PrefetchPromptRangeSha256, A.Sha(A.PrefetchPromptRange));
        // The default (no range) prompts are untouched.
        Assert.Equal(A.AutoPromptSha256, A.Sha(A.AutoPrompt));
        Assert.Equal(A.PrefetchPromptSha256, A.Sha(A.PrefetchPrompt));
    }

    static A.ModelTurn Answer(string content) => new(content, Array.Empty<A.ToolCall>(), "stop");

    static string Decisions(IEnumerable<string> aliases) => new JsonObject
    {
        ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
        ["decisions"] = new JsonArray(aliases.Select(a => (JsonNode)new JsonObject { ["alias"] = a, ["decision"] = "NOT_SELECT" }).ToArray()),
    }.ToJsonString();

    [Fact]
    public void ADecisionForTheContextLineAfterTheLastTarget_IsStillRefused_NeverDropped()
    {
        var (decide, ctx) = P7F1V6DirectCanary410.Scope(U95, "SEG-001");
        var afterLast = U95.Lines[U95.Lines.ToList().FindIndex(l => l.Alias == decide[^1]) + 1].Alias;      // L0090 in D4.11h
        Assert.Equal("L0090:S0", afterLast);
        var outcome = A.RunUnit(A.Arm.Prefetch, P7F1V6DirectD45.Instruction, U95, "SEG-001", decide, ctx,
            (unit, turn, req) => Answer(Decisions(decide.Append(afterLast))), new P7F1V6DirectV2.Budget(100, 100, 2_000_000), rangeStated: true);
        Assert.Equal("FAILED", outcome.Unit.Status);
        Assert.Equal("PROTOCOL:UNEXPECTED_ALIAS", outcome.Unit.Failure);
        Assert.All(outcome.Unit.Decisions.Values, v => Assert.Equal("UNRESOLVED", v));
    }

    // ================= segment selection =================

    /// <summary>The rules, written again here from the published description and the Gold, independently of the code under test.</summary>
    static List<(string Segment, string RulePrefix)> ExpectedPicks(string authority)
    {
        var u = U(authority);
        var gold = P7F1V6DirectD45.ReadGoldPin(TestRepository.Root(), authority);
        var atoms = C.GoldAtoms(TestRepository.Root(), gold);
        var keys = u.Lines.Select(l => P7F1QEvidenceTools.RepeatKey(l.Text)).ToArray();
        var count = keys.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        var rows = new List<(string Seg, int Gold, int Listed, int GoldListed, int NonGoldListed)>();
        for (int core = 0, n = 0; core < u.Lines.Count; core += 48, n++)
        {
            var idx = Enumerable.Range(core, Math.Min(u.Lines.Count, core + 48) - core).ToArray();
            bool Listed(int i) => keys[i].Length > 0 && count[keys[i]] > 1;
            rows.Add(($"SEG-{n:000}", idx.Count(i => atoms.Contains(u.Lines[i].Alias)), idx.Count(Listed),
                idx.Count(i => Listed(i) && atoms.Contains(u.Lines[i].Alias)), idx.Count(i => Listed(i) && !atoms.Contains(u.Lines[i].Alias))));
        }
        var picks = new List<(string, string)>();
        foreach (var a in C.Docs[authority].Anchors) picks.Add((a, "ANCHOR"));
        void Add(string rule, IEnumerable<(string Seg, int Gold, int Listed, int GoldListed, int NonGoldListed)> ordered)
        {
            var best = ordered.FirstOrDefault(r => picks.All(p => p.Item1 != r.Seg));
            if (best.Seg is not null) picks.Add((best.Seg, rule));
        }
        Add("A", rows.Where(r => r.Gold > 0).OrderByDescending(r => r.GoldListed).ThenBy(r => r.Seg, StringComparer.Ordinal));
        Add("B", rows.Where(r => r.Gold == 0 && r.Listed > 0).OrderByDescending(r => r.Listed).ThenBy(r => r.Seg, StringComparer.Ordinal));
        Add("C", rows.Where(r => r.GoldListed > 0 && r.NonGoldListed > 0).OrderByDescending(r => Math.Min(r.GoldListed, r.NonGoldListed)).ThenBy(r => r.Seg, StringComparer.Ordinal));
        Add("D", rows.Where(r => r.Gold > 0).OrderBy(r => r.Listed).ThenBy(r => r.Seg, StringComparer.Ordinal));
        return picks;
    }

    [Theory]
    [InlineData("SRC-095")]
    [InlineData("SRC-042")]
    public void TheCodeAppliesTheRulesAsDescribed_ToTheParserAndTheGold(string authority)
    {
        var u = U(authority);
        var atoms = C.GoldAtoms(TestRepository.Root(), P7F1V6DirectD45.ReadGoldPin(TestRepository.Root(), authority));
        var picks = C.SelectSegments(C.Docs[authority], C.Survey(u, atoms));
        var expected = ExpectedPicks(authority);
        Assert.Equal(expected.Select(e => e.Segment), picks.Select(p => p.Segment));
        Assert.Equal(expected.Select(e => e.RulePrefix), picks.Select(p => p.Rule.Split(' ', ':')[0]));
        Assert.Equal(picks.Count, picks.Select(p => p.Segment).Distinct().Count());
        if (authority == "SRC-095") Assert.Equal(new[] { "SEG-001", "SEG-008" }, picks.Take(2).Select(p => p.Segment));       // the anchors come first
        Assert.InRange(picks.Count, 4, 6);
    }

    // ================= the plans =================

    static readonly Dictionary<string, Lazy<byte[]>> Plans = C.Docs.ToDictionary(kv => kv.Key, kv => new Lazy<byte[]>(() => C.Serialize(C.PinnedValues(TestRepository.Root(), "TEST_COMMIT", kv.Key))));

    [Theory]
    [InlineData("SRC-095")]
    [InlineData("SRC-042")]
    public void Plan_PinsTheDocument_TheSegmentsByRule_TheRepetitions_AndTheDerivedBudget(string authority)
    {
        using var doc = JsonDocument.Parse(Plans[authority].Value);
        var p = doc.RootElement;
        var d = C.Docs[authority];
        Assert.Equal(authority, p.GetProperty("authorityId").GetString());
        Assert.Equal(d.RelativePath, p.GetProperty("document").GetString());
        Assert.Equal(d.Publicity, p.GetProperty("documentPublicity").GetString());
        Assert.Equal(U(authority).UniverseSha256, p.GetProperty("universeSha256").GetString());
        Assert.Equal(P7F1V6DirectD45.ReadGoldPin(TestRepository.Root(), authority).UniverseSha256, p.GetProperty("universeSha256").GetString());   // the rebuild equals the Gold universe
        Assert.False(p.GetProperty("approvalToSend").GetBoolean());
        Assert.Equal(0, p.GetProperty("retries").GetInt32());
        Assert.Equal(5, p.GetProperty("repetitions").GetProperty("perArmAndSegment").GetInt32());
        var segs = p.GetProperty("segments").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Equal(ExpectedPicks(authority).Select(e => e.Segment), segs);
        var units = segs.Length * 5;
        var arms = p.GetProperty("arms").EnumerateArray().ToList();
        Assert.Equal(new[] { "CONTROL", "PREFETCH" }, arms.Select(a => a.GetProperty("arm").GetString()));
        double total = 0;
        Assert.All(arms, a =>
        {
            var b = a.GetProperty("budget");
            Assert.Equal(units + Math.Max(3, units / 5), b.GetProperty("maxCalls").GetInt32());
            // The ceiling covers the reserve of every planned first request (5 repetitions) plus the spare at the byte cap, plus 10%.
            var sum = a.GetProperty("firstRequests").EnumerateArray().Sum(f => P7F1V6Direct.AttemptUsd(f.GetProperty("bytes").GetInt32())) * 5;
            var spare = (b.GetProperty("maxCalls").GetInt32() - units) * P7F1V6Direct.AttemptUsd(131072);
            Assert.Equal(Math.Ceiling((sum + spare) * 1.1 * 100) / 100, b.GetProperty("ceilingUsd").GetDouble(), 6);
            Assert.All(a.GetProperty("firstRequests").EnumerateArray(), f => Assert.InRange(f.GetProperty("bytes").GetInt32(), 1, 131072));
            Assert.Equal(segs.Length, a.GetProperty("firstRequests").GetArrayLength());
            total += b.GetProperty("ceilingUsd").GetDouble();
        });
        Assert.Equal(arms.Sum(a => a.GetProperty("budget").GetProperty("maxCalls").GetInt32()), p.GetProperty("budget").GetProperty("maxCalls").GetInt32());
        Assert.Equal(Math.Round(total, 2), p.GetProperty("budget").GetProperty("ceilingUsd").GetDouble(), 6);
        Assert.Equal(A.AutoPromptRangeSha256, arms[0].GetProperty("promptSha256").GetString());
        Assert.Equal(A.PrefetchPromptRangeSha256, arms[1].GetProperty("promptSha256").GetString());
        // Every first request states the range; none mentions the Gold.
        foreach (var arm in C.Arms)
            foreach (var seg in segs)
            {
                var r = C.FirstRequest(arm.Mode, U(authority), seg);
                Assert.Contains("targetFirstAlias", r.Body);
                Assert.DoesNotContain("gold", r.Body, StringComparison.OrdinalIgnoreCase);
            }
        var rules = p.GetProperty("segmentSelection").GetProperty("picks").EnumerateArray().Select(x => x.GetProperty("rule").GetString()!).ToArray();
        Assert.StartsWith("A:", rules.First(r => r.StartsWith("A:")));
        Assert.Contains("Never part of any request", p.GetProperty("goldForScorerOnly").GetProperty("use").GetString());
        Assert.Contains("not a significance test", string.Join("|", p.GetProperty("readingRules").EnumerateArray().Select(x => x.GetString())));
        var frozen = p.GetProperty("frozen");
        Assert.Equal(authority, frozen.GetProperty("authorityId").GetString());
        Assert.False(frozen.GetProperty("provider").GetProperty("allowFallbacks").GetBoolean());
        Assert.Equal(new[] { "alibaba/us-east-1" }, frozen.GetProperty("provider").GetProperty("ignore").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void Plan_Verify_RefusesAnotherSha_AnotherCommit_ADriftedValue_AndAnUnknownAuthority()
    {
        var plan = Path.Combine(scratch, "plan.json");
        File.WriteAllBytes(plan, Plans["SRC-095"].Value);
        var sha = Convert.ToHexStringLower(SHA256.HashData(Plans["SRC-095"].Value));
        Assert.Empty(C.Verify(TestRepository.Root(), plan, sha, "TEST_COMMIT"));
        Assert.Contains("PLAN_SHA_NOT_CONFIRMED", C.Verify(TestRepository.Root(), plan, new string('0', 64), "TEST_COMMIT"));
        Assert.Contains("CODE_COMMIT_DIFFERS", C.Verify(TestRepository.Root(), plan, sha, "OTHER"));
        foreach (var edit in new Action<JsonObject>[]
        {
            n => n["segments"] = new JsonArray("SEG-001"),
            n => n["segmentSelection"]!["rules"] = new JsonArray("a rule that looks at the model's answers"),
            n => n["frozen"]!["provider"]!["only"] = new JsonArray("other-provider"),
        })
        {
            var node = JsonNode.Parse(Plans["SRC-095"].Value)!.AsObject();
            edit(node);
            var bytes = Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
            var drift = Path.Combine(scratch, "drift-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllBytes(drift, bytes);
            Assert.Contains("PINNED_VALUES_DIFFER", C.Verify(TestRepository.Root(), drift, Convert.ToHexStringLower(SHA256.HashData(bytes)), "TEST_COMMIT"));
        }
        var other = JsonNode.Parse(Plans["SRC-095"].Value)!.AsObject();
        other["authorityId"] = "SRC-999";
        var ob = Encoding.UTF8.GetBytes(other.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        var op = Path.Combine(scratch, "other.json");
        File.WriteAllBytes(op, ob);
        Assert.Contains("AUTHORITY_NOT_IN_PLAN_REGISTRY", C.Verify(TestRepository.Root(), op, Convert.ToHexStringLower(SHA256.HashData(ob)), "TEST_COMMIT"));
    }

    [Fact]
    public void TheAuthorizationDraft_ForAnotherDocument_NamesThatDocumentsPublicity()
    {
        var plan = Path.Combine(scratch, "plan42.json");
        File.WriteAllBytes(plan, Plans["SRC-042"].Value);
        var root = SafeRoot("safe-draft");
        using var draft = JsonDocument.Parse(P7F1V6DirectAuthorization.Draft(plan, root));
        Assert.Equal(C.Docs["SRC-042"].Publicity, draft.RootElement.GetProperty("dataSharing").GetProperty("documentPublicity").GetString());
        Assert.Equal("SRC-042", draft.RootElement.GetProperty("authorityId").GetString());
        Assert.Equal("PENDING_USER_APPROVAL", draft.RootElement.GetProperty("status").GetString());
        // A plan without the field keeps the RFC wording (the earlier plans).
        var old = Path.Combine(scratch, "old.json");
        var node = JsonNode.Parse(Plans["SRC-095"].Value)!.AsObject();
        node.Remove("documentPublicity");
        File.WriteAllText(old, node.ToJsonString());
        using var oldDraft = JsonDocument.Parse(P7F1V6DirectAuthorization.Draft(old, root));
        Assert.Equal("IETF RFC 9114, published publicly by the IETF", oldDraft.RootElement.GetProperty("dataSharing").GetProperty("documentPublicity").GetString());
    }

    // ================= running =================

    string SafeRoot(string name)
    {
        var root = Path.Combine(scratch, name);
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsWindows()) P7F1V6SafeRawStore.RestrictRootToCurrentUser(root);
        return root;
    }

    [Fact]
    public void RunArms_GoesRepetitionBySegmentByArm_ForTheChosenSegments_WithIdenticalRequests()
    {
        var segs = new[] { "SEG-001", "SEG-008", "SEG-031" };
        var seen = new List<(string Arm, string Unit, string Sha)>();
        A.AgentModel Model(string arm) => (unit, turn, req) =>
        {
            seen.Add((arm, unit, req.Sha256));
            return Answer(Decisions(PayloadOf(req).GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!)));
        };
        var budgets = C.Arms.ToDictionary(a => a.Arm, _ => new P7F1V6DirectV2.Budget(100, 100, 2_000_000));
        var state = C.RunArms(U95, segs, C.Arms.ToDictionary(a => a.Arm, a => Model(a.Arm)), budgets, parallelism: 1);
        Assert.All(state, s => { Assert.Equal(15, s.Units.Count); Assert.All(s.Units, u => Assert.Equal("COMPLETE", u.Outcome.Unit.Status)); });
        Assert.Equal(30, seen.Count);
        Assert.Equal(new[] { "CONTROL SEG-001#r1", "PREFETCH SEG-001#r1", "CONTROL SEG-008#r1", "PREFETCH SEG-008#r1", "CONTROL SEG-031#r1", "PREFETCH SEG-031#r1" },
            seen.Take(6).Select(x => x.Arm + " " + x.Unit));
        foreach (var arm in new[] { "CONTROL", "PREFETCH" })
            foreach (var seg in segs)
                Assert.Single(seen.Where(x => x.Arm == arm && x.Unit.StartsWith(seg)).Select(x => x.Sha).Distinct());
        Assert.Equal(Enumerable.Range(1, 5).SelectMany(r => segs.Select(_ => r)), state[0].Units.Select(u => u.Rep));
    }

    sealed class Handler(Func<string, string> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (Bodies) Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(body), Encoding.UTF8, "text/event-stream") };
        }
    }

    static string Sse(string requestBody, string content)
    {
        var prompt = Encoding.UTF8.GetByteCount(requestBody) / 4;
        var sb = new StringBuilder();
        void Data(object o) => sb.Append("data: ").Append(JsonSerializer.Serialize(o)).Append("\n\n");
        var head = new { id = "gen-mock", provider = "Alibaba", model = "qwen/qwen3.7-flash" };
        Data(new { head.id, head.provider, head.model, choices = new[] { new { index = 0, delta = (object)new { content } } } });
        Data(new { head.id, head.provider, head.model, choices = new[] { new { index = 0, delta = (object)new { }, finish_reason = "stop", native_finish_reason = "stop" } } });
        Data(new { head.id, head.provider, head.model, choices = Array.Empty<object>(),
            usage = new { prompt_tokens = prompt, completion_tokens = 300, cost = P7F1V6Usage.Cost(prompt, 300), completion_tokens_details = new { reasoning_tokens = 200 } } });
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    [Fact]
    public void Execute_SendsNothingWithoutAuthorization_ThenRunsEveryPlannedFirstPost_Once()
    {
        var plan = Path.Combine(scratch, "plan.json");
        File.WriteAllBytes(plan, Plans["SRC-095"].Value);
        var sha = Convert.ToHexStringLower(SHA256.HashData(Plans["SRC-095"].Value));
        using var doc = JsonDocument.Parse(Plans["SRC-095"].Value);
        var segs = doc.RootElement.GetProperty("segments").GetArrayLength();
        var root = SafeRoot("safe");
        var http = new Handler(body =>
        {
            using var d = JsonDocument.Parse(body);
            using var p = JsonDocument.Parse(d.RootElement.GetProperty("messages")[1].GetProperty("content").Text()!);
            return Sse(body, Decisions(p.RootElement.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!)));
        });
        Assert.StartsWith("AUTHORIZATION_REFUSED:", Assert.Throws<InvalidOperationException>(() =>
            C.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", Path.Combine(scratch, "none.json"), Key, http, root)).Message, StringComparison.Ordinal);
        Assert.Empty(http.Bodies);

        var auth = Path.Combine(scratch, "auth.json");
        File.WriteAllBytes(auth, P7F1V6DirectAuthorization.Approve(P7F1V6DirectAuthorization.Draft(plan, root), "TEST_USER", "TEST approval",
            "TEST: allow", P7F1V6RoutePolicy.RouteAllowed(P7F1V6Protocol.ProviderTag).Blocking, DateTime.UtcNow));
        var arms = C.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", auth, Key, http, root);

        Assert.Equal(segs * 5 * 2, http.Bodies.Count);
        Assert.All(arms, a =>
        {
            Assert.Equal(segs * 5, a.Ledger.Count);
            Assert.All(a.Units, u => Assert.Equal("COMPLETE", u.Outcome.Unit.Status));
            Assert.Matches(@"^SEG-\d{3}#r[1-5]$", a.Ledger[0].UnitId);
        });
        Assert.All(http.Bodies, b => Assert.Contains("targetFirstAlias", b));
        Assert.Equal(segs * 5, http.Bodies.Count(b => b.Contains("\\\"observations\\\":{")));
        Assert.DoesNotContain(http.Bodies, b => b.Contains(Key, StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => C.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", auth, Key, http, root));
        Assert.Equal(segs * 5 * 2, http.Bodies.Count);
    }
}
