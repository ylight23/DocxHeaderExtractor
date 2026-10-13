using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;
using A = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectAgent;
using C = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectRepeatCanary;
using T = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectOwnerTrial;

// Issue #9 D4.11j: the owner-document trial. d411j-plan writes the pinned plan and a scripted dry-run (no network). d411j-run sends requests and needs the
// owner's approval of the plan and of sending this document. Nothing here prints or writes any line text of the document.
internal static class D411jPlan
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

    public static void Plan(string repoRoot, string artifactDir, string documentPath, string seg = P7F1V6Universe.FixedSegmentation, bool jsonAll = false, bool geometry = false, bool unwrap = false, bool single = false, bool factsUse = false, int reps = C.Reps, int firstSegments = 0, string instruction = P7F1V6DirectD45.Instruction, bool toolRounds = false, bool toolFacts = false)
    {
        if (Git(repoRoot, "status --porcelain -- src scripts tests").Length > 0) { Console.WriteLine("D411j PLAN REFUSED: uncommitted code"); Environment.ExitCode = 2; return; }
        var head = Git(repoRoot, "rev-parse HEAD");
        var tag = (seg == P7F1V6Universe.LayoutSegmentation96 ? ".layout96" : seg == P7F1V6Universe.LayoutSegmentation ? ".layout" : "") + (jsonAll ? ".json" : "") + (geometry ? ".geo" : "") + (unwrap ? ".unwrap" : "") + (single ? ".single" : "") + (factsUse ? ".facts" : "") + (reps != C.Reps ? $".r{reps}" : "") + (firstSegments > 0 ? $".first{firstSegments}" : "") + (instruction == P7F1V6DirectD45.InstructionEnglishAll ? ".en" : instruction == P7F1V6DirectD45.InstructionEnglishMain ? ".main" : instruction == P7F1V6DirectD45.InstructionEnglishMainTools ? ".maintools" : "") + (toolRounds ? ".toolrounds" : "") + (toolFacts ? ".toolfacts" : "");
        var bytes = T.Serialize(T.PinnedValues(documentPath, head, seg, jsonAll, geometry, unwrapFence: unwrap, singleArm: single, factsUse: factsUse, reps: reps, firstSegments: firstSegments, instruction: instruction, fullToolRounds: toolRounds, toolFacts: toolFacts));
        using var plan = JsonDocument.Parse(bytes);
        var authority = plan.RootElement.GetProperty("authorityId").GetString()!;
        var planPath = Path.Combine(artifactDir, $"{T.PlanArtifact.Replace(".v1", "")}.{authority}{tag}.v1.json");
        File.WriteAllBytes(planPath, bytes);
        var planSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var verify = T.Verify(planPath, planSha, head, documentPath);

        var u = T.Load(documentPath, T.Sha256Of(documentPath), seg);
        var segs = plan.RootElement.GetProperty("segments").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var posts = plan.RootElement.GetProperty("arms")[0].GetProperty("budget").GetProperty("maxCalls").GetInt32();
        var ceilings = plan.RootElement.GetProperty("arms").EnumerateArray().ToDictionary(a => a.GetProperty("arm").GetString()!, a => a.GetProperty("budget").GetProperty("ceilingUsd").GetDouble());
        A.ModelTurn Overview(int turn) => new(null, new[] { new A.ToolCall($"dry_{turn}", P7F1V6AgentTools.DocumentOverview, "{}") }, "tool_calls");
        var models = new (string Name, A.AgentModel Model)[]
        {
            ("ANSWER_WITHOUT_TOOLS", (unit, turn, req) => new A.ModelTurn(Final(Targets(req)), Array.Empty<A.ToolCall>(), "stop")),
            ("TOOL_CALL_ON_EVERY_TURN (an overview each of 3 turns, then answer)", (unit, turn, req) => turn <= A.MaxToolTurns ? Overview(turn) : new A.ModelTurn(Final(Targets(req)), Array.Empty<A.ToolCall>(), "stop")),
        };
        var sims = models.Select(m =>
        {
            var budgets = T.ArmsOf(geometry, single, factsUse, toolFacts).ToDictionary(a => a.Arm, a => new P7F1V6DirectV2.Budget(posts, ceilings[a.Arm], C.MaxRequestBytes));
            var state = C.RunArms(u, segs, T.ArmsOf(geometry, single, factsUse, toolFacts).ToDictionary(a => a.Arm, _ => m.Model), budgets, jsonAll, T.ArmsOf(geometry, single, factsUse, toolFacts), unwrapSingleFence: unwrap, reps: reps, instruction: instruction, parallelism: 1);
            return new
            {
                simulation = m.Name,
                arms = state.Select(s => new
                {
                    arm = s.Arm, posts = s.Budget.Calls, reservedUsd = Math.Round(s.Budget.ReservedUsd, 4), ceilingUsd = ceilings[s.Arm], halt = s.Budget.HaltReason,
                    units = s.Units.Count, complete = s.Units.Count(x => x.Outcome.Unit.Status == "COMPLETE"),
                    stoppedByBudget = s.Units.Count(x => x.Outcome.Unit.Failure?.StartsWith("BUDGET:") == true),
                    maxRequestBytes = s.Units.SelectMany(x => x.Outcome.Turns.Select(t => t.RequestBytes)).DefaultIfEmpty(0).Max(),
                }),
            };
        }).ToList();
        var report = new
        {
            artifact = "p7.f1.v6.d4.11j-owner-document-trial-dryrun.v1", authority, providerCalls = 0,
            planFile = Path.GetFileName(planPath), planSha256 = planSha, planVerifiesNow = verify.Count == 0, codeCommit = head,
            firstRequests = T.ArmsOf(geometry, single, factsUse, toolFacts).SelectMany(a => segs.Select(s =>
            {
                var r = C.FirstRequest(a.Mode, u, s, jsonAll, instruction);
                return new { arm = a.Arm, segment = s, sha256 = r.Sha256, bytes = r.Bytes, reserveUsd = Math.Round(P7F1V6Direct.AttemptUsd(r.Bytes), 6) };
            })),
            simulations = sims,
            notes = new[] { "Scripted models only; no provider call; no line text is written to this report." },
        };
        File.WriteAllText(Path.Combine(artifactDir, $"p7.f1.v6.d4.11j-owner-document-trial-dryrun.{authority}{tag}.v1.json"), JsonSerializer.Serialize(report, Indented) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"{authority}: plan {planSha} verifies={verify.Count == 0} commit={head}");
        Console.WriteLine("segments: " + string.Join(", ", segs) + $" | posts per arm {posts} | ceilings {string.Join(", ", ceilings.Select(kv => kv.Key + " $" + kv.Value))}");
        foreach (var f in report.firstRequests) Console.WriteLine(JsonSerializer.Serialize(f));
        foreach (var s in sims)
            foreach (var a in s.arms)
                Console.WriteLine($"{s.simulation} | {a.arm}: posts {a.posts}/{posts}, reserve {a.reservedUsd}/{a.ceilingUsd}, halt {a.halt ?? "none"}, complete {a.complete}/{a.units}, stoppedByBudget {a.stoppedByBudget}, maxBytes {a.maxRequestBytes}");
    }

    public static void Live(string repoRoot, string planPath, string confirmedSha, string authorizationPath, string safeRoot, string documentPath)
    {
        safeRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(safeRoot));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(planPath));
        var planCommit = doc.RootElement.GetProperty("codeCommit").GetString()!;
        var changed = Git(repoRoot, $"diff --name-only {planCommit} HEAD -- src scripts tests");
        var dirty = Git(repoRoot, "status --porcelain -- src scripts tests");
        var current = changed.Length == 0 && dirty.Length == 0 ? planCommit : Git(repoRoot, "rev-parse HEAD");
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) { Console.WriteLine("D411j RUN REFUSED: OPENROUTER_API_KEY not set"); Environment.ExitCode = 2; return; }
        var arms = T.Execute(repoRoot, planPath, confirmedSha, current, authorizationPath, key, P7F1V6OpenRouterTransport.CreateProductionHandler(), safeRoot, documentPath);
        foreach (var a in arms)
        {
            var requests = Path.Combine(a.SafeRunDirectory, "requests");
            Directory.CreateDirectory(requests);
            foreach (var x in a.Units)
                for (int t = 0; t < x.Outcome.Unit.Requests.Count; t++)
                    File.WriteAllText(Path.Combine(requests, $"{x.Outcome.Unit.UnitId}-r{x.Rep}-turn-{t + 1}.{x.Outcome.Unit.Requests[t].Sha256}.json"), x.Outcome.Unit.Requests[t].Body, new UTF8Encoding(false));
            var summary = new
            {
                artifact = P7F1V6Retention.SummaryArtifact, canary = T.PlanArtifact, planSha256 = confirmedSha, parallelism = C.ParallelismFromEnvironment(), a.Arm,
                a.Calls, a.ReservedUsd, a.ActualUsd, a.HaltReason, a.Refusals,
                units = a.Units.Select(x => new
                {
                    x.Rep, x.Outcome.Unit.UnitId, x.Outcome.Unit.Status, x.Outcome.Unit.Calls, x.Outcome.Unit.Failure, x.Outcome.Unit.IncompleteReason, x.Outcome.FenceUnwrapped,
                    decisions = x.Outcome.Unit.Decisions, x.Outcome.Turns, toolCalls = x.Outcome.ToolCalls, x.Outcome.SuccessfulToolCalls, x.Outcome.Scope, x.Outcome.Evidence,
                }),
                ledger = a.Ledger,
            };
            File.WriteAllText(Path.Combine(a.SafeRunDirectory, P7F1V6Retention.SummaryFile), JsonSerializer.Serialize(summary, Indented) + "\n", new UTF8Encoding(false));
            Console.WriteLine($"arm {a.Arm}: posts {a.Calls}, halt {a.HaltReason ?? "none"}, complete {a.Units.Count(x => x.Outcome.Unit.Status == "COMPLETE")}/{a.Units.Count}; summary in {Path.GetFileName(a.SafeRunDirectory)}");
        }
    }
}
