using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using A = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectAgent;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// D4.11i repeat canary (offline planning; a run needs its own plan SHA, budget and owner approval). D4.11h showed, on SRC-095, that when the
/// program puts the repeated-text observations in the request Qwen stops selecting the contents lines of SEG-001 (0 of 48 in 3 of 3 repetitions
/// against 46, 46, 48), without losing the heading of SEG-008. This plan asks whether that holds: more repetitions (5), more segments, a second
/// document, and above all segments where repeated text is not a contents list. Two arms over the same targets, context, instruction, tools and
/// route: CONTROL (no observations) and PREFETCH (observations), both with the first and last target stated in the request (the range slip of
/// D4.11h: an extra decision for the context line after the last target). One document per plan; each has its own authorization.
/// Segments are chosen by fixed rules from the parser and the Gold, which are recomputed by the plan check; Gold is never sent.
/// Development canary, not an F1 estimate.
/// </summary>
internal static class P7F1V6DirectRepeatCanary
{
    public sealed record Doc(string AuthorityId, string RelativePath, string Publicity, string[] Anchors);

    public static readonly IReadOnlyDictionary<string, Doc> Docs = new Dictionary<string, Doc>
    {
        ["SRC-095"] = new("SRC-095", P7F1V6DirectD45.Document, "IETF RFC 9114, published publicly by the IETF", new[] { "SEG-001", "SEG-008" }),
        ["SRC-042"] = new("SRC-042", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/042_IDA_Financial_Statements_June_2025.pdf",
            "International Development Association (World Bank Group) financial statements, June 2025; a document of the repository's public heading corpus (todo10_8/), whose README states the files are real documents downloaded from public sources; the origin is not independently verified here",
            Array.Empty<string>()),
    };

    public static readonly (string Arm, A.Arm Mode)[] Arms = { ("CONTROL", A.Arm.Auto), ("PREFETCH", A.Arm.Prefetch) };
    public const int Reps = 5;
    public const int MaxRequestBytes = 131072;
    public const int Retries = 0;
    public const string PlanArtifactPrefix = "p7.f1.v6.d4.11i-repeat-canary-plan";
    public const string EvaluationLabel = "Development canary: program-read evidence in the request, on segments chosen by fixed rules; Stage 1 only; not an F1 estimate";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string PlanFileName(string authorityId) => PlanArtifactPrefix + "." + authorityId + ".v1.json";

    // ---------------- segments ----------------

    public sealed record SegStat(string Segment, int Targets, int GoldAtoms, int Listed, int GoldListed, int NonGoldListed, int ObservationBytes);

    public static IReadOnlySet<string> GoldAtoms(string repoRoot, P7F1V6DirectD45.GoldPin gold)
    {
        using var g = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repoRoot, gold.Path)));
        return g.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray()
            .SelectMany(c => c.GetProperty("sourceParts").EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>For every 48-line segment: how many targets the prefetch read lists, and how many of the listed targets are Gold headings.</summary>
    public static IReadOnlyList<SegStat> Survey(P7F1V6Universe u, IReadOnlySet<string> gold)
    {
        var tools = new P7F1V6AgentTools(u.Tools);
        var stats = new List<SegStat>();
        for (int n = 0; n < u.Cores.Count; n++)
        {
            var (core, end) = u.Cores[n];
            var decide = Enumerable.Range(core, end - core).Select(i => u.Lines[i].Alias).ToList();
            var pf = tools.Prefetch(decide);
            var listed = pf.Observations["targets"]!.AsArray().Select(t => t!["alias"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            var goldHere = decide.Count(gold.Contains);
            stats.Add(new SegStat($"SEG-{n:000}", decide.Count, goldHere, pf.Listed, decide.Count(a => gold.Contains(a) && listed.Contains(a)),
                listed.Count(a => !gold.Contains(a)), pf.Bytes));
        }
        return stats;
    }

    public sealed record Pick(string Segment, string Rule, SegStat Stat);

    /// <summary>The fixed rules. Anchors first (earlier results to compare with). Then, each taking the best segment not yet chosen, ties to the
    /// lowest index: A, the most Gold headings whose text occurs elsewhere (the risk that evidence makes Qwen drop real headings); B, the most
    /// listed targets among segments with no Gold heading (repeated text that is not a heading); C, the most balanced mix of listed Gold and
    /// listed non-Gold; D, the fewest listed targets among segments with a Gold heading (the added prompt line with little to observe).</summary>
    public static IReadOnlyList<Pick> SelectSegments(Doc doc, IReadOnlyList<SegStat> stats)
    {
        var picks = new List<Pick>();
        bool Taken(SegStat s) => picks.Any(p => p.Segment == s.Segment);
        foreach (var a in doc.Anchors) picks.Add(new Pick(a, "ANCHOR (run in D4.11h)", stats.First(s => s.Segment == a)));
        void Add(string rule, IEnumerable<SegStat> ordered)
        {
            var best = ordered.FirstOrDefault(s => !Taken(s));
            if (best is not null) picks.Add(new Pick(best.Segment, rule, best));
        }
        Add("A: most Gold headings whose text occurs elsewhere", stats.Where(s => s.GoldAtoms > 0).OrderByDescending(s => s.GoldListed).ThenBy(s => s.Segment, StringComparer.Ordinal));
        Add("B: most listed targets among segments with no Gold heading", stats.Where(s => s.GoldAtoms == 0 && s.Listed > 0).OrderByDescending(s => s.Listed).ThenBy(s => s.Segment, StringComparer.Ordinal));
        Add("C: most balanced mix of listed Gold and listed non-Gold", stats.Where(s => s.GoldListed > 0 && s.NonGoldListed > 0).OrderByDescending(s => Math.Min(s.GoldListed, s.NonGoldListed)).ThenBy(s => s.Segment, StringComparer.Ordinal));
        Add("D: fewest listed targets among segments with a Gold heading", stats.Where(s => s.GoldAtoms > 0).OrderBy(s => s.Listed).ThenBy(s => s.Segment, StringComparer.Ordinal));
        return picks;
    }

    // ---------------- budget ----------------

    public static int Units(IReadOnlyList<Pick> picks) => picks.Count * Reps;
    public static int PostsPerArm(IReadOnlyList<Pick> picks) => Units(picks) + Math.Max(3, Units(picks) / 5);
    public static P7F1V6DirectV2.Budget NewArmBudget(IReadOnlyList<Pick> picks, double ceilingUsd) => new(PostsPerArm(picks), ceilingUsd, MaxRequestBytes);

    public static P7F1V6Protocol.PreparedRequest FirstRequest(A.Arm mode, P7F1V6Universe u, string segment)
    {
        var (decide, ctx) = P7F1V6DirectCanary410.Scope(u, segment);
        var (payload, _) = A.FirstPayload(mode, P7F1V6DirectD45.Instruction, u.SourceSha256, segment, decide, ctx.Select(i => u.Lines[i]).ToArray(),
            new P7F1V6AgentTools(u.Tools), rangeStated: true);
        return A.Build(A.Prompt(mode, rangeStated: true), payload, new System.Text.Json.Nodes.JsonArray(), "auto");
    }

    /// <summary>The ceiling is derived, not chosen: the reserve of every planned first request of the arm, repetitions included, plus the spare
    /// posts at the worst case (a request at the byte cap), plus 10%, rounded up to the cent. It bounds the reserve, not the expected spend.</summary>
    public static double CeilingUsd(P7F1V6Universe u, IReadOnlyList<Pick> picks, A.Arm mode)
    {
        var first = picks.Sum(p => P7F1V6Direct.AttemptUsd(FirstRequest(mode, u, p.Segment).Bytes)) * Reps;
        var spare = (PostsPerArm(picks) - Units(picks)) * P7F1V6Direct.AttemptUsd(MaxRequestBytes);
        return Math.Ceiling((first + spare) * 1.1 * 100) / 100;
    }

    // ---------------- plan ----------------

    public static Dictionary<string, object?> PinnedValues(string repoRoot, string codeCommit, string authorityId)
    {
        var doc = Docs[authorityId];
        var gold = P7F1V6DirectD45.ReadGoldPin(repoRoot, authorityId);
        var u = P7F1V6Universe.Rebuild(repoRoot, doc.RelativePath, gold.SourceSha256, out var failure) ?? throw new InvalidOperationException(failure);
        if (u.UniverseSha256 != gold.UniverseSha256) throw new InvalidOperationException("UNIVERSE_DIFFERS_FROM_GOLD");
        var atoms = GoldAtoms(repoRoot, gold);
        var stats = Survey(u, atoms);
        var picks = SelectSegments(doc, stats);
        var segs = picks.Select(p => p.Segment).ToArray();
        var worst = P7F1V6Direct.AttemptUsd(MaxRequestBytes);
        var ceilings = Arms.ToDictionary(a => a.Arm, a => CeilingUsd(u, picks, a.Mode));
        var arms = Arms.Select(a =>
        {
            var first = segs.Select(s => FirstRequest(a.Mode, u, s)).ToList();
            return new
            {
                arm = a.Arm,
                promptSha256 = A.Sha(A.Prompt(a.Mode, rangeStated: true)),
                budget = new { maxCalls = PostsPerArm(picks), ceilingUsd = ceilings[a.Arm], maxRequestBytes = MaxRequestBytes, worstCaseAllAtByteCapUsd = Math.Round(PostsPerArm(picks) * worst, 6) },
                earlyStop = P7F1V6DirectV2.MaxConsecutiveContractFailures,
                firstRequests = first.Select((r, i) => new { unitId = segs[i], turn = 1, sha256 = r.Sha256, bytes = r.Bytes }).ToArray(),
                requestPlanSha256 = P7F1V6Protocol.Sha(string.Join("\n", first.Select(r => r.Sha256))),
            };
        }).ToArray();
        string ProviderBlockSha()
        {
            using var d = JsonDocument.Parse(FirstRequest(A.Arm.Auto, u, segs[0]).Body);
            return P7F1V6Protocol.Sha(d.RootElement.GetProperty("provider").GetRawText());
        }
        var template = A.Build("<<SYSTEM_PROMPT>>", "<<USER_PAYLOAD>>", new System.Text.Json.Nodes.JsonArray(), "auto");
        var totalPosts = PostsPerArm(picks) * Arms.Length;
        var totalCeiling = Math.Round(ceilings.Values.Sum(), 2);
        return new Dictionary<string, object?>
        {
            ["artifact"] = PlanArtifactPrefix,
            ["approvalToSend"] = false,
            ["codeCommit"] = codeCommit,
            ["authorityId"] = authorityId,
            ["document"] = doc.RelativePath,
            ["documentPublicity"] = doc.Publicity,
            ["sourceSha256"] = u.SourceSha256,
            ["universeSha256"] = u.UniverseSha256,
            ["evidenceStoreSha256"] = u.EvidenceStoreSha256,
            ["atoms"] = u.Aliases.Count,
            ["evidenceParserVersion"] = P7F1QEvidenceTools.VersionV3,
            ["goldForScorerOnly"] = new { path = gold.Path, sha256 = gold.Sha256, universeSha256 = gold.UniverseSha256, sentToProvider = false,
                use = "offline: choosing the segments by the fixed rules below, and the diagnostic on their targets afterwards. Never part of any request." },
            ["task"] = P7F1V6DirectD45.TaskId,
            ["evaluationLabel"] = EvaluationLabel,
            ["instruction"] = P7F1V6DirectD45.Instruction,
            ["instructionSha256"] = P7F1V6Core.InstructionSha256(P7F1V6DirectD45.Instruction),
            ["requestVersion"] = A.RequestVersion,
            ["promptVersion"] = A.PromptVersion + " + stated target range",
            ["responseVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion,
            ["versionFieldsInPayload"] = false,
            ["whyThisPlan"] = new
            {
                d411h = "On SRC-095 (13 posts, plan a46eaeba, receipt in d411/): SEG-001 CONTROL selected 46, 46, 48 contents lines (none Gold); PREFETCH decided NOT_SELECT for all 48 targets in 3 of 3 repetitions; SEG-008's Gold heading was kept in every completed unit; 2 of 3 PREFETCH answers on SEG-001 were refused for one extra decision on the context line after the last target.",
                question = "Does the effect hold with more repetitions, on more segments, on another document, and where the repeated text is not a contents list? The risk to look for is the opposite one: evidence that a line's text occurs elsewhere may teach Qwen to drop real headings whose text repeats (segments of rule A), or to change nothing where there is nothing to observe (rule D).",
                rangeFix = "The request states targetFirstAlias and targetLastAlias right after targetCount, and the Fields paragraph of the prompt adds one sentence saying the targets are exactly the aliases from the first to the last and that context lines before the first and after the last are never decided. Nothing else in the prompt or payload changes; removing the two fields and the sentence gives back the D4.11h request (checked by a test).",
            },
            ["documents"] = "one document per plan, each with its own authorization; this plan: " + authorityId,
            ["segmentSelection"] = new
            {
                rules = new[]
                {
                    "ANCHOR: segments already run in D4.11h, kept for comparison (SRC-095 only)",
                    "A: the segment with the most Gold headings whose text occurs on another line (among segments with a Gold heading)",
                    "B: the segment with the most listed targets among segments with no Gold heading: repeated text that is not a heading",
                    "C: the segment with the most balanced mix of listed Gold and listed non-Gold targets",
                    "D: the segment with the fewest listed targets among segments with a Gold heading",
                    "each rule takes its best segment not already chosen; ties go to the lowest segment index; the plan check recomputes the picks from the parser and the Gold",
                },
                listedMeans = "the target's normalized text (the repeat normalization) occurs on another line of the document",
                picks = picks.Select(p => new { segment = p.Segment, rule = p.Rule, targets = p.Stat.Targets, goldAtoms = p.Stat.GoldAtoms, listed = p.Stat.Listed, goldListed = p.Stat.GoldListed,
                    nonGoldListed = p.Stat.NonGoldListed, observationBytes = p.Stat.ObservationBytes }).ToArray(),
                segmentsInDocument = stats.Count,
            },
            ["arms"] = arms,
            ["prefetch"] = new
            {
                rule = "A target is listed if its normalized text (the repeat normalization of get_repeated_occurrences) also occurs on another line of the document. That is the only criterion: nothing says what any line is.",
                shows = "for each listed target: how many other occurrences and on how many pages; for the first 2 other occurrences in reading order: alias, page, reading-order position and distance, whether the text is exactly the same (the text itself when it differs), layout, differences in font size, bold ratio and left edge (other minus target), and the line that follows it",
                normalization = P7F1QEvidenceTools.RepeatNormalization,
                caps = new { perTarget = P7F1V6AgentTools.PrefetchPerTarget, maxBytes = P7F1V6AgentTools.PrefetchMaxBytes, textChars = P7F1V6AgentTools.PrefetchTextChars,
                    overflow = "cut to 1 other occurrence per target, then by whole targets from the end; the object says so (complete false, omittedTargets)" },
                whereInRequest = "the last property of the user payload; every byte before it equals the CONTROL payload",
                notSharedWithAnyAnswer = "the observations come from the parser only; Gold and earlier answers are never used",
            },
            ["repetitions"] = new { perArmAndSegment = Reps, requests = "identical bytes each time, so the spread is sampling variation of the same request", order = "per repetition, per segment: CONTROL then PREFETCH" },
            ["frozen"] = new
            {
                scope = "one document: the runner rebuilds the universe from the pinned PDF and refuses any other document, source hash or universe hash; the plan is re-derived from code, source and Gold and any drift refuses the run",
                authorityId,
                document = doc.RelativePath,
                sourceSha256 = u.SourceSha256,
                universeSha256 = u.UniverseSha256,
                provider = new { only = new[] { P7F1V6Protocol.ProviderTag }, ignore = P7F1V6Protocol.DefaultIgnore, allowFallbacks = false, requireParameters = true,
                    providerBlockSha256 = ProviderBlockSha(), model = P7F1V6Protocol.ChatModel, endpointCheck = "response provider must be Alibaba and model qwen/qwen3.7-flash, else the run halts" },
                requestTemplateSha256 = template.Sha256,
                finalTurnTemplateSha256 = A.Build("<<SYSTEM_PROMPT>>", "<<USER_PAYLOAD>>", new System.Text.Json.Nodes.JsonArray(), "none").Sha256,
                toolDefinitionsSha256 = P7F1V6AgentTools.DefinitionsSha256(),
                promptSha256 = new { control = A.AutoPromptRangeSha256, prefetch = A.PrefetchPromptRangeSha256 },
                firstRequestsSha256 = P7F1V6Protocol.Sha(string.Join("\n", Arms.SelectMany(a => segs.Select(sg => FirstRequest(a.Mode, u, sg).Sha256)))),
                laterTurns = "turn 2 onward are built from the first request plus Qwen's own tool calls and the tool results; their bodies are stored with the run",
            },
            ["tools"] = new { version = P7F1V6AgentTools.Version, definitionsSha256 = P7F1V6AgentTools.DefinitionsSha256(), names = P7F1V6AgentTools.Names,
                resultCapBytes = P7F1V6AgentTools.MaxResultBytes, availability = "declared in both arms, optional (tool_choice auto on turns 1 to 3, none on turn 4)" },
            ["requestShape"] = "SSE (stream true), reasoning enabled true, response_format absent on turns 1 to 3 and json_object on turn 4, temperature 0, max_tokens 32768, usage include; the strict parser refuses any final answer that is not exactly one JSON object",
            ["limits"] = new { A.MaxToolTurns, A.MaxCallsPerTurn, A.MaxPostsPerUnit, A.MaxToolMessageBytesPerUnit },
            ["model"] = P7F1V6Protocol.ChatModel,
            ["route"] = new { only = new[] { P7F1V6Protocol.ProviderTag }, ignore = P7F1V6Protocol.DefaultIgnore, allowFallbacks = false, requireParameters = true },
            ["zdr"] = "false (owner decision, to be accepted again in the authorization)",
            ["maxTokens"] = P7F1V6Protocol.MaxTokens,
            ["retries"] = Retries,
            ["unit"] = P7F1V6DirectCanary410.Unit,
            ["overlap"] = P7F1V6DirectCanary410.Overlap,
            ["stages"] = "SELECT, stage SEGMENT only; no RESOLVE, FINALIZE or GROUP",
            ["segments"] = segs,
            ["budget"] = new
            {
                maxCalls = totalPosts, ceilingUsd = totalCeiling, maxRequestBytes = MaxRequestBytes,
                perArm = $"{PostsPerArm(picks)} posts: {Units(picks)} first posts ({segs.Length} segments x {Reps} repetitions) and {PostsPerArm(picks) - Units(picks)} for tool turns",
                ceilingIs = "derived: the reserve of every planned first request of the arm, repetitions included, plus the spare posts at the worst case (a request at the byte cap), plus 10%, rounded up to the cent. It bounds the reserve, not the expected spend (earlier requests cost about $0.001 to $0.0012 each).",
                ifToolsAreCalledMoreThanTheSpare = "a unit that needs a post beyond the arm's limit is stopped INCOMPLETE with BUDGET:CALL_LIMIT, which the report names; nothing is dropped silently",
            },
            ["measurements"] = new[]
            {
                "per arm, repetition and segment: selected lines, tp, fp, fn against the Gold of the 48 targets; per arm the sums over completed units, and the same by rule (A, B, C, D, anchors)",
                "protocol outcome per unit with its code; refused answers are counted and never repaired; offline counterfactuals (a stripped fence, a dropped extra decision) are labelled not official",
                "which lines were selected and whether the same lines are selected in every repetition of a cell",
                "decisions on context lines (UNEXPECTED_ALIAS), tool calls, cache hits, tokens and cost per request",
                "whether the reasoning mentions the observations (counts only; the reasoning is never used to change an answer)",
            },
            ["readingRules"] = new[]
            {
                "5 repetitions per cell: an indication, not a significance test; every repetition is reported",
                "false negatives (rules A and anchors): PREFETCH is not safe if, summed over completed units of these segments, its fn exceeds CONTROL's fn by more than 10% of the Gold headings in those units",
                "false positives (rule B and contents-like segments): the effect holds if PREFETCH's fp is below CONTROL's fp beyond the spread inside CONTROL",
                "rule D: the two arms should select alike; a difference there would come from the added prompt line or from noise",
                "if PREFETCH has more refused answers than CONTROL, that cost is reported next to its gain",
                "a result on one document says nothing about others; the second document is a separate plan",
            },
            ["rawPublication"] = "Raw SSE responses (reasoning included), request bodies, run summaries and the analysis stay in the safe store outside the repository. They are copied into the repository (d411-publish) and pushed only if the approving message says to push them.",
            ["comparedCampaigns"] = new object[]
            {
                new { planSha256 = "a46eaeba49fdfc35c8ce1c44810a51fbf265e9fb9f0c0284e8056b0c968adda3", note = "D4.11h, SRC-095, 3 repetitions, no stated range" },
            },
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
        var authority = doc.RootElement.GetProperty("authorityId").GetString() ?? "";
        if (!Docs.ContainsKey(authority)) { failures.Add("AUTHORITY_NOT_IN_PLAN_REGISTRY"); return failures; }
        if (!Serialize(PinnedValues(repoRoot, planCommit ?? "", authority)).AsSpan().SequenceEqual(bytes)) failures.Add("PINNED_VALUES_DIFFER");
        return failures;
    }

    // ---------------- running ----------------

    public sealed record RepUnit(int Rep, A.AgentUnitOutcome Outcome);

    /// <summary>All repetitions, segments and arms, interleaved: per repetition, per segment, CONTROL then PREFETCH. The ledger unit id carries the
    /// repetition ("SEG-031#r2"); the request payload does not.</summary>
    public static IReadOnlyList<(string Arm, List<RepUnit> Units, P7F1V6DirectV2.Budget Budget)> RunArms(
        P7F1V6Universe u, IReadOnlyList<string> segments, IReadOnlyDictionary<string, A.AgentModel> modelPerArm, IReadOnlyDictionary<string, P7F1V6DirectV2.Budget> budgetPerArm)
    {
        var state = Arms.Select(a => (a.Arm, a.Mode, Units: new List<RepUnit>(), Budget: budgetPerArm[a.Arm])).ToList();
        for (int rep = 1; rep <= Reps; rep++)
            foreach (var segment in segments)
            {
                var (decide, ctx) = P7F1V6DirectCanary410.Scope(u, segment);
                foreach (var s in state)
                {
                    var model = modelPerArm[s.Arm];
                    var r = rep;
                    s.Units.Add(new RepUnit(rep, A.RunUnit(s.Mode, P7F1V6DirectD45.Instruction, u, segment, decide, ctx,
                        (unit, turn, req) => model(unit + "#r" + r, turn, req), s.Budget, rangeStated: true)));
                }
            }
        return state.Select(s => (s.Arm, s.Units, s.Budget)).ToList();
    }

    public sealed record ArmResult(string Arm, IReadOnlyList<RepUnit> Units, IReadOnlyList<P7F1V6DirectAuditedModel.LedgerEntry> Ledger,
        int Calls, double ReservedUsd, double ActualUsd, string? HaltReason, IReadOnlyList<string> Refusals, string SafeRunDirectory);

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

        using var plan = JsonDocument.Parse(File.ReadAllBytes(planPath));
        var authority = plan.RootElement.GetProperty("authorityId").GetString()!;
        var segments = plan.RootElement.GetProperty("segments").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var ceilings = plan.RootElement.GetProperty("arms").EnumerateArray().ToDictionary(a => a.GetProperty("arm").GetString()!, a => a.GetProperty("budget").GetProperty("ceilingUsd").GetDouble());
        var posts = plan.RootElement.GetProperty("arms")[0].GetProperty("budget").GetProperty("maxCalls").GetInt32();
        var doc = Docs[authority];
        var gold = P7F1V6DirectD45.ReadGoldPin(repoRoot, authority);
        var u = P7F1V6Universe.Rebuild(repoRoot, doc.RelativePath, gold.SourceSha256, out var why) ?? throw new InvalidOperationException(why);
        var budgets = Arms.ToDictionary(a => a.Arm, a => new P7F1V6DirectV2.Budget(posts, ceilings[a.Arm], MaxRequestBytes));
        var stores = Arms.ToDictionary(a => a.Arm, _ => P7F1V6SafeRawStore.CreateRun(safeRoot));
        var models = Arms.ToDictionary(a => a.Arm, a => new P7F1V6AgentAuditedModel(new P7F1V6DirectAuditedModel(
            new P7F1V6OpenRouterTransport(handler, TimeSpan.FromSeconds(300)), apiKey, stores[a.Arm], budgets[a.Arm])));
        var state = RunArms(u, segments, models.ToDictionary(kv => kv.Key, kv => (A.AgentModel)kv.Value.Turn), budgets);
        return state.Select(s => new ArmResult(s.Arm, s.Units, models[s.Arm].Ledger, s.Budget.Calls, s.Budget.ReservedUsd, s.Budget.ActualUsd,
            s.Budget.HaltReason, s.Budget.Refusals.ToArray(), stores[s.Arm].RunDirectory)).ToList();
    }
}
