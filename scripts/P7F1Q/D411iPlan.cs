using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;
using A = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectAgent;
using C = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectRepeatCanary;

// Issue #9 D4.11i: the repeat canary (CONTROL vs PREFETCH, 5 repetitions, segments chosen by fixed rules), one document per plan.
// d411i-plan writes the pinned plan and a dry-run with scripted models (no network). d411i-run sends requests and needs its own plan SHA,
// budget and owner approval.
internal static class D411iPlan
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string Git(string repoRoot, string args)
    {
        var psi = new ProcessStartInfo("git", args) { WorkingDirectory = repoRoot, RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o.Trim();
    }

    static string[] Targets(P7F1V6Protocol.PreparedRequest req)
    {
        using var doc = JsonDocument.Parse(req.Body);
        using var p = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return p.RootElement.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
    }

    static string Final(IReadOnlyList<string> targets) => new JsonObject
    {
        ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
        ["decisions"] = new JsonArray(targets.Select(a => (JsonNode)new JsonObject { ["alias"] = a, ["decision"] = "NOT_SELECT" }).ToArray()),
    }.ToJsonString();

    public static void Plan(string repoRoot, string artifactDir, string authority)
    {
        if (!C.Docs.TryGetValue(authority, out var doc)) { Console.WriteLine("D411i PLAN REFUSED: unknown authority " + authority); Environment.ExitCode = 2; return; }
        if (Git(repoRoot, "status --porcelain -- src scripts tests").Length > 0) { Console.WriteLine("D411i PLAN REFUSED: uncommitted code"); Environment.ExitCode = 2; return; }
        var head = Git(repoRoot, "rev-parse HEAD");
        var bytes = C.Serialize(C.PinnedValues(repoRoot, head, authority));
        var planPath = Path.Combine(artifactDir, C.PlanFileName(authority));
        File.WriteAllBytes(planPath, bytes);
        var planSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var verify = C.Verify(repoRoot, planPath, planSha, head);

        var gold = P7F1V6DirectD45.ReadGoldPin(repoRoot, authority);
        var u = P7F1V6Universe.Rebuild(repoRoot, doc.RelativePath, gold.SourceSha256, out var why) ?? throw new InvalidOperationException(why);
        using var plan = JsonDocument.Parse(bytes);
        var segs = plan.RootElement.GetProperty("segments").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var posts = plan.RootElement.GetProperty("arms")[0].GetProperty("budget").GetProperty("maxCalls").GetInt32();
        var ceilings = plan.RootElement.GetProperty("arms").EnumerateArray().ToDictionary(a => a.GetProperty("arm").GetString()!, a => a.GetProperty("budget").GetProperty("ceilingUsd").GetDouble());
        var pages = u.Lines.Select(l => l.Page).Distinct().ToArray();
        A.ModelTurn Calls(IEnumerable<(string Name, object Args)> c, int turn) =>
            new(null, c.Select((x, k) => new A.ToolCall($"dry_{turn}_{k}", x.Name, JsonSerializer.Serialize(x.Args))).ToArray(), "tool_calls");

        var models = new (string Name, A.AgentModel Model)[]
        {
            ("ANSWER_WITHOUT_TOOLS", (unit, turn, req) => new A.ModelTurn(Final(Targets(req)), Array.Empty<A.ToolCall>(), "stop")),
            ("TOOL_CALL_ON_ABOUT_ONE_UNIT_IN_TEN (an overview on turn 1 of every tenth unit)", (unit, turn, req) =>
                turn == 1 && unit.Sum(c => (int)c) % 10 == 0
                    ? Calls(new (string, object)[] { (P7F1V6AgentTools.DocumentOverview, new { }) }, turn)
                    : new A.ModelTurn(Final(Targets(req)), Array.Empty<A.ToolCall>(), "stop")),
            ("LARGEST_RESULTS (8 full page layouts per turn, three turns, then answer)", (unit, turn, req) => turn <= A.MaxToolTurns
                ? Calls(Enumerable.Range(0, 8).Select(k => (P7F1V6AgentTools.PageLayout, (object)new { page = pages[(turn * 8 + k) % pages.Length], offset = 0, limit = 40 })), turn)
                : new A.ModelTurn(Final(Targets(req)), Array.Empty<A.ToolCall>(), "stop")),
        };
        var sims = models.Select(m =>
        {
            var budgets = C.Arms.ToDictionary(a => a.Arm, a => new P7F1V6DirectV2.Budget(posts, ceilings[a.Arm], C.MaxRequestBytes));
            var state = C.RunArms(u, segs, C.Arms.ToDictionary(a => a.Arm, _ => m.Model), budgets);
            return new
            {
                simulation = m.Name,
                arms = state.Select(s => new
                {
                    arm = s.Arm, posts = s.Budget.Calls, reservedUsd = Math.Round(s.Budget.ReservedUsd, 4), ceilingUsd = ceilings[s.Arm], halt = s.Budget.HaltReason,
                    refusals = s.Budget.Refusals.GroupBy(r => r).Select(g => new { code = g.Key, count = g.Count() }),
                    units = s.Units.Count, complete = s.Units.Count(x => x.Outcome.Unit.Status == "COMPLETE"),
                    stoppedByBudget = s.Units.Count(x => x.Outcome.Unit.Failure?.StartsWith("BUDGET:") == true),
                    maxRequestBytes = s.Units.SelectMany(x => x.Outcome.Turns.Select(t => t.RequestBytes)).DefaultIfEmpty(0).Max(),
                }),
            };
        }).ToList();

        var report = new
        {
            artifact = "p7.f1.v6.d4.11i-repeat-canary-dryrun.v1",
            authority, providerCalls = 0,
            planFile = Path.GetFileName(planPath), planSha256 = planSha, planVerifiesNow = verify.Count == 0, codeCommit = head,
            firstRequests = C.Arms.SelectMany(a => segs.Select(s =>
            {
                var r = C.FirstRequest(a.Mode, u, s);
                return new { arm = a.Arm, segment = s, sha256 = r.Sha256, bytes = r.Bytes, reserveUsd = Math.Round(P7F1V6Direct.AttemptUsd(r.Bytes), 6) };
            })),
            simulations = sims,
            notes = new[]
            {
                "Scripted models only; no provider call. They show the loop, the caps and the reserve, not what Qwen will do.",
                "The ceiling is a bound on the reserve, derived from the planned first requests plus the spare posts; the expected spend is far lower.",
            },
        };
        File.WriteAllText(Path.Combine(artifactDir, $"p7.f1.v6.d4.11i-repeat-canary-dryrun.{authority}.v1.json"), JsonSerializer.Serialize(report, Indented) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"{authority}: plan {planSha} verifies={verify.Count == 0} commit={head}");
        Console.WriteLine("segments: " + string.Join(", ", segs) + $" | posts per arm {posts} | ceilings {string.Join(", ", ceilings.Select(kv => kv.Key + " $" + kv.Value))}");
        foreach (var f in report.firstRequests) Console.WriteLine(JsonSerializer.Serialize(f));
        foreach (var s in sims)
            foreach (var a in s.arms)
                Console.WriteLine($"{s.simulation} | {a.arm}: posts {a.posts}/{posts}, reserve {a.reservedUsd}/{a.ceilingUsd}, halt {a.halt ?? "none"}, complete {a.complete}/{a.units}, stoppedByBudget {a.stoppedByBudget}, maxBytes {a.maxRequestBytes}");
    }

    /// <summary>The live campaign. Needs an APPROVED authorization for the plan. The key is never printed.</summary>
    public static void Live(string repoRoot, string planPath, string confirmedSha, string authorizationPath, string safeRoot)
    {
        safeRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(safeRoot));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(planPath));
        var planCommit = doc.RootElement.GetProperty("codeCommit").GetString()!;
        var changed = Git(repoRoot, $"diff --name-only {planCommit} HEAD -- src scripts tests");
        var dirty = Git(repoRoot, "status --porcelain -- src scripts tests");
        var current = changed.Length == 0 && dirty.Length == 0 ? planCommit : Git(repoRoot, "rev-parse HEAD");
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) { Console.WriteLine("D411i RUN REFUSED: OPENROUTER_API_KEY not set"); Environment.ExitCode = 2; return; }
        var arms = C.Execute(repoRoot, planPath, confirmedSha, current, authorizationPath, key, P7F1V6OpenRouterTransport.CreateProductionHandler(), safeRoot);
        foreach (var a in arms)
        {
            var requests = Path.Combine(a.SafeRunDirectory, "requests");
            Directory.CreateDirectory(requests);
            foreach (var x in a.Units)
                for (int t = 0; t < x.Outcome.Unit.Requests.Count; t++)
                    File.WriteAllText(Path.Combine(requests, $"{x.Outcome.Unit.UnitId}-r{x.Rep}-turn-{t + 1}.{x.Outcome.Unit.Requests[t].Sha256}.json"), x.Outcome.Unit.Requests[t].Body, new UTF8Encoding(false));
            var summary = new
            {
                artifact = P7F1V6Retention.SummaryArtifact, canary = C.PlanArtifactPrefix, planSha256 = confirmedSha, a.Arm,
                a.Calls, a.ReservedUsd, a.ActualUsd, a.HaltReason, a.Refusals,
                units = a.Units.Select(x => new
                {
                    x.Rep, x.Outcome.Unit.UnitId, x.Outcome.Unit.Status, x.Outcome.Unit.Calls, x.Outcome.Unit.Failure, x.Outcome.Unit.IncompleteReason,
                    decisions = x.Outcome.Unit.Decisions, x.Outcome.Turns, toolCalls = x.Outcome.ToolCalls, x.Outcome.SuccessfulToolCalls, x.Outcome.Scope, x.Outcome.Evidence,
                }),
                ledger = a.Ledger,
            };
            File.WriteAllText(Path.Combine(a.SafeRunDirectory, P7F1V6Retention.SummaryFile), JsonSerializer.Serialize(summary, Indented) + "\n", new UTF8Encoding(false));
            Console.WriteLine($"arm {a.Arm}: posts {a.Calls}, halt {a.HaltReason ?? "none"}, complete {a.Units.Count(x => x.Outcome.Unit.Status == "COMPLETE")}/{a.Units.Count}; summary in {Path.GetFileName(a.SafeRunDirectory)}");
        }
    }
}
