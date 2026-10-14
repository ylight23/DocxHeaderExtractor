using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using F = DocxHeaderExtractor.V6Qualification.P7.P7F1V6DirectTwoStage.SelectFormat;

namespace DocxHeaderExtractor.V6Qualification.P7;

/// <summary>
/// D4.10 development canary (offline planning; the run needs its own approval). Stage 1 SELECT only, on SEG-001, SEG-008 and SEG-009 of
/// SRC-095, for two arms over the same targets, context, instruction and model: B (object decisions) and C (compact codes). Only the reply
/// format and its protocol prompt differ. The two arms run interleaved per segment (B then C). Each arm has its own budget (9 posts,
/// $0.30), its own early stop, its own audited model, ledger and safe-store run: one arm can never spend the other's budget or move the
/// other's early-stop count. No FINALIZE, no GROUP, no other segment. Segments were chosen from earlier errors: this is a development
/// canary, not an F1 estimate.
/// </summary>
internal static class P7F1V6DirectCanary410
{
    public static readonly string[] Segments = { "SEG-001", "SEG-008", "SEG-009" };
    public static readonly (string Arm, F Format)[] Arms = { ("B", F.Objects), ("C", F.Compact) };
    public const int Unit = 48;
    public const int Overlap = P7F1V6Direct.DefaultOverlapLines;
    public const int PostsPerArm = 9;
    public const double CeilingUsdPerArm = 0.30;
    public const int MaxRequestBytes = 131072;
    public const int Retries = 0;
    public const string PlanArtifact = "p7.f1.v6.d4.10-canary-plan.v1";
    public const string EvaluationLabel = "Development canary — segments chosen from previous errors; Stage 1 only; not an F1 estimate";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static P7F1V6DirectV2.Budget NewArmBudget() => new(PostsPerArm, CeilingUsdPerArm, MaxRequestBytes);

    /// <summary>The scope of one segment, exactly as the full run would build it: 48 targets and 20 lines of context on each side.</summary>
    public static (string[] Decide, int[] Context) Scope(P7F1V6Universe u, string segment)
    {
        var n = int.Parse(segment.AsSpan(4));
        if (n < 0 || n >= u.Cores.Count) throw new ArgumentException("SEGMENT_OUT_OF_RANGE", nameof(segment));
        var (core, end) = u.Cores[n];
        var from = Math.Max(0, core - Overlap);
        return (Enumerable.Range(core, end - core).Select(i => u.Lines[i].Alias).ToArray(),
            Enumerable.Range(from, Math.Min(u.Lines.Count, end + Overlap) - from).ToArray());
    }

    public static P7F1V6Protocol.PreparedRequest FirstRequest(F format, P7F1V6Universe u, string segment)
    {
        var (decide, ctx) = Scope(u, segment);
        return P7F1V6DirectTwoStage.BuildSelectRequest(format, P7F1V6DirectD45.Instruction, u.SourceSha256, "SEGMENT", segment, 1, decide,
            ctx.Select(i => u.Lines[i]).ToArray(), Array.Empty<P7F1V6DirectV2.Retrieval>());
    }

    public static Dictionary<string, object?> PinnedValues(string repoRoot, string codeCommit)
    {
        var gold = P7F1V6DirectD45.ReadGoldPin(repoRoot);
        var u = P7F1V6Universe.Rebuild(repoRoot, P7F1V6DirectD45.Document, gold.SourceSha256, out var failure) ?? throw new InvalidOperationException(failure);
        var arms = Arms.Select(a =>
        {
            var first = Segments.Select(s => FirstRequest(a.Format, u, s)).ToList();
            return new
            {
                arm = a.Arm,
                format = a.Format.ToString(),
                responseVersion = P7F1V6DirectTwoStage.SelectVersion(a.Format),
                promptSha256 = P7F1V6DirectTwoStage.Sha(P7F1V6DirectTwoStage.SelectPrompt(a.Format)),
                budget = new { maxCalls = PostsPerArm, ceilingUsd = CeilingUsdPerArm, maxRequestBytes = MaxRequestBytes },
                earlyStop = P7F1V6DirectV2.MaxConsecutiveContractFailures,
                firstRequests = first.Select((r, i) => new { unitId = Segments[i], sha256 = r.Sha256, bytes = r.Bytes }).ToArray(),
                requestPlanSha256 = P7F1V6Protocol.Sha(string.Join("\n", first.Select(r => r.Sha256))),
            };
        }).ToArray();
        return new Dictionary<string, object?>
        {
            ["artifact"] = PlanArtifact,
            ["approvalToSend"] = false,
            ["codeCommit"] = codeCommit,
            ["authorityId"] = P7F1V6DirectD45.AuthorityId,
            ["document"] = P7F1V6DirectD45.Document,
            ["sourceSha256"] = u.SourceSha256,
            ["universeSha256"] = u.UniverseSha256,
            ["evidenceStoreSha256"] = u.EvidenceStoreSha256,
            ["atoms"] = u.Aliases.Count,
            ["evidenceParserVersion"] = P7F1QEvidenceTools.VersionV3,
            ["goldForScorerOnly"] = new { path = gold.Path, sha256 = gold.Sha256, sentToProvider = false, use = "offline diagnostic on the 144 targets only" },
            ["task"] = P7F1V6DirectD45.TaskId,
            ["evaluationLabel"] = EvaluationLabel,
            ["instruction"] = P7F1V6DirectD45.Instruction,
            ["instructionSha256"] = P7F1V6Core.InstructionSha256(P7F1V6DirectD45.Instruction),
            ["requestVersion"] = P7F1V6DirectTwoStage.SelectRequestVersion,
            ["model"] = P7F1V6Protocol.ChatModel,
            ["route"] = new { only = new[] { P7F1V6Protocol.ProviderTag }, ignore = P7F1V6Protocol.DefaultIgnore, allowFallbacks = false, requireParameters = true },
            ["zdr"] = "false (owner decision, accepted again in the authorization)",
            ["maxTokens"] = P7F1V6Protocol.MaxTokens,
            ["reasoning"] = "not declared (endpoint default)",
            ["retries"] = Retries,
            ["unit"] = Unit,
            ["overlap"] = Overlap,
            ["stages"] = "SELECT, stage SEGMENT only; no RESOLVE, FINALIZE or GROUP",
            ["segments"] = Segments,
            ["order"] = "per segment: arm B, then arm C",
            ["arms"] = arms,
            ["budget"] = new { maxCalls = PostsPerArm * Arms.Length, ceilingUsd = CeilingUsdPerArm * Arms.Length, maxRequestBytes = MaxRequestBytes,
                perArm = "each arm has its own budget and early stop; totals are the sum" },
            ["rawResponses"] = "safe store outside the repository, one run directory per arm",
            ["liveAuthorization"] = new { required = true, artifact = P7F1V6DirectAuthorization.Artifact, status = P7F1V6DirectAuthorization.Approved, campaigns = 1 },
        };
    }

    public static byte[] Serialize(Dictionary<string, object?> plan) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan, Indented) + "\n");

    public static IReadOnlyList<string> Verify(string repoRoot, string planPath, string confirmedSha256, string currentCommit)
    {
        var failures = new List<string>();
        if (!File.Exists(planPath)) return new[] { "PLAN_FILE_MISSING" };
        var bytes = File.ReadAllBytes(planPath);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != confirmedSha256) failures.Add("PLAN_SHA_NOT_CONFIRMED");
        using var doc = JsonDocument.Parse(bytes);
        var planCommit = doc.RootElement.GetProperty("codeCommit").GetString();
        if (planCommit != currentCommit) failures.Add("CODE_COMMIT_DIFFERS");
        if (!Serialize(PinnedValues(repoRoot, planCommit ?? "")).AsSpan().SequenceEqual(bytes)) failures.Add("PINNED_VALUES_DIFFER");
        return failures;
    }

    public sealed record ArmResult(string Arm, string Format, IReadOnlyList<P7F1V6DirectV2.UnitOutcome> Units,
        IReadOnlyList<P7F1V6DirectAuditedModel.LedgerEntry> Ledger, int Calls, double ReservedUsd, double ActualUsd, string? HaltReason,
        IReadOnlyList<string> Refusals, string SafeRunDirectory);

    /// <summary>Runs the three segments for both arms, interleaved, with a scripted or audited model per arm. Used by Execute and by tests.</summary>
    public static IReadOnlyList<(string Arm, F Format, List<P7F1V6DirectV2.UnitOutcome> Units, P7F1V6DirectV2.Budget Budget)> RunArms(
        P7F1V6Universe u, IReadOnlyDictionary<string, P7F1V6DirectV2.ReplyModel> modelPerArm, IReadOnlyDictionary<string, P7F1V6DirectV2.Budget> budgetPerArm)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < u.Lines.Count; i++) index[u.Lines[i].Alias] = i;
        var state = Arms.Select(a => (a.Arm, a.Format, Units: new List<P7F1V6DirectV2.UnitOutcome>(), Budget: budgetPerArm[a.Arm])).ToList();
        foreach (var segment in Segments)
        {
            var (decide, ctx) = Scope(u, segment);
            foreach (var s in state)
                s.Units.Add(P7F1V6DirectTwoStage.RunSelectUnit(s.Format, P7F1V6DirectD45.Instruction, u.SourceSha256, "SEGMENT", u.Lines, index,
                    segment, decide, ctx, modelPerArm[s.Arm], s.Budget));
        }
        return state;
    }

    /// <summary>Verifies the plan, the safe root and the authorization, marks the campaign consumed, then runs both arms through their own
    /// audited models. With a production handler this sends requests and needs its own approval; tests pass a fake handler.</summary>
    public static IReadOnlyList<ArmResult> Execute(string repoRoot, string planPath, string confirmedSha256, string currentCommit, string authorizationPath,
        string apiKey, HttpMessageHandler handler, string safeRoot)
    {
        var failures = Verify(repoRoot, planPath, confirmedSha256, currentCommit);
        if (failures.Count > 0) throw new InvalidOperationException("PLAN_REFUSED:" + string.Join(",", failures));
        var rootFailures = P7F1V6DirectD45.SafeRootFailures(repoRoot, safeRoot);
        if (rootFailures.Count > 0) throw new InvalidOperationException("SAFE_ROOT_REFUSED:" + string.Join(",", rootFailures));
        var authFailures = P7F1V6DirectAuthorization.Check(authorizationPath, planPath, confirmedSha256, safeRoot);
        if (authFailures.Count > 0) throw new InvalidOperationException("AUTHORIZATION_REFUSED:" + string.Join(",", authFailures));
        var marker = P7F1V6DirectD45.ConsumedMarker(safeRoot, confirmedSha256);
        try
        {
            using var fs = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { planSha256 = confirmedSha256,
                authorizationSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(authorizationPath))), startedUtc = DateTime.UtcNow.ToString("O") }) + "\n"));
        }
        catch (IOException) when (File.Exists(marker)) { throw new InvalidOperationException("CAMPAIGN_ALREADY_CONSUMED"); }

        var gold = P7F1V6DirectD45.ReadGoldPin(repoRoot);
        var u = P7F1V6Universe.Rebuild(repoRoot, P7F1V6DirectD45.Document, gold.SourceSha256, out var why) ?? throw new InvalidOperationException(why);
        var budgets = Arms.ToDictionary(a => a.Arm, _ => NewArmBudget());
        var stores = Arms.ToDictionary(a => a.Arm, _ => P7F1V6SafeRawStore.CreateRun(safeRoot));
        var models = Arms.ToDictionary(a => a.Arm, a => new P7F1V6DirectAuditedModel(
            new P7F1V6OpenRouterTransport(handler, TimeSpan.FromSeconds(300)), apiKey, stores[a.Arm], budgets[a.Arm]));
        var state = RunArms(u, models.ToDictionary(kv => kv.Key, kv => (P7F1V6DirectV2.ReplyModel)kv.Value.Reply), budgets);
        return state.Select(s => new ArmResult(s.Arm, s.Format.ToString(), s.Units, models[s.Arm].Ledger, s.Budget.Calls, s.Budget.ReservedUsd,
            s.Budget.ActualUsd, s.Budget.HaltReason, s.Budget.Refusals.ToArray(), stores[s.Arm].RunDirectory)).ToList();
    }
}
