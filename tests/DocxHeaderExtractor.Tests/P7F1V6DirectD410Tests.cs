using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;
using F = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectTwoStage.SelectFormat;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.10 (offline): the B/C development canary on SEG-001, SEG-008 and SEG-009. Scripted models and fake HTTP only.</summary>
public sealed class P7F1V6DirectD410Tests : IDisposable
{
    const string Key = "sk-or-TEST-not-a-real-key-d410-0000";
    readonly string scratch = Path.Combine(Path.GetTempPath(), "dhx-d410-" + Guid.NewGuid().ToString("N"));

    public P7F1V6DirectD410Tests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static readonly Lazy<P7F1V6Universe> U = new(() =>
        P7F1V6Universe.Rebuild(TestRepository.Root(), P7F1V6DirectD45.Document, P7F1V6DirectD45.ReadGoldPin(TestRepository.Root()).SourceSha256, out var f)
        ?? throw new InvalidOperationException(f));

    static readonly Lazy<byte[]> PlanBytes = new(() => P7F1V6DirectCanary410.Serialize(P7F1V6DirectCanary410.PinnedValues(TestRepository.Root(), "TEST_COMMIT")));

    static JsonElement Payload(string body)
    {
        using var doc = JsonDocument.Parse(body);
        using var p = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return p.RootElement.Clone();
    }

    static string[] TargetsOf(string body) => Payload(body).GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();

    static string Compact(IReadOnlyList<string> t, int? count = null)
    {
        var blocks = new JsonArray();
        for (int s = 0; s < t.Count; s += 8)
        {
            var len = Math.Min(8, t.Count - s);
            blocks.Add(new JsonObject { ["start"] = s, ["firstAlias"] = t[s], ["lastAlias"] = t[s + len - 1], ["codes"] = new string('N', len) });
        }
        return new JsonObject { ["protocolVersion"] = P7F1V6DirectTwoStage.SelectCompactVersion, ["status"] = "COMPLETE", ["targetCount"] = count ?? t.Count,
            ["blocks"] = blocks, ["selectedEvidence"] = new JsonArray() }.ToJsonString();
    }

    static string Objects(IReadOnlyList<string> t) => new JsonObject
    {
        ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
        ["decisions"] = new JsonArray(t.Select(a => (JsonNode)new JsonObject { ["alias"] = a, ["decision"] = "NOT_SELECT" }).ToArray()),
    }.ToJsonString();

    static string ArmOf(string body) => Payload(body).GetProperty("responseVersion").GetString() == P7F1V6DirectTwoStage.SelectCompactVersion ? "C" : "B";

    static P7F1V6DirectV2.Budget Big() => new(100, 100, 2_000_000);

    // ---------------- plan ----------------

    [Fact]
    public void Plan_PinsBothArms_TheirBudgets_AndOneRequestPlanShaEach()
    {
        using var doc = JsonDocument.Parse(PlanBytes.Value);
        var p = doc.RootElement;
        Assert.Equal(P7F1V6DirectCanary410.PlanArtifact, p.GetProperty("artifact").GetString());
        Assert.Equal(new[] { "SEG-001", "SEG-008", "SEG-009" }, p.GetProperty("segments").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(18, p.GetProperty("budget").GetProperty("maxCalls").GetInt32());
        Assert.Equal(0.6, p.GetProperty("budget").GetProperty("ceilingUsd").GetDouble(), 9);
        var arms = p.GetProperty("arms").EnumerateArray().ToList();
        Assert.Equal(new[] { "B", "C" }, arms.Select(a => a.GetProperty("arm").GetString()));
        Assert.All(arms, a =>
        {
            Assert.Equal(9, a.GetProperty("budget").GetProperty("maxCalls").GetInt32());
            Assert.Equal(0.30, a.GetProperty("budget").GetProperty("ceilingUsd").GetDouble(), 9);
            Assert.Equal(3, a.GetProperty("firstRequests").GetArrayLength());
            Assert.Equal(64, a.GetProperty("requestPlanSha256").GetString()!.Length);
        });
        Assert.Equal(P7F1V6DirectTwoStage.SelectObjectsPromptSha256, arms[0].GetProperty("promptSha256").GetString());
        Assert.Equal(P7F1V6DirectTwoStage.SelectCompactPromptSha256, arms[1].GetProperty("promptSha256").GetString());
        Assert.NotEqual(arms[0].GetProperty("requestPlanSha256").GetString(), arms[1].GetProperty("requestPlanSha256").GetString());
    }

    [Fact]
    public void Plan_Verifies_AndRefusesEdits()
    {
        var path = Path.Combine(scratch, "plan.json");
        File.WriteAllBytes(path, PlanBytes.Value);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(PlanBytes.Value));
        Assert.Empty(P7F1V6DirectCanary410.Verify(TestRepository.Root(), path, sha, "TEST_COMMIT"));
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"SEG-008\"", "\"SEG-007\""));
        Assert.Contains("PINNED_VALUES_DIFFER", P7F1V6DirectCanary410.Verify(TestRepository.Root(), path, sha, "TEST_COMMIT"));
    }

    // ---------------- requests ----------------

    [Fact]
    public void Requests_BothArms_SameTargetsAndContext_CarryTargetCount_OnlyCompactNumbersTargets()
    {
        foreach (var seg in P7F1V6DirectCanary410.Segments)
        {
            var b = Payload(P7F1V6DirectCanary410.FirstRequest(F.Objects, U.Value, seg).Body);
            var c = Payload(P7F1V6DirectCanary410.FirstRequest(F.Compact, U.Value, seg).Body);
            Assert.Equal(48, b.GetProperty("targetCount").GetInt32());
            Assert.Equal(48, c.GetProperty("targetCount").GetInt32());
            Assert.Equal(b.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()),
                c.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()));
            Assert.Equal(b.GetProperty("context").GetRawText(), c.GetProperty("context").GetRawText());
            Assert.Equal(b.GetProperty("instruction").GetString(), c.GetProperty("instruction").GetString());
            Assert.False(b.GetProperty("targets")[0].TryGetProperty("i", out _));
            Assert.Equal(47, c.GetProperty("targets")[47].GetProperty("i").GetInt32());
        }
    }

    [Fact]
    public void Compact_ACountCopiedFromThePromptExample_IsRefused()
    {
        var (decide, ctx) = P7F1V6DirectCanary410.Scope(U.Value, "SEG-001");
        var shown = ctx.ToDictionary(i => U.Value.Lines[i].Alias, i => i);
        var r = P7F1V6DirectTwoStage.ParseSelect(F.Compact, Compact(decide, count: 12), shown, decide);
        Assert.Equal("TARGET_COUNT_MISMATCH", r.Failure);
        Assert.Contains("use the request's own count", P7F1V6DirectTwoStage.SelectCompactPrompt, StringComparison.Ordinal);
    }

    // ---------------- runner ----------------

    [Fact]
    public void Runner_RunsOnlyTheThreeSegments_InterleavedPerArm()
    {
        var calls = new List<string>();
        P7F1V6DirectV2.ReplyModel B = (u, r, req) => { calls.Add("B:" + u); return Objects(TargetsOf(req.Body)); };
        P7F1V6DirectV2.ReplyModel C = (u, r, req) => { calls.Add("C:" + u); return Compact(TargetsOf(req.Body)); };
        var state = P7F1V6DirectCanary410.RunArms(U.Value, new Dictionary<string, P7F1V6DirectV2.ReplyModel> { ["B"] = B, ["C"] = C },
            new Dictionary<string, P7F1V6DirectV2.Budget> { ["B"] = Big(), ["C"] = Big() });
        Assert.Equal(new[] { "B:SEG-001", "C:SEG-001", "B:SEG-008", "C:SEG-008", "B:SEG-009", "C:SEG-009" }, calls);
        Assert.All(state, s => Assert.All(s.Units, x => Assert.Equal("COMPLETE", x.Status)));
        Assert.All(state, s => Assert.Equal(48 * 3, s.Units.Sum(x => x.Decisions.Count)));
    }

    [Fact]
    public void Runner_ArmsAreIsolated_FailuresInBDoNotMoveCsEarlyStop()
    {
        var budgets = new Dictionary<string, P7F1V6DirectV2.Budget> { ["B"] = P7F1V6DirectCanary410.NewArmBudget(), ["C"] = P7F1V6DirectCanary410.NewArmBudget() };
        var state = P7F1V6DirectCanary410.RunArms(U.Value, new Dictionary<string, P7F1V6DirectV2.ReplyModel>
        {
            ["B"] = (u, r, req) => "{\"decisions\":[]}",
            ["C"] = (u, r, req) => Compact(TargetsOf(req.Body)),
        }, budgets);
        Assert.Equal("CONSECUTIVE_CONTRACT_FAILURES", budgets["B"].HaltReason);
        Assert.Null(budgets["C"].HaltReason);
        Assert.Equal(0, budgets["C"].ContractFailures);
        Assert.All(state.Single(s => s.Arm == "C").Units, x => Assert.Equal("COMPLETE", x.Status));
    }

    [Fact]
    public void Runner_ArmBUsingItsWholeBudget_LeavesCUntouched()
    {
        var budgets = new Dictionary<string, P7F1V6DirectV2.Budget> { ["B"] = P7F1V6DirectCanary410.NewArmBudget(), ["C"] = P7F1V6DirectCanary410.NewArmBudget() };
        var needs = JsonSerializer.Serialize(new { protocolVersion = P7F1V6DirectTwoStage.SelectObjectsVersion, status = "NEEDS_EVIDENCE",
            retrieve = new[] { new { kind = "SEARCH", text = "HTTP" } } });
        P7F1V6DirectCanary410.RunArms(U.Value, new Dictionary<string, P7F1V6DirectV2.ReplyModel>
        {
            ["B"] = (u, r, req) => r < 3 ? needs : Objects(TargetsOf(req.Body)),
            ["C"] = (u, r, req) => Compact(TargetsOf(req.Body)),
        }, budgets);
        Assert.Equal(9, budgets["B"].Calls);
        Assert.Equal(3, budgets["C"].Calls);
        Assert.True(budgets["B"].ReservedUsd <= P7F1V6DirectCanary410.CeilingUsdPerArm);
    }

    // ---------------- execute over fake HTTP ----------------

    sealed class Handler(Func<string, string> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(body), Encoding.UTF8, "application/json") };
        }
    }

    static string Chat(string content, string requestBody)
    {
        var prompt = Encoding.UTF8.GetByteCount(requestBody) / 4;
        return JsonSerializer.Serialize(new
        {
            id = "gen-mock", model = "qwen/qwen3.7-flash", provider = "Alibaba",
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
            usage = new { prompt_tokens = prompt, completion_tokens = 300, cost = P7F1V6Usage.Cost(prompt, 300), completion_tokens_details = new { reasoning_tokens = 200 } },
        });
    }

    string SafeRoot(string name)
    {
        var root = Path.Combine(scratch, name);
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsWindows()) P7F1V6SafeRawStore.RestrictRootToCurrentUser(root);
        return root;
    }

    [Fact]
    public void Execute_RunsSixPosts_WithSeparateLedgersAndStores_OnlyAfterAuthorization()
    {
        var plan = Path.Combine(scratch, "plan.json");
        File.WriteAllBytes(plan, PlanBytes.Value);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(PlanBytes.Value));
        var root = SafeRoot("safe");
        var http = new Handler(body => Chat(ArmOf(body) == "C" ? Compact(TargetsOf(body)) : Objects(TargetsOf(body)), body));

        var missing = Path.Combine(scratch, "none.json");
        Assert.StartsWith("AUTHORIZATION_REFUSED:", Assert.Throws<InvalidOperationException>(() =>
            P7F1V6DirectCanary410.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", missing, Key, http, root)).Message, StringComparison.Ordinal);
        Assert.Empty(http.Bodies);

        var auth = Path.Combine(scratch, "auth.json");
        File.WriteAllBytes(auth, P7F1V6DirectAuthorization.Approve(P7F1V6DirectAuthorization.Draft(plan, root), "TEST_USER", "TEST approval",
            "TEST: allow", P7F1V6RoutePolicy.RouteAllowed(P7F1V6Protocol.ProviderTag).Blocking, DateTime.UtcNow));
        var arms = P7F1V6DirectCanary410.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", auth, Key, http, root);

        Assert.Equal(6, http.Bodies.Count);
        Assert.Equal(new[] { "B", "C", "B", "C", "B", "C" }, http.Bodies.Select(ArmOf));
        Assert.All(arms, a => Assert.Equal(3, a.Ledger.Count));
        Assert.NotEqual(arms[0].SafeRunDirectory, arms[1].SafeRunDirectory);
        Assert.All(arms, a => Assert.All(a.Units, x => Assert.Equal("COMPLETE", x.Status)));
        Assert.Throws<InvalidOperationException>(() => P7F1V6DirectCanary410.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", auth, Key, http, root));
        Assert.Equal(6, http.Bodies.Count);
    }
}
