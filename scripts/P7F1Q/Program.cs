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
            version = "P7_F1Q_HELDOUT_PLAN_V1", status = "FROZEN_BEFORE_PROVIDER", issue = "ylight23/DocxHeaderExtractor#6",
            pageSelectionSha256 = SpatialCanonical.Hash(selectionBytes), frozenV3PlanSha256 = SpatialCanonical.Hash(File.ReadAllBytes("artifacts/web-pdf-semantic-diagnostic/p7.f1q.execution-plan.v3.json")),
            systemPromptSha256 = promptSha, toolDefinitionsSha256 = toolSha, promptSchemaToolsRoute = "IDENTICAL_TO_FROZEN_V3",
            protocolVersion = P7F1QProtocolV2.Version, model = policyV3.Model, providerRoute = new { order = new[] { "alibaba" }, allowFallbacks = false, requireParameters = true },
            responseFormat = "json_object_on_every_turn", chunkSize = P7F1QProtocolV2.ChunkSize, caps = capsHeldout,
            budget = new { hardCapUsd = capsHeldout.HardCapUsd, softAlertUsd = 1.00m, perDocumentCapUsd = PerDocumentCapUsd, preCallWorstCaseCheck = true },
            retryPolicy = "AT_MOST_ONE_IDENTICAL_BODY_RETRY_PER_REQUEST_ON_TRANSPORT_OR_CONTRACT_FAILURE_NO_ERROR_FEEDBACK_NO_SEMANTIC_RETRY",
            fenceNormalization = P7F1QFenceNormalization.Version, contractViews = new[] { "STRICT_RAW", "NORMALIZED_ACCEPTANCE" },
            goldGate = "EXECUTION_REFUSES_TO_RUN_WITHOUT_A_PINNED_USER_APPROVED_GOLD_FREEZE_COVERING_EVERY_DOCUMENT",
            cases = cases.Select(c => new { c.Case, c.Document, c.Pack, sourceSha256 = c.Source.SourceSha256, evidenceStoreSha256 = c.ToolsV2.EvidenceStoreSha256, packSha256 = c.PackSha256, issued = c.Issued.Count }),
            requests, worstCaseTotalUsdAllRequestsTwoAttempts = requests.Sum(r => JsonSerializer.SerializeToElement(r).GetProperty("worstCaseRequestUsd").GetDecimal()) * 2,
            goldRead = false, providerCalls = 0, productionChanged = false,
            sources = new[] { "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QEvidenceTools.cs", "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QProtocolV2.cs",
                "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QConversation.cs", "src/DocxHeaderExtractor.V5Qualification/P7/P7F1QFenceNormalization.cs", "scripts/P7F1Q/Program.cs" }
                .Select(path => new { path, sha256 = SpatialCanonical.Hash(File.ReadAllBytes(path)) }),
        });
        P7F1QConversationRunner.WriteNew(args[2], plan);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "PLAN_FROZEN", planSha256 = SpatialCanonical.Hash(plan), cases = cases.Count, requests = requests.Count, providerCalls = 0 }));
        break;
    }
    case "execute-heldout":
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
    default: throw new ArgumentException("UNKNOWN_MODE");
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
        foreach (var pack in P7PilotRequestPreflight.SelectPacks(source, details, pages))
            list.Add(Case($"H{id}-{Short(pack.PackId)}", id, pack, pages, "P7_F1Q_HELDOUT_ISSUE_6", source, details, store,
                P7PilotRequestPreflight.F1(source, details, pack), null));
    }
    return list;
}

static decimal SpentFor(string root, string document) => !Directory.Exists(Path.Combine(root, "requests")) ? 0 :
    Directory.GetDirectories(Path.Combine(root, "requests"), $"H{document}-*").Sum(d =>
        Directory.GetFiles(d, "observation.json", SearchOption.AllDirectories).Sum(f =>
        { using var j = JsonDocument.Parse(File.ReadAllBytes(f)); return j.RootElement.GetProperty("assembled").GetProperty("usage") is { ValueKind: JsonValueKind.Object } u && u.TryGetProperty("cost", out var c) ? c.GetDecimal() : 0m; }));

static IReadOnlyList<IReadOnlyList<P7F1QProtocolV2.V5Issued>> ChunksOf(F1QCase c)
{
    using var user = JsonDocument.Parse(c.Control.ControlUserMessage);
    var correspondences = user.RootElement.GetProperty("occurrences").EnumerateArray().ToDictionary(o => o.GetProperty("id").GetString()!, o => o.GetProperty("correspondences").Clone());
    return P7F1QProtocolV2.Chunks(c.Issued.Select(i => new P7F1QProtocolV2.V5Issued(i.Occurrence, i.SourceAlias, i.Page, i.Text, correspondences[i.Occurrence])).ToArray());
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
