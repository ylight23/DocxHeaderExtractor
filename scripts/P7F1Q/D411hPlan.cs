using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;
using A = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectAgent;
using C = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectPrefetchCanary;

// Issue #9 D4.11h: the prefetch canary (CONTROL vs PREFETCH, 3 repetitions) on SEG-001 and SEG-008. d411h-plan writes the pinned plan and a dry-run with
// scripted models (no network). d411h-run sends requests and needs its own plan SHA, budget and owner approval.
internal static class D411hPlan
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

    static A.ModelTurn Calls(IEnumerable<(string Name, object Args)> calls, int turn) =>
        new(null, calls.Select((c, k) => new A.ToolCall($"dry_{turn}_{k}", c.Name, JsonSerializer.Serialize(c.Args))).ToArray(), "tool_calls");

    public static void Plan(string repoRoot, string artifactDir)
    {
        if (Git(repoRoot, "status --porcelain -- src scripts tests").Length > 0) { Console.WriteLine("D411h PLAN REFUSED: uncommitted code"); Environment.ExitCode = 2; return; }
        var head = Git(repoRoot, "rev-parse HEAD");
        var bytes = C.Serialize(C.PinnedValues(repoRoot, head));
        var planPath = Path.Combine(artifactDir, C.PlanArtifact + ".json");
        File.WriteAllBytes(planPath, bytes);
        var planSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var verify = C.Verify(repoRoot, planPath, planSha, head);

        var gold = P7F1V6DirectD45.ReadGoldPin(repoRoot);
        var u = P7F1V6Universe.Rebuild(repoRoot, P7F1V6DirectD45.Document, gold.SourceSha256, out var why) ?? throw new InvalidOperationException(why);
        var pages = u.Lines.Select(l => l.Page).Distinct().ToArray();

        var control = C.Segments.Select(s => (Segment: s, Request: P7F1V6DirectAgentCanary.FirstRequest(A.Arm.Auto, u, s))).ToArray();
        var controlMatches = control.All(c => C.D411gAutoFirstRequests[c.Segment] == c.Request.Sha256);

        var models = new (string Name, A.AgentModel Model)[]
        {
            ("ANSWER_WITHOUT_TOOLS", (unit, turn, req) => new A.ModelTurn(Final(Targets(req)), Array.Empty<A.ToolCall>(), "stop")),
            ("TOOL_CALL_ON_EVERY_TURN (an overview each of 3 turns, then answer)", (unit, turn, req) => turn <= A.MaxToolTurns
                ? Calls(new (string, object)[] { (P7F1V6AgentTools.DocumentOverview, new { }) }, turn)
                : new A.ModelTurn(Final(Targets(req)), Array.Empty<A.ToolCall>(), "stop")),
            ("LARGEST_RESULTS (8 full page layouts per turn, three turns, then answer)", (unit, turn, req) => turn <= A.MaxToolTurns
                ? Calls(Enumerable.Range(0, 8).Select(k => (P7F1V6AgentTools.PageLayout, (object)new { page = pages[(turn * 8 + k) % pages.Length], offset = 0, limit = 40 })), turn)
                : new A.ModelTurn(Final(Targets(req)), Array.Empty<A.ToolCall>(), "stop")),
        };
        var sims = models.Select(m =>
        {
            var budgets = C.Arms.ToDictionary(a => a.Arm, _ => C.NewArmBudget());
            var state = C.RunArms(u, C.Arms.ToDictionary(a => a.Arm, _ => m.Model), budgets);
            return new
            {
                simulation = m.Name,
                arms = state.Select(s => new
                {
                    arm = s.Arm, posts = s.Budget.Calls, reservedUsd = Math.Round(s.Budget.ReservedUsd, 6), halt = s.Budget.HaltReason, refusals = s.Budget.Refusals,
                    units = s.Units.Select(x => new
                    {
                        x.Rep, x.Outcome.Unit.UnitId, x.Outcome.Unit.Status, x.Outcome.Unit.Failure, posts = x.Outcome.Unit.Calls,
                        maxRequestBytes = x.Outcome.Turns.Select(t => t.RequestBytes).DefaultIfEmpty(0).Max(), toolMessageBytes = x.Outcome.Evidence.ToolMessageBytes,
                        observationTargetsListed = x.Outcome.Evidence.ObservationTargetsListed, observationBytes = x.Outcome.Evidence.ObservationBytes,
                        observationsComplete = x.Outcome.Evidence.ObservationsComplete,
                    }),
                }),
            };
        }).ToList();

        var report = new
        {
            artifact = "p7.f1.v6.d4.11h-prefetch-canary-dryrun.v1",
            providerCalls = 0,
            planFile = Path.GetFileName(planPath), planSha256 = planSha, planVerifiesNow = verify.Count == 0, codeCommit = head,
            controlEqualsD411gAutoFirstRequests = controlMatches,
            firstRequests = C.Arms.SelectMany(a => C.Segments.Select(s =>
            {
                var r = P7F1V6DirectAgentCanary.FirstRequest(a.Mode, u, s);
                return new { arm = a.Arm, segment = s, sha256 = r.Sha256, bytes = r.Bytes, reserveUsd = Math.Round(P7F1V6Direct.AttemptUsd(r.Bytes), 6) };
            })),
            worstCaseReservePerArmUsd = Math.Round(C.PostsPerArm * P7F1V6Direct.AttemptUsd(C.MaxRequestBytes), 6),
            simulations = sims,
            notes = new[]
            {
                "Scripted models only; no provider call. They show the loop, the caps and the reserve, not what Qwen will do.",
                "Each arm has 9 posts and $0.25: 6 first posts (2 segments x 3 repetitions) and 3 for tool turns. If a model called tools in every unit the budget would stop later units INCOMPLETE (BUDGET:CALL_LIMIT); the second simulation shows it.",
            },
        };
        File.WriteAllText(Path.Combine(artifactDir, "p7.f1.v6.d4.11h-prefetch-canary-dryrun.v1.json"), JsonSerializer.Serialize(report, Indented) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"plan {planSha} verifies={verify.Count == 0} commit={head} controlEqualsD411gAuto={controlMatches}");
        foreach (var f in report.firstRequests) Console.WriteLine(JsonSerializer.Serialize(f));
        foreach (var s in sims)
            foreach (var a in s.arms)
                Console.WriteLine($"{s.simulation} | {a.arm}: posts {a.posts}, reserve {a.reservedUsd}, halt {a.halt ?? "none"}; " +
                    string.Join("; ", a.units.Select(x => $"r{x.Rep} {x.UnitId} {x.Status} {x.Failure} posts={x.posts} maxBytes={x.maxRequestBytes}")));
    }

    /// <summary>The live campaign. Not run in D4.11h's preparation: it needs an APPROVED authorization for the plan. The key is never printed.</summary>
    public static void Live(string repoRoot, string planPath, string confirmedSha, string authorizationPath, string safeRoot)
    {
        safeRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(safeRoot));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(planPath));
        var planCommit = doc.RootElement.GetProperty("codeCommit").GetString()!;
        var changed = Git(repoRoot, $"diff --name-only {planCommit} HEAD -- src scripts tests");
        var dirty = Git(repoRoot, "status --porcelain -- src scripts tests");
        var current = changed.Length == 0 && dirty.Length == 0 ? planCommit : Git(repoRoot, "rev-parse HEAD");
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) { Console.WriteLine("D411h RUN REFUSED: OPENROUTER_API_KEY not set"); Environment.ExitCode = 2; return; }
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
                artifact = P7F1V6Retention.SummaryArtifact, canary = C.PlanArtifact, planSha256 = confirmedSha, a.Arm,
                a.Calls, a.ReservedUsd, a.ActualUsd, a.HaltReason, a.Refusals,
                units = a.Units.Select(x => new
                {
                    x.Rep, x.Outcome.Unit.UnitId, x.Outcome.Unit.Status, x.Outcome.Unit.Calls, x.Outcome.Unit.Failure, x.Outcome.Unit.IncompleteReason,
                    decisions = x.Outcome.Unit.Decisions, x.Outcome.Turns, toolCalls = x.Outcome.ToolCalls, x.Outcome.SuccessfulToolCalls, x.Outcome.Scope, x.Outcome.Evidence,
                }),
                ledger = a.Ledger,
            };
            File.WriteAllText(Path.Combine(a.SafeRunDirectory, P7F1V6Retention.SummaryFile), JsonSerializer.Serialize(summary, Indented) + "\n", new UTF8Encoding(false));
            Console.WriteLine($"arm {a.Arm}: posts {a.Calls}, halt {a.HaltReason ?? "none"}, units {string.Join(", ", a.Units.Select(x => $"r{x.Rep}/{x.Outcome.Unit.UnitId}={x.Outcome.Unit.Status}"))}; summary in {Path.GetFileName(a.SafeRunDirectory)}");
        }
    }
}
