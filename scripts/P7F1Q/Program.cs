using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

// P7-F1Q: qualification-only, F1-only, tool-augmented semantic question experiment (Issue #5).
// Modes: probe | prepare | execute | freeze | score. No production, G2/H2 or Gold mutation.
// Gold is never opened by probe/prepare/execute/freeze.
if (args.Length < 1) throw new ArgumentException("probe|prepare|execute|freeze|score (see README)");
var policy = new F1QBodyPolicy("qwen/qwen3.7-flash", "alibaba", 32768, JsonObjectResponse: false);
var caps = new F1QRunCaps();
// Plan V3: json_object on every turn, tool set V2, no in-turn transport retry (one request-level retry instead).
var policyV3 = new F1QBodyPolicy("qwen/qwen3.7-flash", "alibaba", 32768, JsonObjectResponse: true, ToolSet: 2);
var capsV3 = new F1QRunCaps(MaxTransportAttemptsPerTurn: 1);
// Held-out (Issue #6): V3 caps plus the USD 3.00 hard cap checked against worst-case cost before every call.
var capsHeldout = capsV3 with { HardCapUsd = 3.00m };
// Issue #7 V4.2 (-v42 modes only): tool set 3 = targets are this request's issued ids, V3 tool definitions.
var policyV42 = policyV3 with { ToolSet = 3 };

// "-layout" modes run the layout-aware chunking arm (2026-10-11): same composition, only pack/chunk boundaries differ.
LayoutArm.On = args[0].EndsWith("-layout", StringComparison.Ordinal);
// "-v11" direct modes use P7_F1_DIRECT_HEADING_V1_1 (opaque ids, no hash echo, at-location prompt); V1 stays for the pilot.
DirectV.V5 = args[0].EndsWith("-v5", StringComparison.Ordinal);
DirectV.V11 = args[0].EndsWith("-v11", StringComparison.Ordinal) || args[0].EndsWith("-v42", StringComparison.Ordinal) || DirectV.V5;
DirectV.V42 = args[0].EndsWith("-v42", StringComparison.Ordinal) || DirectV.V5;
switch (args[0])
{
    case "probe":
    {
        // Source-only sizing for held-out-style documents: packs per page. No Gold, no network.
        foreach (var (id, pages) in ExternalDocuments())
        {
            var row = UniverseRow(id); var (source, details) = Parse(S(row, "pdf"), S(row, "sourceSha256"), S(row, "sourceAliasUniverseHash"));
            foreach (var p in pages)
            {
                var packs = P7PilotRequestPreflight.SelectPacks(source, details, [p]);
                Console.WriteLine($"{id} page {p}: atoms={source.Atoms.Count(a => a.Page == p)} packs=[{string.Join(",", packs.Select(k => $"{k.PackId}:{k.Owned.Count}"))}]");
            }
        }
        break;
    }
    case "prepare":
    {
        if (args.Length is not (5 or 6)) throw new ArgumentException("prepare <frozen-request-dir> <full-source-dir> <new-plan.json> <new-bodies-dir> [arm,...]");
        // V1: Control (fresh only where no historical D3 Control) + F1QNoTools + F1QTools. V2: an explicit arm list
        // (F1QToolsMandatory), frozen as a separate plan and never pooled with V1.
        var armOverride = args.Length == 6 ? args[5].Split(',').Select(Enum.Parse<F1QArm>).ToArray() : null;
        var cases = Cohort(args[1], args[2]);
        Need(!Directory.Exists(args[4]) && !File.Exists(args[3]), "PLAN_OR_BODIES_EXIST");
        Directory.CreateDirectory(args[4]);
        var runner = new P7F1QConversationRunner(new NoTransport(), policy, caps);
        var requests = new List<object>();
        foreach (var c in cases)
            foreach (var arm in armOverride ?? Arms(c))
            {
                var body = InitialBody(runner, c, arm);
                var file = $"{c.Case}.{arm}.initial-body.json";
                P7F1QConversationRunner.WriteNew(Path.Combine(args[4], file), body);
                requests.Add(new
                {
                    handle = Handle(c, arm), @case = c.Case, document = c.Document, pack = c.Pack, arm = arm.ToString(),
                    bodyFile = file, bodySha256 = SpatialCanonical.Hash(body),
                    systemPromptSha256 = SpatialCanonical.Hash(Encoding.UTF8.GetBytes(SystemFor(c, arm))),
                    userMessageSha256 = SpatialCanonical.Hash(Encoding.UTF8.GetBytes(UserFor(c, arm))),
                    issuedOccurrences = c.Issued.Count, maxModelTurns = arm is F1QArm.F1QTools or F1QArm.F1QToolsMandatory ? caps.MaxToolRounds + 1 : 1,
                    controlSource = arm == F1QArm.Control ? "FRESH_FROZEN_PRODUCTION_F1_CONTROL_BODY" : null,
                });
            }
        var plan = SpatialCanonical.Bytes(new
        {
            version = armOverride is null ? "P7_F1Q_TOOL_AUGMENTED_EXECUTION_PLAN_V1" : "P7_F1Q_TOOL_CHAIN_QUALIFICATION_PLAN_V2",
            status = "FROZEN_BEFORE_PROVIDER_AND_BEFORE_GOLD_READ",
            acceptance = armOverride is null ? null : "RAW_TRANSCRIPT_MUST_SHOW_QWEN_TOOL_CALL_TO_CSHARP_EVIDENCE_TOOL_TO_REAL_SOURCE_EVIDENCE_TO_QWEN_FINAL_F1_NO_MOCKS",
            issue = "ylight23/DocxHeaderExtractor#5", protocolVersion = P7F1QProtocol.Version, toolsVersion = P7F1QEvidenceTools.Version,
            model = policy.Model, providerRoute = new { order = new[] { "alibaba" }, allowFallbacks = false, requireParameters = true },
            endpoint = P7F1QOpenRouterTransport.Endpoint, temperature = 0, maxTokens = policy.MaxTokens, reasoning = "enabled",
            f1qResponseFormat = "NONE_STRICT_RAW_JSON_PARSER", controlResponseFormat = "json_object (frozen production Control body)",
            caps, toolDefinitionsSha256 = SpatialCanonical.Hash(Encoding.UTF8.GetBytes(P7F1QEvidenceTools.Definitions().ToJsonString())),
            toolCaps = new { P7F1QEvidenceTools.MaxNeighbors, P7F1QEvidenceTools.MaxGeometryLines, P7F1QEvidenceTools.MaxSpanTargets,
                P7F1QEvidenceTools.MaxRepeated, P7F1QEvidenceTools.ResultByteCap, P7F1QEvidenceTools.MaxTextChars },
            retryPolicy = "ONE_IDENTICAL_BODY_RETRY_PER_TURN_ON_TRANSPORT_FAILURE_ONLY_NEVER_ON_CONTRACT_OR_SEMANTICS",
            repair = false, fallback = false, modelSwap = false,
            reasoningPassBack = "NOT_SENT_ASSISTANT_TOOL_TURNS_CARRY_CONTENT_AND_TOOL_CALLS_ONLY",
            budget = new { approvedUsdCap = (decimal?)null, userAuthorization = "USER_APPROVED_EXCEEDING_2_USD_2026_10_09_ISSUE_5_BOUNDED_COHORT",
                runawayStopUsd = caps.RunawayUsd, anomalousRequestStopUsd = caps.AnomalousRequestUsd, stopOnRepeatedSystemicFailure = 3 },
            executionOrder = "CANARY_D05_F1Q_TOOLS_FIRST_THEN_REMAINING_IN_PLAN_ORDER",
            cases = cases.Select(c => new
            {
                c.Case, c.Document, c.Pack, c.Origin, sourceSha256 = c.Source.SourceSha256, sourceAliasUniverseSha256 = c.Source.SourceAliasUniverseHash,
                evidenceStoreSha256 = c.Tools.EvidenceStoreSha256, packSha256 = c.PackSha256, selectedPages = c.Pages,
                issuedMappingSha256 = SpatialCanonical.Hash(SpatialCanonical.Bytes(c.Issued)), issued = c.Issued.Count,
                contextOnly = c.Context.Count, controlUserMessageSha256 = SpatialCanonical.Hash(Encoding.UTF8.GetBytes(c.Control.ControlUserMessage)),
                historicalControl = c.HistoricalControlBodySha256,
            }),
            requests,
            goldRead = false, goldOrReviewerFieldsProjected = false, priorPredictionsProjected = false,
            productionChanged = false, g2h2Coupling = "NONE", providerCalls = 0,
            sources = new[] { "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QEvidenceTools.cs", "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QProtocol.cs",
                "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QConversation.cs", "scripts/P7F1Q/Program.cs" }
                .Select(path => new { path, sha256 = SpatialCanonical.Hash(File.ReadAllBytes(path)) }),
        });
        P7F1QConversationRunner.WriteNew(args[3], plan);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "PLAN_FROZEN", planSha256 = SpatialCanonical.Hash(plan), requests = requests.Count, providerCalls = 0 }));
        break;
    }
    case "execute":
    {
        if (args.Length < 7) throw new ArgumentException("execute <plan.json> <plan-sha256> <bodies-dir> <frozen-request-dir> <full-source-dir> <capture-root> [handle,...]");
        var planBytes = ReadPinned(args[1], args[2]); using var plan = JsonDocument.Parse(planBytes);
        foreach (var src in plan.RootElement.GetProperty("sources").EnumerateArray()) ReadPinned(S(src, "path"), S(src, "sha256"));
        var cases = Cohort(args[4], args[5]).ToDictionary(c => c.Case);
        var only = args.Length > 7 ? args[7].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal) : null;
        var root = Path.GetFullPath(args[6]); Directory.CreateDirectory(root);
        var metadata = await FetchEndpointMetadata();
        P7F1QConversationRunner.WriteNew(Path.Combine(root, $"endpoint-metadata.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json"), metadata);
        VerifyEndpoint(metadata);
        using var transport = new P7F1QOpenRouterTransport(() => Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "", TimeSpan.FromSeconds(300));
        var runner = new P7F1QConversationRunner(transport, policy, caps);
        var consecutiveSystemic = 0;
        foreach (var r in plan.RootElement.GetProperty("requests").EnumerateArray())
        {
            var handle = S(r, "handle");
            if (only is not null && !only.Contains(handle)) continue;
            var dir = Path.Combine(root, "requests", handle.Replace('|', '.'));
            if (Directory.Exists(dir)) { Console.WriteLine($"SKIP_EXISTING {handle}"); continue; }
            var c = cases[S(r, "case")]; var arm = Enum.Parse<F1QArm>(S(r, "arm"));
            var body = ReadPinned(Path.Combine(args[3], S(r, "bodyFile")), S(r, "bodySha256"));
            Need(body.AsSpan().SequenceEqual(InitialBody(runner, c, arm)), "RECOMPOSED_BODY_DRIFT:" + handle);
            var spec = new F1QRequestSpec(handle, c.Case, arm, SystemFor(c, arm), UserFor(c, arm), c.Issued, c.InitialCitable,
                arm is F1QArm.F1QTools or F1QArm.F1QToolsMandatory ? c.Tools : null, body,
                arm == F1QArm.Control ? response => P7F1QProtocol.ValidateControl(response, c.Control) : null);
            var outcome = await runner.RunAsync(spec, dir, () => Spent(root), CancellationToken.None);
            var line = JsonSerializer.Serialize(new { handle, outcome.Status, outcome.FailureCode, turns = outcome.Turns.Count,
                outcome.CostUsd, toolCalls = outcome.ToolCallsByName, outcome.InvalidToolCalls, spentUsd = Spent(root) });
            Console.WriteLine(line);
            File.AppendAllText(Path.Combine(root, "execution-log.jsonl"), line + "\n");
            consecutiveSystemic = outcome.Status is "TRANSPORT_FAILED" ? consecutiveSystemic + 1 : 0;
            if (outcome.Status == "BUDGET_STOP" || consecutiveSystemic >= 3) { Console.WriteLine("STOP " + outcome.FailureCode + " systemic=" + consecutiveSystemic); break; }
        }
        break;
    }
    case "freeze":
    {
        if (args.Length != 4) throw new ArgumentException("freeze <plan.json> <capture-root> <new-capture-freeze.json>");
        var root = Path.GetFullPath(args[2]);
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => f != "manifest.json").Order(StringComparer.Ordinal)
            .Select(f => new { path = f, bytes = new FileInfo(Path.Combine(root, f)).Length, sha256 = SpatialCanonical.Hash(File.ReadAllBytes(Path.Combine(root, f))) }).ToArray();
        var receipts = Directory.GetFiles(Path.Combine(root, "requests"), "request-receipt.json", SearchOption.AllDirectories)
            .Select(f => JsonNode.Parse(File.ReadAllBytes(f))!).OrderBy(n => n["handle"]!.GetValue<string>(), StringComparer.Ordinal).ToArray();
        decimal total = receipts.Sum(n => n["costUsd"]!.GetValue<decimal>());
        var turns = receipts.SelectMany(n => n["turns"]!.AsArray()).ToArray();
        var tools = receipts.SelectMany(n => n["toolCallsByName"]!.AsObject()).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value!.GetValue<int>()));
        var manifest = SpatialCanonical.Bytes(new
        {
            version = "P7_F1Q_TOOL_AUGMENTED_RAW_MANIFEST_V1", plans = args[1].Split(',').Select(p => new { path = p.Replace('\\', '/'), sha256 = SpatialCanonical.Hash(File.ReadAllBytes(p)) }),
            requests = receipts.Length, httpAttempts = turns.Sum(t => t!["attempts"]!.GetValue<int>()), modelTurns = turns.Length,
            statuses = receipts.GroupBy(n => n["status"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.Count()),
            reportedCostUsd = total, allCostsReported = receipts.All(n => n["allCostsReported"]!.GetValue<bool>()),
            promptTokens = turns.Sum(t => t!["promptTokens"]!.GetValue<long>()), completionTokens = turns.Sum(t => t!["completionTokens"]!.GetValue<long>()),
            reasoningTokens = turns.Sum(t => t!["reasoningTokens"]!.GetValue<long>()), toolCallsByName = tools,
            invalidToolCalls = receipts.Sum(n => n["invalidToolCalls"]!.GetValue<int>()),
            files, goldReadBeforeFreeze = false, rawIsAuthority = true,
        });
        P7F1QConversationRunner.WriteNew(Path.Combine(root, "manifest.json"), manifest);
        P7F1QConversationRunner.WriteNew(args[3], manifest);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "RAW_FROZEN", manifestSha256 = SpatialCanonical.Hash(manifest), files = files.Length, total }));
        break;
    }
    case "prepare-v3":
    {
        // Plan V3 (P7_F1Q_V2 protocol) after the 2026-10-09 audit: layout in the payload, evidence-grounded judgment,
        // tool set V2, per-decision identity/relevance checks, json_object, packs split into <=48-occurrence chunks.
        if (args.Length != 5) throw new ArgumentException("prepare-v3 <frozen-request-dir> <full-source-dir> <new-plan.json> <new-bodies-dir>");
        var cases = Cohort(args[1], args[2]);
        Need(!Directory.Exists(args[4]) && !File.Exists(args[3]), "PLAN_OR_BODIES_EXIST");
        Directory.CreateDirectory(args[4]);
        var runner = new P7F1QConversationRunner(new NoTransport(), policyV3, capsV3);
        var requests = new List<object>();
        foreach (var c in cases)
            foreach (var (chunk, k) in ChunksOf(c).Select((x, i) => (x, i + 1)))
            {
                var handle = $"{c.Case}-C{k}|{F1QArm.F1QEvidenceV2}";
                var body = InitialBodyV3(runner, c, chunk);
                var file = $"{c.Case}-C{k}.{F1QArm.F1QEvidenceV2}.initial-body.json";
                P7F1QConversationRunner.WriteNew(Path.Combine(args[4], file), body);
                requests.Add(new
                {
                    handle, @case = c.Case, chunk = k, document = c.Document, pack = c.Pack, arm = F1QArm.F1QEvidenceV2.ToString(),
                    bodyFile = file, bodySha256 = SpatialCanonical.Hash(body), issued = chunk.Select(i => new { occurrence = i.Occurrence, sourceAlias = i.SourceAlias }),
                    systemPromptSha256 = SpatialCanonical.Hash(Encoding.UTF8.GetBytes(P7F1QProtocolV2.SystemPrompt())),
                    maxModelTurns = capsV3.MaxToolRounds + 1, maxAttempts = 2,
                });
            }
        var plan = SpatialCanonical.Bytes(new
        {
            version = "P7_F1Q_EVIDENCE_GROUNDED_PLAN_V3", status = "FROZEN_BEFORE_PROVIDER_AND_BEFORE_GOLD_READ",
            issue = "ylight23/DocxHeaderExtractor#5", supersedes = "AUDIT_OF_PLANS_V1_V2_2026_10_09_V1_V2_RAW_UNCHANGED",
            protocolVersion = P7F1QProtocolV2.Version, toolsVersion = P7F1QEvidenceTools.VersionV2, claimAudit = P7F1QClaimAudit.Version,
            model = policyV3.Model, providerRoute = new { order = new[] { "alibaba" }, allowFallbacks = false, requireParameters = true },
            endpoint = P7F1QOpenRouterTransport.Endpoint, temperature = 0, maxTokens = policyV3.MaxTokens, reasoning = "enabled",
            responseFormat = "json_object_on_every_turn", chunkSize = P7F1QProtocolV2.ChunkSize, caps = capsV3,
            toolDefinitionsSha256 = SpatialCanonical.Hash(Encoding.UTF8.GetBytes(P7F1QEvidenceTools.DefinitionsV2().ToJsonString())),
            retryPolicy = "AT_MOST_ONE_IDENTICAL_INITIAL_BODY_RETRY_PER_REQUEST_ON_TRANSPORT_OR_CONTRACT_FAILURE_NEVER_ON_SEMANTICS_NO_REPAIR_NO_IN_TURN_TRANSPORT_RETRY",
            repair = false, fallback = false, modelSwap = false,
            budget = new { approvedUsdCap = (decimal?)null, userAuthorization = "USER_APPROVED_EXCEEDING_2_USD_2026_10_09_ISSUE_5_BOUNDED_COHORT_RERUN_WITH_ONE_RETRY_PER_REQUEST",
                runawayStopUsd = capsV3.RunawayUsd, anomalousRequestStopUsd = capsV3.AnomalousRequestUsd, stopOnRepeatedSystemicFailure = 3 },
            executionOrder = "CANARY_D05_CHUNKS_FIRST_THEN_REMAINING_IN_PLAN_ORDER",
            cases = cases.Select(c => new { c.Case, c.Document, c.Pack, sourceSha256 = c.Source.SourceSha256, evidenceStoreSha256 = c.ToolsV2.EvidenceStoreSha256,
                packSha256 = c.PackSha256, issued = c.Issued.Count, historicalControl = c.HistoricalControlBodySha256 }),
            requests, goldRead = false, goldOrReviewerFieldsProjected = false, priorPredictionsProjected = false,
            productionChanged = false, g2h2Coupling = "NONE", providerCalls = 0,
            sources = new[] { "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QEvidenceTools.cs", "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QProtocol.cs",
                "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QProtocolV2.cs", "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QConversation.cs",
                "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QClaimAudit.cs", "scripts/P7F1Q/Program.cs" }
                .Select(path => new { path, sha256 = SpatialCanonical.Hash(File.ReadAllBytes(path)) }),
        });
        P7F1QConversationRunner.WriteNew(args[3], plan);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "PLAN_FROZEN", planSha256 = SpatialCanonical.Hash(plan), requests = requests.Count, providerCalls = 0 }));
        break;
    }
    case "execute-v3":
    {
        if (args.Length < 7) throw new ArgumentException("execute-v3 <plan.json> <plan-sha256> <bodies-dir> <frozen-request-dir> <full-source-dir> <capture-root> [handle,...]");
        var planBytes = ReadPinned(args[1], args[2]); using var plan = JsonDocument.Parse(planBytes);
        foreach (var src in plan.RootElement.GetProperty("sources").EnumerateArray()) ReadPinned(S(src, "path"), S(src, "sha256"));
        var cases = Cohort(args[4], args[5]).ToDictionary(c => c.Case);
        var only = args.Length > 7 ? args[7].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal) : null;
        var root = Path.GetFullPath(args[6]); Directory.CreateDirectory(root);
        var metadata = await FetchEndpointMetadata();
        P7F1QConversationRunner.WriteNew(Path.Combine(root, $"endpoint-metadata.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json"), metadata);
        VerifyEndpoint(metadata);
        using var transport = new P7F1QOpenRouterTransport(() => Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "", TimeSpan.FromSeconds(300));
        var runner = new P7F1QConversationRunner(transport, policyV3, capsV3);
        var consecutiveSystemic = 0; var stop = false;
        foreach (var r in plan.RootElement.GetProperty("requests").EnumerateArray())
        {
            if (stop) break;
            var handle = S(r, "handle");
            if (only is not null && !only.Contains(handle)) continue;
            var requestDir = Path.Combine(root, "requests", handle.Replace('|', '.'));
            if (Directory.Exists(requestDir)) { Console.WriteLine($"SKIP_EXISTING {handle}"); continue; }
            var c = cases[S(r, "case")]; var k = r.GetProperty("chunk").GetInt32();
            var chunk = ChunksOf(c)[k - 1];
            var body = ReadPinned(Path.Combine(args[3], S(r, "bodyFile")), S(r, "bodySha256"));
            Need(body.AsSpan().SequenceEqual(InitialBodyV3(runner, c, chunk)), "RECOMPOSED_BODY_DRIFT:" + handle);
            var issued = chunk.Select(i => new F1QIssuedOccurrence(i.Occurrence, i.SourceAlias, i.Page, i.Text)).ToArray();
            var citable = c.Issued.Select(i => i.SourceAlias).Concat(c.Context.Select(x => x.Alias)).ToHashSet(StringComparer.Ordinal);
            var spec = new F1QRequestSpec(handle, c.Case, F1QArm.F1QEvidenceV2, P7F1QProtocolV2.SystemPrompt(), UserV3(c, chunk), issued, citable,
                c.ToolsV2, body, null);
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var outcome = await runner.RunAsync(spec, Path.Combine(requestDir, $"attempt-{attempt}"), () => Spent(root), CancellationToken.None);
                var line = JsonSerializer.Serialize(new { handle, attempt, outcome.Status, outcome.FailureCode, turns = outcome.Turns.Count,
                    outcome.CostUsd, toolCalls = outcome.ToolCallsByName, outcome.InvalidToolCalls, spentUsd = Spent(root) });
                Console.WriteLine(line);
                File.AppendAllText(Path.Combine(root, "execution-log.jsonl"), line + "\n");
                if (outcome.Status == "BUDGET_STOP") { stop = true; break; }
                consecutiveSystemic = outcome.Status == "TRANSPORT_FAILED" ? consecutiveSystemic + 1 : 0;
                if (consecutiveSystemic >= 3) { Console.WriteLine("STOP REPEATED_TRANSPORT_FAILURE"); stop = true; break; }
                if (outcome.Status == "ACCEPTED") break;
            }
        }
        break;
    }
    case "prepare-heldout":
    case "prepare-heldout-layout":
    {
        // Issue #6 held-out plan: the frozen V3 composition (prompt, payload, schema, tools, json_object, chunking)
        // applied to the source-only selected pages of the 24 audited documents. No Gold, no provider calls.
        if (args.Length != 4) throw new ArgumentException("prepare-heldout <page-selection.json> <new-plan.json> <new-bodies-dir>");
        Need(!Directory.Exists(args[3]) && !File.Exists(args[2]), "PLAN_OR_BODIES_EXIST");
        var selectionBytes = File.ReadAllBytes(args[1]);
        var cases = HeldoutCases(selectionBytes);
        using var v3 = JsonDocument.Parse(File.ReadAllBytes("artifacts/web-pdf-semantic-diagnostic/p7.f1q.execution-plan.v3.json"));
        var promptSha = SpatialCanonical.Hash(Encoding.UTF8.GetBytes(P7F1QProtocolV2.SystemPrompt()));
        var toolSha = SpatialCanonical.Hash(Encoding.UTF8.GetBytes(P7F1QEvidenceTools.DefinitionsV2().ToJsonString()));
        Need(v3.RootElement.GetProperty("requests")[0].GetProperty("systemPromptSha256").GetString() == promptSha, "V3_PROMPT_CHANGED");
        Need(v3.RootElement.GetProperty("toolDefinitionsSha256").GetString() == toolSha, "V3_TOOLS_CHANGED");
        Directory.CreateDirectory(args[3]);
        var runner = new P7F1QConversationRunner(new NoTransport(), policyV3, capsHeldout);
        var requests = new List<object>();
        foreach (var c in cases)
            foreach (var (chunk, k) in ChunksOf(c).Select((x, i) => (x, i + 1)))
            {
                var handle = $"{c.Case}-C{k}|{F1QArm.F1QEvidenceV2}";
                var body = InitialBodyV3(runner, c, chunk);
                var file = $"{c.Case}-C{k}.{F1QArm.F1QEvidenceV2}.initial-body.json";
                P7F1QConversationRunner.WriteNew(Path.Combine(args[3], file), body);
                requests.Add(new { handle, @case = c.Case, chunk = k, document = c.Document, pack = c.Pack, bodyFile = file, bodySha256 = SpatialCanonical.Hash(body),
                    issued = chunk.Select(i => new { occurrence = i.Occurrence, sourceAlias = i.SourceAlias }), worstCaseRequestUsd = capsHeldout.WorstCaseUsd(body.Length, policyV3.MaxTokens) * (capsHeldout.MaxToolRounds + 1) });
            }
        var plan = SpatialCanonical.Bytes(new
        {
            // Version from the plan file name (…execution-plan.v2.json -> P7_F1Q_HELDOUT_PLAN_V2).
            version = "P7_F1Q_HELDOUT_PLAN_" + Path.GetFileNameWithoutExtension(args[2]).Split('.').Last().ToUpperInvariant(), status = "FROZEN_BEFORE_PROVIDER", issue = "ylight23/DocxHeaderExtractor#6",
            pageSelectionSha256 = SpatialCanonical.Hash(selectionBytes), frozenV3PlanSha256 = SpatialCanonical.Hash(File.ReadAllBytes("artifacts/web-pdf-semantic-diagnostic/p7.f1q.execution-plan.v3.json")),
            systemPromptSha256 = promptSha, toolDefinitionsSha256 = toolSha, promptSchemaToolsRoute = "IDENTICAL_TO_FROZEN_V3",
            protocolVersion = P7F1QProtocolV2.Version, model = policyV3.Model, providerRoute = new { order = new[] { "alibaba" }, allowFallbacks = false, requireParameters = true },
            responseFormat = "json_object_on_every_turn", chunkSize = P7F1QProtocolV2.ChunkSize, caps = capsHeldout,
            packing = LayoutArm.On ? P7F1QLayoutPacking.Version : "P05_RESOURCE_BOUNDED_PACKS_BALANCED_COUNT_CHUNKS",
            budget = new { hardCapUsd = capsHeldout.HardCapUsd, softAlertUsd = 1.00m, perDocumentCapUsd = PerDocumentCapUsd, preCallWorstCaseCheck = true },
            retryPolicy = "AT_MOST_ONE_IDENTICAL_BODY_RETRY_PER_REQUEST_ON_TRANSPORT_OR_CONTRACT_FAILURE_NO_ERROR_FEEDBACK_NO_SEMANTIC_RETRY",
            fenceNormalization = P7F1QFenceNormalization.Version, contractViews = new[] { "STRICT_RAW", "NORMALIZED_ACCEPTANCE" },
            goldGate = "EXECUTION_REFUSES_TO_RUN_WITHOUT_A_PINNED_GOLD_FREEZE_COVERING_EVERY_DOCUMENT_STATUS_USER_APPROVED_OR_AI_REVIEWED_USER_DELEGATED",
            cases = cases.Select(c => new { c.Case, c.Document, c.Pack, sourceSha256 = c.Source.SourceSha256, evidenceStoreSha256 = c.ToolsV2.EvidenceStoreSha256, packSha256 = c.PackSha256, issued = c.Issued.Count }),
            requests, worstCaseTotalUsdAllRequestsTwoAttempts = requests.Sum(r => JsonSerializer.SerializeToElement(r).GetProperty("worstCaseRequestUsd").GetDecimal()) * 2,
            goldRead = false, providerCalls = 0, productionChanged = false,
            sources = new[] { "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QEvidenceTools.cs", "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QProtocolV2.cs",
                "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QConversation.cs", "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QFenceNormalization.cs",
                "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QLayoutPacking.cs", "scripts/P7F1Q/Program.cs" }
                .Select(path => new { path, sha256 = SpatialCanonical.Hash(File.ReadAllBytes(path)) }),
        });
        P7F1QConversationRunner.WriteNew(args[2], plan);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "PLAN_FROZEN", planSha256 = SpatialCanonical.Hash(plan), cases = cases.Count, requests = requests.Count, providerCalls = 0 }));
        break;
    }
    case "execute-heldout":
    case "execute-heldout-layout":
    {
        if (args.Length < 7) throw new ArgumentException("execute-heldout <plan.json> <plan-sha256> <bodies-dir> <gold-freeze.json> <gold-freeze-sha256> <capture-root> [handle,...]");
        var planBytes = ReadPinned(args[1], args[2]); using var plan = JsonDocument.Parse(planBytes);
        foreach (var src in plan.RootElement.GetProperty("sources").EnumerateArray()) ReadPinned(S(src, "path"), S(src, "sha256"));
        // Gold gate (Issue #6 Decision 2): approved, frozen Gold for every planned document, before any call. Approved means
        // by the user, or by the AI reviewer the user delegated Gold approval to (2026-10-10); freeze-gold names which.
        using var gold = JsonDocument.Parse(ReadPinned(args[4], args[5]));
        Need(S(gold.RootElement, "status") is "USER_APPROVED_GOLD_FROZEN" or "GOLD_FROZEN_AI_REVIEWED_USER_DELEGATED", "GOLD_NOT_APPROVED_AND_FROZEN");
        var goldDocs = gold.RootElement.GetProperty("documents").EnumerateArray().Select(d => S(d, "id")).ToHashSet();
        Need(plan.RootElement.GetProperty("cases").EnumerateArray().All(c => goldDocs.Contains(S(c, "document"))), "GOLD_FREEZE_DOES_NOT_COVER_EVERY_DOCUMENT");
        var cases = HeldoutCases(File.ReadAllBytes("artifacts/web-pdf-semantic-diagnostic/p7.f1q.heldout.page-selection.v1.json")).ToDictionary(c => c.Case);
        var only = args.Length > 7 ? args[7].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal) : null;
        var root = Path.GetFullPath(args[6]); Directory.CreateDirectory(root);
        var metadata = await FetchEndpointMetadata();
        P7F1QConversationRunner.WriteNew(Path.Combine(root, $"endpoint-metadata.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json"), metadata);
        VerifyEndpoint(metadata);
        using var transport = new P7F1QOpenRouterTransport(() => Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "", TimeSpan.FromSeconds(300));
        var runner = new P7F1QConversationRunner(transport, policyV3, capsHeldout);
        var consecutiveSystemic = 0; var stop = false; var alerted = false;
        foreach (var r in plan.RootElement.GetProperty("requests").EnumerateArray())
        {
            if (stop) break;
            var handle = S(r, "handle");
            if (only is not null && !only.Contains(handle)) continue;
            var requestDir = Path.Combine(root, "requests", handle.Replace('|', '.'));
            if (Directory.Exists(requestDir)) { Console.WriteLine($"SKIP_EXISTING {handle}"); continue; }
            var c = cases[S(r, "case")]; var chunk = ChunksOf(c)[r.GetProperty("chunk").GetInt32() - 1];
            var body = ReadPinned(Path.Combine(args[3], S(r, "bodyFile")), S(r, "bodySha256"));
            Need(body.AsSpan().SequenceEqual(InitialBodyV3(runner, c, chunk)), "RECOMPOSED_BODY_DRIFT:" + handle);
            var docSpent = SpentFor(root, c.Document);
            if (docSpent + r.GetProperty("worstCaseRequestUsd").GetDecimal() > PerDocumentCapUsd)
            { var skip = JsonSerializer.Serialize(new { handle, status = "SKIPPED", reason = "PER_DOCUMENT_CAP_WORST_CASE", docSpent }); Console.WriteLine(skip); File.AppendAllText(Path.Combine(root, "execution-log.jsonl"), skip + "\n"); continue; }
            var issued = chunk.Select(i => new F1QIssuedOccurrence(i.Occurrence, i.SourceAlias, i.Page, i.Text)).ToArray();
            var citable = c.Issued.Select(i => i.SourceAlias).Concat(c.Context.Select(x => x.Alias)).ToHashSet(StringComparer.Ordinal);
            var spec = new F1QRequestSpec(handle, c.Case, F1QArm.F1QEvidenceV2, P7F1QProtocolV2.SystemPrompt(), UserV3(c, chunk), issued, citable,
                c.ToolsV2, body, null, FenceNormalization: true);
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var outcome = await runner.RunAsync(spec, Path.Combine(requestDir, $"attempt-{attempt}"), () => Spent(root), CancellationToken.None);
                var spent = Spent(root);
                var line = JsonSerializer.Serialize(new { handle, attempt, outcome.Status, outcome.FailureCode, turns = outcome.Turns.Count,
                    outcome.CostUsd, toolCalls = outcome.ToolCallsByName, outcome.InvalidToolCalls, spentUsd = spent });
                Console.WriteLine(line);
                File.AppendAllText(Path.Combine(root, "execution-log.jsonl"), line + "\n");
                if (!alerted && spent >= 1.00m) { alerted = true; Console.WriteLine("SOFT_ALERT_1_USD_REACHED"); File.AppendAllText(Path.Combine(root, "execution-log.jsonl"), "{\"alert\":\"SOFT_ALERT_1_USD_REACHED\"}\n"); }
                if (outcome.Status == "BUDGET_STOP") { stop = true; break; }
                consecutiveSystemic = outcome.Status == "TRANSPORT_FAILED" ? consecutiveSystemic + 1 : 0;
                if (consecutiveSystemic >= 3) { Console.WriteLine("STOP REPEATED_TRANSPORT_FAILURE"); stop = true; break; }
                if (outcome.Status == "ACCEPTED") break;
            }
        }
        break;
    }
    case "prepare-direct":
    case "prepare-direct-v11":
    case "prepare-direct-v42":
    case "prepare-direct-v5":
    {
        // Issue #7 P7_F1_DIRECT_HEADING_V1: layout-aware packs/chunks (same as the F1Q V2 layout control), the direct
        // heading-membership prompt and source enrichment V1. No Gold, no provider calls.
        if (args.Length != 4) throw new ArgumentException("prepare-direct <page-selection.json> <new-plan.json> <new-bodies-dir>");
        Need(!Directory.Exists(args[3]) && !File.Exists(args[2]), "PLAN_OR_BODIES_EXIST");
        LayoutArm.On = true;
        var selectionBytes = File.ReadAllBytes(args[1]);
        var requests = DirectRequests(selectionBytes).ToArray();
        Directory.CreateDirectory(args[3]);
        var runner = new P7F1QConversationRunner(new NoTransport(), DirectV.V42 ? policyV42 : policyV3, capsHeldout);
        var rows = new List<object>();
        foreach (var r in requests)
        {
            var body = DirectBody(runner, r);
            var file = r.RequestId + ".direct.initial-body.json";
            P7F1QConversationRunner.WriteNew(Path.Combine(args[3], file), body);
            rows.Add(new { handle = r.RequestId + "|DIRECT", requestId = r.RequestId, @case = r.Case.Case, chunk = r.Chunk, document = r.Case.Document, pack = r.Case.Pack,
                issuedSetSha256 = r.Scope, issued = r.Subjects.Select(s => new { occurrence = s.Occurrence, sourceAlias = s.SourceAlias, page = s.Page, textSha256 = P7F1DirectProtocol.TextSha256(s.Text), id = r.Ids?.GetValueOrDefault(s.SourceAlias) }), // text itself stays out of the public plan (source emails)
                contextAliases = r.Context.Select(c => c.Alias), bodyFile = file, bodySha256 = SpatialCanonical.Hash(body), bodyBytes = body.Length,
                worstCaseRequestUsd = capsHeldout.WorstCaseUsd(body.Length, policyV3.MaxTokens) * (capsHeldout.MaxToolRounds + 1) });
        }
        var plan = SpatialCanonical.Bytes(new
        {
            version = "P7_F1_DIRECT_PLAN_" + Path.GetFileNameWithoutExtension(args[2]).Split('.').Last().ToUpperInvariant(), status = "FROZEN_BEFORE_PROVIDER",
            issue = DirectV.V5 ? "ylight23/DocxHeaderExtractor#8" : "ylight23/DocxHeaderExtractor#7", protocolVersion = DirectV.V5 ? P7F1RegionProtocolV5.Version : DirectV.V11 ? P7F1DirectProtocolV11.Version : P7F1DirectProtocol.Version, systemPromptSha256 = DirectV.V5 ? P7F1RegionProtocolV5.PromptSha256() : DirectV.V11 ? P7F1DirectProtocolV11.PromptSha256() : P7F1DirectProtocol.PromptSha256(),
            enrichmentVersion = DirectV.V42 ? P7F1SourceEnrichment.VersionV2 : P7F1SourceEnrichment.Version, packing = P7F1QLayoutPacking.Version, chunkSize = P7F1QProtocolV2.ChunkSize,
            toolsVersion = DirectV.V42 ? P7F1QEvidenceTools.VersionV3 : P7F1QEvidenceTools.VersionV2, toolDefinitionsSha256 = SpatialCanonical.Hash(Encoding.UTF8.GetBytes(P7F1QEvidenceTools.DefinitionsV2(DirectV.V42).ToJsonString())),
            model = policyV3.Model, providerRoute = new { order = new[] { "alibaba" }, allowFallbacks = false, requireParameters = true },
            responseFormat = "json_object_on_every_turn", reasoning = "enabled", temperature = 0, maxTokens = policyV3.MaxTokens, caps = capsHeldout,
            retryPolicy = "AT_MOST_ONE_IDENTICAL_BODY_RETRY_PER_REQUEST_ON_TRANSPORT_OR_CONTRACT_FAILURE_NO_ERROR_FEEDBACK_NO_SEMANTIC_RETRY",
            fenceNormalization = P7F1QFenceNormalization.Version,
            providerGate = "EXECUTE_DIRECT_REFUSES_WITHOUT_A_PINNED_USER_PROVIDER_AUTHORIZATION_FOR_THIS_PLAN_SHA",
            pageSelectionSha256 = SpatialCanonical.Hash(selectionBytes),
            requests = rows, worstCaseTotalUsdAllRequestsTwoAttempts = rows.Sum(x => JsonSerializer.SerializeToElement(x).GetProperty("worstCaseRequestUsd").GetDecimal()) * 2,
            goldRead = false, providerCalls = 0, productionChanged = false,
            sources = new[] { "src/DocxHeaderExtractor.V5Qualification/P7/P7F1DirectProtocol.cs", "src/DocxHeaderExtractor.V5Qualification/P7/P7F1DirectProtocolV11.cs",
                "src/DocxHeaderExtractor.V5Qualification/P7/P7F1DirectBinderV11.cs", "src/DocxHeaderExtractor.V5Qualification/P7/P7F1RegionProtocolV5.cs", "src/DocxHeaderExtractor.V5Qualification/P7/P7F1RegionBinderV5.cs", "src/DocxHeaderExtractor.V5Qualification/P7/P7F1SourceEnrichment.cs",
                "src/DocxHeaderExtractor.V5Qualification/P7/P7F1DirectBinder.cs", "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QEvidenceTools.cs",
                "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QConversation.cs", "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QFenceNormalization.cs",
                "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QLayoutPacking.cs", "scripts/P7F1Q/Program.cs" }
                .Concat(DirectV.V5 ? new[] { "artifacts/web-pdf-semantic-diagnostic/p7.f1.v5.design.v1.md" } : Array.Empty<string>())
                .Select(path => new { path, sha256 = SpatialCanonical.Hash(File.ReadAllBytes(path)) }),
        });
        P7F1QConversationRunner.WriteNew(args[2], plan);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "PLAN_FROZEN", planSha256 = SpatialCanonical.Hash(plan), requests = rows.Count,
            maxBodyBytes = rows.Max(x => JsonSerializer.SerializeToElement(x).GetProperty("bodyBytes").GetInt32()), providerCalls = 0 }));
        break;
    }
    case "execute-direct":
    case "execute-direct-v11":
    case "execute-direct-v42":
    case "execute-direct-v5":
    {
        // Paid-call gate (Issue #7): a user provider authorization pinned to THIS plan sha, with its own USD cap, is required.
        if (args.Length < 6) throw new ArgumentException("execute-direct <plan.json> <plan-sha256> <bodies-dir> <authorization.json> <capture-root> [handle,...]");
        LayoutArm.On = true;
        var planBytes = ReadPinned(args[1], args[2]); using var plan = JsonDocument.Parse(planBytes);
        foreach (var src in plan.RootElement.GetProperty("sources").EnumerateArray()) ReadPinned(S(src, "path"), S(src, "sha256"));
        using var auth = JsonDocument.Parse(File.ReadAllBytes(args[4]));
        Need(S(auth.RootElement, "version") == "P7_F1_DIRECT_PROVIDER_AUTHORIZATION_V1", "AUTHORIZATION_VERSION");
        Need(S(auth.RootElement, "planSha256") == args[2], "AUTHORIZATION_NOT_FOR_THIS_PLAN");
        Need(S(auth.RootElement, "authorizedBy") == "user", "AUTHORIZATION_NOT_BY_USER");
        var capUsd = auth.RootElement.GetProperty("capUsd").GetDecimal();
        Need(capUsd > 0 && capUsd <= 10m, "AUTHORIZATION_CAP_INVALID");
        var directCaps = capsHeldout with { HardCapUsd = capUsd };
        var direct = DirectRequests(File.ReadAllBytes(PageSelectionFor(plan.RootElement))).ToDictionary(r => r.RequestId);
        var only = args.Length > 6 ? args[6].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal) : null;
        var root = Path.GetFullPath(args[5]); Directory.CreateDirectory(root);
        // Replicate runs under one authorization share ONE cap: the capture root must be a named replicate, and every
        // worker of every replicate reserves a worst-case turn in a locked ledger before the runner's pre-call check.
        Func<decimal> spentFn = () => Spent(root);
        SharedBudget? shared = null;
        if (auth.RootElement.TryGetProperty("replicateRoots", out var reps))
        {
            var names = reps.EnumerateArray().Select(v => v.GetString()!).ToArray();
            Need(names.Contains(Path.GetFileName(root), StringComparer.Ordinal), "CAPTURE_ROOT_NOT_AN_AUTHORIZED_REPLICATE");
            var parent = Path.GetDirectoryName(root)!;
            // Reservation per in-flight turn: full completion budget plus a prompt bound of 4x the largest initial body
            // (later turns append tool results).
            var worstTurn = plan.RootElement.GetProperty("requests").EnumerateArray()
                .Max(q => directCaps.WorstCaseUsd(q.GetProperty("bodyBytes").GetInt32() * 4, policyV3.MaxTokens));
            shared = new SharedBudget(Path.Combine(parent, "budget-ledger"), names.Select(n => Path.Combine(parent, n)).ToArray(), worstTurn, capUsd, Spent);
            spentFn = shared.ReserveAndReadOthers;
        }
        var metadata = await FetchEndpointMetadata();
        P7F1QConversationRunner.WriteNew(Path.Combine(root, $"endpoint-metadata.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.{Environment.ProcessId}.json"), metadata);
        VerifyEndpoint(metadata);
        using var transport = new P7F1QOpenRouterTransport(() => Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "", TimeSpan.FromSeconds(300));
        var runner = new P7F1QConversationRunner(transport, DirectV.V42 ? policyV42 : policyV3, directCaps);
        var stop = false; var systemic = 0;
        foreach (var q in plan.RootElement.GetProperty("requests").EnumerateArray())
        {
            if (stop) break;
            var handle = S(q, "handle");
            if (only is not null && !only.Contains(handle)) continue;
            var requestDir = Path.Combine(root, "requests", handle.Replace('|', '.'));
            if (Directory.Exists(requestDir)) { Console.WriteLine($"SKIP_EXISTING {handle}"); continue; }
            var r = direct[S(q, "requestId")];
            var body = ReadPinned(Path.Combine(args[3], S(q, "bodyFile")), S(q, "bodySha256"));
            Need(body.AsSpan().SequenceEqual(DirectBody(runner, r)), "RECOMPOSED_BODY_DRIFT:" + handle);
            var spec = DirectSpec(r, body, handle);
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var outcome = await runner.RunAsync(spec, Path.Combine(requestDir, $"attempt-{attempt}"), spentFn, CancellationToken.None);
                var line = JsonSerializer.Serialize(new { handle, attempt, outcome.Status, outcome.FailureCode, turns = outcome.Turns.Count, outcome.CostUsd,
                    toolCalls = outcome.ToolCallsByName, spentUsd = Spent(root), sharedSpentUsd = shared?.Realized() });
                Console.WriteLine(line);
                if (outcome.Status == "BUDGET_STOP") { stop = true; break; }
                systemic = outcome.Status == "TRANSPORT_FAILED" ? systemic + 1 : 0;
                if (systemic >= 3) { Console.WriteLine("STOP REPEATED_TRANSPORT_FAILURE"); stop = true; break; }
                if (outcome.Status == "ACCEPTED") break;
            }
        }
        shared?.Release();
        break;
    }
    case "bind-direct":
    case "bind-direct-v11":
    case "bind-direct-v42":
    case "bind-direct-v5":
    {
        // Exact binding of FROZEN raw (manifest pinned) to parser-authoritative source; no Gold is read here.
        if (args.Length != 6) throw new ArgumentException("bind-direct <plan.json> <plan-sha256> <capture-root> <raw-manifest-sha256> <new-bound.json>");
        Need(!File.Exists(args[5]), "OUTPUT_EXISTS");
        LayoutArm.On = true;
        var planBytes = ReadPinned(args[1], args[2]); using var plan = JsonDocument.Parse(planBytes);
        var root = Path.GetFullPath(args[3]);
        ReadPinned(Path.Combine(root, "manifest.json"), args[4]);
        var bound = BindDirect(plan.RootElement, root, PageSelectionFor(plan.RootElement));
        var bytes = SpatialCanonical.Bytes(new { version = "P7_F1_DIRECT_BOUND_PREDICTIONS_V1", binder = DirectV.V5 ? P7F1RegionBinderV5.Version : DirectV.V11 ? P7F1DirectBinderV11.Version : P7F1DirectBinder.Version, planSha256 = args[2], rawManifestSha256 = args[4],
            goldRead = false, statuses = bound.GroupBy(b => b["status"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.Count()), requests = bound });
        P7F1QConversationRunner.WriteNew(args[5], bytes);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "BOUND", sha256 = SpatialCanonical.Hash(bytes), requests = bound.Count }));
        break;
    }
    case "preflight-v5":
    {
        // Issue #8 output preflight on REAL source (094 chunks from the parser), no provider call. Checks plan pins and sizes,
        // then runs the V5 contract on real issued ids: complete coverage passes; missing, gapped, truncated and
        // navigation-described responses behave as designed (a region's words never remove a heading in C#).
        if (args.Length != 5) throw new ArgumentException("preflight-v5 <plan.json> <plan-sha256> <bodies-dir> <receipt.json>");
        LayoutArm.On = true; DirectV.V5 = true; DirectV.V11 = true; DirectV.V42 = true;
        var planBytes = ReadPinned(args[1], args[2]); using var plan = JsonDocument.Parse(planBytes);
        var sizes = new List<object>();
        foreach (var q in plan.RootElement.GetProperty("requests").EnumerateArray())
        {
            ReadPinned(Path.Combine(args[3], S(q, "bodyFile")), S(q, "bodySha256"));
            var n = q.GetProperty("issued").GetArrayLength();
            sizes.Add(new { requestId = S(q, "requestId"), issued = n, bodyBytes = q.GetProperty("bodyBytes").GetInt32(),
                // Upper bound of a valid response's characters: each subject at most one region and one heading.
                responseCharsBound = n * (P7F1RegionProtocolV5.MaxFunctionChars + 80) + n * (P7F1RegionProtocolV5.MaxJustificationChars + 160) });
        }
        var direct = DirectRequests(File.ReadAllBytes(PageSelectionFor(plan.RootElement))).ToDictionary(r => r.RequestId);
        var r94 = direct["D094-PACK_001-C2"];
        var subjects = r94.Subjects; var ids = r94.Ids!;
        var citable = new HashSet<string>(r94.Case.Source.Atoms.Select(a => a.Alias), StringComparer.Ordinal);
        string Id(int k) => ids[subjects[k].SourceAlias];
        string Json(string regions, string headings) => $"{{\"protocolVersion\":\"{P7F1RegionProtocolV5.Version}\",\"requestId\":\"{r94.RequestId}\",\"assessment\":\"COMPLETE\",\"regions\":[{regions}],\"headings\":[{headings}]}}";
        string Region(int a, int b, string f) => $"{{\"from\":\"{Id(a)}\",\"to\":\"{Id(b)}\",\"function\":\"{f}\",\"evidence\":[]}}";
        var last = subjects.Count - 1;
        var cases = new (string Name, string Json)[]
        {
            ("full_coverage_one_region_no_headings", Json(Region(0, last, "table of contents entries"), "")),
            ("two_regions_title_then_body_heading_selected", Json(Region(0, 0, "title") + "," + Region(1, last, "body"),
                $"{{\"id\":\"{Id(0)}\",\"region\":0,\"evidenceRefs\":[\"{subjects[0].SourceAlias}\"],\"justification\":\"title line\"}}")),
            ("navigation_described_region_heading_still_selected_by_model", Json(Region(0, last, "table of contents entries"),
                $"{{\"id\":\"{Id(0)}\",\"region\":0,\"evidenceRefs\":[\"{subjects[0].SourceAlias}\"],\"justification\":\"opens content here\"}}")),
            ("missing_regions_must_be_rejected", Json("", "")),
            ("gap_in_middle_must_be_rejected", Json(Region(0, 0, "title") + "," + Region(2, last, "body"), "")),
            ("short_by_one_at_the_end_must_be_rejected", Json(Region(0, last - 1, "body"), "")),
            ("truncated_json_must_be_rejected", Json(Region(0, last, "body"), "").Substring(0, 120)),
        };
        var results = cases.Select(c =>
        {
            var v = P7F1RegionProtocolV5.Validate(c.Json, r94.RequestId, subjects, ids, citable);
            return (object)new { c.Name, accepted = v.StrictAccepted, failureCode = v.FailureCode, responseChars = c.Json.Length };
        }).ToArray();
        var receipt = SpatialCanonical.Bytes(new { version = "P7_F1_REGION_PREFLIGHT_V1", planSha256 = args[2], real = "094 chunk C2 from the parser, not a fixture",
            requestId = r94.RequestId, issuedSubjects = subjects.Count, maxIssuedPerRequest = sizes.Max(x => JsonSerializer.SerializeToElement(x).GetProperty("issued").GetInt32()),
            maxBodyBytes = sizes.Max(x => JsonSerializer.SerializeToElement(x).GetProperty("bodyBytes").GetInt32()), requests = sizes, contractCases = results,
            outputBudget = new { maxTokens = policyV42.MaxTokens, note = "reasoning counts toward max_tokens; responseCharsBound is a character bound, not a token count" }, providerCalls = 0 });
        P7F1QConversationRunner.WriteNew(args[4], receipt);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "PREFLIGHT_WRITTEN", sha256 = SpatialCanonical.Hash(receipt), cases = results.Length, providerCalls = 0 }));
        break;
    }
    case "preflight-v6":
        // Issue #9 D1 preflight (offline): preflight-v6 <docs.json> <out-dir>. No provider call.
        if (args.Length != 3) throw new ArgumentException("preflight-v6 <docs.json> <out-dir>");
        V6Preflight.Run(args[1], args[2]);
        break;
    case "preflight-d2":
        // Issue #9 D2 preflight (offline): preflight-d2 <docs.json> <out-dir>. Hierarchical run per shard size. No provider call.
        if (args.Length is not (3 or 4) || (args.Length == 4 && args[3] != "retrieve")) throw new ArgumentException("preflight-d2 <docs.json> <out-dir> [retrieve]");
        V6PreflightD2.Run(args[1], args[2], args.Length == 4);
        break;
    case "c0-pin":
        // Gate C0 (D3.4): c0-pin <repo-root> <artifact-dir>. Writes the pins for document 073. No network call.
        if (args.Length != 3) throw new ArgumentException("c0-pin <repo-root> <artifact-dir>");
        C0Replay.Pin(args[1], args[2]);
        break;
    case "c0-replay":
        // Gate C0 (D3.4): c0-replay <repo-root> <artifact-dir> <safe-root>. Refuses when the pins differ. No network call.
        if (args.Length != 4) throw new ArgumentException("c0-replay <repo-root> <artifact-dir> <safe-root>");
        C0Replay.Replay(args[1], args[2], args[3]);
        break;
    case "d4-size-audit":
        // Issue #9 D4 (offline): d4-size-audit <repo-root> <artifact-dir>. Sizes the DIRECT-F1 requests for RFC 9113. No network call.
        if (args.Length != 3) throw new ArgumentException("d4-size-audit <repo-root> <artifact-dir>");
        C0Replay.D4SizeAudit(args[1], args[2]);
        break;
    case "d43-size-audit":
        // Issue #9 D4.3 (offline): d43-size-audit <repo-root> <artifact-dir>. Sizes the DIRECT-F1 V2 requests for RFC 9113. No network call.
        if (args.Length != 3) throw new ArgumentException("d43-size-audit <repo-root> <artifact-dir>");
        C0Replay.D43SizeAudit(args[1], args[2]);
        break;
    case "d44-universe-check":
        // Issue #9 D4.4 (offline): d44-universe-check <repo-root> <artifact-dir>. Rebuilds PDF Gold universes and compares. No network call.
        if (args.Length != 3) throw new ArgumentException("d44-universe-check <repo-root> <artifact-dir>");
        C0Replay.D44UniverseCheck(args[1], args[2]);
        break;
    case "d44-plan":
        // Issue #9 D4.4 (offline): d44-plan <repo-root> <artifact-dir>. Unit-size study and task A/B/C requests. No network call.
        if (args.Length != 3) throw new ArgumentException("d44-plan <repo-root> <artifact-dir>");
        C0Replay.D44Plan(args[1], args[2]);
        break;
    case "d45-plan":
        // Issue #9 D4.5 (offline): d45-plan <repo-root> <artifact-dir>. Writes the pinned plan and the dry-run report. No network call.
        if (args.Length != 3) throw new ArgumentException("d45-plan <repo-root> <artifact-dir>");
        D45Plan.Run(args[1], args[2]);
        break;
    case "d410-plan":
        // Issue #9 D4.10 (offline): d410-plan <repo-root> <artifact-dir>. Pinned canary plan and per-arm dry-run. No network call.
        if (args.Length != 3) throw new ArgumentException("d410-plan <repo-root> <artifact-dir>");
        D410Plan.Plan(args[1], args[2]);
        break;
    case "d410-run":
        // Issue #9 D4.10: d410-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>. Sends requests; needs an APPROVED authorization.
        if (args.Length != 6) throw new ArgumentException("d410-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>");
        D410Plan.Live(args[1], args[2], args[3], args[4], args[5]);
        break;
    case "d411-plan":
        // Issue #9 D4.11 (offline): d411-plan <repo-root> <artifact-dir>. Pinned tool-calling canary plan and scripted dry-run. No network call.
        if (args.Length != 3) throw new ArgumentException("d411-plan <repo-root> <artifact-dir>");
        D411Plan.Plan(args[1], args[2]);
        break;
    case "d411-run":
        // Issue #9 D4.11: d411-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>. Sends requests; needs an APPROVED authorization.
        if (args.Length != 6) throw new ArgumentException("d411-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>");
        D411Plan.Live(args[1], args[2], args[3], args[4], args[5]);
        break;
    case "d411h-plan":
        // Issue #9 D4.11h (offline): d411h-plan <repo-root> <artifact-dir>. Pinned prefetch-canary plan and scripted dry-run. No network call.
        if (args.Length != 3) throw new ArgumentException("d411h-plan <repo-root> <artifact-dir>");
        D411hPlan.Plan(args[1], args[2]);
        break;
    case "d411h-run":
        // Issue #9 D4.11h: d411h-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>. Sends requests; needs an APPROVED authorization.
        if (args.Length != 6) throw new ArgumentException("d411h-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>");
        D411hPlan.Live(args[1], args[2], args[3], args[4], args[5]);
        break;
    case "d411i-plan":
        // Issue #9 D4.11i (offline): d411i-plan <repo-root> <artifact-dir> <authority-id>. Pinned repeat-canary plan for one document and a scripted dry-run. No network call.
        if (args.Length != 4) throw new ArgumentException("d411i-plan <repo-root> <artifact-dir> <authority-id>");
        D411iPlan.Plan(args[1], args[2], args[3]);
        break;
    case "d411i-run":
        // Issue #9 D4.11i: d411i-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>. Sends requests; needs an APPROVED authorization.
        if (args.Length != 6) throw new ArgumentException("d411i-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>");
        D411iPlan.Live(args[1], args[2], args[3], args[4], args[5]);
        break;
    case "d411j-plan":
        // Issue #9 D4.11j (offline): d411j-plan <repo-root> <artifact-dir> <document.pdf>. Pinned owner-document trial plan and a scripted dry-run. No network call; no line text is written.
        if (args.Length < 4) throw new ArgumentException("d411j-plan <repo-root> <artifact-dir> <document.pdf> [fixed|layout|layout96] [jsonall] [geometry] [unwrap] [single] [factsuse] [reps=N] [segments=N] [english|main|maintools|mainplain] [toolrounds] [toolfacts] [toolfactsanalysis]");
        {
            var opts = args.Skip(4).ToHashSet();
            var segPolicy = opts.Contains("layout96") ? P7F1V6Universe.LayoutSegmentation96 : opts.Contains("layout") ? P7F1V6Universe.LayoutSegmentation : P7F1V6Universe.FixedSegmentation;
            var repsArg = opts.FirstOrDefault(o => o.StartsWith("reps=", StringComparison.Ordinal));
            var segsArg = opts.FirstOrDefault(o => o.StartsWith("segments=", StringComparison.Ordinal));
            var known = new HashSet<string> { "fixed", "layout", "layout96", "jsonall", "geometry", "unwrap", "single", "factsuse", "english", "main", "maintools", "mainplain", "toolrounds", "toolfacts", "toolfactsanalysis" };
            if (opts.Any(o => !known.Contains(o) && o != repsArg && o != segsArg)) throw new ArgumentException("unknown d411j-plan option");
            D411jPlan.Plan(args[1], args[2], args[3], segPolicy, opts.Contains("jsonall"), opts.Contains("geometry"), opts.Contains("unwrap"), opts.Contains("single"), opts.Contains("factsuse"),
                repsArg is null ? P7F1V6DirectRepeatCanary.Reps : int.Parse(repsArg[5..]), segsArg is null ? 0 : int.Parse(segsArg[9..]),
                opts.Contains("mainplain") ? P7F1V6DirectD45.InstructionEnglishMainPlain : opts.Contains("maintools") ? P7F1V6DirectD45.InstructionEnglishMainTools : opts.Contains("main") ? P7F1V6DirectD45.InstructionEnglishMain : opts.Contains("english") ? P7F1V6DirectD45.InstructionEnglishAll : P7F1V6DirectD45.Instruction, opts.Contains("toolrounds"), opts.Contains("toolfacts"), opts.Contains("toolfactsanalysis"));
        }
        break;
    case "d411j-run":
        // Issue #9 D4.11j: d411j-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root> <document.pdf>. Sends requests; needs an APPROVED authorization.
        if (args.Length != 7) throw new ArgumentException("d411j-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root> <document.pdf>");
        D411jPlan.Live(args[1], args[2], args[3], args[4], args[5], args[6]);
        break;
    case "d411-publish":
        // Issue #9 D4.11: d411-publish <plan> <out-dir> <run-dir> [<run-dir>...]. Copies the safe-store runs of a consumed plan into the repo. No network call.
        if (args.Length < 4) throw new ArgumentException("d411-publish <plan> <out-dir> <run-dir> [<run-dir>...]");
        D411Plan.Publish(args[1], args[2], args.Skip(3).Where(a => a != "--gzip").ToArray(), args.Contains("--gzip"));
        break;
    case "d49-dryrun":
        // Issue #9 D4.9 (offline): d49-dryrun <repo-root> <artifact-dir>. Variants A/B/C with scripted models. No network call.
        if (args.Length != 3) throw new ArgumentException("d49-dryrun <repo-root> <artifact-dir>");
        D49DryRun.Run(args[1], args[2]);
        break;
    case "d49-replay":
        // Issue #9 D4.9 (offline): d49-replay <repo-root> <run-dir-with-raw> <out>. Representation-change replay. No network call.
        if (args.Length != 4) throw new ArgumentException("d49-replay <repo-root> <run-dir> <out>");
        D49DryRun.Replay(args[1], args[2], args[3]);
        break;
    case "d45-run":
        // Issue #9 D4.5: d45-run <repo-root> <plan-path> <confirmed-plan-sha> <authorization-path> <safe-root>. Sends requests; needs an APPROVED authorization.
        if (args.Length != 6) throw new ArgumentException("d45-run <repo-root> <plan-path> <confirmed-plan-sha> <authorization-path> <safe-root>");
        D45Plan.Live(args[1], args[2], args[3], args[4], args[5]);
        break;
    case "d45-safe-root":
        // Issue #9 D4.5a: d45-safe-root <repo-root> <safe-root> prepare|check. Creates (prepare) and checks the safe root. No network call.
        if (args.Length != 4 || args[3] is not ("prepare" or "check")) throw new ArgumentException("d45-safe-root <repo-root> <safe-root> prepare|check");
        D45Plan.SafeRoot(args[1], args[2], args[3] == "prepare");
        break;
    case "d45-authorization-draft":
        // Issue #9 D4.5a: d45-authorization-draft <plan-path> <safe-root> <out-path>. Writes a PENDING draft. No network call.
        if (args.Length != 4) throw new ArgumentException("d45-authorization-draft <plan-path> <safe-root> <out-path>");
        D45Plan.AuthorizationDraft(args[1], args[2], args[3]);
        break;
    case "d45-approve":
        // Issue #9 D4.5a: d45-approve <draft> <out> <approved-by> <approval-text> <data-collection-decision> accept-open-items|no. Only on the user's explicit approval.
        if (args.Length != 7) throw new ArgumentException("d45-approve <draft> <out> <approved-by> <approval-text> <data-collection-decision> accept-open-items|no");
        D45Plan.Approve(args[1], args[2], args[3], args[4], args[5], args[6] == "accept-open-items");
        break;
    case "d45-preview":
        // Issue #9 D4.5 (offline): d45-preview <repo-root> <scratch-dir>. The same simulations on uncommitted code; writes outside the repo; not a plan.
        if (args.Length != 3) throw new ArgumentException("d45-preview <repo-root> <scratch-dir>");
        if (Path.GetFullPath(args[2]).StartsWith(Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("PREVIEW_MUST_BE_OUTSIDE_REPO");
        D45Plan.Run(args[1], args[2], preview: true);
        break;
    case "d4-canary-artifact":
        // Issue #9 D4.1 (offline): d4-canary-artifact <artifact-dir> <generated-at-commit>. Writes the canary T1 body and audit. No network call.
        if (args.Length != 3) throw new ArgumentException("d4-canary-artifact <artifact-dir> <generated-at-commit>");
        C0Replay.D4CanaryArtifact(args[1], args[2]);
        break;
    case "d4-canary-send":
        // Issue #9 D4.2: d4-canary-send <artifact-dir> <confirmed-sha>. Sends the single pinned canary POST. Needs OPENROUTER_API_KEY. Not run in D4.2.
        if (args.Length != 3) throw new ArgumentException("d4-canary-send <artifact-dir> <confirmed-sha>");
        C0Replay.D4CanarySend(args[1], args[2]);
        break;
    case "d31-snapshot":
        // Issue #9 D3.1 candidate (offline): d31-snapshot <repo-root>. Writes the manifest and the candidate document. No network call.
        if (args.Length != 2) throw new ArgumentException("d31-snapshot <repo-root>");
        D31Snapshot.Run(args[1]);
        break;
    default: throw new ArgumentException("UNKNOWN_MODE");
}

IEnumerable<DirectRequest> DirectRequests(byte[] selectionBytes)
{
    var enrichments = new Dictionary<string, P7F1SourceEnrichment>(StringComparer.Ordinal);
    foreach (var c in HeldoutCases(selectionBytes))
    {
        if (!enrichments.TryGetValue(c.Document, out var enrichment))
            enrichments[c.Document] = enrichment = new P7F1SourceEnrichment(c.ToolsV2, c.Source, LayoutArm.Details[c.Document], exactEvidence: DirectV.V42);
        var chunks = ChunksOf(c);
        foreach (var (chunk, k) in chunks.Select((x, i) => (x, i + 1)))
        {
            var requestId = $"D{c.Case[1..]}-C{k}";
            var subjects = chunk.Select(i => new P7F1DirectProtocol.Subject(i.Occurrence, i.SourceAlias, i.Page, i.Text)).ToArray();
            var inChunk = subjects.Select(s => s.SourceAlias).ToHashSet(StringComparer.Ordinal);
            var context = chunks.SelectMany(x => x).Where(p => !inChunk.Contains(p.SourceAlias)).Select(p => (Alias: p.SourceAlias, p.Page, p.Text)).Concat(c.Context).ToArray();
            var ids = DirectV.V11 ? P7F1DirectProtocolV11.OpaqueIds(requestId, subjects) : null;
            var scope = ids is null ? P7F1DirectProtocol.IssuedSetSha256(requestId, c.Document, subjects) : P7F1DirectProtocolV11.ScopeSha256(requestId, c.Document, subjects, ids);
            // V4.2: this request's own tools instance resolves only its issued ids (plus source aliases).
            var requestTools = DirectV.V42 && ids is not null ? c.ToolsV2.WithIssued(ids.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal)) : null;
            yield return new DirectRequest(requestId, c, k, subjects, context, scope, enrichment, ids, requestTools);
        }
    }
}

static byte[] DirectBody(P7F1QConversationRunner runner, DirectRequest r) => runner.Body(new JsonArray
{
    new JsonObject { ["role"] = "system", ["content"] = r.Ids is null ? P7F1DirectProtocol.SystemPrompt() : DirectV.V5 ? P7F1RegionProtocolV5.SystemPrompt() : P7F1DirectProtocolV11.SystemPrompt() },
    new JsonObject { ["role"] = "user", ["content"] = DirectUser(r) },
}, true, "auto");

static string DirectUser(DirectRequest r) => r.Ids is null
    ? P7F1DirectProtocol.UserMessage(r.RequestId, r.Scope, r.Subjects, r.Context, r.Case.ToolsV2, r.Enrichment)
    : DirectV.V5 ? P7F1RegionProtocolV5.UserMessage(r.RequestId, r.Subjects, r.Ids, r.Context, r.Case.ToolsV2, r.Enrichment)
    : P7F1DirectProtocolV11.UserMessage(r.RequestId, r.Subjects, r.Ids, r.Context, r.Case.ToolsV2, r.Enrichment);

static F1QRequestSpec DirectSpec(DirectRequest r, byte[] body, string handle)
{
    var issued = r.Subjects.Select(s => new F1QIssuedOccurrence(s.Occurrence, s.SourceAlias, s.Page, s.Text)).ToArray();
    // Runner-level check cites any alias of this document (tool results may return lines outside the request);
    // the binder then verifies every reference against the request evidence and the raw tool transcript.
    var citable = r.Case.Source.Atoms.Select(a => a.Alias).ToHashSet(StringComparer.Ordinal);
    Func<string, F1QValidation> validator = r.Ids is null
        ? text => P7F1DirectProtocol.Validate(text, r.RequestId, r.Scope, issued, citable)
        : DirectV.V5 ? text => P7F1RegionProtocolV5.Validate(text, r.RequestId, r.Subjects, r.Ids, citable)
        : text => P7F1DirectProtocolV11.Validate(text, r.RequestId, r.Subjects, r.Ids, citable);
    return new F1QRequestSpec(handle, r.Case.Case, F1QArm.F1QEvidenceV2, r.Ids is null ? P7F1DirectProtocol.SystemPrompt() : DirectV.V5 ? P7F1RegionProtocolV5.SystemPrompt() : P7F1DirectProtocolV11.SystemPrompt(),
        DirectUser(r), issued, citable, r.RequestTools ?? r.Case.ToolsV2, body, validator, FenceNormalization: true);
}

List<JsonObject> BindDirect(JsonElement plan, string root, string selectionPath)
{
    var facts = new Dictionary<string, P7F1QEvidenceTools>(StringComparer.Ordinal);
    foreach (var c in HeldoutCases(File.ReadAllBytes(selectionPath))) facts.TryAdd(c.Document, c.ToolsV2);
    var list = new List<JsonObject>();
    foreach (var q in plan.GetProperty("requests").EnumerateArray())
    {
        var tools = facts[S(q, "document")];
        Func<string, P7F1DirectBinder.SourceFact> fact = alias => { var l = tools.LineOf(alias); return new P7F1DirectBinder.SourceFact(l.Page ?? -1, l.Text, l.Bbox); };
        var dir = Path.Combine(root, "requests", S(q, "handle").Replace('|', '.'));
        list.Add(DirectV.V5 ? P7F1RegionBinderV5.Bind(q, dir, fact) : DirectV.V11 ? P7F1DirectBinderV11.Bind(q, dir, fact) : P7F1DirectBinder.Bind(q, dir, fact));
    }
    return list;
}

static string PageSelectionFor(JsonElement plan)
{
    // The plan pins the page-selection sha; the selection is looked up among the known selection files.
    var sha = plan.GetProperty("pageSelectionSha256").GetString();
    foreach (var f in Directory.GetFiles("artifacts/web-pdf-semantic-diagnostic", "*page-selection*.json"))
        if (SpatialCanonical.Hash(File.ReadAllBytes(f)) == sha) return f;
    throw new InvalidOperationException("PAGE_SELECTION_FOR_PLAN_NOT_FOUND");
}

List<F1QCase> HeldoutCases(byte[] selectionBytes)
{
    using var selection = JsonDocument.Parse(selectionBytes);
    var list = new List<F1QCase>();
    foreach (var d in selection.RootElement.GetProperty("documents").EnumerateArray())
    {
        var id = S(d, "id");
        var (source, details) = Parse(S(d, "sourceKey"), S(d, "sourceSha256"), S(d, "sourceAliasUniverseSha256"));
        var store = PdfSourceEvidenceStore.Build(source, details);
        Need(store.StoreSha256 == S(d, "evidenceStoreSha256"), "HELDOUT_STORE_DRIFT:" + id);
        var pages = d.GetProperty("selectedPages").EnumerateArray().Select(p => p.GetProperty("page").GetInt32()).ToArray();
        var packs = LayoutArm.On ? P7F1QLayoutPacking.SelectPacks(source, details, pages) : P7PilotRequestPreflight.SelectPacks(source, details, pages);
        LayoutArm.Details[id] = details;
        foreach (var pack in packs)
        {
            var name = $"{LayoutArm.Prefix}{id}-{Short(pack.PackId)}";
            if (LayoutArm.On)
                foreach (var e in pack.Owned) LayoutArm.Block[name + "|" + e.SourceAlias] = P7F1QLayoutPacking.Block(details, e);
            list.Add(Case(name, id, pack, pages, "P7_F1Q_HELDOUT_ISSUE_6", source, details, store,
                P7PilotRequestPreflight.F1(source, details, pack), null));
        }
    }
    return list;
}

static decimal SpentFor(string root, string document) => !Directory.Exists(Path.Combine(root, "requests")) ? 0 :
    Directory.GetDirectories(Path.Combine(root, "requests"), $"{LayoutArm.Prefix}{document}-*").Sum(d =>
        Directory.GetFiles(d, "observation.json", SearchOption.AllDirectories).Sum(f =>
        { using var j = JsonDocument.Parse(File.ReadAllBytes(f)); return j.RootElement.GetProperty("assembled").GetProperty("usage") is { ValueKind: JsonValueKind.Object } u && u.TryGetProperty("cost", out var c) ? c.GetDecimal() : 0m; }));

static IReadOnlyList<IReadOnlyList<P7F1QProtocolV2.V5Issued>> ChunksOf(F1QCase c)
{
    using var user = JsonDocument.Parse(c.Control.ControlUserMessage);
    var correspondences = user.RootElement.GetProperty("occurrences").EnumerateArray().ToDictionary(o => o.GetProperty("id").GetString()!, o => o.GetProperty("correspondences").Clone());
    var issued = c.Issued.Select(i => new P7F1QProtocolV2.V5Issued(i.Occurrence, i.SourceAlias, i.Page, i.Text, correspondences[i.Occurrence])).ToArray();
    return LayoutArm.On
        ? P7F1QLayoutPacking.Chunks(issued, i => i.Page, i => LayoutArm.Block[c.Case + "|" + i.SourceAlias], P7F1QProtocolV2.ChunkSize)
        : P7F1QProtocolV2.Chunks(issued);
}

static string UserV3(F1QCase c, IReadOnlyList<P7F1QProtocolV2.V5Issued> chunk) =>
    P7F1QProtocolV2.UserMessage(chunk, ChunksOf(c).SelectMany(x => x).ToArray(), c.Context, c.ToolsV2);

static byte[] InitialBodyV3(P7F1QConversationRunner runner, F1QCase c, IReadOnlyList<P7F1QProtocolV2.V5Issued> chunk) =>
    runner.Body(new JsonArray
    {
        new JsonObject { ["role"] = "system", ["content"] = P7F1QProtocolV2.SystemPrompt() },
        new JsonObject { ["role"] = "user", ["content"] = UserV3(c, chunk) },
    }, true, "auto");

static IEnumerable<F1QArm> Arms(F1QCase c) => c.HistoricalControlBodySha256 is null
    ? [F1QArm.Control, F1QArm.F1QNoTools, F1QArm.F1QTools] : [F1QArm.F1QNoTools, F1QArm.F1QTools];
static string Handle(F1QCase c, F1QArm arm) => $"{c.Case}|{arm}";
static string SystemFor(F1QCase c, F1QArm arm) => arm == F1QArm.Control ? c.Control.ControlSystemPrompt : P7F1QProtocol.SystemPrompt(arm);
static string UserFor(F1QCase c, F1QArm arm) => arm == F1QArm.Control ? c.Control.ControlUserMessage : c.F1QUser;

static byte[] InitialBody(P7F1QConversationRunner runner, F1QCase c, F1QArm arm)
{
    if (arm == F1QArm.Control) return ControlBody(c.Control.ControlSystemPrompt, c.Control.ControlUserMessage);
    var messages = new JsonArray
    {
        new JsonObject { ["role"] = "system", ["content"] = P7F1QProtocol.SystemPrompt(arm) },
        new JsonObject { ["role"] = "user", ["content"] = c.F1QUser },
    };
    var tools = arm is F1QArm.F1QTools or F1QArm.F1QToolsMandatory;
    return runner.Body(messages, tools, tools ? "auto" : "absent");
}

// Frozen production Control body shape, reproduced from the D3 template (verified byte-identical on D01-D05).
static byte[] ControlBody(string system, string user)
{
    var template = JsonNode.Parse(File.ReadAllBytes(ControlTemplatePath))!.AsObject();
    template["messages"] = new JsonArray(
        new JsonObject { ["role"] = "system", ["content"] = system },
        new JsonObject { ["role"] = "user", ["content"] = user });
    return JsonSerializer.SerializeToUtf8Bytes(template);
}

List<F1QCase> Cohort(string frozenDir, string fullSourceDir)
{
    var cases = new List<F1QCase>();
    var manifestBytes = ReadPinned(Path.Combine(frozenDir, "request-manifest.v1.json"), P7F1ExecutionReadiness.FrozenRequestManifestSha256);
    using var manifest = JsonDocument.Parse(manifestBytes);
    ControlTemplatePath = "";
    var n = 0;
    foreach (var doc in manifest.RootElement.GetProperty("documents").EnumerateArray())
    {
        n++;
        var id = S(doc, "document");
        var (source, details) = Parse(Path.Combine(fullSourceDir, id, "source.pdf"), S(doc, "sourceSha256"), S(doc, "sourceUniverseSha256"));
        var store = PdfSourceEvidenceStore.Build(source, details);
        Need(SpatialCanonical.Hash(store.CanonicalBytes()) == S(doc, "storeSha256"), "STORE_DRIFT:" + id);
        var pages = doc.GetProperty("evaluationPages").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        foreach (var pack in P7PilotRequestPreflight.SelectPacks(source, details, pages))
        {
            var control = P7PilotRequestPreflight.F1(source, details, pack);
            var prefix = Path.Combine(frozenDir, id, Short(pack.PackId));
            Need(File.ReadAllText(prefix + ".F1.CONTROL.system.txt", Encoding.UTF8) == control.ControlSystemPrompt &&
                 File.ReadAllBytes(prefix + ".F1.CONTROL.user.json").AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(control.ControlUserMessage)), "FROZEN_CONTROL_DRIFT:" + id + pack.PackId);
            using var issuedFrozen = JsonDocument.Parse(File.ReadAllBytes(prefix + ".issued-universe.json"));
            var issuedMap = issuedFrozen.RootElement.EnumerateArray().ToDictionary(e => S(e, "occurrence"), e => S(e, "alias"));
            Need(control.Owned.All(o => issuedMap[o.Id] == o.Atom.Alias) && issuedMap.Count == control.Owned.Count, "ISSUED_MAPPING_DRIFT:" + id + pack.PackId);
            var frozenBody = File.ReadAllBytes(prefix + ".F1.CONTROL.provider-body.json");
            if (ControlTemplatePath.Length == 0) ControlTemplatePath = prefix + ".F1.CONTROL.provider-body.json";
            Need(ControlBody(control.ControlSystemPrompt, control.ControlUserMessage).AsSpan().SequenceEqual(frozenBody), "CONTROL_TEMPLATE_NOT_BYTE_IDENTICAL:" + id + pack.PackId);
            cases.Add(Case($"D{n:D2}-{Short(pack.PackId)}", id, pack, pages, "P7_APPROVED_PILOT_D01_D05", source, details, store, control, SpatialCanonical.Hash(frozenBody)));
        }
    }
    foreach (var (docId, pages) in ExternalDocuments())
    {
        var row = UniverseRow(docId);
        var (source, details) = Parse(S(row, "pdf"), S(row, "sourceSha256"), S(row, "sourceAliasUniverseHash"));
        var store = PdfSourceEvidenceStore.Build(source, details);
        foreach (var pack in P7PilotRequestPreflight.SelectPacks(source, details, pages))
            cases.Add(Case($"{docId}-{Short(pack.PackId)}", docId, pack, pages, "A99_AUTHORED_GOLD_DOCUMENT_PAGES_SELECTED_SOURCE_ONLY", source, details, store,
                P7PilotRequestPreflight.F1(source, details, pack), null));
    }
    return cases;
}

static F1QCase Case(string name, string document, DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical.SemanticEvidencePack pack, int[] pages, string origin,
    DocumentSourceSnapshot source, PdfSourceDetails details, PdfSourceEvidenceStore store, InterpretationRequest control, string? historical)
{
    var context = P7F1QProtocol.Context(source, pack);
    var issued = control.Owned.Select(o => new F1QIssuedOccurrence(o.Id, o.Atom.Alias, o.Atom.Page, o.Atom.Text)).ToArray();
    var tools = P7F1QEvidenceTools.FromStore(store, issued.ToDictionary(i => i.Occurrence, i => i.SourceAlias));
    var citable = issued.Select(i => i.SourceAlias).Concat(context.Select(c => c.Alias)).ToHashSet(StringComparer.Ordinal);
    return new(name, document, Short(pack.PackId), pages, origin, source, control, context, issued, tools,
        P7F1QProtocol.UserMessage(control, context), citable, SpatialCanonical.Hash(SpatialCanonical.Bytes(pack)), historical,
        P7F1QEvidenceTools.FromStore(store, issued.ToDictionary(i => i.Occurrence, i => i.SourceAlias), toolSet: 2));
}

static (string Id, int[] Pages)[] ExternalDocuments() => [("SRC-089", [1, 2]), ("SRC-095", [1, 2, 3, 5])];

static JsonElement UniverseRow(string id)
{
    using var doc = JsonDocument.Parse(File.ReadAllBytes("eval/a99-closed-loop/pdf-source-determinism-v2/universe-hashes.v1.json"));
    return doc.RootElement.GetProperty("rows").EnumerateArray().Single(r => S(r, "documentId") == id).Clone();
}

static (DocumentSourceSnapshot, PdfSourceDetails) Parse(string pdf, string sha, string universe)
{
    Need(SpatialCanonical.Hash(File.ReadAllBytes(pdf)) == sha, "SOURCE_SHA_DRIFT:" + pdf);
    var parsed = PdfSourceAdapter.BuildWithDetails(pdf);
    Need(parsed.Snapshot.SourceSha256 == sha && parsed.Snapshot.SourceAliasUniverseHash == universe, "UNIVERSE_DRIFT:" + pdf);
    return (parsed.Snapshot, parsed.Details);
}

static async Task<byte[]> FetchEndpointMetadata()
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    return await http.GetByteArrayAsync("https://openrouter.ai/api/v1/models/qwen/qwen3.7-flash/endpoints");
}

static void VerifyEndpoint(byte[] metadata)
{
    using var doc = JsonDocument.Parse(metadata);
    var alibaba = doc.RootElement.GetProperty("data").GetProperty("endpoints").EnumerateArray().SingleOrDefault(e => S(e, "tag") == "alibaba");
    Need(alibaba.ValueKind == JsonValueKind.Object, "ALIBABA_ENDPOINT_MISSING");
    var supported = alibaba.GetProperty("supported_parameters").EnumerateArray().Select(v => v.GetString()).ToHashSet();
    Need(new[] { "tools", "tool_choice", "reasoning", "max_tokens", "temperature", "response_format" }.All(supported.Contains), "ALIBABA_ENDPOINT_PARAMETERS_UNSUPPORTED");
}

static decimal Spent(string root) => !Directory.Exists(Path.Combine(root, "requests")) ? 0 :
    Directory.GetFiles(Path.Combine(root, "requests"), "observation.json", SearchOption.AllDirectories)
        .Sum(f => { using var d = JsonDocument.Parse(File.ReadAllBytes(f)); return d.RootElement.GetProperty("assembled").GetProperty("usage") is { ValueKind: JsonValueKind.Object } u && u.TryGetProperty("cost", out var c) ? c.GetDecimal() : 0m; });

static string Short(string packId) => packId[(packId.LastIndexOf(':') + 1)..];
static string S(JsonElement e, string key) => e.GetProperty(key).GetString()!;
static void Need(bool ok, string code) { if (!ok) throw new InvalidOperationException(code); }
static byte[] ReadPinned(string path, string hash) { var b = File.ReadAllBytes(path); Need(SpatialCanonical.Hash(b) == hash, "PINNED_INPUT_DRIFT:" + Path.GetFileName(path)); return b; }

/// <summary>
/// One USD cap across replicate capture roots and concurrent workers. Under an exclusive lock a worker records its own
/// worst-case in-flight turn and gets back realized spend of all replicates plus OTHER workers' reservations, so the
/// runner's pre-call check bounds realized + every in-flight call by the cap. When only other in-flight reservations
/// would breach the cap the worker holds no reservation and waits for them to settle (no deadlock: waiting workers
/// reserve nothing), so the tail of a run serializes instead of stopping early. Own finished calls are counted twice
/// until the next reservation (conservative).
/// </summary>
internal sealed class SharedBudget(string ledger, string[] roots, decimal worstTurn, decimal cap, Func<string, decimal> spent)
{
    readonly string mine = Path.Combine(ledger, $"reservation.{Environment.ProcessId}.{Guid.NewGuid():N}.txt");
    public decimal Realized() => roots.Sum(spent);
    public decimal ReserveAndReadOthers()
    {
        Directory.CreateDirectory(ledger);
        for (var waited = 0; ; waited++)
        {
            using (var gate = Lock())
            {
                File.Delete(mine);
                var others = Directory.GetFiles(ledger, "reservation.*.txt")
                    .Sum(f => decimal.Parse(File.ReadAllText(f), System.Globalization.CultureInfo.InvariantCulture));
                var realized = Realized();
                // Proceed when the cap holds with every in-flight call, when nothing else is in flight (the runner then
                // decides alone), or after 15 minutes (a crashed worker's stale reservation: the runner then stops).
                if (realized + others + worstTurn <= cap || others == 0 || waited >= 900)
                {
                    File.WriteAllText(mine, worstTurn.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return realized + others;
                }
            }
            Thread.Sleep(1000);
        }
    }
    public void Release() { using var gate = Lock(); File.Delete(mine); }
    FileStream Lock()
    {
        for (var i = 0; ; i++)
            try { return new FileStream(Path.Combine(ledger, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (i < 1200) { Thread.Sleep(50); }
    }
}

/// <summary>Layout-aware chunking arm switch: held-out cases are prefixed L (P05 arm: H); layout block per (case, alias).</summary>
internal static class LayoutArm
{
    public static bool On;
    public static string Prefix => On ? "L" : "H";
    public static readonly Dictionary<string, string> Block = new(StringComparer.Ordinal);
    public static readonly Dictionary<string, PdfSourceDetails> Details = new(StringComparer.Ordinal);
}

internal static class DirectV { public static bool V11; public static bool V42; public static bool V5; }

internal sealed record DirectRequest(string RequestId, F1QCase Case, int Chunk, IReadOnlyList<P7F1DirectProtocol.Subject> Subjects,
    IReadOnlyList<(string Alias, int Page, string Text)> Context, string Scope, P7F1SourceEnrichment Enrichment, IReadOnlyDictionary<string, string>? Ids = null,
    P7F1QEvidenceTools? RequestTools = null);

internal sealed record F1QCase(string Case, string Document, string Pack, int[] Pages, string Origin, DocumentSourceSnapshot Source,
    InterpretationRequest Control, IReadOnlyList<(string Alias, int Page, string Text)> Context, IReadOnlyList<F1QIssuedOccurrence> Issued,
    P7F1QEvidenceTools Tools, string F1QUser, IReadOnlySet<string> InitialCitable, string PackSha256, string? HistoricalControlBodySha256,
    P7F1QEvidenceTools ToolsV2);

internal sealed class NoTransport : IF1QTransport
{
    public Task<F1QHttpObservation> SendAsync(byte[] body, CancellationToken ct) => throw new InvalidOperationException("NO_NETWORK_IN_PREPARE");
}

internal static partial class Program
{
    internal static string ControlTemplatePath = "";
    internal const decimal PerDocumentCapUsd = 0.30m;
}
