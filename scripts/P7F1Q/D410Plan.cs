using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;
using F = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectTwoStage.SelectFormat;

// Issue #9 D4.10: the B/C development canary on SEG-001, SEG-008 and SEG-009. d410-plan writes the pinned plan and a per-arm dry-run
// (scripted models; the oracle reads the Gold for capacity only). d410-run sends requests and needs its own approval.
internal static class D410Plan
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

    static JsonElement Payload(P7F1V6Protocol.PreparedRequest req)
    {
        using var doc = JsonDocument.Parse(req.Body);
        using var p = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return p.RootElement.Clone();
    }

    static string[] Targets(P7F1V6Protocol.PreparedRequest req) => Payload(req).GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();

    static string Reply(F format, IReadOnlyList<string> t, ISet<string> sel)
    {
        if (format == F.Objects)
            return new JsonObject
            {
                ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
                ["decisions"] = new JsonArray(t.Select(a => (JsonNode)(sel.Contains(a)
                    ? new JsonObject { ["alias"] = a, ["decision"] = "SELECT", ["evidenceRefs"] = new JsonArray(a) }
                    : new JsonObject { ["alias"] = a, ["decision"] = "NOT_SELECT" })).ToArray()),
            }.ToJsonString();
        var codes = t.Select(a => sel.Contains(a) ? 'S' : 'N').ToArray();
        var blocks = new JsonArray();
        for (int s = 0; s < t.Count; s += 8)
        {
            var len = Math.Min(8, t.Count - s);
            blocks.Add(new JsonObject { ["start"] = s, ["firstAlias"] = t[s], ["lastAlias"] = t[s + len - 1], ["codes"] = new string(codes, s, len) });
        }
        return new JsonObject
        {
            ["protocolVersion"] = P7F1V6DirectTwoStage.SelectCompactVersion, ["status"] = "COMPLETE", ["targetCount"] = t.Count, ["blocks"] = blocks,
            ["selectedEvidence"] = new JsonArray(Enumerable.Range(0, t.Count).Where(i => codes[i] == 'S')
                .Select(i => (JsonNode)new JsonObject { ["targetIndex"] = i, ["evidenceRefs"] = new JsonArray(t[i]) }).ToArray()),
        }.ToJsonString();
    }

    public static void Plan(string repoRoot, string artifactDir)
    {
        if (Git(repoRoot, "status --porcelain -- src scripts tests").Length > 0) { Console.WriteLine("D410 PLAN REFUSED: uncommitted code"); Environment.ExitCode = 2; return; }
        var head = Git(repoRoot, "rev-parse HEAD");
        var bytes = P7F1V6DirectCanary410.Serialize(P7F1V6DirectCanary410.PinnedValues(repoRoot, head));
        var planPath = Path.Combine(artifactDir, "p7.f1.v6.d4.10-canary-plan.v1.json");
        File.WriteAllBytes(planPath, bytes);
        var planSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var verify = P7F1V6DirectCanary410.Verify(repoRoot, planPath, planSha, head);

        var gold = P7F1V6DirectD45.ReadGoldPin(repoRoot);
        var u = P7F1V6Universe.Rebuild(repoRoot, P7F1V6DirectD45.Document, gold.SourceSha256, out var why) ?? throw new InvalidOperationException(why);
        using var g = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, gold.Path)));
        var atoms = g.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray()
            .SelectMany(c => c.GetProperty("sourceParts").EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!)).ToHashSet(StringComparer.Ordinal);

        // Per-arm reserve matrix: first requests as pinned, and a bound for a final round that carries the window plus the 300 largest lines.
        var largest = Enumerable.Range(0, u.Lines.Count)
            .OrderByDescending(i => u.Lines[i].Text.Length + JsonSerializer.Serialize(u.Lines[i].Layout).Length).ThenBy(i => i).ToArray();
        var matrix = P7F1V6DirectCanary410.Arms.Select(a => new
        {
            arm = a.Arm,
            segments = P7F1V6DirectCanary410.Segments.Select(seg =>
            {
                var first = P7F1V6DirectCanary410.FirstRequest(a.Format, u, seg);
                var (decide, ctx) = P7F1V6DirectCanary410.Scope(u, seg);
                var window = ctx.ToHashSet();
                var shown = window.Concat(largest.Where(i => !window.Contains(i)).Take(P7F1V6DirectV2.MaxAddedLinesPerUnit)).OrderBy(i => i).Select(i => u.Lines[i]).ToArray();
                var worst = P7F1V6DirectTwoStage.BuildSelectRequest(a.Format, P7F1V6DirectD45.Instruction, u.SourceSha256, "SEGMENT", seg,
                    P7F1V6DirectV2.MaxRounds, decide, shown, Enumerable.Range(0, 8).Select(_ => new P7F1V6DirectV2.Retrieval(
                        "SEARCH " + new string('"', P7F1V6DirectV2.MaxSearchChars), "BUDGET_EXHAUSTED", Array.Empty<string>())).ToArray());
                return new
                {
                    segment = seg,
                    firstRequestBytes = first.Bytes, firstReserveUsd = Math.Round(P7F1V6Direct.AttemptUsd(first.Bytes), 6),
                    worstFinalRoundBytes = worst.Bytes, worstWithinCap = worst.Bytes <= P7F1V6DirectCanary410.MaxRequestBytes,
                    worstReserveUsd = Math.Round(P7F1V6Direct.AttemptUsd(worst.Bytes), 6),
                };
            }).ToArray(),
        }).ToArray();

        object Sim(string name, Func<F, P7F1V6DirectV2.ReplyModel> model)
        {
            var budgets = P7F1V6DirectCanary410.Arms.ToDictionary(a => a.Arm, _ => P7F1V6DirectCanary410.NewArmBudget());
            var state = P7F1V6DirectCanary410.RunArms(u, P7F1V6DirectCanary410.Arms.ToDictionary(a => a.Arm, a => model(a.Format)), budgets);
            return new
            {
                simulation = name,
                arms = state.Select(s => new
                {
                    arm = s.Arm, calls = s.Budget.Calls, reservedUsd = Math.Round(s.Budget.ReservedUsd, 6), halt = s.Budget.HaltReason,
                    refusals = s.Budget.Refusals.GroupBy(r => r).Select(x => new { code = x.Key, count = x.Count() }),
                    units = s.Units.Select(x => new { x.UnitId, x.Status, x.Failure, x.Calls, selected = x.Decisions.Count(d => d.Value == "SELECT") }),
                }),
            };
        }
        var none = new HashSet<string>(StringComparer.Ordinal);
        var sims = new List<object>
        {
            Sim("ORACLE_FROM_GOLD (capacity only)", f => (unit, r, req) => Reply(f, Targets(req), atoms)),
            Sim("ALL_NEGATIVE", f => (unit, r, req) => Reply(f, Targets(req), none)),
            Sim("RETRIEVAL_EVERY_ROUND (two searches, then answer)", f => (unit, r, req) => r < P7F1V6DirectV2.MaxRounds
                ? JsonSerializer.Serialize(new { protocolVersion = P7F1V6DirectTwoStage.SelectVersion(f), status = "NEEDS_EVIDENCE",
                    retrieve = new[] { new { kind = "SEARCH", text = "HTTP" }, new { kind = "SEARCH", text = "Section" } } })
                : Reply(f, Targets(req), atoms)),
        };
        var report = new
        {
            artifact = "p7.f1.v6.d4.10-canary-dryrun.v1",
            providerCalls = 0,
            planFile = Path.GetFileName(planPath), planSha256 = planSha, planVerifiesNow = verify.Count == 0, codeCommit = head,
            armRequestPlans = P7F1V6DirectCanary410.Arms.Select(a => new { a.Arm, requestPlanSha256 = P7F1V6Protocol.Sha(string.Join("\n",
                P7F1V6DirectCanary410.Segments.Select(s => P7F1V6DirectCanary410.FirstRequest(a.Format, u, s).Sha256))) }),
            reserveMatrix = matrix,
            simulations = sims,
            notes = new[]
            {
                "Scripted models only. The oracle reads the Gold for capacity; it is not a model result.",
                "Each arm has its own 9-post, $0.30 budget and its own early stop. A request over 131,072 B or a reserve over the arm ceiling is refused, never trimmed.",
                "Worst final-round bytes assume the 300 largest lines of the document were retrieved; real retrievals are usually smaller.",
            },
        };
        File.WriteAllText(Path.Combine(artifactDir, "p7.f1.v6.d4.10-canary-dryrun.v1.json"), JsonSerializer.Serialize(report, Indented) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"plan {planSha} verifies={verify.Count == 0} commit={head}");
        Console.WriteLine(JsonSerializer.Serialize(report.armRequestPlans));
        foreach (var m in matrix) Console.WriteLine(JsonSerializer.Serialize(m));
        foreach (var s in sims) Console.WriteLine(JsonSerializer.Serialize(s, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    /// <summary>The live canary. Not run in D4.10: it needs an APPROVED authorization for the plan. The key is never printed.</summary>
    public static void Live(string repoRoot, string planPath, string confirmedSha, string authorizationPath, string safeRoot)
    {
        safeRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(safeRoot));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(planPath));
        var planCommit = doc.RootElement.GetProperty("codeCommit").GetString()!;
        var changed = Git(repoRoot, $"diff --name-only {planCommit} HEAD -- src scripts tests");
        var dirty = Git(repoRoot, "status --porcelain -- src scripts tests");
        var current = changed.Length == 0 && dirty.Length == 0 ? planCommit : Git(repoRoot, "rev-parse HEAD");
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) { Console.WriteLine("D410 RUN REFUSED: OPENROUTER_API_KEY not set"); Environment.ExitCode = 2; return; }
        var arms = P7F1V6DirectCanary410.Execute(repoRoot, planPath, confirmedSha, current, authorizationPath, key, P7F1V6OpenRouterTransport.CreateProductionHandler(), safeRoot);
        foreach (var a in arms)
        {
            var summary = new
            {
                artifact = P7F1V6Retention.SummaryArtifact, canary = P7F1V6DirectCanary410.PlanArtifact, planSha256 = confirmedSha, a.Arm, a.Format,
                a.Calls, a.ReservedUsd, a.ActualUsd, a.HaltReason, a.Refusals,
                units = a.Units.Select(x => new { x.UnitId, x.Stage, x.Status, x.Calls, x.Failure, decisions = x.Decisions }),
                ledger = a.Ledger,
            };
            File.WriteAllText(Path.Combine(a.SafeRunDirectory, P7F1V6Retention.SummaryFile), JsonSerializer.Serialize(summary, Indented) + "\n", new UTF8Encoding(false));
            Console.WriteLine($"arm {a.Arm}: calls {a.Calls}, halt {a.HaltReason ?? "none"}, units {string.Join(", ", a.Units.Select(x => x.UnitId + "=" + x.Status))}; summary in {Path.GetFileName(a.SafeRunDirectory)}");
        }
    }
}
