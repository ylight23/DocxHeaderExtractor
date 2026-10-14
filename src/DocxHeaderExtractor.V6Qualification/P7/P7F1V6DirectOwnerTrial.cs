using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using A = DocxHeaderExtractor.V6Qualification.P7.P7F1V6DirectAgent;
using C = DocxHeaderExtractor.V6Qualification.P7.P7F1V6DirectRepeatCanary;

namespace DocxHeaderExtractor.V6Qualification.P7;

/// <summary>
/// D4.11j owner-document trial (offline planning; a run needs the owner's approval of this exact plan and of sending this document). The owner asked
/// to see which lines Qwen selects as headings, under the request of the repeat canary, in a document from outside the public corpus. There is no
/// Gold for it, so nothing is scored: the result is the set of lines each arm selects in each repetition, read by the owner. The same two arms
/// (CONTROL, PREFETCH), 5 identical repetitions, the stated target range, the same prompts, instruction, tools and route; every segment of the
/// document. The document is read from a path given at plan and run time and pinned by its SHA-256; telephone numbers and e-mail addresses are
/// replaced by placeholders before any text reaches a request, a tool result or the prefetch. Raw material never leaves the safe store.
/// </summary>
internal static class P7F1V6DirectOwnerTrial
{
    public const string PlanArtifact = "p7.f1.v6.d4.11j-owner-document-trial-plan.v1";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Sha256Of(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    public static string AuthorityIdOf(string fileSha256) => "OWNER-DOC-" + fileSha256[..8];

    public static IReadOnlyList<string> SegmentsOf(P7F1V6Universe u) =>
        Enumerable.Range(0, u.Cores.Count).Select(n => $"SEG-{n:000}").ToArray();

    public static P7F1V6Universe Load(string documentPath, string fileSha256, string segmentation = P7F1V6Universe.FixedSegmentation, int factsVersion = 1) =>
        (P7F1V6Universe.RebuildOwnerSupplied(documentPath, fileSha256, null, out var why) ?? throw new InvalidOperationException(why)).WithSegmentation(segmentation).WithFactsVersion(factsVersion);

    /// <summary>The layout-facts version a plan pins; a plan without the field uses version 1.</summary>
    public static int FactsVersionOf(JsonElement plan) => plan.TryGetProperty("layoutFactsVersion", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1;

    /// <summary>The segmentation a plan pins; a plan without the field is the original fixed 48-line cut.</summary>
    public static (string Arm, A.Arm Mode)[] ArmsOf(bool geometry, bool single = false, bool factsUse = false, bool toolFacts = false, bool toolFactsAnalysis = false, bool promptFactsV2 = false, bool hideLayout = false) => hideLayout ? C.ArmsToolFactsAnalysisV3Only : promptFactsV2 ? C.ArmsToolFactsAnalysisV2Only : toolFactsAnalysis ? C.ArmsToolFactsAnalysisOnly : toolFacts ? C.ArmsToolFactsOnly : factsUse ? C.ArmsFactsOnly : single ? C.ArmsGeometryOnly : geometry ? C.ArmsWithGeometry : C.Arms;

    public static int RepsOf(JsonElement plan) => plan.GetProperty("repetitions").GetProperty("perArmAndSegment").GetInt32();

    /// <summary>The number of leading segments a plan runs; 0 means every segment.</summary>
    public static int FirstSegmentsOf(JsonElement plan) => plan.TryGetProperty("firstSegmentsOnly", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    /// <summary>True when the file lies in the repository's public heading corpus (todo10_8/heading_corpus_100).</summary>
    public static bool InPublicCorpus(string documentPath) => Path.GetFullPath(documentPath).Replace('\\', '/').Contains("/todo10_8/heading_corpus_100/", StringComparison.Ordinal);

    /// <summary>The user instruction a plan pins; a plan from before D4.12c carries the Vietnamese structural instruction.</summary>
    public static string InstructionOf(JsonElement plan) => plan.GetProperty("instruction").GetString()!;

    public static int RetriesOf(JsonElement plan) => plan.TryGetProperty("failureHandling", out var f) ? f.GetProperty("transportRetries").GetInt32() : 0;
    public static bool RepairOf(JsonElement plan) => plan.TryGetProperty("failureHandling", out var f) && f.GetProperty("repairTurn").GetBoolean();
    public static bool CacheHistoryMarkOf(JsonElement plan) => plan.TryGetProperty("promptCacheHistoryMark", out var v) && v.ValueKind == JsonValueKind.String;
    public static bool PromptCacheOf(JsonElement plan) => plan.TryGetProperty("promptCache", out var v) && v.ValueKind == JsonValueKind.String;
    public static int MaxRepairsOf(JsonElement plan) => plan.TryGetProperty("failureHandling", out var f) && f.TryGetProperty("maxRepairs", out var m) ? m.GetInt32() : 1;

    public static bool HideLayoutOf(JsonElement plan) => plan.TryGetProperty("hideLayoutArm", out var v) && v.ValueKind == JsonValueKind.String;

    public static bool PromptFactsV2Of(JsonElement plan) => plan.TryGetProperty("promptFactsV2Arm", out var v) && v.ValueKind == JsonValueKind.String;

    public static bool ToolFactsAnalysisOf(JsonElement plan) => plan.TryGetProperty("toolFactsAnalysisArm", out var v) && v.ValueKind == JsonValueKind.String;

    public static bool ToolFactsOf(JsonElement plan) => plan.TryGetProperty("toolFactsArm", out var v) && v.ValueKind == JsonValueKind.String;

    public static bool ToolRoundsOf(JsonElement plan) => plan.TryGetProperty("toolRoundsBudget", out var v) && v.ValueKind == JsonValueKind.String;

    public static bool FactsUseOf(JsonElement plan) => plan.TryGetProperty("factsUseArm", out var v) && v.ValueKind == JsonValueKind.String;

    public static bool SingleArmOf(JsonElement plan) => plan.TryGetProperty("singleArm", out var v) && v.ValueKind == JsonValueKind.String;

    public static bool FenceUnwrapOf(JsonElement plan) => plan.TryGetProperty("fenceUnwrap", out var v) && v.ValueKind == JsonValueKind.String;

    public static bool GeometryArmOf(JsonElement plan) => plan.TryGetProperty("geometryArm", out var v) && v.ValueKind == JsonValueKind.String;

    public static bool JsonAllTurnsOf(JsonElement plan) => plan.TryGetProperty("responseFormatTurns", out var v) && v.ValueKind == JsonValueKind.String;

    public static string SegmentationOf(JsonElement plan) =>
        plan.TryGetProperty("segmentation", out var s) && s.ValueKind == JsonValueKind.Object ? s.GetProperty("policy").GetString()! : P7F1V6Universe.FixedSegmentation;

    public static Dictionary<string, object?> PinnedValues(string documentPath, string codeCommit, string segmentation = P7F1V6Universe.FixedSegmentation, bool jsonObjectAllTurns = false, bool geometryArm = false, bool unwrapFence = false, bool singleArm = false, bool factsUse = false, int reps = C.Reps, int firstSegments = 0, string instruction = P7F1V6DirectD45.Instruction, bool fullToolRounds = false, bool toolFacts = false, bool toolFactsAnalysis = false, int factsVersion = 1, bool promptFactsV2 = false, bool hideLayout = false, int transportRetries = 0, bool repairTurn = false, int maxRepairs = 1, bool promptCache = false, bool cacheHistoryMark = false)
    {
        if (cacheHistoryMark && !promptCache) throw new ArgumentException("HISTORY_MARK_NEEDS_PROMPT_CACHE", nameof(cacheHistoryMark));
        // The spare calls of a unit: its retries and two posts per repair; a repair with no limit of its own is budgeted as four repairs, and the arm's call limit is the bound.
        var extraPerUnit = transportRetries + (repairTurn ? 2 * (maxRepairs < 0 ? 4 : maxRepairs) : 0);
        if (hideLayout && factsVersion != 3) throw new ArgumentException("HIDE_LAYOUT_NEEDS_FACTS_V3", nameof(hideLayout));
        if (promptFactsV2 && factsVersion != 2) throw new ArgumentException("PROMPT_FACTS_V2_NEEDS_FACTS_V2", nameof(promptFactsV2));
        var fileSha = Sha256Of(documentPath);
        var u = Load(documentPath, fileSha, segmentation, factsVersion);
        var segs = firstSegments > 0 ? SegmentsOf(u).Take(firstSegments).ToArray() : SegmentsOf(u);
        var picks = segs.Select(s => new C.Pick(s, "ALL: every segment of the document", new C.SegStat(s, 0, 0, 0, 0, 0, 0))).ToArray();
        var armSet = ArmsOf(geometryArm, singleArm, factsUse, toolFacts, toolFactsAnalysis, promptFactsV2, hideLayout);
        var ceilings = armSet.ToDictionary(a => a.Arm, a => C.CeilingUsd(u, picks, a.Mode, jsonObjectAllTurns, reps, instruction, fullToolRounds, extraPerUnit));
        var worst = P7F1V6Direct.AttemptUsd(C.MaxRequestBytes);
        var arms = armSet.Select(a =>
        {
            var first = segs.Select(s => C.FirstRequest(a.Mode, u, s, jsonObjectAllTurns, instruction, promptCache)).ToList();
            return new
            {
                arm = a.Arm,
                promptSha256 = A.Sha(A.Prompt(a.Mode, rangeStated: true)),
                budget = new { maxCalls = C.PostsPerArm(picks, reps, fullToolRounds, extraPerUnit), ceilingUsd = ceilings[a.Arm], maxRequestBytes = C.MaxRequestBytes, worstCaseAllAtByteCapUsd = Math.Round(C.PostsPerArm(picks, reps, fullToolRounds, extraPerUnit) * worst, 6) },
                earlyStop = P7F1V6DirectV2.MaxConsecutiveContractFailures,
                firstRequests = first.Select((r, i) => new { unitId = segs[i], turn = 1, sha256 = r.Sha256, bytes = r.Bytes }).ToArray(),
                requestPlanSha256 = P7F1V6Protocol.Sha(string.Join("\n", first.Select(r => r.Sha256))),
            };
        }).ToArray();
        string ProviderBlockSha()
        {
            using var d = JsonDocument.Parse(C.FirstRequest(A.Arm.Auto, u, segs[0], jsonObjectAllTurns, instruction).Body);
            return P7F1V6Protocol.Sha(d.RootElement.GetProperty("provider").GetRawText());
        }
        var template = A.Build("<<SYSTEM_PROMPT>>", "<<USER_PAYLOAD>>", new System.Text.Json.Nodes.JsonArray(), "auto");
        var authority = AuthorityIdOf(fileSha);
        var pages = u.Lines.Select(l => l.Page).Distinct().Count();
        var fileName = Path.GetFileName(documentPath);
        var publicCorpus = InPublicCorpus(documentPath);
        var armNames = armSet.Select(a => a.Arm).ToArray();
        var factsToolDeclared = toolFacts || toolFactsAnalysis || promptFactsV2 || hideLayout;
        // D4.12t: the prose of a plan follows its flags. What the request has that the repeat-canary request has not; a plan with none of these
        // keeps the D4.11j wording byte for byte.
        var requestDiffers = new List<string>();
        if (!armNames.SequenceEqual(C.Arms.Select(a => a.Arm))) requestDiffers.Add("arms " + string.Join(", ", armNames) + " instead of CONTROL and PREFETCH");
        if (segmentation != P7F1V6Universe.FixedSegmentation) requestDiffers.Add("segments cut by " + segmentation);
        if (instruction != P7F1V6DirectD45.Instruction) requestDiffers.Add("instruction " + P7F1V6Core.InstructionSha256(instruction)[..8] + "…");
        if (jsonObjectAllTurns) requestDiffers.Add("response_format json_object on every turn");
        if (factsVersion != 1) requestDiffers.Add("layout facts V" + factsVersion);
        if (hideLayout) requestDiffers.Add("no layout in the request and documentStyles in the payload");
        if (promptCache) requestDiffers.Add("the system prompt and the payload as cache-marked text blocks");        var prefetch = segs.Select(s =>
        {
            var (decide, _) = P7F1V6DirectCanary410.Scope(u, s);
            var r = new P7F1V6AgentTools(u.Tools).Prefetch(decide);
            return new { segment = s, targets = decide.Length, listed = r.Listed, withoutOtherOccurrence = r.WithoutOther, observationBytes = r.Bytes };
        }).ToArray();
        var plan = new Dictionary<string, object?>
        {
            ["artifact"] = PlanArtifact,
            ["approvalToSend"] = false,
            ["codeCommit"] = codeCommit,
            ["authorityId"] = authority,
            ["document"] = "owner-supplied (outside the repository): " + fileName,
            ["documentPublicity"] = publicCorpus
                ? "A document of the repository's public heading corpus (todo10_8/heading_corpus_100), whose README states the files are real documents downloaded from public sources (" + fileName + "); the origin is not independently verified here. Text is sent as it is. Requests, raw responses and the analysis stay in the safe store and are not committed or pushed."
                : "NOT part of the repository's public corpus. A document supplied by the owner (" + fileName + "), read from outside the repository; whether it is public is not known to the program. "
                + "The owner decided that nothing is redacted: the text of every line, including any names, telephone numbers and e-mail addresses it contains, is sent as it is. "
                + "Requests, raw responses and the analysis stay in the safe store and are not committed or pushed.",
            ["sourceSha256"] = fileSha,
            ["universeSha256"] = u.UniverseSha256,
            ["evidenceStoreSha256"] = u.EvidenceStoreSha256,
            ["atoms"] = u.Aliases.Count,
            ["ownerSupplied"] = new
            {
                fileName, fileBytes = new FileInfo(documentPath).Length, fileSha256 = fileSha, pagesWithText = pages, lines = u.Lines.Count,
                publicCorpus,
                sourceGate = "the public-corpus source gate is not used; this plan pins the file hash and the loader refuses any other bytes",
                pathNotPinned = "the file path is an argument of the plan check and of the run, not part of the plan",
                redaction = "none: the owner chose to send the document text unchanged",
                lineTextsSha256 = P7F1V6Protocol.Sha(string.Join("\n", u.Lines.Select(l => l.Alias + "\t" + l.Text))),
            },
            ["goldForScorerOnly"] = publicCorpus
                ? "not used by the run: no Gold is read, sent or scored while the plan runs. The document belongs to the public corpus and may have an all-heading Gold in the registry; any score against it is computed offline afterwards from the stored decisions (tools/p7_f1_v6_d412_freeze.py) and is not part of this plan."
                : "none: there is no Gold for this document. Nothing is scored. The result is the set of lines each arm selects in each repetition, read by the owner.",
            ["task"] = P7F1V6DirectD45.TaskId,
            ["evaluationLabel"] = requestDiffers.Count == 0
                ? "Owner-document trial: which lines Qwen selects as headings under the repeat-canary request; no Gold, no score"
                : "Owner-document trial: which lines Qwen selects as headings under this plan's request (arms " + string.Join(", ", armNames) + "); no score during the run",
            ["instruction"] = instruction,
            ["instructionSha256"] = P7F1V6Core.InstructionSha256(instruction),
            ["requestVersion"] = A.RequestVersion,
            ["promptVersion"] = A.PromptVersion + " + stated target range",
            ["responseVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion,
            ["versionFieldsInPayload"] = false,
            ["requestIdenticalTo"] = requestDiffers.Count == 0
                ? "the repeat-canary request (plans e73415f7 and df369552, code b672c59): same system prompts (CONTROL 5a83e083…, PREFETCH 2290301b…), instruction, payload layout, tools, envelope and route; only the document differs"
                : "none: built on the repeat-canary request (plans e73415f7 and df369552, code b672c59) with these differences besides the document: " + string.Join("; ", requestDiffers) + "; the arms' prompt hashes are in arms[].promptSha256",
            ["segments"] = segs,
            ["arms"] = arms,
            ["prefetchMeasuredPerSegment"] = prefetch,
            ["repetitions"] = new { perArmAndSegment = reps, requests = "identical bytes each time, so the spread is sampling variation of the same request", order = armNames.Length == 1 ? "per repetition, per segment: " + armNames[0] + " only" : "per repetition, per segment: " + string.Join(" then ", armNames) },
            ["frozen"] = new
            {
                scope = "one owner-supplied document: the loader refuses any other bytes; the plan is re-derived from code and the file and any drift refuses the run",
                authorityId = authority,
                sourceSha256 = fileSha,
                universeSha256 = u.UniverseSha256,
                provider = new { only = new[] { P7F1V6Protocol.ProviderTag }, ignore = P7F1V6Protocol.DefaultIgnore, allowFallbacks = false, requireParameters = true,
                    providerBlockSha256 = ProviderBlockSha(), model = P7F1V6Protocol.ChatModel, endpointCheck = "response provider must be Alibaba and model qwen/qwen3.7-flash, else the run halts" },
                requestTemplateSha256 = template.Sha256,
                toolDefinitionsSha256 = P7F1V6AgentTools.DefinitionsSha256(factsToolDeclared),
                promptSha256 = armNames.SequenceEqual(C.Arms.Select(a => a.Arm))
                    ? (object)new { control = A.AutoPromptRangeSha256, prefetch = A.PrefetchPromptRangeSha256 }
                    : armSet.ToDictionary(a => a.Arm, a => A.Sha(A.Prompt(a.Mode, rangeStated: true))),
                firstRequestsSha256 = P7F1V6Protocol.Sha(string.Join("\n", armSet.SelectMany(a => segs.Select(sg => C.FirstRequest(a.Mode, u, sg, jsonObjectAllTurns, instruction, promptCache).Sha256)))),
            },
            ["tools"] = new { version = P7F1V6AgentTools.Version, definitionsSha256 = P7F1V6AgentTools.DefinitionsSha256(factsToolDeclared), names = factsToolDeclared ? P7F1V6AgentTools.Names.Append(P7F1V6AgentTools.LineFacts).ToArray() : P7F1V6AgentTools.Names,
                availability = factsToolDeclared
                    ? "declared on every turn with tool_choice auto; the first reply must call get_line_facts (an answer before a successful call is refused, PROTOCOL:TOOL_FACTS_REQUIRED_NOT_REQUESTED); every other call is optional; tool results come from the document lines"
                    : armNames.Length == 1 ? "declared in the arm, optional; tool results come from the document lines" : "declared in " + (armNames.Length == 2 ? "both" : "all") + " arms, optional; tool results come from the document lines" },
            ["requestShape"] = "SSE (stream true), reasoning enabled true, "
                + (jsonObjectAllTurns ? "response_format json_object on every turn" : "response_format absent on turns 1 to 3 and json_object on turn 4")
                + ", temperature 0, max_tokens 32768; the strict parser refuses any final answer that is not exactly one JSON object"
                + (unwrapFence ? " (after the single-fence unwrap, see fenceUnwrap)" : ""),
            ["model"] = P7F1V6Protocol.ChatModel,
            ["route"] = new { only = new[] { P7F1V6Protocol.ProviderTag }, ignore = P7F1V6Protocol.DefaultIgnore, allowFallbacks = false, requireParameters = true },
            ["zdr"] = "false (owner decision, to be accepted again in the authorization)",
            ["maxTokens"] = P7F1V6Protocol.MaxTokens,
            ["retries"] = transportRetries,
            ["budget"] = new
            {
                maxCalls = C.PostsPerArm(picks, reps, fullToolRounds, extraPerUnit) * armSet.Length, ceilingUsd = Math.Round(ceilings.Values.Sum(), 2), maxRequestBytes = C.MaxRequestBytes,
                perArm = $"{C.PostsPerArm(picks, reps, fullToolRounds, extraPerUnit)} posts: {C.Units(picks, reps)} first posts ({segs.Count} segments x {reps} repetitions) and {C.PostsPerArm(picks, reps, fullToolRounds, extraPerUnit) - C.Units(picks, reps)} for tool turns",
                ceilingIs = "derived as in the repeat canary: the reserve of every planned first request, repetitions included, plus the spare posts at the worst case, plus 10%; a bound on the reserve, not the expected spend",
            },
            ["measurements"] = new[]
            {
                "per arm and repetition: the lines selected in each segment (aliases; the owner reads their text)",
                armNames.SequenceEqual(C.Arms.Select(a => a.Arm)) ? "whether the same lines are selected in every repetition of a cell, and where CONTROL and PREFETCH differ"
                    : armNames.Length == 1 ? (reps == 1 ? "one repetition: the lines selected once in each segment; stability is not measured" : "whether the same lines are selected in every repetition of a cell")
                    : "whether the same lines are selected in every repetition of a cell, and where the arms differ",
                repairTurn
                    ? "protocol outcome per unit with its code; a refused answer gets repair turns (see failureHandling) and the unit's outcome is its last answer, with RepairedFrom kept; offline counterfactuals are labelled not official"
                    : "protocol outcome per unit with its code; refused answers are counted and never repaired; offline counterfactuals are labelled not official",
                "tool calls, tokens and cost per request",
            },
            ["readingRules"] = new[]
            {
                publicCorpus
                    ? "no Gold in the run: the program makes no claim that a selected line is or is not a heading; a score against the corpus Gold is offline and separate"
                    : "no Gold: the program makes no claim that a selected line is or is not a heading; the owner judges",
                reps == C.Reps ? "5 repetitions per cell: an indication of stability, not a significance test"
                    : reps == 1 ? "1 repetition per cell: run-to-run variation is not seen, so a difference between two plans may be sampling variation"
                    : reps + " repetitions per cell: an indication of stability, not a significance test",
                "one document: nothing here says how the request behaves on other documents",
            },
            ["rawPublication"] = "None. Requests (which hold the document text), raw responses, reasoning and the analysis stay in the safe store outside the repository. Only a receipt without any line text is committed.",
            ["liveAuthorization"] = new { required = true, artifact = P7F1V6DirectAuthorization.Artifact, status = P7F1V6DirectAuthorization.Approved, campaigns = 1 },
        };
        if (unwrapFence)
            plan["fenceUnwrap"] = P7F1V6ReplyFence.Version + ": a final answer wrapped in exactly one markdown fence (optional json label) around exactly one JSON object is read from inside the fence and the object goes through the unchanged strict parser; prose outside the fence, a second fence or another label is still REPLY_NOT_JSON; a unit that used the unwrap is flagged FenceUnwrapped";
        if (factsUse)
            plan["factsUseArm"] = "only the FACTS arm is run: the same request as GEOMETRY (repeated-text observations, layout block, segments, shared left edge) with one more sentence in the system prompt telling the model to read the observations of a target and of the lines next to it and to compare the target with its neighbours; the sentence names no kind of line";
        if (firstSegments > 0)
            plan["firstSegmentsOnly"] = firstSegments;
        if (promptCache)
            plan["promptCache"] = "the system prompt and the unit payload are each one text block marked cache_control ephemeral (Alibaba caches only marked prefixes; 5-minute TTL renewed on each hit; writes 1.25x, reads 0.1x of the input price); the text is unchanged, only the content shape";
        if (cacheHistoryMark)
            plan["promptCacheHistoryMark"] = "a third cache mark on the last history message with text (a tool result or a repair message), so a later turn reads the conversation so far from the cache";
        if (transportRetries > 0 || repairTurn)
            plan["failureHandling"] = maxRepairs == 1
                ? new { transportRetries, retriable = A.RetriableTransport, repairTurn, repairRule = "at most one repair per unit: the refused reply and a message naming the refusal are sent once more; the new answer goes through the same strict parser; the unit records RepairedFrom", spareCallsPerUnit = extraPerUnit }
                : (object)new { transportRetries, retriable = A.RetriableTransport, repairTurn, maxRepairs, repairRule = (maxRepairs < 0 ? "repairs with no limit of their own (owner, 2026-10-13): " : "up to " + maxRepairs + " repairs per unit: ") + "each time an answer is refused, the refused reply and a message naming the refusal are sent again; every new answer goes through the same strict parser; the arm's call limit stops a unit that keeps failing; the unit records RepairedFrom (the first refusal) and RepairsUsed", spareCallsPerUnit = extraPerUnit };
        if (hideLayout)
            plan["hideLayoutArm"] = "TOOLFACTS_ANALYSIS_V3: the request shows each line's alias, page and text only (no bbox, no font); the layout of a line comes from get_line_facts (layout facts V3: V2 plus the line's layout and its style against the document); the payload carries documentStyles, the style table of the whole document; contract as TOOLFACTS_ANALYSIS";
        if (promptFactsV2)
            plan["promptFactsV2Arm"] = "TOOLFACTS_ANALYSIS with the facts sentence of the system prompt also describing the layout facts V2 measurements (left edge against the page's common edge; font size against the page median); request, tools and contract unchanged";
        if (factsVersion != 1)
            plan["layoutFactsVersion"] = factsVersion;
        if (toolFactsAnalysis)
            plan["toolFactsAnalysisArm"] = "TOOLFACTS plus room for the explanation inside the answer: a COMPLETE answer may carry one extra top-level string, analysis (no length limit of its own; the whole reply stays within the 16 KB reply cap), and each decision may carry reason (at most " + P7F1V6DirectTwoStage.MaxReasonChars + " characters), asked for every SELECT; the parser reads the decisions exactly as before and neither string decides anything; the prompt says to write nothing outside the JSON object";
        if (toolFacts)
            plan["toolFactsArm"] = "the facts are read through get_line_facts: the request has no observations, the first reply must be tool calls that read the facts (an answer before a successful get_line_facts call is refused, PROTOCOL:TOOL_FACTS_REQUIRED_NOT_REQUESTED, and counts as a contract failure); the facts are the same three kinds as in the FACTS payload; the other seven tools and all earlier requests are unchanged";
        if (fullToolRounds)
            plan["toolRoundsBudget"] = "the spare posts of an arm are every unit's full tool rounds (" + A.MaxToolTurns + " per unit), so a unit that uses its tools is not cut by the call limit; the ceiling is derived from that call limit";
        if (singleArm)
            plan["singleArm"] = "only the GEOMETRY arm is run: one request that carries every observation the program reads (repeated-text observations, layout block, segments on the visual line with gaps, shared left edge); no CONTROL and no PREFETCH arm";
        if (geometryArm)
            plan["geometryArm"] = "a third arm, GEOMETRY: PREFETCH plus observations.layoutFacts (" + P7F1V6LayoutFacts.Version + "), the program's measurements of each shown line's layout block, segments on its visual line and shared left edge; no label, no text reading";
        if (jsonObjectAllTurns)
            plan["responseFormatTurns"] = "json_object on EVERY turn, including the turns where Qwen may call a tool (tool_choice auto). Reason: with no response_format on those turns Qwen wrapped the whole answer in a markdown fence in 3 of 3 first answers of the D4.11j layout CONTROL arm (REPLY_NOT_JSON, halted by the early-stop rule). The strict parser is unchanged and still refuses any fence or prose.";
        if (segmentation != P7F1V6Universe.FixedSegmentation)
        {
            plan["segmentation"] = new
            {
                policy = segmentation,
                rule = "the F1Q arm-B rule: whole pages when they fit in 48 lines, else parser layout blocks of the page, cores of at most 48 lines; only page numbers and parser block ids decide; context stays 20 lines each side",
                cores = u.Cores.Select(c => new { start = c.Start, end = c.End, firstPage = u.Lines[c.Start].Page, lastPage = u.Lines[c.End - 1].Page }).ToArray(),
                coresSha256 = P7F1V6Protocol.Sha(string.Join("\n", u.Cores.Select(c => c.Start + "-" + c.End))),
            };
        }
        return plan;
    }

    public static byte[] Serialize(Dictionary<string, object?> plan) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan, Indented) + "\n");

    public static IReadOnlyList<string> Verify(string planPath, string confirmedSha256, string currentCommit, string documentPath)
    {
        var failures = new List<string>();
        if (!File.Exists(planPath)) return new[] { "PLAN_FILE_MISSING" };
        if (!File.Exists(documentPath)) return new[] { "OWNER_DOCUMENT_MISSING" };
        var bytes = File.ReadAllBytes(planPath);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != confirmedSha256) failures.Add("PLAN_SHA_NOT_CONFIRMED");
        using var doc = JsonDocument.Parse(bytes);
        var planCommit = doc.RootElement.GetProperty("codeCommit").GetString();
        if (planCommit != currentCommit) failures.Add("CODE_COMMIT_DIFFERS");
        if (doc.RootElement.GetProperty("sourceSha256").GetString() != Sha256Of(documentPath)) { failures.Add("OWNER_DOCUMENT_SHA_DIFFERS"); return failures; }
        if (!Serialize(PinnedValues(documentPath, planCommit ?? "", SegmentationOf(doc.RootElement), JsonAllTurnsOf(doc.RootElement), GeometryArmOf(doc.RootElement), FenceUnwrapOf(doc.RootElement), SingleArmOf(doc.RootElement), FactsUseOf(doc.RootElement), RepsOf(doc.RootElement), FirstSegmentsOf(doc.RootElement), InstructionOf(doc.RootElement), ToolRoundsOf(doc.RootElement), ToolFactsOf(doc.RootElement), ToolFactsAnalysisOf(doc.RootElement), FactsVersionOf(doc.RootElement), PromptFactsV2Of(doc.RootElement), HideLayoutOf(doc.RootElement), RetriesOf(doc.RootElement), RepairOf(doc.RootElement), MaxRepairsOf(doc.RootElement), PromptCacheOf(doc.RootElement), CacheHistoryMarkOf(doc.RootElement))).AsSpan().SequenceEqual(bytes)) failures.Add("PINNED_VALUES_DIFFER");
        return failures;
    }

    public static IReadOnlyList<C.ArmResult> Execute(string repoRoot, string planPath, string confirmedSha256, string currentCommit, string authorizationPath,
        string apiKey, HttpMessageHandler handler, string safeRoot, string documentPath)
    {
        var failures = Verify(planPath, confirmedSha256, currentCommit, documentPath);
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
        var segments = plan.RootElement.GetProperty("segments").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var ceilings = plan.RootElement.GetProperty("arms").EnumerateArray().ToDictionary(a => a.GetProperty("arm").GetString()!, a => a.GetProperty("budget").GetProperty("ceilingUsd").GetDouble());
        var posts = plan.RootElement.GetProperty("arms")[0].GetProperty("budget").GetProperty("maxCalls").GetInt32();
        var u = Load(documentPath, plan.RootElement.GetProperty("sourceSha256").GetString()!, SegmentationOf(plan.RootElement), FactsVersionOf(plan.RootElement));
        var armSet = ArmsOf(GeometryArmOf(plan.RootElement), SingleArmOf(plan.RootElement), FactsUseOf(plan.RootElement), ToolFactsOf(plan.RootElement), ToolFactsAnalysisOf(plan.RootElement), PromptFactsV2Of(plan.RootElement), HideLayoutOf(plan.RootElement));
        var budgets = armSet.ToDictionary(a => a.Arm, a => new P7F1V6DirectV2.Budget(posts, ceilings[a.Arm], C.MaxRequestBytes));
        var stores = armSet.ToDictionary(a => a.Arm, _ => P7F1V6SafeRawStore.CreateRun(safeRoot));
        var models = armSet.ToDictionary(a => a.Arm, a => new P7F1V6AgentAuditedModel(new P7F1V6DirectAuditedModel(
            new P7F1V6OpenRouterTransport(handler, TimeSpan.FromSeconds(300)), apiKey, stores[a.Arm], budgets[a.Arm])));
        var state = C.RunArms(u, segments, models.ToDictionary(kv => kv.Key, kv => (A.AgentModel)kv.Value.Turn), budgets, JsonAllTurnsOf(plan.RootElement), armSet, unwrapSingleFence: FenceUnwrapOf(plan.RootElement), reps: RepsOf(plan.RootElement), instruction: InstructionOf(plan.RootElement), parallelism: C.ParallelismFromEnvironment(),
            transportRetries: RetriesOf(plan.RootElement), repairTurn: RepairOf(plan.RootElement), maxRepairs: MaxRepairsOf(plan.RootElement), cacheMarkers: PromptCacheOf(plan.RootElement), cacheHistoryMark: CacheHistoryMarkOf(plan.RootElement));
        return state.Select(s => new C.ArmResult(s.Arm, s.Units, models[s.Arm].Ledger, s.Budget.Calls, s.Budget.ReservedUsd, s.Budget.ActualUsd,
            s.Budget.HaltReason, s.Budget.Refusals.ToArray(), stores[s.Arm].RunDirectory)).ToList();
    }
}
