using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using A = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectAgent;
using C = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectRepeatCanary;

namespace DocxHeaderExtractor.V5Qualification.P7;

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

    public static P7F1V6Universe Load(string documentPath, string fileSha256) =>
        P7F1V6Universe.RebuildOwnerSupplied(documentPath, fileSha256, null, out var why) ?? throw new InvalidOperationException(why);

    public static Dictionary<string, object?> PinnedValues(string documentPath, string codeCommit)
    {
        var fileSha = Sha256Of(documentPath);
        var u = Load(documentPath, fileSha);
        var segs = SegmentsOf(u);
        var picks = segs.Select(s => new C.Pick(s, "ALL: every segment of the document", new C.SegStat(s, 0, 0, 0, 0, 0, 0))).ToArray();
        var ceilings = C.Arms.ToDictionary(a => a.Arm, a => C.CeilingUsd(u, picks, a.Mode));
        var worst = P7F1V6Direct.AttemptUsd(C.MaxRequestBytes);
        var arms = C.Arms.Select(a =>
        {
            var first = segs.Select(s => C.FirstRequest(a.Mode, u, s)).ToList();
            return new
            {
                arm = a.Arm,
                promptSha256 = A.Sha(A.Prompt(a.Mode, rangeStated: true)),
                budget = new { maxCalls = C.PostsPerArm(picks), ceilingUsd = ceilings[a.Arm], maxRequestBytes = C.MaxRequestBytes, worstCaseAllAtByteCapUsd = Math.Round(C.PostsPerArm(picks) * worst, 6) },
                earlyStop = P7F1V6DirectV2.MaxConsecutiveContractFailures,
                firstRequests = first.Select((r, i) => new { unitId = segs[i], turn = 1, sha256 = r.Sha256, bytes = r.Bytes }).ToArray(),
                requestPlanSha256 = P7F1V6Protocol.Sha(string.Join("\n", first.Select(r => r.Sha256))),
            };
        }).ToArray();
        string ProviderBlockSha()
        {
            using var d = JsonDocument.Parse(C.FirstRequest(A.Arm.Auto, u, segs[0]).Body);
            return P7F1V6Protocol.Sha(d.RootElement.GetProperty("provider").GetRawText());
        }
        var template = A.Build("<<SYSTEM_PROMPT>>", "<<USER_PAYLOAD>>", new System.Text.Json.Nodes.JsonArray(), "auto");
        var authority = AuthorityIdOf(fileSha);
        var pages = u.Lines.Select(l => l.Page).Distinct().Count();
        var fileName = Path.GetFileName(documentPath);
        var prefetch = segs.Select(s =>
        {
            var (decide, _) = P7F1V6DirectCanary410.Scope(u, s);
            var r = new P7F1V6AgentTools(u.Tools).Prefetch(decide);
            return new { segment = s, targets = decide.Length, listed = r.Listed, withoutOtherOccurrence = r.WithoutOther, observationBytes = r.Bytes };
        }).ToArray();
        return new Dictionary<string, object?>
        {
            ["artifact"] = PlanArtifact,
            ["approvalToSend"] = false,
            ["codeCommit"] = codeCommit,
            ["authorityId"] = authority,
            ["document"] = "owner-supplied (outside the repository): " + fileName,
            ["documentPublicity"] = "NOT part of the repository's public corpus. A document supplied by the owner (" + fileName + "), read from outside the repository; whether it is public is not known to the program. "
                + "The owner decided that nothing is redacted: the text of every line, including any names, telephone numbers and e-mail addresses it contains, is sent as it is. "
                + "Requests, raw responses and the analysis stay in the safe store and are not committed or pushed.",
            ["sourceSha256"] = fileSha,
            ["universeSha256"] = u.UniverseSha256,
            ["evidenceStoreSha256"] = u.EvidenceStoreSha256,
            ["atoms"] = u.Aliases.Count,
            ["ownerSupplied"] = new
            {
                fileName, fileBytes = new FileInfo(documentPath).Length, fileSha256 = fileSha, pagesWithText = pages, lines = u.Lines.Count,
                publicCorpus = false,
                sourceGate = "the public-corpus source gate is not used; this plan pins the file hash and the loader refuses any other bytes",
                pathNotPinned = "the file path is an argument of the plan check and of the run, not part of the plan",
                redaction = "none: the owner chose to send the document text unchanged",
                lineTextsSha256 = P7F1V6Protocol.Sha(string.Join("\n", u.Lines.Select(l => l.Alias + "\t" + l.Text))),
            },
            ["goldForScorerOnly"] = "none: there is no Gold for this document. Nothing is scored. The result is the set of lines each arm selects in each repetition, read by the owner.",
            ["task"] = P7F1V6DirectD45.TaskId,
            ["evaluationLabel"] = "Owner-document trial: which lines Qwen selects as headings under the repeat-canary request; no Gold, no score",
            ["instruction"] = P7F1V6DirectD45.Instruction,
            ["instructionSha256"] = P7F1V6Core.InstructionSha256(P7F1V6DirectD45.Instruction),
            ["requestVersion"] = A.RequestVersion,
            ["promptVersion"] = A.PromptVersion + " + stated target range",
            ["responseVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion,
            ["versionFieldsInPayload"] = false,
            ["requestIdenticalTo"] = "the repeat-canary request (plans e73415f7 and df369552, code b672c59): same system prompts (CONTROL 5a83e083…, PREFETCH 2290301b…), instruction, payload layout, tools, envelope and route; only the document differs",
            ["segments"] = segs,
            ["arms"] = arms,
            ["prefetchMeasuredPerSegment"] = prefetch,
            ["repetitions"] = new { perArmAndSegment = C.Reps, requests = "identical bytes each time, so the spread is sampling variation of the same request", order = "per repetition, per segment: CONTROL then PREFETCH" },
            ["frozen"] = new
            {
                scope = "one owner-supplied document: the loader refuses any other bytes; the plan is re-derived from code and the file and any drift refuses the run",
                authorityId = authority,
                sourceSha256 = fileSha,
                universeSha256 = u.UniverseSha256,
                provider = new { only = new[] { P7F1V6Protocol.ProviderTag }, ignore = P7F1V6Protocol.DefaultIgnore, allowFallbacks = false, requireParameters = true,
                    providerBlockSha256 = ProviderBlockSha(), model = P7F1V6Protocol.ChatModel, endpointCheck = "response provider must be Alibaba and model qwen/qwen3.7-flash, else the run halts" },
                requestTemplateSha256 = template.Sha256,
                toolDefinitionsSha256 = P7F1V6AgentTools.DefinitionsSha256(),
                promptSha256 = new { control = A.AutoPromptRangeSha256, prefetch = A.PrefetchPromptRangeSha256 },
                firstRequestsSha256 = P7F1V6Protocol.Sha(string.Join("\n", C.Arms.SelectMany(a => segs.Select(sg => C.FirstRequest(a.Mode, u, sg).Sha256)))),
            },
            ["tools"] = new { version = P7F1V6AgentTools.Version, definitionsSha256 = P7F1V6AgentTools.DefinitionsSha256(), names = P7F1V6AgentTools.Names,
                availability = "declared in both arms, optional; tool results come from the document lines" },
            ["requestShape"] = "SSE (stream true), reasoning enabled true, response_format absent on turns 1 to 3 and json_object on turn 4, temperature 0, max_tokens 32768; the strict parser refuses any final answer that is not exactly one JSON object",
            ["model"] = P7F1V6Protocol.ChatModel,
            ["route"] = new { only = new[] { P7F1V6Protocol.ProviderTag }, ignore = P7F1V6Protocol.DefaultIgnore, allowFallbacks = false, requireParameters = true },
            ["zdr"] = "false (owner decision, to be accepted again in the authorization)",
            ["maxTokens"] = P7F1V6Protocol.MaxTokens,
            ["retries"] = C.Retries,
            ["budget"] = new
            {
                maxCalls = C.PostsPerArm(picks) * C.Arms.Length, ceilingUsd = Math.Round(ceilings.Values.Sum(), 2), maxRequestBytes = C.MaxRequestBytes,
                perArm = $"{C.PostsPerArm(picks)} posts: {C.Units(picks)} first posts ({segs.Count} segments x {C.Reps} repetitions) and {C.PostsPerArm(picks) - C.Units(picks)} for tool turns",
                ceilingIs = "derived as in the repeat canary: the reserve of every planned first request, repetitions included, plus the spare posts at the worst case, plus 10%; a bound on the reserve, not the expected spend",
            },
            ["measurements"] = new[]
            {
                "per arm and repetition: the lines selected in each segment (aliases; the owner reads their text)",
                "whether the same lines are selected in every repetition of a cell, and where CONTROL and PREFETCH differ",
                "protocol outcome per unit with its code; refused answers are counted and never repaired; offline counterfactuals are labelled not official",
                "tool calls, tokens and cost per request",
            },
            ["readingRules"] = new[]
            {
                "no Gold: the program makes no claim that a selected line is or is not a heading; the owner judges",
                "5 repetitions per cell: an indication of stability, not a significance test",
                "one document: nothing here says how the request behaves on other documents",
            },
            ["rawPublication"] = "None. Requests (which hold the document text), raw responses, reasoning and the analysis stay in the safe store outside the repository. Only a receipt without any line text is committed.",
            ["liveAuthorization"] = new { required = true, artifact = P7F1V6DirectAuthorization.Artifact, status = P7F1V6DirectAuthorization.Approved, campaigns = 1 },
        };
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
        if (!Serialize(PinnedValues(documentPath, planCommit ?? "")).AsSpan().SequenceEqual(bytes)) failures.Add("PINNED_VALUES_DIFFER");
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
        var u = Load(documentPath, plan.RootElement.GetProperty("sourceSha256").GetString()!);
        var budgets = C.Arms.ToDictionary(a => a.Arm, a => new P7F1V6DirectV2.Budget(posts, ceilings[a.Arm], C.MaxRequestBytes));
        var stores = C.Arms.ToDictionary(a => a.Arm, _ => P7F1V6SafeRawStore.CreateRun(safeRoot));
        var models = C.Arms.ToDictionary(a => a.Arm, a => new P7F1V6AgentAuditedModel(new P7F1V6DirectAuditedModel(
            new P7F1V6OpenRouterTransport(handler, TimeSpan.FromSeconds(300)), apiKey, stores[a.Arm], budgets[a.Arm])));
        var state = C.RunArms(u, segments, models.ToDictionary(kv => kv.Key, kv => (A.AgentModel)kv.Value.Turn), budgets);
        return state.Select(s => new C.ArmResult(s.Arm, s.Units, models[s.Arm].Ledger, s.Budget.Calls, s.Budget.ReservedUsd, s.Budget.ActualUsd,
            s.Budget.HaltReason, s.Budget.Refusals.ToArray(), stores[s.Arm].RunDirectory)).ToList();
    }
}
