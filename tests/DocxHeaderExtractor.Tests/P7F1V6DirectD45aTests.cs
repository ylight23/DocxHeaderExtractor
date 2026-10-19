using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.V6Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.5a (offline): the live authorization gate, one campaign per plan, the safe root checks, retention of the run summary,
/// and the early stop on consecutive contract failures. Every HTTP response is from a fake handler; no connection is opened. Approvals here
/// are written by the test in a scratch directory, playing the user; they are not approvals of anything real.</summary>
public sealed class P7F1V6DirectD45aTests : IDisposable
{
    const string Key = "sk-or-TEST-not-a-real-key-d45a-00000";
    readonly string scratch = Path.Combine(Path.GetTempPath(), "dhx-d45a-" + Guid.NewGuid().ToString("N"));

    public P7F1V6DirectD45aTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        foreach (var j in junctions) try { Process.Start(new ProcessStartInfo("cmd", $"/c rmdir \"{j}\"") { UseShellExecute = false, CreateNoWindow = true })!.WaitForExit(); } catch { }
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    readonly List<string> junctions = new();

    // ---------------- helpers ----------------

    static readonly Lazy<List<List<string>>> Claims095 = new(() =>
    {
        using var g = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRepository.Root(), P7F1V6DirectD45.ReadGoldPin(TestRepository.Root()).Path)));
        return g.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray()
            .Select(c => c.GetProperty("sourceParts").EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!).ToList()).ToList();
    });

    static readonly Lazy<byte[]> PlanBytes = new(() => P7F1V6DirectD45.Serialize(P7F1V6DirectD45.PinnedValues(TestRepository.Root(), "TEST_COMMIT")));

    string Plan(string name = "plan.json")
    {
        var path = Path.Combine(scratch, name);
        File.WriteAllBytes(path, PlanBytes.Value);
        return path;
    }

    static string Sha(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    string SafeRoot(string name)
    {
        var root = Path.Combine(scratch, name);
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsWindows()) P7F1V6SafeRawStore.RestrictRootToCurrentUser(root);
        return root;
    }

    string Approved(string planPath, string safeRoot, bool acceptOpenItems = true)
    {
        var path = Path.Combine(scratch, "auth-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, P7F1V6DirectAuthorization.Approve(P7F1V6DirectAuthorization.Draft(planPath, safeRoot), "TEST_USER", "TEST approval text",
            "TEST: allow accepted for a public RFC", acceptOpenItems ? P7F1V6RoutePolicy.RouteAllowed(P7F1V6Protocol.ProviderTag).Blocking : Array.Empty<string>(),
            DateTime.UtcNow));
        return path;
    }

    sealed class Handler(Func<string, string> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(body), Encoding.UTF8, "application/json") };
        }
    }

    static string[] DecideOf(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        using var p = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[1].GetProperty("content").Text()!);
        return p.RootElement.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
    }

    static string OracleChat(string requestBody)
    {
        var d = DecideOf(requestBody);
        var owned = d.ToHashSet(StringComparer.Ordinal);
        var atoms = Claims095.Value.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);
        var content = JsonSerializer.Serialize(new
        {
            protocolVersion = P7F1V6DirectV2.ResponseVersion,
            status = "COMPLETE",
            decisions = d.Select(a => new { alias = a, decision = atoms.Contains(a) ? "SELECT" : "NOT_SELECT", evidenceRefs = new[] { a } }),
            headings = Claims095.Value.Where(g => g.Any(owned.Contains)).Select(g => new { parts = g }),
        });
        var prompt = Encoding.UTF8.GetByteCount(requestBody) / 4;
        return JsonSerializer.Serialize(new
        {
            id = "gen-mock", model = "qwen/qwen3.7-flash", provider = "Alibaba",
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
            usage = new { prompt_tokens = prompt, completion_tokens = 400, cost = P7F1V6Usage.Cost(prompt, 400), completion_tokens_details = new { reasoning_tokens = 300 } },
        });
    }

    // ---------------- authorization ----------------

    [Fact]
    public void Authorization_Draft_IsPending_AndAuthorizesNothing()
    {
        var plan = Plan();
        var root = SafeRoot("root");
        var draft = Path.Combine(scratch, "draft.json");
        File.WriteAllBytes(draft, P7F1V6DirectAuthorization.Draft(plan, root));
        var f = P7F1V6DirectAuthorization.Check(draft, plan, Sha(plan), root);
        Assert.Contains("AUTHORIZATION_NOT_APPROVED:PENDING_USER_APPROVAL", f);
        Assert.Contains("AUTHORIZATION_DATA_COLLECTION_UNDECIDED", f);
        Assert.Contains(f, x => x.StartsWith("AUTHORIZATION_OPEN_ITEM_NOT_ACCEPTED:alibaba:ZDR decision by the user:WAIVER_NOT_VERIFIED", StringComparison.Ordinal));
        Assert.Contains(f, x => x.StartsWith("AUTHORIZATION_OPEN_ITEM_NOT_ACCEPTED:alibaba:logging of request content", StringComparison.Ordinal));
        Assert.Contains("AUTHORIZATION_APPROVER_MISSING", f);
        using var d = JsonDocument.Parse(File.ReadAllText(draft));
        Assert.Equal(100, d.RootElement.GetProperty("maxPosts").GetInt32());
        Assert.Equal(1.0, d.RootElement.GetProperty("ceilingUsd").GetDouble());
        Assert.Equal(1, d.RootElement.GetProperty("campaigns").GetInt32());
        Assert.False(d.RootElement.GetProperty("dataSharing").GetProperty("zdr").GetBoolean());
    }

    [Fact]
    public void Authorization_Approved_PassesEveryCheck()
    {
        var plan = Plan();
        var root = SafeRoot("root");
        Assert.Empty(P7F1V6DirectAuthorization.Check(Approved(plan, root), plan, Sha(plan), root));
    }

    [Fact]
    public void Authorization_EditedAfterApproval_IsRefused()
    {
        var plan = Plan();
        var root = SafeRoot("root");
        var auth = Approved(plan, root);
        File.WriteAllText(auth, File.ReadAllText(auth).Replace("\"maxPosts\": 100", "\"maxPosts\": 200"));
        var f = P7F1V6DirectAuthorization.Check(auth, plan, Sha(plan), root);
        Assert.Contains("AUTHORIZATION_DIGEST_MISMATCH", f);
        Assert.Contains("AUTHORIZATION_FIELD_DIFFERS:maxPosts", f);
    }

    [Fact]
    public void Authorization_WithoutAcceptingTheOpenDataItems_IsRefused()
    {
        var plan = Plan();
        var root = SafeRoot("root");
        var f = P7F1V6DirectAuthorization.Check(Approved(plan, root, acceptOpenItems: false), plan, Sha(plan), root);
        var blocking = P7F1V6RoutePolicy.RouteAllowed(P7F1V6Protocol.ProviderTag).Blocking;
        Assert.Equal(3, blocking.Count);   // ZDR not supported, ZDR decision not verified, logging through OpenRouter unknown
        Assert.Equal(blocking.Select(b => "AUTHORIZATION_OPEN_ITEM_NOT_ACCEPTED:" + b),
            f.Where(x => x.StartsWith("AUTHORIZATION_OPEN_ITEM_NOT_ACCEPTED:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Authorization_CannotBeApprovedWithAnOpenDecision()
    {
        var draft = P7F1V6DirectAuthorization.Draft(Plan(), SafeRoot("root"));
        Assert.Throws<ArgumentException>(() => P7F1V6DirectAuthorization.Approve(draft, "TEST_USER", "ok", P7F1V6DirectAuthorization.PendingDecision,
            Array.Empty<string>(), DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => P7F1V6DirectAuthorization.Approve(draft, "", "ok", "allow", Array.Empty<string>(), DateTime.UtcNow));
    }

    [Fact]
    public void Authorization_ForAnotherPlanOrRoot_IsRefused()
    {
        var plan = Plan();
        var root = SafeRoot("root");
        var auth = Approved(plan, root);
        var other = Plan("other.json");
        File.AppendAllText(other, " ");
        Assert.Contains("AUTHORIZATION_PLAN_SHA_DIFFERS", P7F1V6DirectAuthorization.Check(auth, other, Sha(other), root));
        Assert.Contains("AUTHORIZATION_FIELD_DIFFERS:safeRoot", P7F1V6DirectAuthorization.Check(auth, plan, Sha(plan), SafeRoot("root2")));
    }

    [Fact]
    public void Execute_WithoutAnApprovedAuthorization_SendsNothing()
    {
        var plan = Plan();
        var root = SafeRoot("root");
        var http = new Handler(OracleChat);
        var draft = Path.Combine(scratch, "draft.json");
        File.WriteAllBytes(draft, P7F1V6DirectAuthorization.Draft(plan, root));
        foreach (var auth in new[] { Path.Combine(scratch, "missing.json"), draft })
        {
            var e = Assert.Throws<InvalidOperationException>(() =>
                P7F1V6DirectD45.Execute(TestRepository.Root(), plan, Sha(plan), "TEST_COMMIT", auth, Key, http, root));
            Assert.StartsWith("AUTHORIZATION_REFUSED:", e.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0, http.Calls);
        Assert.False(File.Exists(P7F1V6DirectD45.ConsumedMarker(root, Sha(plan))));
    }

    [Fact]
    public void Execute_RunsOneCampaignPerPlan()
    {
        var plan = Plan();
        var root = SafeRoot("root");
        var auth = Approved(plan, root);
        var http = new Handler(OracleChat);
        var first = P7F1V6DirectD45.Execute(TestRepository.Root(), plan, Sha(plan), "TEST_COMMIT", auth, Key, http, root);
        Assert.Equal("COMPLETE", first.Outcome.Status);
        Assert.True(first.Score.Official, first.Score.Reason);
        var calls = http.Calls;
        Assert.True(File.Exists(P7F1V6DirectD45.ConsumedMarker(root, Sha(plan))));

        var e = Assert.Throws<InvalidOperationException>(() =>
            P7F1V6DirectD45.Execute(TestRepository.Root(), plan, Sha(plan), "TEST_COMMIT", auth, Key, http, root));
        Assert.Equal("CAMPAIGN_ALREADY_CONSUMED", e.Message);
        Assert.Equal(calls, http.Calls);
    }

    // ---------------- safe root ----------------

    [Fact]
    public void SafeRoot_PreparedRootPasses_AndEveryFlawIsNamed()
    {
        var repo = TestRepository.Root();
        Assert.Empty(P7F1V6DirectD45.SafeRootFailures(repo, SafeRoot("good")));
        Assert.Equal(new[] { "SAFE_ROOT_MISSING" }, P7F1V6DirectD45.SafeRootFailures(repo, Path.Combine(scratch, "absent")));
        Assert.Contains("SAFE_ROOT_INSIDE_REPOSITORY", P7F1V6DirectD45.SafeRootFailures(repo, repo));
        if (!OperatingSystem.IsWindows()) return;
        var open = Path.Combine(scratch, "open");
        Directory.CreateDirectory(open);
        Assert.Contains("ACL_INHERITS_FROM_PARENT", P7F1V6DirectD45.SafeRootFailures(repo, open));
    }

    [Fact]
    public void SafeRoot_WithAJunctionInItsPath_IsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Path.Combine(scratch, "target");
        Directory.CreateDirectory(Path.Combine(target, "inner"));
        var link = Path.Combine(scratch, "link");
        var p = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        junctions.Add(link);
        var root = Path.Combine(link, "inner");
        P7F1V6SafeRawStore.RestrictRootToCurrentUser(root);
        Assert.Contains(P7F1V6DirectD45.SafeRootFailures(TestRepository.Root(), root), f => f.StartsWith("REPARSE_POINT_IN_PATH:", StringComparison.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(target, "inner")));
    }

    // ---------------- retention of the run summary ----------------

    string RunDir(string root, string? summaryArtifact, bool foreign = false)
    {
        var store = P7F1V6SafeRawStore.CreateRun(root);
        store.Put(Encoding.UTF8.GetBytes("{\"raw\":1}"));
        if (summaryArtifact is not null)
            File.WriteAllText(Path.Combine(store.RunDirectory, P7F1V6Retention.SummaryFile), JsonSerializer.Serialize(new { artifact = summaryArtifact, status = "COMPLETE" }));
        if (foreign) File.WriteAllText(Path.Combine(store.RunDirectory, "notes.txt"), "not the store's");
        P7F1V6Retention.RecordReview(store.RunDirectory, DateTime.UtcNow);
        return store.RunDirectory;
    }

    [Fact]
    public void Retention_DeletesARunWithItsSummary_WhenDue_AndNothingElse()
    {
        var root = SafeRoot("retention");
        var withSummary = RunDir(root, P7F1V6Retention.SummaryArtifact);
        var wrongSummary = RunDir(root, "something.else");
        var foreign = RunDir(root, P7F1V6Retention.SummaryArtifact, foreign: true);
        var marker = Path.Combine(root, "consumed-" + new string('a', 64) + ".json");
        File.WriteAllText(marker, "{}");

        Assert.Empty(P7F1V6Retention.Due(root, DateTime.UtcNow));            // reviewed today: not due yet
        var later = DateTime.UtcNow.AddDays(40);
        var removed = P7F1V6Retention.Sweep(root, later);

        Assert.Equal(new[] { withSummary }, removed);
        Assert.False(Directory.Exists(withSummary));
        Assert.True(Directory.Exists(wrongSummary));
        Assert.True(Directory.Exists(foreign));
        Assert.True(File.Exists(marker));
        Assert.Throws<InvalidOperationException>(() => P7F1V6Retention.DeleteRun(root, foreign));
    }

    // ---------------- early stop ----------------

    static string A(int i) => $"L{i:0000}:S0";

    [Fact]
    public void EarlyStop_ThreeContractFailuresInARow_StopTheCampaign_ASuccessResetsTheCount()
    {
        var lines = Enumerable.Range(0, 100).Select(i => new P7F1V6Direct.Line(A(i), 1, $"Dòng {i}")).ToList();
        var budget = new P7F1V6DirectV2.Budget(100, 100, 2_000_000);
        var seen = new List<string>();
        var run = P7F1V6DirectV2.Run("Trích xuất tất cả heading", "sha", lines, (unit, round, req) =>
        {
            seen.Add(unit);
            if (unit == "SEG-002")
            {
                using var doc = JsonDocument.Parse(req.Body);
                using var p = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[1].GetProperty("content").Text()!);
                var d = p.RootElement.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
                return JsonSerializer.Serialize(new { protocolVersion = P7F1V6DirectV2.ResponseVersion, status = "COMPLETE",
                    decisions = d.Select(a => new { alias = a, decision = "NOT_SELECT", evidenceRefs = new[] { a } }), headings = Array.Empty<object>() });
            }
            return "{\"decisions\":[]}";
        }, budget, overlap: 2, perUnit: 10);

        // SEG-000 and SEG-001 fail, SEG-002 passes (count resets), SEG-003..005 fail: the third in a row halts the campaign.
        Assert.Equal(new[] { "SEG-000", "SEG-001", "SEG-002", "SEG-003", "SEG-004", "SEG-005" }, seen);
        Assert.Equal("CONSECUTIVE_CONTRACT_FAILURES", budget.HaltReason);
        Assert.Equal(5, budget.ContractFailures);
        Assert.Equal("BUDGET:HALTED:CONSECUTIVE_CONTRACT_FAILURES", run.Units.Single(u => u.UnitId == "SEG-006").Failure);
        Assert.Equal("INCOMPLETE", run.Status);
        Assert.Equal("NOT_SELECT", run.Decisions[A(25)]);
        Assert.Equal("UNRESOLVED", run.Decisions[A(95)]);
    }

    [Fact]
    public void Plan_PinsTheD45aValues()
    {
        using var doc = JsonDocument.Parse(PlanBytes.Value);
        var p = doc.RootElement;
        Assert.Equal(100, p.GetProperty("budget").GetProperty("maxCalls").GetInt32());
        Assert.Equal(1.0, p.GetProperty("budget").GetProperty("ceilingUsd").GetDouble());
        Assert.Equal(3, p.GetProperty("earlyStop").GetProperty("consecutiveContractFailures").GetInt32());
        Assert.True(p.GetProperty("liveAuthorization").GetProperty("required").GetBoolean());
        Assert.Equal(48, p.GetProperty("unit").GetInt32());
        Assert.Equal(P7F1V6DirectV2.SystemPromptSha256, p.GetProperty("systemPromptSha256").GetString());
    }
}
