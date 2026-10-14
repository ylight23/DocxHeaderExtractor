using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.V6Qualification.P7;

// Issue #9 D4.5 plan (offline): writes the pinned plan for SRC-095 / task A / unit 48 and a dry-run report. Nothing is sent: every simulated
// reply comes from a scripted model in this process. The scripted oracle reads the Gold to play a perfect model for capacity checks only;
// its replies are not a model result, and no Gold value is ever part of a provider request.
internal static class D45Plan
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

    public static void Run(string repoRoot, string artifactDir, bool preview = false)
    {
        // The plan pins a committed code state: refuse to write it from a working tree with uncommitted code. A preview skips that check,
        // pins no commit, and must be written outside the repository; it is never a plan a reviewer confirms.
        var dirty = Git(repoRoot, "status --porcelain -- src scripts tests");
        if (!preview && dirty.Length > 0) { Console.WriteLine("D45 PLAN REFUSED: uncommitted code under src/, scripts/ or tests/"); Environment.ExitCode = 2; return; }
        var head = preview ? "PREVIEW_NOT_A_PLAN" : Git(repoRoot, "rev-parse HEAD");

        var planBytes = P7F1V6DirectD45.Serialize(P7F1V6DirectD45.PinnedValues(repoRoot, head));
        var planPath = Path.Combine(artifactDir, "p7.f1.v6.d4.8-plan.v1.json");
        File.WriteAllBytes(planPath, planBytes);
        var planSha = Convert.ToHexStringLower(SHA256.HashData(planBytes));
        var verify = P7F1V6DirectD45.Verify(repoRoot, planPath, planSha, head);

        var gold = P7F1V6DirectD45.ReadGoldPin(repoRoot);
        var u = P7F1V6Universe.Rebuild(repoRoot, P7F1V6DirectD45.Document, gold.SourceSha256, out var why) ?? throw new InvalidOperationException(why);
        var claims = GoldClaims(Path.Combine(repoRoot, gold.Path));
        var goldAtoms = claims.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);

        var sims = new List<object>
        {
            Simulate("ORACLE_FROM_GOLD (perfect scripted model; capacity check only)", u, repoRoot, Oracle(claims, goldAtoms)),
            Simulate("ALL_NEGATIVE (every segment NOT_SELECT)", u, repoRoot, (unit, round, req) => Reply(Decide(req), new HashSet<string>(), claims)),
            Simulate("SELECT_ALL (every atom its own heading)", u, repoRoot, (unit, round, req) =>
                Reply(Decide(req), Decide(req).ToHashSet(StringComparer.Ordinal), Decide(req).Select(a => new List<string> { a }).ToList())),
            Simulate("RETRIEVAL_HEAVY (one SEARCH per unit, then the oracle)", u, repoRoot, (unit, round, req) =>
                round == 1 && Stage(req) == "SEGMENT"
                    ? JsonSerializer.Serialize(new { protocolVersion = P7F1V6DirectV2.ResponseVersion, status = "NEEDS_EVIDENCE", retrieve = new[] { new { kind = "SEARCH", text = "Section" } } })
                    : Oracle(claims, goldAtoms)(unit, round, req)),
        };

        var bound = P7F1V6DirectV2.WorstCase(P7F1V6DirectD45.Instruction, u.SourceSha256, u.Lines, P7F1V6DirectD45.Overlap, P7F1V6DirectD45.Unit);
        int cap = P7F1V6DirectD45.MaxRequestBytes;
        var matrix = new object[]
        {
            new { stage = "SEGMENT round 1", minBytes = bound.Round1MinBytes, medianBytes = bound.Round1MedianBytes, maxBytes = bound.Round1MaxBytes,
                withinCap = bound.Round1MaxBytes <= cap, reserveUsdAtMax = P7F1V6Direct.AttemptUsd(bound.Round1MaxBytes) },
            new { stage = "SEGMENT worst (all retrieval)", maxBytes = bound.WorstSegmentRequestBytes, withinCap = bound.WorstSegmentRequestBytes <= cap,
                reserveUsdAtMax = P7F1V6Direct.AttemptUsd(bound.WorstSegmentRequestBytes) },
            new { stage = "RESOLVE worst", maxBytes = bound.WorstResolveRequestBytes, withinCap = bound.WorstResolveRequestBytes <= cap,
                reserveUsdAtMax = P7F1V6Direct.AttemptUsd(bound.WorstResolveRequestBytes) },
            new { stage = "FINALIZE worst (1,200-line outline)", maxBytes = bound.WorstFinalizeRequestBytes, withinCap = bound.WorstFinalizeRequestBytes <= cap,
                reserveUsdAtMax = P7F1V6Direct.AttemptUsd(bound.WorstFinalizeRequestBytes) },
        };

        var report = new
        {
            artifact = "p7.f1.v6.d4.8-dryrun.v1",
            providerCalls = 0,
            planFile = Path.GetFileName(planPath),
            planSha256 = planSha,
            planVerifiesNow = verify.Count == 0,
            planVerifyFailures = verify,
            codeCommit = head,
            budget = new { P7F1V6DirectD45.MaxCalls, P7F1V6DirectD45.MaxRequestBytes, P7F1V6DirectD45.CeilingUsd },
            goldAtoms = goldAtoms.Count,
            goldHeadings = claims.Count,
            simulations = sims,
            stageMatrix = matrix,
            worstCaseBound = bound,
            notes = new[]
            {
                "Scripted models only. The oracle reads the Gold to play a perfect model so that capacity can be checked; it is not a Qwen result and its F1 is not a finding.",
                "Bytes bound prompt tokens from above; reserves use max_tokens 32,768 per attempt at the tier the bytes reach.",
                "A run that is refused by the budget is fail-closed: aliases stay UNRESOLVED, nothing is trimmed and no budget is raised.",
            },
        };
        File.WriteAllText(Path.Combine(artifactDir, "p7.f1.v6.d4.8-dryrun.v1.json"), JsonSerializer.Serialize(report, Indented) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"plan {planSha} verifies={verify.Count == 0} commit={head}");
        foreach (var s in sims) Console.WriteLine(JsonSerializer.Serialize(s, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        foreach (var m in matrix) Console.WriteLine(JsonSerializer.Serialize(m));
    }

    /// <summary>The live run of a confirmed plan. Not run in D4.5: it needs its own approval. Refuses when the code under src/, scripts/ or
    /// tests/ differs from the plan's commit, when the safe root is missing or inside the repository, or when the key is absent. The key is
    /// never printed. Raw responses and the ledger stay in the safe store; only a summary without source text is printed.</summary>
    /// <summary>Creates the safe root if it is missing and restricts it to the current user, then reports every check. Never inside the repository.</summary>
    public static void SafeRoot(string repoRoot, string safeRoot, bool prepare)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(safeRoot));
        if (full.StartsWith(Path.GetFullPath(repoRoot), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("REFUSED: inside the repository"); Environment.ExitCode = 2; return; }
        if (prepare && OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(full);
            P7F1V6SafeRawStore.RestrictRootToCurrentUser(full);
        }
        var failures = P7F1V6DirectD45.SafeRootFailures(repoRoot, full);
        Console.WriteLine($"safe root {full}: {(failures.Count == 0 ? "OK" : string.Join(", ", failures))}");
        if (failures.Count > 0) Environment.ExitCode = 2;
    }

    /// <summary>Writes the PENDING authorization draft for a plan. It authorizes nothing until the user approves it.</summary>
    public static void AuthorizationDraft(string planPath, string safeRoot, string outPath)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(safeRoot));
        File.WriteAllBytes(outPath, P7F1V6DirectAuthorization.Draft(planPath, full));
        Console.WriteLine($"authorization draft written: {outPath} (status {P7F1V6DirectAuthorization.Pending})");
    }

    /// <summary>Records the user's approval in a new file. Run only on the user's explicit approval, with the user's own words and decisions.</summary>
    public static void Approve(string draftPath, string outPath, string approvedBy, string approvalText, string dataCollectionDecision, bool acceptOpenItems)
    {
        var items = acceptOpenItems ? P7F1V6RoutePolicy.RouteAllowed(P7F1V6Protocol.ProviderTag).Blocking : Array.Empty<string>();
        File.WriteAllBytes(outPath, P7F1V6DirectAuthorization.Approve(File.ReadAllBytes(draftPath), approvedBy, approvalText, dataCollectionDecision, items, DateTime.UtcNow));
        Console.WriteLine($"authorization approved: {outPath}");
    }

    public static void Live(string repoRoot, string planPath, string confirmedSha, string authorizationPath, string safeRoot)
    {
        safeRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(safeRoot));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(planPath));
        var planCommit = doc.RootElement.GetProperty("codeCommit").GetString()!;
        var changed = Git(repoRoot, $"diff --name-only {planCommit} HEAD -- src scripts tests");
        var dirty = Git(repoRoot, "status --porcelain -- src scripts tests");
        var current = changed.Length == 0 && dirty.Length == 0 ? planCommit : Git(repoRoot, "rev-parse HEAD");
        if (!Directory.Exists(safeRoot) || Path.GetFullPath(safeRoot).StartsWith(Path.GetFullPath(repoRoot), StringComparison.OrdinalIgnoreCase))
        { Console.WriteLine("D45 RUN REFUSED: safe root missing or inside the repository"); Environment.ExitCode = 2; return; }
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) { Console.WriteLine("D45 RUN REFUSED: OPENROUTER_API_KEY not set"); Environment.ExitCode = 2; return; }

        var result = P7F1V6DirectD45.Execute(repoRoot, planPath, confirmedSha, current, authorizationPath, key, P7F1V6OpenRouterTransport.CreateProductionHandler(), safeRoot);
        var summary = new
        {
            artifact = P7F1V6Retention.SummaryArtifact,
            planSha256 = confirmedSha,
            result.Outcome.Status, result.Outcome.Finalization, result.Outcome.Assurance, result.Outcome.Calls,
            reservedUsd = result.Outcome.ReservedUsd, refusals = result.Outcome.BudgetRefusals,
            units = result.Outcome.Units.Select(u => new { u.UnitId, u.Stage, u.Status, u.Calls, u.Failure }),
            score = new { result.Score.Status, result.Score.Reason, result.Score.Official, result.Score.Label,
                atomF1 = result.Score.AtomMembership?.F1Exact, headingF1 = result.Score.HeadingOccurrence?.F1Exact },
            ledger = result.Ledger,
        };
        var text = JsonSerializer.Serialize(summary, Indented) + "\n";
        File.WriteAllText(Path.Combine(result.SafeRunDirectory, P7F1V6Retention.SummaryFile), text, new UTF8Encoding(false));
        Console.WriteLine($"D45 run: {result.Outcome.Status} / {result.Outcome.Finalization}, calls {result.Outcome.Calls}, official {result.Score.Official}; summary in the safe store");
    }

    static object Simulate(string name, P7F1V6Universe u, string repoRoot, P7F1V6DirectV2.ReplyModel model)
    {
        var budget = P7F1V6DirectD45.NewBudget();
        var seen = new List<(string Stage, int Bytes, double Reserve)>();
        var outcome = P7F1V6DirectV2.Run(P7F1V6DirectD45.Instruction, u, (unit, round, req) =>
        {
            seen.Add((Stage(req), req.Bytes, P7F1V6Direct.AttemptUsd(req.Bytes)));
            return model(unit, round, req);
        }, budget, P7F1V6DirectD45.Overlap, P7F1V6DirectD45.Unit, finalize: true);
        var score = outcome.Status == "CLARIFICATION_REQUIRED" ? null
            : P7F1V6DirectScorer.Score(repoRoot, P7F1V6DirectD45.Registry, P7F1V6DirectD45.AuthorityId, P7F1V6DirectScorer.RunEvidence.From(u, outcome));
        return new
        {
            simulation = name,
            status = outcome.Status,
            finalization = outcome.Finalization,
            assurance = outcome.Assurance,
            calls = outcome.Calls,
            reservedUsd = Math.Round(outcome.ReservedUsd, 6),
            unresolved = outcome.Unresolved.Count,
            headings = outcome.Headings.Count,
            budgetRefusals = budget.Refusals.GroupBy(r => r).Select(g => new { code = g.Key, count = g.Count() }),
            refusedRequestMaxBytes = budget.RefusedRequestBytes.Count == 0 ? (int?)null : budget.RefusedRequestBytes.Max(),
            byStage = seen.GroupBy(s => s.Stage).Select(g => new { stage = g.Key, calls = g.Count(), minBytes = g.Min(x => x.Bytes), maxBytes = g.Max(x => x.Bytes),
                reserveUsd = Math.Round(g.Sum(x => x.Reserve), 6) }),
            score = score is null ? null : new { score.Status, score.Reason, score.Official, score.Label, atomF1 = score.AtomMembership?.F1Exact,
                headingF1 = score.HeadingOccurrence?.F1Exact },
        };
    }

    static List<List<string>> GoldClaims(string goldPath)
    {
        using var g = JsonDocument.Parse(File.ReadAllText(goldPath));
        return g.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().Select(c =>
            c.TryGetProperty("sourceParts", out var ps)
                ? ps.EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!).ToList()
                : new List<string> { c.GetProperty("sourceAlias").GetString()! }).ToList();
    }

    static JsonElement Payload(P7F1V6Protocol.PreparedRequest req)
    {
        using var doc = JsonDocument.Parse(req.Body);
        using var p = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return p.RootElement.Clone();
    }

    static string[] Decide(P7F1V6Protocol.PreparedRequest req) => Payload(req).GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
    static string Stage(P7F1V6Protocol.PreparedRequest req) => Payload(req).GetProperty("stage").GetString()!;

    /// <summary>A COMPLETE reply: decide aliases in select are SELECT; headings are the given groups that hold at least one of them.</summary>
    static string Reply(string[] decide, ISet<string> select, List<List<string>> groups)
    {
        var d = decide.ToHashSet(StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            protocolVersion = P7F1V6DirectV2.ResponseVersion,
            status = "COMPLETE",
            decisions = decide.Select(a => new { alias = a, decision = select.Contains(a) ? "SELECT" : "NOT_SELECT", evidenceRefs = new[] { a } }),
            headings = groups.Where(g => g.Any(a => d.Contains(a) && select.Contains(a))).Select(g => new { parts = g }),
        }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    static P7F1V6DirectV2.ReplyModel Oracle(List<List<string>> claims, HashSet<string> goldAtoms) =>
        (unit, round, req) => Reply(Decide(req), goldAtoms, claims);
}
