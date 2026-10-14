using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using A = DocxHeaderExtractor.V6Qualification.P7.P7F1V6DirectAgent;

namespace DocxHeaderExtractor.V6Qualification.P7;

/// <summary>
/// D4.11h prefetch canary (offline planning; a run needs its own plan SHA, budget and owner approval). Qwen did not call a tool in 12 of 12
/// requests over three campaigns (D4.11b, D4.11f, D4.11g), whatever the prompt, stream, reasoning or response_format. So the program reads the
/// evidence itself: for every target whose text also occurs on another line of the document it runs the neutral repeated-text read
/// (<see cref="P7F1V6AgentTools.Prefetch"/>) and puts the result in the request. The question: given that fact, does Qwen still select the
/// contents lines of SEG-001 (46 of 48 targets in 4 of 5 earlier answers, none of them Gold), and does it keep the real heading of SEG-008?
/// Two arms over the same targets, context, instruction, tools, route and prompt text: CONTROL is the D4.11g AUTO request byte for byte;
/// PREFETCH adds one prompt line and an observations object at the end of the payload. Each request is sent three times per arm and segment
/// (identical bytes) because the same request has given 46, 0, 46, 46 selections: a single answer per cell cannot be read.
/// Development canary, not an F1 estimate. Gold is used only offline afterwards.
/// </summary>
internal static class P7F1V6DirectPrefetchCanary
{
    public static readonly string[] Segments = { "SEG-001", "SEG-008" };
    public static readonly (string Arm, A.Arm Mode)[] Arms = { ("CONTROL", A.Arm.Auto), ("PREFETCH", A.Arm.Prefetch) };
    public const int Reps = 3;
    // Raised by the owner's permission to exceed the first budget (9 posts, $0.25 per arm), so that no unit can be stopped by the call limit:
    // 6 units per arm (2 segments x 3 repetitions) x the 4 posts a unit may use at most (3 tool turns and the forced-answer turn) = 24.
    public const int PostsPerArm = 24;
    public const double CeilingUsdPerArm = 0.65;      // 24 posts x the worst-case reserve of one 131,072-byte request (0.0262) = 0.629; a bound on the reserve, not the expected spend
    public const int MaxRequestBytes = 131072;
    public const int Retries = 0;
    public const string PlanArtifact = "p7.f1.v6.d4.11h-prefetch-canary-plan.v1";
    public const string EvaluationLabel = "Development canary: program-read evidence in the request, on segments chosen from earlier errors; Stage 1 only; not an F1 estimate";
    /// <summary>The D4.11g AUTO first requests, which the CONTROL arm must equal byte for byte.</summary>
    public static readonly IReadOnlyDictionary<string, string> D411gAutoFirstRequests = new Dictionary<string, string>
    {
        ["SEG-001"] = "1b1e2fa465415bdd78dff500c21a52b5bcc27c3afe2029b7da31d53b546cd05f",
        ["SEG-008"] = "88c464b808795b08e1d6862160df83660ac82b37c8477ad0100dc7c6b0235ca6",
    };

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static P7F1V6DirectV2.Budget NewArmBudget() => new(PostsPerArm, CeilingUsdPerArm, MaxRequestBytes);

    public static Dictionary<string, object?> PinnedValues(string repoRoot, string codeCommit)
    {
        var gold = P7F1V6DirectD45.ReadGoldPin(repoRoot);
        var u = P7F1V6Universe.Rebuild(repoRoot, P7F1V6DirectD45.Document, gold.SourceSha256, out var failure) ?? throw new InvalidOperationException(failure);
        var worst = P7F1V6Direct.AttemptUsd(MaxRequestBytes);
        var tools = new P7F1V6AgentTools(u.Tools);
        var arms = Arms.Select(a =>
        {
            var first = Segments.Select(s => P7F1V6DirectAgentCanary.FirstRequest(a.Mode, u, s)).ToList();
            return new
            {
                arm = a.Arm,
                promptSha256 = A.Sha(A.Prompt(a.Mode)),
                budget = new { maxCalls = PostsPerArm, ceilingUsd = CeilingUsdPerArm, maxRequestBytes = MaxRequestBytes, worstCaseReserveUsd = Math.Round(PostsPerArm * worst, 6) },
                earlyStop = P7F1V6DirectV2.MaxConsecutiveContractFailures,
                firstRequests = first.Select((r, i) => new { unitId = Segments[i], turn = 1, sha256 = r.Sha256, bytes = r.Bytes }).ToArray(),
                requestPlanSha256 = P7F1V6Protocol.Sha(string.Join("\n", first.Select(r => r.Sha256))),
            };
        }).ToArray();
        var prefetch = Segments.Select(s =>
        {
            var (decide, _) = P7F1V6DirectCanary410.Scope(u, s);
            var r = tools.Prefetch(decide);
            return new { segment = s, targets = decide.Length, listed = r.Listed, withoutOtherOccurrence = r.WithoutOther, omitted = r.Omitted, shownPerTarget = r.PerTarget,
                observationBytes = r.Bytes, linesShown = r.ShownAliases.Count };
        }).ToArray();
        var template = A.Build("<<SYSTEM_PROMPT>>", "<<USER_PAYLOAD>>", new System.Text.Json.Nodes.JsonArray(), "auto");
        string ProviderBlockSha()
        {
            using var d = JsonDocument.Parse(P7F1V6DirectAgentCanary.FirstRequest(A.Arm.Auto, u, Segments[0]).Body);
            return P7F1V6Protocol.Sha(d.RootElement.GetProperty("provider").GetRawText());
        }
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
            ["goldForScorerOnly"] = new { path = gold.Path, sha256 = gold.Sha256, sentToProvider = false, use = "offline diagnostic on the 96 targets only" },
            ["task"] = P7F1V6DirectD45.TaskId,
            ["evaluationLabel"] = EvaluationLabel,
            ["instruction"] = P7F1V6DirectD45.Instruction,
            ["instructionSha256"] = P7F1V6Core.InstructionSha256(P7F1V6DirectD45.Instruction),
            ["requestVersion"] = A.RequestVersion,
            ["promptVersion"] = A.PromptVersion,
            ["responseVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion,
            ["versionFieldsInPayload"] = false,
            ["whyThisPlan"] = new
            {
                observed = "Over three consumed campaigns (D4.11b c50833bb, D4.11f a57d260f, D4.11g 41376cd2) Qwen emitted 0 tool calls in 12 of 12 requests, with a tool-first instruction, a procedural prompt, SSE, reasoning enabled and response_format removed on the tool turns. Its reasoning says no tool call is needed because the information is sufficient.",
                selections = "SEG-001 AUTO selected the same 46 contents lines (pages 2 and 3, none Gold; 44 of them have another occurrence in the document that is a Gold heading) in D4.10 arm B, D4.11f and D4.11g, and none in D4.11b: 46, 0, 46, 46 on near-identical requests.",
                change = "The program reads the evidence itself and puts it in the request, instead of waiting for the model to ask. This no longer tests whether Qwen calls tools; it tests whether evidence, once in front of Qwen, changes what it selects.",
                precedent = "F1Q payloads already carried a program-computed repeated-text hint (correspondences) in every occurrence of the Issue #5 experiment.",
            },
            ["arms"] = arms,
            ["controlIdentity"] = new
            {
                rule = "The CONTROL first requests must equal the D4.11g AUTO first requests of plan 41376cd2 byte for byte (same prompt text, same payload, same envelope); checked by the plan and by a test.",
                d411gAutoFirstRequests = D411gAutoFirstRequests,
            },
            ["prefetch"] = new
            {
                rule = "A target is listed if its normalized text (the repeat normalization of get_repeated_occurrences) also occurs on another line of the document. That is the only criterion: nothing says what any line is.",
                shows = "for each listed target: how many other occurrences and on how many pages; for the first 2 other occurrences in reading order: alias, page, reading-order position and distance from the target, whether the text is exactly the same (the text itself when it differs), layout, differences in font size, bold ratio and left edge (other minus target), and the line that follows it",
                normalization = P7F1QEvidenceTools.RepeatNormalization,
                caps = new { perTarget = P7F1V6AgentTools.PrefetchPerTarget, maxBytes = P7F1V6AgentTools.PrefetchMaxBytes, textChars = P7F1V6AgentTools.PrefetchTextChars,
                    overflow = "cut to 1 other occurrence per target, then by whole targets from the end; the object says so (complete false, omittedTargets)" },
                whereInRequest = "the last property of the user payload, after targets and context; every byte before it equals the CONTROL payload",
                promptLine = "one added line (PREFETCH prompt SHA differs from CONTROL by that line only) saying the observations are facts read by the program, not labels, that evidenceRefs may cite the aliases they show, and when a target not listed has no other occurrence",
                measuredPerSegment = prefetch,
                notSharedWithAnyAnswer = "the observations come from the parser only; Gold and earlier answers are never used",
            },
            ["repetitions"] = new { perArmAndSegment = Reps, requests = "identical bytes each time, so the spread is sampling variation of the same request", order = "per repetition, per segment: CONTROL then PREFETCH" },
            ["frozen"] = new
            {
                scope = "SRC-095 only: the runner rebuilds the universe from the pinned PDF and refuses any other document, source hash or universe hash; the plan is re-derived from code and source and any drift refuses the run",
                document = P7F1V6DirectD45.Document,
                sourceSha256 = u.SourceSha256,
                universeSha256 = u.UniverseSha256,
                provider = new { only = new[] { P7F1V6Protocol.ProviderTag }, ignore = P7F1V6Protocol.DefaultIgnore, allowFallbacks = false, requireParameters = true,
                    providerBlockSha256 = ProviderBlockSha(), model = P7F1V6Protocol.ChatModel, endpointCheck = "response provider must be Alibaba and model qwen/qwen3.7-flash, else the run halts" },
                requestTemplateSha256 = template.Sha256,
                finalTurnTemplateSha256 = A.Build("<<SYSTEM_PROMPT>>", "<<USER_PAYLOAD>>", new System.Text.Json.Nodes.JsonArray(), "none").Sha256,
                toolDefinitionsSha256 = P7F1V6AgentTools.DefinitionsSha256(),
                promptSha256 = new { control = A.AutoPromptSha256, prefetch = A.PrefetchPromptSha256 },
                firstRequestsSha256 = P7F1V6Protocol.Sha(string.Join("\n", Arms.SelectMany(a => Segments.Select(sg => P7F1V6DirectAgentCanary.FirstRequest(a.Mode, u, sg).Sha256)))),
                laterTurns = "turn 2 onward are built from the first request plus Qwen's own tool calls and the tool results; their bodies are stored with the run",
            },
            ["tools"] = new { version = P7F1V6AgentTools.Version, definitionsSha256 = P7F1V6AgentTools.DefinitionsSha256(), names = P7F1V6AgentTools.Names,
                resultCapBytes = P7F1V6AgentTools.MaxResultBytes, availability = "declared in both arms, optional (tool_choice auto on turns 1 to 3, none on turn 4)" },
            ["toolResultCache"] = "per unit, in memory, as in plan 41376cd2 (repeats return the stored result and say so; counted in full toward the byte cap; never shared between units or arms)",
            ["promptCache"] = "telemetry only: cached_tokens, cache_write_tokens and cache_discount are read from each response when present; no cache_control, no session_id, no response caching",
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
            ["segments"] = Segments,
            ["budget"] = new { maxCalls = PostsPerArm * Arms.Length, ceilingUsd = CeilingUsdPerArm * Arms.Length, maxRequestBytes = MaxRequestBytes,
                perArm = "each arm has its own budget and early stop; totals are the sum",
                ifToolsAreCalled = "each arm has 24 posts, the most its 6 units can use (4 each), so the call limit cannot stop a unit; the dollar ceiling is the worst-case reserve of 24 posts at the byte cap, a bound and not the expected spend (the earlier requests cost about $0.001 to $0.0012 each)",
                raisedFrom = "9 posts and $0.25 per arm (18 posts, $0.50) in plan b76a2c7e, by the owner's permission to exceed the budget; nothing else in the plan changed" },
            ["measurements"] = new[]
            {
                "primary, per arm and repetition: SEG-001 selected lines, and how many are Gold (Gold has none among the 48 targets); SEG-008 selected lines against the 1 Gold atom (tp, fp, fn)",
                "the spread of those numbers across the 3 repetitions of one arm, against the difference between the arms",
                "which lines were selected: whether the 44 contents lines that have a Gold heading elsewhere are still selected, and whether the 2 running-header lines stay unselected",
                "whether an answer cites an alias that only the observations show (EvidenceUsedFromObservations), and whether the reasoning mentions the observations (counts only; the reasoning is never used to change an answer)",
                "tool calls per arm (any), invalid and rejected calls, cache hits",
                "protocol outcome per unit; parse failures are reported with their code, never repaired; a fence counterfactual is offline and not official",
                "tokens and cost per request, reasoning tokens, observation bytes",
            },
            ["readingRules"] = new[]
            {
                "3 repetitions per cell: an indication, not a significance test",
                "PREFETCH selects clearly fewer SEG-001 contents lines than CONTROL, beyond the spread inside each arm, and keeps SEG-008: the evidence changes the decision",
                "PREFETCH selects the same lines as CONTROL: Qwen is shown that the text occurs elsewhere and still selects it, so the cause is not missing evidence of that kind",
                "PREFETCH loses the SEG-008 heading or selects other lines: the observations disturb the decision, to be examined before any use",
                "CONTROL differs between its own repetitions: the same request is unstable, which bounds what any single run shows",
            },
            ["rawPublication"] = "Raw SSE responses (reasoning included), request bodies, run summaries and the analysis stay in the safe store outside the repository. They are copied into the repository (d411-publish) and pushed only if the approving message says to push them.",
            ["comparedCampaigns"] = new object[]
            {
                new { planSha256 = "c50833bb0fe5ca8a609ee6d883e6c84dc86887a49ec0f4daebca26d494c83f08", toolCalls = "0/4" },
                new { planSha256 = "a57d260f3eb8eb506b75556008a34e3e805e3c0e698ea16df2681edf3fba8137", toolCalls = "0/4", rawIn = "d411/run-a57d260f/" },
                new { planSha256 = "41376cd2d11e011d90396e20b4b0ae06af14c3a676fa7bad33da27476308ed0e", toolCalls = "0/4", note = "its AUTO first requests are the CONTROL arm of this plan" },
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
        if (!Serialize(PinnedValues(repoRoot, planCommit ?? "")).AsSpan().SequenceEqual(bytes)) failures.Add("PINNED_VALUES_DIFFER");
        return failures;
    }

    public sealed record RepUnit(int Rep, A.AgentUnitOutcome Outcome);

    /// <summary>All repetitions, segments and arms, interleaved: per repetition, per segment, CONTROL then PREFETCH. The unit id given to the
    /// model (and so kept in its ledger) carries the repetition, "SEG-001#r2"; the request payload does not, so the repetitions are identical
    /// requests. Used by Execute and by tests.</summary>
    public static IReadOnlyList<(string Arm, List<RepUnit> Units, P7F1V6DirectV2.Budget Budget)> RunArms(
        P7F1V6Universe u, IReadOnlyDictionary<string, A.AgentModel> modelPerArm, IReadOnlyDictionary<string, P7F1V6DirectV2.Budget> budgetPerArm)
    {
        var state = Arms.Select(a => (a.Arm, a.Mode, Units: new List<RepUnit>(), Budget: budgetPerArm[a.Arm])).ToList();
        for (int rep = 1; rep <= Reps; rep++)
            foreach (var segment in Segments)
            {
                var (decide, ctx) = P7F1V6DirectCanary410.Scope(u, segment);
                foreach (var s in state)
                {
                    var model = modelPerArm[s.Arm];
                    var r = rep;
                    s.Units.Add(new RepUnit(rep, A.RunUnit(s.Mode, P7F1V6DirectD45.Instruction, u, segment, decide, ctx,
                        (unit, turn, req) => model(unit + "#r" + r, turn, req), s.Budget)));
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

        var gold = P7F1V6DirectD45.ReadGoldPin(repoRoot);
        var u = P7F1V6Universe.Rebuild(repoRoot, P7F1V6DirectD45.Document, gold.SourceSha256, out var why) ?? throw new InvalidOperationException(why);
        var budgets = Arms.ToDictionary(a => a.Arm, _ => NewArmBudget());
        var stores = Arms.ToDictionary(a => a.Arm, _ => P7F1V6SafeRawStore.CreateRun(safeRoot));
        var models = Arms.ToDictionary(a => a.Arm, a => new P7F1V6AgentAuditedModel(new P7F1V6DirectAuditedModel(
            new P7F1V6OpenRouterTransport(handler, TimeSpan.FromSeconds(300)), apiKey, stores[a.Arm], budgets[a.Arm])));
        var state = RunArms(u, models.ToDictionary(kv => kv.Key, kv => (A.AgentModel)kv.Value.Turn), budgets);
        return state.Select(s => new ArmResult(s.Arm, s.Units, models[s.Arm].Ledger, s.Budget.Calls, s.Budget.ReservedUsd, s.Budget.ActualUsd,
            s.Budget.HaltReason, s.Budget.Refusals.ToArray(), stores[s.Arm].RunDirectory)).ToList();
    }
}
