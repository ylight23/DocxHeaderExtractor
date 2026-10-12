using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// D4.11 tool-calling canary (offline planning; a run needs its own plan SHA, budget and owner approval). Arm B's object answer, as an
/// evidence agent with native tool calls, on SEG-001 (contents pages) and SEG-008 (body) of SRC-095. Two arms over the same targets,
/// context, instruction, tools and model; only one prompt line differs:
///   AUTO: tools are optional (Issue #9: Qwen may answer without tools when the lines shown are enough);
///   EVIDENCE_FIRST: an answer before any successful tool result is refused. Diagnostic only: it proves whether the chain
///   tool_call -> C# handler -> tool message -> Qwen answer works on this route, since tool_choice required is not supported there.
/// Arms run interleaved per segment (AUTO, then EVIDENCE_FIRST), each with its own budget, early stop, audited model and safe-store run.
/// Development canary, not an F1 estimate. Gold is used only offline afterwards, for the diagnostic on the 96 targets.
/// </summary>
internal static class P7F1V6DirectAgentCanary
{
    public static readonly string[] Segments = { "SEG-001", "SEG-008" };
    public static readonly (string Arm, P7F1V6DirectAgent.Arm Mode)[] Arms = { ("AUTO", P7F1V6DirectAgent.Arm.Auto), ("EVIDENCE_FIRST", P7F1V6DirectAgent.Arm.EvidenceFirst) };
    public const int PostsPerArm = 8;                 // 2 segments x (3 tool turns + 1 final turn)
    public const double CeilingUsdPerArm = 0.25;      // >= 8 posts x the worst-case reserve of one 131,072-byte request
    public const int MaxRequestBytes = 131072;
    public const int Retries = 0;
    /// <summary>D4.11b. Earlier plans, never run, are superseded: D4.11 (e3f8e57e) by the tool_call_id and finish_reason checks of D4.11a,
    /// and D4.11a (51daf941) by the hard tool-message byte cap and the split telemetry of D4.11b.</summary>
    public const string PlanArtifact = "p7.f1.v6.d4.11e-tool-canary-plan.v1";
    /// <summary>The D4.11b campaign that ran (plan consumed): non-streaming, no reasoning field; Qwen made 0 tool calls in 4/4 replies.</summary>
    public const string PreviousCampaignPlanSha256 = "c50833bb0fe5ca8a609ee6d883e6c84dc86887a49ec0f4daebca26d494c83f08";
    public static readonly (string Sha256, string Reason)[] SupersededPlans =
    {
        ("e3f8e57eb43275db5166a27eb930afe85fa002f03b9e6076213063e00367ebcf", "D4.11a review: tool_call_id integrity and finish_reason/tool_calls consistency"),
        ("51daf9412732f9685b0d3ea36a7637a0e4d2b561ee7044d857c662c7fc2e6cfa", "D4.11b review: 64 KB tool-message cap not enforced on rejected messages (68,938 B in the dry-run); ToolInvoked undercounted refused turns"),
        ("9a6bcbd1aecd961a25e4a0c0c52359135750ab2f0ab4bc4642a9854634729697", "D4.11d review: 'event: error' with a payload lacking an error key passed the SSE check; the stored response was a decoded string encoded again, not the received bytes"),
        ("281ad5d0b99ce308e389121cf0c92b53521351b8858c1d0b34354f585610fadd", "D4.11e: the agent prompt is rewritten so that the model is told when and how to gather evidence by tool calls (prompt V2)"),
    };
    public const string EvaluationLabel = "Development canary — tool-calling chain on segments chosen from previous errors; Stage 1 only; not an F1 estimate";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static P7F1V6DirectV2.Budget NewArmBudget() => new(PostsPerArm, CeilingUsdPerArm, MaxRequestBytes);

    /// <summary>Turn 1 of one arm and segment: the deterministic part of the canary. Later turns depend on Qwen's tool calls.</summary>
    public static P7F1V6Protocol.PreparedRequest FirstRequest(P7F1V6DirectAgent.Arm arm, P7F1V6Universe u, string segment)
    {
        var (decide, ctx) = P7F1V6DirectCanary410.Scope(u, segment);
        return P7F1V6DirectAgent.Build(P7F1V6DirectAgent.Prompt(arm),
            P7F1V6DirectAgent.Payload(P7F1V6DirectD45.Instruction, u.SourceSha256, segment, decide, ctx.Select(i => u.Lines[i]).ToArray()),
            new System.Text.Json.Nodes.JsonArray(), "auto");
    }

    public static Dictionary<string, object?> PinnedValues(string repoRoot, string codeCommit)
    {
        var gold = P7F1V6DirectD45.ReadGoldPin(repoRoot);
        var u = P7F1V6Universe.Rebuild(repoRoot, P7F1V6DirectD45.Document, gold.SourceSha256, out var failure) ?? throw new InvalidOperationException(failure);
        var worst = P7F1V6Direct.AttemptUsd(MaxRequestBytes);
        var arms = Arms.Select(a =>
        {
            var first = Segments.Select(s => FirstRequest(a.Mode, u, s)).ToList();
            return new
            {
                arm = a.Arm,
                promptSha256 = P7F1V6DirectAgent.Sha(P7F1V6DirectAgent.Prompt(a.Mode)),
                answerWithoutTools = a.Mode == P7F1V6DirectAgent.Arm.Auto ? "allowed" : "refused: TOOL_EVIDENCE_REQUIRED_NOT_REQUESTED",
                budget = new { maxCalls = PostsPerArm, ceilingUsd = CeilingUsdPerArm, maxRequestBytes = MaxRequestBytes, worstCaseReserveUsd = Math.Round(PostsPerArm * worst, 6) },
                earlyStop = P7F1V6DirectV2.MaxConsecutiveContractFailures,
                firstRequests = first.Select((r, i) => new { unitId = Segments[i], turn = 1, sha256 = r.Sha256, bytes = r.Bytes }).ToArray(),
                requestPlanSha256 = P7F1V6Protocol.Sha(string.Join("\n", first.Select(r => r.Sha256))),
            };
        }).ToArray();
        return new Dictionary<string, object?>
        {
            ["artifact"] = PlanArtifact,
            ["approvalToSend"] = false,
            ["supersedes"] = SupersededPlans.Select(x => new { planSha256 = x.Sha256, run = false, reason = x.Reason }).ToArray(),
            ["previousCampaign"] = new { planSha256 = PreviousCampaignPlanSha256, run = true, consumed = true,
                outcome = "4 of 16 POSTs; Qwen emitted 0 tool calls in 4/4 replies (AUTO and EVIDENCE_FIRST); requests were non-streaming with no reasoning field" },
            ["promptChange"] = new
            {
                from = "P7_F1_V6_SELECT_AGENT_PROMPT_V1 (AUTO 3ffe3356…, EVIDENCE_FIRST f36f16eb…)",
                to = P7F1V6DirectAgent.PromptVersion,
                what = "The tools paragraph now says what the tools read (neighbouring lines, a page, a line's full record, text search, same-text lines, two lines side by side; observations only), that the request shows only part of the document, that before answering the model asks for each target whether the lines shown are enough under the instruction, that if not it gathers evidence first by replying with tool calls instead of the JSON (up to 8 calls per reply, 3 rounds), and that if they are enough it answers directly. AUTO still allows an answer without tools. EVIDENCE_FIRST adds that the first reply must be a batch of tool calls and to aim them at the targets for which the lines shown are least sufficient. evidenceRefs cite aliases a result shows (not evidence ids).",
                neverSays = "no kind of line is named and nothing says what any line is; what to select stays in the user's instruction; a regression bans structure vocabulary from the prompt",
                evidenceBehind = "D4.11b: with the V1 wording Qwen made 0 tool calls in 4/4 requests. F1Q: the arm whose prompt required a first reply of tool calls had them in 9/10, the arm that said only to call a tool when ambiguous had 0/10. The F1Q prompts named kinds of line; this one does not. Whether this wording makes Qwen call tools is unknown until a run."
            },
            ["changeFromPreviousCampaign"] = "two changes together: the request shape (\"stream\": true (SSE) and \"reasoning\": {\"enabled\": true} in every request) and the agent prompt (V2, see promptChange); tools, instruction, arms, segments, caps and budget unchanged",
            ["requestShapeEvidence"] = "F1Q raw (branch experiment/p7-f1-semantic-question-v1), verified from SSE, tool files and next-turn request bodies: every one of 1,422 F1Q turn requests was stream true with reasoning enabled true; all 45 turns with tool calls were tool_choice auto, finish_reason tool_calls, provider Alibaba; Issue #5 mandatory arm 9/10 requests with tool calls, 28/28 results OK and byte-identical in the next request under the same tool_call_id, final answer finish stop 10/10; optional arm 0/10. This is why the shape is changed; it is not evidence that V6 will get tool calls.",
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
            ["requestVersion"] = P7F1V6DirectAgent.RequestVersion,
            ["promptVersion"] = P7F1V6DirectAgent.PromptVersion,
            ["responseVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion,
            ["versionFieldsInPayload"] = false,
            ["needsEvidenceStatus"] = "refused in agent mode (NEEDS_EVIDENCE_NOT_IN_AGENT_MODE); evidence comes through tool calls",
            ["tools"] = new
            {
                version = P7F1V6AgentTools.Version,
                definitionsSha256 = P7F1V6AgentTools.DefinitionsSha256(),
                names = P7F1V6AgentTools.Names,
                lineage = P7F1V6AgentTools.Lineage,
                getPageImage = P7F1V6AgentTools.PageImageStatus,
                resultCapBytes = P7F1V6AgentTools.MaxResultBytes,
                argumentCapBytes = P7F1V6AgentTools.MaxArgumentBytes,
                transport = "native OpenRouter tools/tool_calls; each result returned as role tool with the call's tool_call_id",
            },
            ["toolCallIds"] = "Qwen's own ids only: every id non-empty and unique in the conversation; a missing or repeated id refuses the whole tool turn before any tool runs (TOOL_CALL_ID_MISSING, TOOL_CALL_ID_DUPLICATE); calls over a cap are answered REJECTED under their own id; C# never makes up an id",
            ["finishReason"] = "tool calls only with finish_reason tool_calls (else TOOL_CALLS_WITH_FINISH_<x>); an answer only with stop (else ANSWER_WITH_FINISH_<x>); tool_calls without calls is refused",
            ["toolMessageBytes"] = "hard cap 65,536 content bytes per unit over every role tool message, rejected and invalid ones included; a rejection is reserved for each call before a turn runs; a call runs only if a full 16,384-byte result plus the reserve for the calls after it and for the rejections of up to 8 calls in each later tool turn fits (else UNIT_TOOL_MESSAGE_BYTES_EXHAUSTED); a turn whose rejections alone do not fit is refused before dispatch (BUDGET:UNIT_TOOL_MESSAGE_BYTES_EXHAUSTED, unit INCOMPLETE); an unregistered tool name is never echoed back",
            ["promptCache"] = "telemetry only: cached_tokens, cache_write_tokens and cache_discount are read from each response when present (ABSENT, ZERO or POSITIVE) and kept in the ledger; no cache_control, no session_id, no response caching; request bytes unchanged",
            ["toolChoice"] = new { turns1To3 = "auto", turn4 = "none", required = "not used: unsupported on the Alibaba route (F1Q 2026-10-09)" },
            ["limits"] = new { P7F1V6DirectAgent.MaxToolTurns, P7F1V6DirectAgent.MaxCallsPerTurn, P7F1V6DirectAgent.MaxPostsPerUnit, P7F1V6DirectAgent.MaxToolMessageBytesPerUnit },
            ["model"] = P7F1V6Protocol.ChatModel,
            ["route"] = new { only = new[] { P7F1V6Protocol.ProviderTag }, ignore = P7F1V6Protocol.DefaultIgnore, allowFallbacks = false, requireParameters = true },
            ["zdr"] = "false (owner decision, to be accepted again in the authorization)",
            ["responseFormat"] = "json_object on every turn (as in F1Q plan V3 with tools on this route)",
            ["maxTokens"] = P7F1V6Protocol.MaxTokens,
            ["reasoning"] = "enabled true, stated in every request (D4.11c); reasoning on/off is deferred and not part of this canary",
            ["stream"] = "true: the reply is SSE. The stored response is the body exactly as received from HTTP (bytes, not a decoded string encoded again); a 2xx body that is not valid UTF-8 fails the call (RESPONSE_NOT_UTF8) and is kept as received. A stream fails the call (TRANSPORT:STREAM_*) if it lacks [DONE], has an error event in the payload (STREAM_ERROR), has \"event: error\" in any form (STREAM_ERROR_EVENT), any other named event (STREAM_NAMED_EVENT), a field other than data (STREAM_FIELD_UNKNOWN), data after [DONE] (STREAM_DATA_AFTER_DONE), a data event without choices or error (STREAM_EVENT_SHAPE_UNKNOWN) or a malformed event (STREAM_MALFORMED); comments and blank lines are allowed. The audit reads the view assembled from the stream.",
            ["reasoningUse"] = "never read to change an answer; stored with the raw response only",
            ["retries"] = Retries,
            ["unit"] = P7F1V6DirectCanary410.Unit,
            ["overlap"] = P7F1V6DirectCanary410.Overlap,
            ["stages"] = "SELECT, stage SEGMENT only; no RESOLVE, FINALIZE or GROUP",
            ["segments"] = Segments,
            ["order"] = "per segment: arm AUTO, then arm EVIDENCE_FIRST",
            ["arms"] = arms,
            ["budget"] = new { maxCalls = PostsPerArm * Arms.Length, ceilingUsd = CeilingUsdPerArm * Arms.Length, maxRequestBytes = MaxRequestBytes,
                perArm = "each arm has its own budget and early stop; totals are the sum" },
            ["measurements"] = new[]
            {
                "tool calls per arm, segment and tool name; invalid and rejected calls with their codes",
                "levels kept apart per unit: model_tool_calls_emitted (in Qwen's responses, refused turns included), tools_dispatched, tools_rejected_before_dispatch, tool_succeeded, tool_message_bytes, targets shown by a successful result (observation only), evidence_used (final refs to lines seen only through tools); relevant_evidence_observed is audited offline after the responses are frozen, not by C#",
                "EVIDENCE_FIRST counts any successful call (an overview included) as meeting its requirement; the four levels show whether it was more than that",
                "chain proof: a tool_call answered by a role tool message with the same tool_call_id, followed by a final answer",
                "use proof: final evidenceRefs that cite a line seen only through a tool result (RefsToToolOnlyLines)",
                "scopes: evaluated targets, lines observed (request and tools), whole document observed or not",
                "SEG-001: targets whose normalized text also occurs elsewhere, and whether a tool result showed that other occurrence before the answer",
                "protocol outcome per unit; offline diagnostic against Gold on the 96 targets (not F1)",
                "no-tool reference: the D4.10 arm B replies on the same segments (prompt differs; not a controlled comparison)",
            },
            ["rawResponses"] = "safe store outside the repository, one run directory per arm; tool results and request bodies kept with them",
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

    /// <summary>Both arms over both segments, interleaved, with a scripted or audited model per arm. Used by Execute and by tests.</summary>
    public static IReadOnlyList<(string Arm, List<P7F1V6DirectAgent.AgentUnitOutcome> Units, P7F1V6DirectV2.Budget Budget)> RunArms(
        P7F1V6Universe u, IReadOnlyDictionary<string, P7F1V6DirectAgent.AgentModel> modelPerArm, IReadOnlyDictionary<string, P7F1V6DirectV2.Budget> budgetPerArm)
    {
        var state = Arms.Select(a => (a.Arm, a.Mode, Units: new List<P7F1V6DirectAgent.AgentUnitOutcome>(), Budget: budgetPerArm[a.Arm])).ToList();
        foreach (var segment in Segments)
        {
            var (decide, ctx) = P7F1V6DirectCanary410.Scope(u, segment);
            foreach (var s in state)
                s.Units.Add(P7F1V6DirectAgent.RunUnit(s.Mode, P7F1V6DirectD45.Instruction, u, segment, decide, ctx, modelPerArm[s.Arm], s.Budget));
        }
        return state.Select(s => (s.Arm, s.Units, s.Budget)).ToList();
    }

    public sealed record ArmResult(string Arm, IReadOnlyList<P7F1V6DirectAgent.AgentUnitOutcome> Units, IReadOnlyList<P7F1V6DirectAuditedModel.LedgerEntry> Ledger,
        int Calls, double ReservedUsd, double ActualUsd, string? HaltReason, IReadOnlyList<string> Refusals, string SafeRunDirectory);

    /// <summary>Verifies the plan, the safe root and the authorization, marks the campaign consumed, then runs both arms. With a production
    /// handler this sends requests and needs its own approval; tests pass a fake handler.</summary>
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
        var state = RunArms(u, models.ToDictionary(kv => kv.Key, kv => (P7F1V6DirectAgent.AgentModel)kv.Value.Turn), budgets);
        return state.Select(s => new ArmResult(s.Arm, s.Units, models[s.Arm].Ledger, s.Budget.Calls, s.Budget.ReservedUsd, s.Budget.ActualUsd,
            s.Budget.HaltReason, s.Budget.Refusals.ToArray(), stores[s.Arm].RunDirectory)).ToList();
    }
}
