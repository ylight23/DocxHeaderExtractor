using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.V5Qualification.P7;

// V2 is a separately authorized policy; immutable V1 financial/readiness receipts stay unchanged.
if (args.Length < 1) throw new ArgumentException("prepare|execute|score (see README)");
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
if (args[0] == "prepare")
{
    if (args.Length != 3) throw new ArgumentException("prepare <frozen-request-dir> <new-public-plan.json>");
    var manifest = ReadPinned(Path.Combine(args[1], "request-manifest.v1.json"), P7F1ExecutionReadiness.FrozenRequestManifestSha256);
    var plan = P7F1ExecutionReadiness.Prepare(manifest);
    foreach (var c in plan.Calls) P7F1ExecutionReadiness.ValidateBody(c, ReadBody(args[1], c.ProviderBodyFile));
    foreach (var source in JsonDocument.Parse(manifest).RootElement.GetProperty("generationSourceFiles").EnumerateArray())
        ReadPinned(S(source, "path"), S(source, "sha256"));
    var pinned = P7FinancialQualification.Bind(File.ReadAllBytes("artifacts/web-pdf-semantic-diagnostic/p7.d3.financial-endpoint-snapshot.v1/endpoint-metadata.json"));
    const string instruction = "CHO PHÉP EXECUTION CHẠY LUÔN, RETRY MỖI 1 REQUEST 1 LẦN NẾU FAIL VÀ GHI RÕ LỖI REQUEST FAIL, SAU ĐÓ TỔNG HỢP VÀ PUSH KẾT QUẢ, CỨ CHẠY RỒI CÂN ĐỐI NGÂN SÁCH SAU";
    var authorization = new P7PostAccountedAuthorization(P7F1PostAccountedRunner.Version, true, 14, 28, 1,
        plan.RequestManifestSha256, P7F1PostAccountedRunner.BudgetPolicy, pinned.BindingSha256, instruction,
        SpatialCanonical.Hash(Encoding.UTF8.GetBytes(instruction)));
    P7F1PostAccountedRunner.ValidateAuthorization(authorization, plan, pinned);
    var bytes = SpatialCanonical.Bytes(new { version = P7F1PostAccountedRunner.Version, status = "AUTHORIZED_F1_ONLY_BEFORE_PROVIDER",
        authorization, authorizedAt = DateTimeOffset.UtcNow, model = pinned.Model, provider = pinned.Tag, endpoint = pinned.ChatEndpoint,
        baselineExecutionManifestSha256 = P7FinancialQualification.ExecutionManifestSha256,
        baselineFinancialManifestSha256 = P7F1FinancialRunner.FinancialManifestSha256,
        retryPolicy = "ONE_IDENTICAL_BODY_RETRY_ON_TRANSPORT_OR_STRICT_CONTRACT_FAILURE_NEVER_ON_SEMANTIC_SCORE",
        internalRetries = 0, repair = false, fallback = false, timeoutSeconds = 300, maxTokens = 32768,
        maximumRequestedCompletionTokens = 28L * 32768, approvedUsdCap = (decimal?)null,
        dedicatedKeyLimitVerified = false, billingUpperBoundVerified = false, unknownChargesAreZero = false,
        exactTokenizerMapping = "UNVERIFIED", goldReadUntilRawFreeze = false, downstreamExecution = "LOCKED", productionPromotion = "LOCKED",
        calls = plan.Calls, sources = new[] { "src/DocxHeaderExtractor.V5Qualification/P7/P7F1PostAccountedRunner.cs",
            "scripts/P7AuthorizedF1/Program.cs", "scripts/P7AuthorizedF1/P7AuthorizedF1.csproj" }
            .Select(path => new { path, sha256 = SpatialCanonical.Hash(File.ReadAllBytes(path)) }) });
    P7F1PostAccountedRunner.Write(args[2], bytes);
    Console.WriteLine(JsonSerializer.Serialize(new { status = "PLAN_FROZEN", hash = SpatialCanonical.Hash(bytes), providerCalls = 0 }));
}
else if (args[0] == "execute")
{
    if (args.Length != 7) throw new ArgumentException("execute <frozen-request-dir> <full-source-dir> <plan.json> <plan-sha256> <new-private-capture-dir> <new-public-receipt.json>");
    var planBytes = ReadPinned(args[3], args[4]); using var approval = JsonDocument.Parse(planBytes);
    foreach (var src in approval.RootElement.GetProperty("sources").EnumerateArray()) ReadPinned(S(src, "path"), S(src, "sha256"));
    var auth = approval.RootElement.GetProperty("authorization").Deserialize<P7PostAccountedAuthorization>(options)!;
    var frozen = ReadPinned(Path.Combine(args[1], "request-manifest.v1.json"), auth.RequestManifestSha256);
    var plan = P7F1ExecutionReadiness.Prepare(frozen); using var manifest = JsonDocument.Parse(frozen);
    var bodies = plan.Calls.ToDictionary(c => c.CallHandle, c => ReadBody(args[1], c.ProviderBodyFile));
    var binding = P7FinancialQualification.Bind(File.ReadAllBytes("artifacts/web-pdf-semantic-diagnostic/p7.d3.financial-endpoint-snapshot.v1/endpoint-metadata.json"));
    P7F1PostAccountedRunner.ValidateAuthorization(auth, plan, binding);
    ReadPinned(Path.Combine(args[2], "source-manifest.json"), "c4f3fd0e75920c9f2472381a2702fd74b15829834f3af2ac32c8e8fadb179d30");
    var contexts = new Dictionary<string, (DocumentSourceSnapshot Source, PdfSourceDetails Details, InterpretationRequest Request)>();
    foreach (var doc in manifest.RootElement.GetProperty("documents").EnumerateArray())
    {
        var id = S(doc, "document"); ReadPinned(Path.Combine(args[2], id, "source.pdf"), S(doc, "sourceSha256"));
        var snapshotBytes = ReadPinned(Path.Combine(args[2], id, "snapshot.json"), S(doc, "snapshotSha256"));
        var parsed = PdfSourceAdapter.BuildWithDetails(Path.Combine(args[2], id, "source.pdf")); var source = parsed.Snapshot;
        Need(source.SourceSha256 == S(doc, "sourceSha256") && source.SourceAliasUniverseHash == S(doc, "sourceUniverseSha256"), "REPARSE_IDENTITY_DRIFT");
        using var snapshot = JsonDocument.Parse(snapshotBytes);
        Need(JsonElement.DeepEquals(snapshot.RootElement.GetProperty("atoms"), SpatialCanonical.Element(source.Atoms)) &&
            JsonElement.DeepEquals(snapshot.RootElement.GetProperty("evidence"), SpatialCanonical.Element(source.Evidence)), "REPARSE_SOURCE_DRIFT");
        var storeBytes = ReadPinned(Path.Combine(args[1], id, "source-evidence-store.json"), S(doc, "storeSha256"));
        Need(PdfSourceEvidenceStore.Build(source, parsed.Details).CanonicalBytes().SequenceEqual(storeBytes), "STORE_DRIFT");
        var pages = doc.GetProperty("evaluationPages").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        foreach (var pack in P7PilotRequestPreflight.SelectPacks(source, parsed.Details, pages))
        {
            var request = P7PilotRequestPreflight.F1(source, parsed.Details, pack);
            foreach (var c in plan.Calls.Where(c => c.Identity.Document == id && c.Identity.Pack == pack.PackId))
            {
                Need(SpatialCanonical.Hash(SpatialCanonical.Bytes(pack)) == c.Identity.PackSha256, "PACK_DRIFT");
                var prompt = c.Identity.Arm == P7CaptureArm.Control ? request.ControlSystemPrompt : request.SystemPrompt;
                var user = c.Identity.Arm == P7CaptureArm.Control ? request.ControlUserMessage : request.UserMessage;
                Need(SpatialCanonical.Hash(Encoding.UTF8.GetBytes(prompt)) == c.Identity.SystemPromptSha256 &&
                    SpatialCanonical.Hash(Encoding.UTF8.GetBytes(user)) == c.Identity.UserMessageSha256, "RECONSTRUCTED_MESSAGE_DRIFT");
                contexts.Add(c.CallHandle, (source, parsed.Details, request));
            }
        }
    }
    Need(contexts.Count == 14, "PARSER_CONTEXT_UNIVERSE_DRIFT");
    Need(!File.Exists(args[6]), "PUBLIC_RECEIPT_EXISTS");
    using var metadataClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    async Task<P7RunnerMetadata> Fresh(CancellationToken ct) => new(await metadataClient.GetByteArrayAsync(P7FinancialQualification.MetadataEndpoint, ct), DateTimeOffset.UtcNow);
    using var transport = new P7OpenRouterF1RunnerTransport(() => Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "");
    JsonElement Validate(P7F1ExecutionCall c, string response)
    {
        var context = contexts[c.CallHandle];
        if (c.Identity.Arm == P7CaptureArm.B) return PdfInterpretationProtocol.Validate(response, context.Request, context.Source, context.Details).StageDecision;
        using var json = JsonDocument.Parse(response);
        PdfInterpretationProtocol.ValidateStage(json.RootElement, context.Request, context.Source);
        return json.RootElement.Clone();
    }
    var attempts = await new P7F1PostAccountedRunner().RunAsync(auth, plan, bodies, binding, transport, args[5], Fresh, Validate);
    var captureBytes = File.ReadAllBytes(Path.Combine(args[5], "capture-freeze.json"));
    var receipt = SpatialCanonical.Bytes(new { version = P7F1PostAccountedRunner.Version, executionPlanSha256 = args[4],
        requestManifestSha256 = auth.RequestManifestSha256, rawCaptureFreezeSha256 = SpatialCanonical.Hash(captureBytes),
        attempts, primaryCalls = attempts.Count(a => a.Attempt == 1), retries = attempts.Count(a => a.Attempt == 2),
        httpAttempts = attempts.Count, acceptedRequests = attempts.Count(a => a.Status == "ACCEPTED"),
        knownCostUsd = attempts.Sum(a => a.Usage.ReportedCostUsd ?? 0), unknownChargeAttempts = attempts.Count(a => a.Usage.ReportedCostUsd is null),
        budgetPolicy = auth.BudgetPolicy, totalCostIsComplete = attempts.All(a => a.Usage.ReportedCostUsd is not null),
        providerExecution = "CLOSED", goldReadDuringExecution = false, downstreamCalls = 0, productionChanged = false, goldMutation = "NONE",
        internalRetries = 0, repair = false, fallback = false, runtimePromotion = "LOCKED", rawResponsesPublic = false,
        rawRetention = "PRIVATE_LOCAL_HASH_FROZEN_NOT_INDEPENDENTLY_REPARSABLE_FROM_GIT" });
    P7F1PostAccountedRunner.Write(args[6], receipt);
    Console.WriteLine(JsonSerializer.Serialize(new { status = "CAPTURE_CLOSED", attempts = attempts.Count, receiptSha256 = SpatialCanonical.Hash(receipt) }));
}
else if (args[0] == "score")
{
    if (args.Length != 6) throw new ArgumentException("score <frozen-request-dir> <capture-dir> <public-capture-receipt> <gold-dir> <new-public-score>");
    // Explicit scoring boundary, reachable only after the complete raw freeze exists. No transport in this branch.
    var capture = File.ReadAllBytes(Path.Combine(args[2], "capture-freeze.json"));
    var receiptBytes = File.ReadAllBytes(args[3]); using var receipt = JsonDocument.Parse(receiptBytes);
    Need(S(receipt.RootElement, "rawCaptureFreezeSha256") == SpatialCanonical.Hash(capture) && S(receipt.RootElement, "providerExecution") == "CLOSED", "RAW_FREEZE_REQUIRED");
    var attempts = receipt.RootElement.GetProperty("attempts").Deserialize<P7PostAccountedAttempt[]>(options)!;
    var manifest = ReadPinned(Path.Combine(args[1], "request-manifest.v1.json"), P7F1ExecutionReadiness.FrozenRequestManifestSha256);
    var plan = P7F1ExecutionReadiness.Prepare(manifest);
    var goldManifestBytes = ReadPinned(Path.Combine(args[4], "gold-manifest.v2.json"), "614b2e09e7568446d343dbab18f4f9d405007657d6f5a665fb1538909de073f7");
    using var goldManifest = JsonDocument.Parse(goldManifestBytes);
    var policy = ReadPinned(Path.Combine(args[4], "evaluation-policy.v1.json"), "88f9885fa39832cfbf8915758f68637a215b8e0ae2a0d5adad5633b80236125c");
    var scorerManifest = ReadPinned(Path.Combine(args[4], "scorer-manifest.v1.json"), "1083c2ddd1b9183225feeba4638a751f9be228cd8e6a535aa0203d5a3ad5909a");
    var rows = new List<(string Document, string Alias, string Gold, string? Control, string? B)>();
    var perDocument = new List<object>();
    foreach (var doc in goldManifest.RootElement.GetProperty("documents").EnumerateArray())
    {
        var id = S(doc, "document");
        var gold = P7PilotGoldReader.Read(ReadPinned(Path.Combine(args[4], S(doc, "goldFile")), S(doc, "goldSha256")), policy);
        var predicted = new Dictionary<P7CaptureArm, Dictionary<string, string>>();
        foreach (var arm in Enum.GetValues<P7CaptureArm>())
        {
            predicted[arm] = [];
            foreach (var c in plan.Calls.Where(c => c.Identity.Document == id && c.Identity.Arm == arm))
            {
                var accepted = attempts.SingleOrDefault(a => a.CallHandle == c.CallHandle && a.Status == "ACCEPTED");
                if (accepted is null) continue;
                var parsedBytes = ReadPinned(Path.Combine(args[2], accepted.SelectedDecisionFile!), accepted.ParsedDecisionSha256!);
                using var parsed = JsonDocument.Parse(parsedBytes);
                var issuedFile = c.ProviderBodyFile.Replace(".F1.CONTROL.provider-body.json", ".issued-universe.json").Replace(".F1.B.provider-body.json", ".issued-universe.json");
                using var issued = JsonDocument.Parse(ReadPinned(Path.Combine(args[1], issuedFile), c.Identity.IssuedUniverseSha256));
                var map = issued.RootElement.EnumerateArray().ToDictionary(e => S(e, "occurrence"), e => S(e, "alias"));
                foreach (var d in parsed.RootElement.GetProperty("decisions").EnumerateArray()) predicted[arm].Add(map[S(d, "occurrence")], S(d, "function"));
            }
        }
        var start = rows.Count;
        foreach (var g in gold.ReviewedRows) rows.Add((id, g.Alias, g.SemanticFunction,
            predicted[P7CaptureArm.Control].GetValueOrDefault(g.Alias), predicted[P7CaptureArm.B].GetValueOrDefault(g.Alias)));
        perDocument.Add(new { document = id, reviewed = gold.ReviewedRows.Count, control = Metrics(rows.Skip(start).Select(r => (r.Gold, r.Control))),
            treatmentB = Metrics(rows.Skip(start).Select(r => (r.Gold, r.B))) });
    }
    Need(rows.Count == 213, "GOLD_DENOMINATOR_DRIFT");
    var paired = rows.Where(r => r.Control is not null && r.B is not null).ToArray();
    int wrongRight = paired.Count(r => r.Control != r.Gold && r.B == r.Gold), rightWrong = paired.Count(r => r.Control == r.Gold && r.B != r.Gold);
    var score = SpatialCanonical.Bytes(new { version = "P7_F1_ISOLATED_POST_ACCOUNTED_SCORE_V1", mode = "F1_ISOLATED_NOT_END_TO_END",
        captureReceiptSha256 = SpatialCanonical.Hash(receiptBytes), rawCaptureFreezeSha256 = SpatialCanonical.Hash(capture),
        requestManifestSha256 = plan.RequestManifestSha256, goldManifestSha256 = SpatialCanonical.Hash(goldManifestBytes),
        scorerManifestSha256 = SpatialCanonical.Hash(scorerManifest), policySha256 = SpatialCanonical.Hash(policy),
        control = Metrics(rows.Select(r => (r.Gold, r.Control))), treatmentB = Metrics(rows.Select(r => (r.Gold, r.B))),
        paired = new { evaluable = paired.Length, wrongToRight = wrongRight, rightToWrong = rightWrong,
            bothCorrect = paired.Count(r => r.Control == r.Gold && r.B == r.Gold), bothWrong = paired.Count(r => r.Control != r.Gold && r.B != r.Gold) },
        perDocument, disagreements = rows.Where(r => r.Control != r.B || r.Control != r.Gold).Select(r => new { r.Document, r.Alias, r.Gold, r.Control, r.B }),
        outsideScopePerArm = 430, outsideScopeStatus = "UNKNOWN_UNSCORED", failuresBecomeOTHER = false,
        retryRecoveredOutputsIncluded = true, cleanZeroRetryQualification = attempts.All(a => a.Attempt == 1),
        semanticCausality = "COMPOSITE_B_ONLY_NOT_GEOMETRY_OR_REFERENCES_ISOLATED", titleBoundaryMetric = "NOT_EVALUATED_F1_ONLY",
        providerCallsDuringScoring = 0, goldMutation = "NONE", reviewerSidecarRead = false, productionChanged = false,
        generalization = "NOT_ESTABLISHED_DEVELOPMENT_PILOT", downstreamExecution = "LOCKED", productionPromotion = "LOCKED" });
    P7F1PostAccountedRunner.Write(args[5], score);
    Console.WriteLine(Encoding.UTF8.GetString(score));
}
else throw new ArgumentException("UNKNOWN_MODE");

static object Metrics(IEnumerable<(string Gold, string? Prediction)> source)
{
    var rows = source.ToArray(); var available = rows.Where(r => r.Prediction is not null).ToArray();
    var labels = new[] { "ESTABLISHES_STRUCTURE", "REPRESENTS_STRUCTURE", "OTHER" };
    var tp = available.Count(r => r.Gold == labels[0] && r.Prediction == labels[0]);
    var fp = available.Count(r => r.Gold != labels[0] && r.Prediction == labels[0]);
    var fn = available.Count(r => r.Gold == labels[0] && r.Prediction != labels[0]);
    return new { evaluationUniverse = rows.Length, evaluated = available.Length, missing = rows.Length - available.Length,
        correct = available.Count(r => r.Gold == r.Prediction), confusion = labels.ToDictionary(g => g, g => labels.ToDictionary(p => p, p => available.Count(r => r.Gold == g && r.Prediction == p))),
        establishes = new { tp, fp, fn, tn = available.Length - tp - fp - fn,
            precision = tp + fp == 0 ? (double?)null : (double)tp / (tp + fp), recall = tp + fn == 0 ? (double?)null : (double)tp / (tp + fn) } };
}
static string S(JsonElement e, string key) => e.GetProperty(key).GetString()!;
static void Need(bool ok, string code) => P7FinancialQualification.Need(ok, code);
static byte[] ReadPinned(string path, string hash) { var b = File.ReadAllBytes(path); Need(SpatialCanonical.Hash(b) == hash, "PINNED_INPUT_DRIFT:" + Path.GetFileName(path)); return b; }
static byte[] ReadBody(string root, string relative) {
    var directory = Path.GetFullPath(root) + Path.DirectorySeparatorChar; var path = Path.GetFullPath(Path.Combine(directory, relative));
    Need(path.StartsWith(directory, StringComparison.OrdinalIgnoreCase), "BODY_PATH_OUTSIDE_FREEZE"); return File.ReadAllBytes(path);
}
