using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;
using A = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectAgent;

// Issue #9 D4.11: the tool-calling canary (arm B answer, native tool calls) on SEG-001 and SEG-008. d411-plan writes the pinned plan and a
// dry-run with scripted agent models (no network). d411-run sends requests and needs its own plan SHA, budget and owner approval.
internal static class D411Plan
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
        if (Git(repoRoot, "status --porcelain -- src scripts tests").Length > 0) { Console.WriteLine("D411 PLAN REFUSED: uncommitted code"); Environment.ExitCode = 2; return; }
        var head = Git(repoRoot, "rev-parse HEAD");
        var bytes = P7F1V6DirectAgentCanary.Serialize(P7F1V6DirectAgentCanary.PinnedValues(repoRoot, head));
        var planPath = Path.Combine(artifactDir, P7F1V6DirectAgentCanary.PlanArtifact + ".json");
        File.WriteAllBytes(planPath, bytes);
        var planSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var verify = P7F1V6DirectAgentCanary.Verify(repoRoot, planPath, planSha, head);

        var gold = P7F1V6DirectD45.ReadGoldPin(repoRoot);
        var u = P7F1V6Universe.Rebuild(repoRoot, P7F1V6DirectD45.Document, gold.SourceSha256, out var why) ?? throw new InvalidOperationException(why);
        var pages = u.Lines.Select(l => l.Page).Distinct().ToArray();

        // Scripted models. None is a model result; they exercise the loop, the caps and the reserve.
        var models = new (string Name, A.AgentModel Model)[]
        {
            ("ANSWER_WITHOUT_TOOLS", (unit, turn, req) => new A.ModelTurn(Final(Targets(req)), Array.Empty<A.ToolCall>(), "stop")),
            ("REPEATS_OF_EVERY_TARGET (8 per turn, three turns, then answer)", (unit, turn, req) => turn <= A.MaxToolTurns
                ? Calls(Targets(req).Skip((turn - 1) * 8).Take(8).Select(a => (P7F1V6AgentTools.Repeated, (object)new { alias = a, maxResults = 5 })), turn)
                : new A.ModelTurn(Final(Targets(req)), Array.Empty<A.ToolCall>(), "stop")),
            ("LARGEST_RESULTS (8 full page layouts per turn, three turns, then answer)", (unit, turn, req) => turn <= A.MaxToolTurns
                ? Calls(Enumerable.Range(0, 8).Select(k => (P7F1V6AgentTools.PageLayout, (object)new { page = pages[(turn * 8 + k) % pages.Length], offset = 0, limit = 40 })), turn)
                : new A.ModelTurn(Final(Targets(req)), Array.Empty<A.ToolCall>(), "stop")),
        };
        var sims = models.Select(m =>
        {
            var budgets = P7F1V6DirectAgentCanary.Arms.ToDictionary(a => a.Arm, _ => P7F1V6DirectAgentCanary.NewArmBudget());
            var state = P7F1V6DirectAgentCanary.RunArms(u, P7F1V6DirectAgentCanary.Arms.ToDictionary(a => a.Arm, _ => m.Model), budgets);
            return new
            {
                simulation = m.Name,
                arms = state.Select(s => new
                {
                    arm = s.Arm, calls = s.Budget.Calls, reservedUsd = Math.Round(s.Budget.ReservedUsd, 6), halt = s.Budget.HaltReason,
                    refusals = s.Budget.Refusals,
                    units = s.Units.Select(x => new
                    {
                        x.Unit.UnitId, x.Unit.Status, x.Unit.Failure, posts = x.Unit.Calls,
                        requestBytes = x.Turns.Select(t => t.RequestBytes), maxRequestBytes = x.Turns.Max(t => t.RequestBytes),
                        toolCalls = x.ToolCalls.Count, okToolCalls = x.SuccessfulToolCalls,
                        rejected = x.ToolCalls.Where(c => c.Status != "OK").GroupBy(c => c.ErrorCode).Select(g => new { code = g.Key, count = g.Count() }),
                        toolResultBytes = x.ToolCalls.Sum(c => c.ContentBytes), truncatedResults = x.ToolCalls.Count(c => c.Truncated),
                        scope = x.Scope, evidence = x.Evidence,
                    }),
                }),
            };
        }).ToList();

        var report = new
        {
            artifact = "p7.f1.v6.d4.11g-tool-canary-dryrun.v1",
            providerCalls = 0,
            planFile = Path.GetFileName(planPath), planSha256 = planSha, planVerifiesNow = verify.Count == 0, codeCommit = head,
            firstRequests = P7F1V6DirectAgentCanary.Arms.SelectMany(a => P7F1V6DirectAgentCanary.Segments.Select(s =>
            {
                var r = P7F1V6DirectAgentCanary.FirstRequest(a.Mode, u, s);
                return new { arm = a.Arm, segment = s, sha256 = r.Sha256, bytes = r.Bytes, reserveUsd = Math.Round(P7F1V6Direct.AttemptUsd(r.Bytes), 6) };
            })),
            worstCaseReservePerArmUsd = Math.Round(P7F1V6DirectAgentCanary.PostsPerArm * P7F1V6Direct.AttemptUsd(P7F1V6DirectAgentCanary.MaxRequestBytes), 6),
            simulations = sims,
            notes = new[]
            {
                "Scripted models only; no provider call. They show the loop, the caps and the reserve, not what Qwen will do.",
                "Every tool message of a unit, rejected ones included, counts against a 65,536-byte hard cap: room for a rejection of each call is reserved before a turn runs; a call runs only if a full 16 KB result plus that reserve and the rejections of up to 8 calls in each later tool turn fit (else UNIT_TOOL_MESSAGE_BYTES_EXHAUSTED); a turn whose rejections alone would not fit is refused before dispatch.",
                "Each arm has its own 8-post, $0.25 budget and early stop.",
            },
        };
        File.WriteAllText(Path.Combine(artifactDir, "p7.f1.v6.d4.11g-tool-canary-dryrun.v1.json"), JsonSerializer.Serialize(report, Indented) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"plan {planSha} verifies={verify.Count == 0} commit={head}");
        foreach (var f in report.firstRequests) Console.WriteLine(JsonSerializer.Serialize(f));
        foreach (var s in sims)
            foreach (var a in s.arms)
                Console.WriteLine($"{s.simulation} | {a.arm}: posts {a.calls}, reserve {a.reservedUsd}, halt {a.halt ?? "none"}; " +
                    string.Join("; ", a.units.Select(x => $"{x.UnitId} {x.Status} {x.Failure} posts={x.posts} maxBytes={x.maxRequestBytes} tools={x.okToolCalls}/{x.toolCalls} msgBytes={x.evidence.ToolMessageBytes}")));
    }

    /// <summary>Copies the safe-store runs of a consumed plan into the repository (P7F1V6Publication): only when the approving message said to push.</summary>
    public static void Publish(string planPath, string outDir, IReadOnlyList<string> runDirs, bool gzip = false)
    {
        var r = P7F1V6Publication.Publish(planPath, outDir, runDirs, Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"), gzip);
        if (r.Refusal is not null) { Console.WriteLine("D411 PUBLISH REFUSED: " + r.Refusal); Environment.ExitCode = 2; return; }
        Console.WriteLine($"published {r.Files.Count} files to {outDir}");
    }

    /// <summary>The live canary. Not run in D4.11: it needs an APPROVED authorization for the plan. The key is never printed. Request bodies
    /// (with every tool result) and the run summary go to each arm's safe-store run directory, outside the repository.</summary>
    public static void Live(string repoRoot, string planPath, string confirmedSha, string authorizationPath, string safeRoot)
    {
        safeRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(safeRoot));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(planPath));
        var planCommit = doc.RootElement.GetProperty("codeCommit").GetString()!;
        var changed = Git(repoRoot, $"diff --name-only {planCommit} HEAD -- src scripts tests");
        var dirty = Git(repoRoot, "status --porcelain -- src scripts tests");
        var current = changed.Length == 0 && dirty.Length == 0 ? planCommit : Git(repoRoot, "rev-parse HEAD");
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) { Console.WriteLine("D411 RUN REFUSED: OPENROUTER_API_KEY not set"); Environment.ExitCode = 2; return; }
        var arms = P7F1V6DirectAgentCanary.Execute(repoRoot, planPath, confirmedSha, current, authorizationPath, key, P7F1V6OpenRouterTransport.CreateProductionHandler(), safeRoot);
        foreach (var a in arms)
        {
            var requests = Path.Combine(a.SafeRunDirectory, "requests");
            Directory.CreateDirectory(requests);
            foreach (var x in a.Units)
                for (int t = 0; t < x.Unit.Requests.Count; t++)
                    File.WriteAllText(Path.Combine(requests, $"{x.Unit.UnitId}-turn-{t + 1}.{x.Unit.Requests[t].Sha256}.json"), x.Unit.Requests[t].Body, new UTF8Encoding(false));
            var summary = new
            {
                artifact = P7F1V6Retention.SummaryArtifact, canary = P7F1V6DirectAgentCanary.PlanArtifact, planSha256 = confirmedSha, a.Arm,
                a.Calls, a.ReservedUsd, a.ActualUsd, a.HaltReason, a.Refusals,
                units = a.Units.Select(x => new
                {
                    x.Unit.UnitId, x.Unit.Status, x.Unit.Calls, x.Unit.Failure, x.Unit.IncompleteReason, decisions = x.Unit.Decisions,
                    x.Turns, toolCalls = x.ToolCalls, x.SuccessfulToolCalls, x.Scope, x.Evidence,
                }),
                ledger = a.Ledger,
            };
            File.WriteAllText(Path.Combine(a.SafeRunDirectory, P7F1V6Retention.SummaryFile), JsonSerializer.Serialize(summary, Indented) + "\n", new UTF8Encoding(false));
            Console.WriteLine($"arm {a.Arm}: calls {a.Calls}, halt {a.HaltReason ?? "none"}, units {string.Join(", ", a.Units.Select(x => x.Unit.UnitId + "=" + x.Unit.Status))}; summary in {Path.GetFileName(a.SafeRunDirectory)}");
        }
    }
}
