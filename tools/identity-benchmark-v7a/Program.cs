using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Eval.ReasoningRetention;
using DocxHeaderExtractor.Infrastructure.AI;

namespace IdentityBenchmarkV7A;

internal static class Program
{
    private const string SourceRelative = "artifacts/identity-benchmark/v6/owner-evidence/source-only-freeze-v1";
    private const string OutputRelative = "artifacts/identity-benchmark/v7/identity-proposal/preflight-v1-sparse-positive";
    private const string ExecutionRelative = "artifacts/identity-benchmark/v7/identity-proposal/execution-v1-sparse-positive";
    private const string RequestSetSha256 = "a32a02ab1c55dce4db5042ddb9af47a0e91512bd817770b9269bee65a190e219";
    private const string Provider = "OpenRouter";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const int ExpectedOccurrences = 226;
    private static readonly string[] ExpectedDocuments = ["DOC-0123", "DOC-0133", "DOC-0252"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            if (args.Any(x => string.Equals(x, "--execute-v7a", StringComparison.Ordinal))) return await ExecuteAsync(root);
            if (args.Any(x => string.Equals(x, "--finalize-v7a", StringComparison.Ordinal))) return await FinalizeAsync(root);
            await PrepareAsync(root); Console.WriteLine("V7A_STATUS=SPARSE_IDENTITY_PROPOSAL_PREFLIGHT_FROZEN PROVIDER_CALLS=0 GOLD_READ_COUNT=0"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"V7A_ERROR={ex.Message}"); return 2; }
    }

    private static async Task PrepareAsync(string root)
    {
        var source = Full(root, SourceRelative);
        var output = Full(root, OutputRelative);
        Directory.CreateDirectory(output);
        using var sourceManifest = Read(Path.Combine(source, "manifest.json"));
        using var packetsDoc = Read(Path.Combine(source, "packets.json"));
        ValidateSource(sourceManifest.RootElement, packetsDoc.RootElement);
        var packets = packetsDoc.RootElement.EnumerateArray().ToArray();
        var requests = packets.GroupBy(x => x.GetProperty("documentId").GetString()!, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g => BuildRequest(g.Key, g.ToArray())).ToArray();
        var requestJson = requests.Select(x => JsonSerializer.Serialize(x, JsonOptions)).ToArray();
        var hashes = requestJson.Select(Sha256Text).ToArray();
        var sourceFingerprint = new { sourceManifestSha256 = Sha256File(Path.Combine(source, "manifest.json")), packetsSha256 = Sha256File(Path.Combine(source, "packets.json")) };
        await WriteAsync(Path.Combine(output, "request-contract.json"), new
        {
            schemaVersion = "a99-v7a-sparse-positive-identity-proposal-contract-v1",
            input = new[] { "sourceOccurrences", "documentOrder", "sourceContainerIdentity", "localContext", "parserOwnedEvidence", "deterministicCandidatePairReasons" },
            output = new[] { "positiveProposals", "unresolvedPairIds" },
            proposalRelations = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF" },
            defaultPolicy = "KEEP_SPLIT",
            modelPositiveStatus = "MODEL_PROPOSED_ONLY",
            autoCollapse = false,
            forbidden = new[] { "Gold", "V4H/V5/V6D predictions", "parent", "ROOT", "level", "pair labels for omitted pairs", "canonical occurrence IDs in output", "automatic merge authority" },
            validation = new[] { "known pair handle", "known occurrence handles", "no self pair", "one proposal per pair", "valid relation enum", "continuation direction only for CONTINUATION_OF", "no automatic component collapse" },
        });
        await WriteAsync(Path.Combine(output, "source-fingerprint.json"), sourceFingerprint);
        var candidatePairCount = requests.Sum(CountCandidatePairs);
        await WriteAsync(Path.Combine(output, "requests.json"), new { schemaVersion = "a99-v7a-sparse-positive-identity-requests-v1", status = "FROZEN_SOURCE_ONLY_SPARSE_POSITIVE_REQUESTS", requestCount = requests.Length, occurrenceCount = packets.Length, candidatePairCount, sourceFingerprint, goldDerivedInput = false, v4hEvaluationIncluded = false, v5PredictionIncluded = false, v6dPredictionIncluded = false, autoCollapse = false, requests = requests.Select((x, i) => new { request = x, requestHash = hashes[i] }).ToArray() });
        await WriteAsync(Path.Combine(output, "manifest.json"), new { schemaVersion = "a99-v7a-preflight-manifest-v1", status = "READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION", requestCount = requests.Length, documentCount = requests.Length, occurrenceCount = packets.Length, candidatePairCount, requestHashes = hashes, sourceFingerprint, modelCalls = 0, providerCalls = 0, goldReadCount = 0, goldDerivedInput = false, v4hEvaluationIncluded = false, v5PredictionIncluded = false, v6dPredictionIncluded = false, autoCollapse = false, generalizationClaim = false, candidatePolicy = "Source-only deterministic attention candidates; candidate pairs are not merge evidence." });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), $"# A99 V7A — sparse positive identity proposal preflight\n\nFrozen document-global requests: **{requests.Length}**. Source occurrences: **{packets.Length}**. Candidate pairs: **{candidatePairCount}**.\n\nThe model may propose only positive identity relations. Omitted pairs remain `NO_CLAIM / KEEP_SPLIT`; proposals remain `MODEL_PROPOSED_ONLY` and cannot collapse nodes. Gold, V4H/V5/V6D predictions, parent, ROOT, and level are excluded.\n", new UTF8Encoding(false));
    }

    private static async Task<int> ExecuteAsync(string root)
    {
        var preflight = Full(root, OutputRelative);
        var execution = Full(root, ExecutionRelative);
        var frozen = LoadFrozenRequests(preflight);
        Require(!Directory.Exists(execution) || !Directory.EnumerateFiles(execution, "*", SearchOption.AllDirectories).Any(), "V7A_EXECUTION_ALREADY_STARTED_NO_RESUME");
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return await BlockExecutionAsync(execution);
        var attemptsDir = Path.Combine(execution, "attempts"); var rawDir = Path.Combine(execution, "raw-responses"); var parsedDir = Path.Combine(execution, "parsed");
        Directory.CreateDirectory(attemptsDir); Directory.CreateDirectory(rawDir); Directory.CreateDirectory(parsedDir);
        await WriteAsync(Path.Combine(execution, "execution-manifest.json"), new { schemaVersion = "a99-v7a-execution-manifest-v1", status = "EXECUTING", provider = Provider, model = Model, endpoint = Endpoint, requestSetSha256 = RequestSetSha256, scheduledCalls = 3, transientRequestRetries = 0, maxParallelRequests = 1, goldReadCount = 0, historicalPredictionReadCount = 0, evaluationReadCount = 0, modelProposedOnly = true, autoCollapse = false, defaultPolicy = "KEEP_SPLIT" });
        var options = new RemoteInferenceOptions { Endpoint = new Uri(Endpoint), ApiKey = apiKey, Model = Model, ContextSize = 1_000_000, MaxOutputTokens = 24_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0, MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true };
        var capability = new OpenRouterModelCapability { ModelId = Model, ContextLength = 1_000_000, ReasoningSupported = true, StructuredOutputSupported = true, SelectedReasoningEffort = "enabled", ReasoningEnabled = true, EffortListReported = false, MaxCompletionTokens = 65_536 };
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(root, "identity-benchmark-v7a", "3-document-global-sparse-positive-identity-proposals");
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var completed = new List<object>();
        for (var i = 0; i < frozen.Count; i++)
        {
            var item = frozen[i]; var sequence = i + 1; var attemptId = $"primary-{sequence:D3}";
            await WriteAsync(Path.Combine(attemptsDir, $"{sequence:D3}.started.json"), new { schemaVersion = "a99-v7a-attempt-start-v1", attemptId, sequence, documentId = item.DocumentId, requestHash = item.RequestHash, requestSetSha256 = RequestSetSha256, retry = false, goldReadBeforeAttempt = false, historicalPredictionReadBeforeAttempt = false, evaluationReadBeforeAttempt = false, modelProposedOnly = true, autoCollapse = false });
            var status = "PROVIDER_ERROR"; string? error = null; string? rawHash = null; string? parsedHash = null; int? httpStatus = null; string? providerCallId = null; RequestPacketTelemetry? telemetry = null; object? parsed = null;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var requestJson = item.Request.GetRawText();
                var result = await model.CompleteRawStructuredSemanticAsync(item.DocumentId, "V7A_SPARSE_POSITIVE_IDENTITY_PROPOSALS", $"v7a:{item.DocumentId}:{item.RequestHash}", requestJson, item.OccurrenceCount, item.CandidatePairCount, item.CandidatePairCount, SystemPromptV7A, $"TASK=V7A_SPARSE_POSITIVE_IDENTITY_PROPOSALS\n{requestJson}\nReturn exactly the requested JSON object.", ResponseSchemaV7A(), "a99_v7a_sparse_positive_identity_proposals_v1");
                telemetry = result.Telemetry; httpStatus = telemetry.HttpStatus; providerCallId = telemetry.ProviderCallId; rawHash = Sha256Text(result.Content);
                await File.WriteAllTextAsync(Path.Combine(rawDir, $"{sequence:D3}.json"), result.Content, new UTF8Encoding(false));
                using var response = JsonDocument.Parse(result.Content);
                var validation = ValidateResponse(item.Request, response.RootElement);
                parsed = new { response = response.RootElement.Clone(), validation }; parsedHash = Sha256Text(JsonSerializer.Serialize(parsed, JsonOptions));
                await WriteAsync(Path.Combine(parsedDir, $"{sequence:D3}.json"), parsed); status = validation.Accepted ? "VALID" : "INVALID_VALIDATION"; error = validation.Accepted ? null : validation.Errors.FirstOrDefault();
            }
            catch (JsonException ex) { status = "INVALID_SCHEMA"; error = ex.Message; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or ReasoningCompletionException or FormatException) { status = "PROVIDER_ERROR"; error = ex.GetType().Name + ":" + ex.Message; }
            stopwatch.Stop();
            var completedAttempt = new { schemaVersion = "a99-v7a-attempt-v1", attemptId, sequence, documentId = item.DocumentId, requestHash = item.RequestHash, requestSetSha256 = RequestSetSha256, status, httpStatus, providerCallId, rawResponseSha256 = rawHash, parsedResponseSha256 = parsedHash, provider = Provider, model = Model, latencyMs = stopwatch.ElapsedMilliseconds, reportedInputTokens = telemetry?.ReportedInputTokens, reportedReasoningTokens = telemetry?.ReportedReasoningTokens, reportedOutputTokens = telemetry?.ReportedOutputTokens, finishReason = telemetry?.FinishReason, error, retryCount = 0, goldReadBeforeFreeze = false, historicalPredictionReadBeforeFreeze = false, evaluationReadBeforeFreeze = false, modelProposedOnly = true, autoCollapse = false, response = parsed };
            await WriteAsync(Path.Combine(attemptsDir, $"{sequence:D3}.completed.json"), completedAttempt); completed.Add(completedAttempt);
            await WriteAsync(Path.Combine(execution, "attempt-manifest.json"), new { schemaVersion = "a99-v7a-attempt-manifest-v1", immutableAttemptRecords = true, expectedAttemptCount = 3, actualAttemptCount = completed.Count, actualModelCalls = model.ProviderCalls, actualProviderCalls = model.ProviderCalls, retryCount = 0, goldReadCount = 0, historicalPredictionReadCount = 0, evaluationReadCount = 0, modelProposedOnly = true, autoCollapse = false, attempts = completed });
            Console.WriteLine($"V7A_ATTEMPT={sequence}/3 DOCUMENT={item.DocumentId} STATUS={status} PROVIDER_CALLS={model.ProviderCalls}");
        }
        Require(completed.Count == 3 && model.ProviderCalls == 3, "V7A_PROVIDER_CALL_COUNT");
        await WriteAsync(Path.Combine(execution, "prediction-freeze.json"), new { schemaVersion = "a99-v7a-prediction-freeze-v1", status = "V7A_PROPOSALS_FROZEN_BEFORE_EVALUATION_OR_PROMOTION", requestSetSha256 = RequestSetSha256, completedAttempts = 3, modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls, goldReadCount = 0, historicalPredictionReadCount = 0, evaluationReadCount = 0, modelProposedOnly = true, autoCollapse = false, defaultPolicy = "KEEP_SPLIT", connectedComponentsAuthority = false, attempts = completed });
        Console.WriteLine($"V7A_STATUS=PROPOSALS_FROZEN REQUESTS=3 MODEL_CALLS={model.ProviderCalls} PROVIDER_CALLS={model.ProviderCalls} GOLD_READ_COUNT=0 AUTO_COLLAPSE=false");
        return 0;
    }

    private static async Task<int> BlockExecutionAsync(string execution)
    {
        Directory.CreateDirectory(execution);
        await WriteAsync(Path.Combine(execution, "execution-manifest.json"), new { schemaVersion = "a99-v7a-execution-manifest-v1", status = "BLOCKED_ON_PROVIDER_API_KEY", scheduledCalls = 3, modelCalls = 0, providerCalls = 0, retryCount = 0, goldReadCount = 0, historicalPredictionReadCount = 0, evaluationReadCount = 0, modelProposedOnly = true, autoCollapse = false });
        return 1;
    }

    private static async Task<int> FinalizeAsync(string root)
    {
        var execution = Full(root, ExecutionRelative); using var manifest = Read(Path.Combine(execution, "attempt-manifest.json"));
        var m = manifest.RootElement; Require(m.GetProperty("actualAttemptCount").GetInt32() == 3 && m.GetProperty("actualProviderCalls").GetInt32() == 3 && m.GetProperty("retryCount").GetInt32() == 0 && m.GetProperty("goldReadCount").GetInt32() == 0 && m.GetProperty("historicalPredictionReadCount").GetInt32() == 0 && m.GetProperty("evaluationReadCount").GetInt32() == 0 && m.GetProperty("modelProposedOnly").GetBoolean() && !m.GetProperty("autoCollapse").GetBoolean(), "V7A_FINALIZE_FIREWALL");
        var valid = 0; var invalid = 0; var providerErrors = 0; var positiveEdges = 0; var noClaim = 0; var contEdges = 0; var touched = 0; var unknown = 0; var duplicate = 0; var self = 0; var directionConflict = 0; var schemaFailures = 0; var componentDiagnostics = new List<ComponentDiagnostic>();
        foreach (var attempt in m.GetProperty("attempts").EnumerateArray())
        {
            var status = attempt.GetProperty("status").GetString()!; if (status == "VALID") valid++; else if (status is "INVALID_VALIDATION" or "INVALID_SCHEMA") invalid++; else providerErrors++;
            if (status != "VALID") { if (status == "INVALID_SCHEMA") schemaFailures++; continue; }
            var responseEnvelope = attempt.GetProperty("response");
            var response = responseEnvelope.GetProperty("response"); var proposals = response.GetProperty("positiveProposals").EnumerateArray().ToArray(); positiveEdges += proposals.Length; contEdges += proposals.Count(x => x.GetProperty("relation").GetString() == "CONTINUATION_OF");
            if (responseEnvelope.TryGetProperty("validation", out var validation))
            {
                unknown += validation.GetProperty("unknownHandleCount").GetInt32();
                duplicate += validation.GetProperty("duplicateEdgeCount").GetInt32();
                self += validation.GetProperty("selfEdgeCount").GetInt32();
                directionConflict += validation.GetProperty("directionConflictCount").GetInt32();
            }
            var request = LoadFrozenRequests(Full(root, OutputRelative)).Single(x => x.DocumentId == attempt.GetProperty("documentId").GetString()); var candidateIds = request.CandidateIds; var proposedIds = proposals.Select(x => x.GetProperty("pairId").GetString()!).ToHashSet(StringComparer.Ordinal); noClaim += candidateIds.Count(x => !proposedIds.Contains(x)); touched += proposals.SelectMany(x => new[] { x.GetProperty("left").GetString(), x.GetProperty("right").GetString() }).Distinct(StringComparer.Ordinal).Count();
            foreach (var p in proposals) { var left = p.GetProperty("left").GetString()!; var right = p.GetProperty("right").GetString()!; if (left == right) self++; if (p.GetProperty("relation").GetString() == "CONTINUATION_OF" && (!p.TryGetProperty("from", out var from) || !p.TryGetProperty("to", out var to) || from.ValueKind != JsonValueKind.String || to.ValueKind != JsonValueKind.String || ((from.GetString() != left && from.GetString() != right) || (to.GetString() != left && to.GetString() != right) || from.GetString() == to.GetString()))) directionConflict++; }
            var components = proposals.Select(x => (x.GetProperty("left").GetString()!, x.GetProperty("right").GetString()!)).ToArray(); var componentCount = CountComponents(components); componentDiagnostics.Add(new ComponentDiagnostic(attempt.GetProperty("documentId").GetString()!, candidateIds.Count, proposals.Length, candidateIds.Count - proposedIds.Count, touched, componentCount, LargestComponent(components)));
        }
        var summary = new { schemaVersion = "a99-v7a-proposal-summary-v1", status = "V7A_PROPOSALS_FROZEN_BEFORE_EVALUATION_OR_PROMOTION", scheduledRequests = 3, valid, invalidValidation = invalid, invalidSchema = schemaFailures, providerErrors, proposedPositiveEdges = positiveEdges, noClaimPairs = noClaim, proposedContinuationEdges = contEdges, occurrencesTouchedByPositiveProposals = touched, connectedComponentCountDiagnostic = componentDiagnostics.Sum(x => x.ConnectedComponentCount), largestProposedComponentDiagnostic = componentDiagnostics.Count == 0 ? 0 : componentDiagnostics.Max(x => x.LargestProposedComponent), unknownHandles = unknown, duplicateEdges = duplicate, selfEdges = self, directionConflicts = directionConflict, componentDiagnostics, modelCalls = 3, providerCalls = 3, goldReadCount = 0, historicalPredictionReadCount = 0, evaluationReadCount = 0, modelProposedOnly = true, autoCollapse = false, connectedComponentsAuthority = false };
        await WriteAsync(Path.Combine(execution, "prediction-summary.json"), summary); await WriteAsync(Path.Combine(execution, "execution-final.json"), new { schemaVersion = "a99-v7a-execution-final-v1", status = "V7A_PROPOSAL_FREEZE_COMPLETE", valid, invalidValidation = invalid, providerErrors, modelCalls = 3, providerCalls = 3, goldReadCount = 0, historicalPredictionReadCount = 0, evaluationReadCount = 0, modelProposedOnly = true, autoCollapse = false, frozenBeforeEvaluationOrPromotion = true });
        Console.WriteLine($"V7A_OFFLINE_SUMMARY_COMPLETE VALID={valid} INVALID={invalid} PROVIDER_ERRORS={providerErrors} PROPOSED_EDGES={positiveEdges} NO_CLAIM={noClaim} MODEL_CALLS=3 PROVIDER_CALLS=3 GOLD_READ_COUNT=0"); return 0;
    }

    private static IReadOnlyList<FrozenRequest> LoadFrozenRequests(string preflight)
    {
        using var manifest = Read(Path.Combine(preflight, "manifest.json"));
        var m = manifest.RootElement;
        Require(m.GetProperty("status").GetString() == "READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION", "V7A_PREFLIGHT_STATUS");
        Require(m.GetProperty("requestCount").GetInt32() == 3 && m.GetProperty("documentCount").GetInt32() == 3 && m.GetProperty("occurrenceCount").GetInt32() == ExpectedOccurrences && m.GetProperty("candidatePairCount").GetInt32() == 291, "V7A_PREFLIGHT_COUNTS");
        Require(m.GetProperty("providerCalls").GetInt32() == 0 && m.GetProperty("goldReadCount").GetInt32() == 0 && !m.GetProperty("autoCollapse").GetBoolean() && !m.GetProperty("v4hEvaluationIncluded").GetBoolean() && !m.GetProperty("v5PredictionIncluded").GetBoolean() && !m.GetProperty("v6dPredictionIncluded").GetBoolean(), "V7A_PREFLIGHT_FIREWALL");
        var hashSet = string.Join("\n", m.GetProperty("requestHashes").EnumerateArray().Select(x => x.GetString()));
        Require(Sha256Text(hashSet) == RequestSetSha256, "V7A_REQUEST_SET_HASH");
        using var requests = Read(Path.Combine(preflight, "requests.json"));
        var items = requests.RootElement.GetProperty("requests").EnumerateArray().Select(record =>
        {
            var request = record.GetProperty("request").Clone(); var hash = record.GetProperty("requestHash").GetString()!;
            Require(hash == Sha256Text(JsonSerializer.Serialize(request, JsonOptions)), "V7A_REQUEST_HASH");
            Require(request.GetProperty("identityDefault").GetString() == "EACH_OCCURRENCE_SEPARATE_UNLESS_PROMOTION_LATER_ACCEPTS_PROPOSAL" && !request.GetProperty("outputContract").GetProperty("autoCollapse").GetBoolean() && !request.GetProperty("goldDerivedInput").GetBoolean() && !request.GetProperty("v4hEvaluationIncluded").GetBoolean() && !request.GetProperty("v5PredictionIncluded").GetBoolean() && !request.GetProperty("v6dPredictionIncluded").GetBoolean(), "V7A_REQUEST_CONTRACT");
            var candidates = request.GetProperty("candidatePairs").EnumerateArray().Select(x => x.GetProperty("pairId").GetString()!).ToArray();
            Require(candidates.Distinct(StringComparer.Ordinal).Count() == candidates.Length, "V7A_CANDIDATE_ID_DUPLICATE");
            return new FrozenRequest(request.GetProperty("documentId").GetString()!, hash, request, request.GetProperty("occurrences").GetArrayLength(), candidates.Length, candidates);
        }).OrderBy(x => x.DocumentId, StringComparer.Ordinal).ToArray();
        Require(items.Select(x => x.DocumentId).SequenceEqual(ExpectedDocuments, StringComparer.Ordinal) && items.Sum(x => x.OccurrenceCount) == ExpectedOccurrences && items.Sum(x => x.CandidatePairCount) == 291, "V7A_REQUEST_SET");
        return items;
    }

    private static object ResponseSchemaV7A() => new
    {
        type = "object", additionalProperties = false, required = new[] { "positiveProposals" }, properties = new
        {
            positiveProposals = new { type = "array", items = new { type = "object", additionalProperties = false, required = new[] { "pairId", "left", "right", "relation" }, properties = new { pairId = new { type = "string", pattern = "^P[0-9]{4}$" }, left = new { type = "string", pattern = "^U[0-9]{3}$" }, right = new { type = "string", pattern = "^U[0-9]{3}$" }, relation = new { type = "string", @enum = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF" } }, from = new { type = "string", pattern = "^U[0-9]{3}$" }, to = new { type = "string", pattern = "^U[0-9]{3}$" } } } },
        },
    };

    private const string SystemPromptV7A = """
You are the A99 V7A sparse-positive semantic identity proposal reasoner. The default is KEEP
SPLIT: every occurrence remains separate unless you find strong source-backed evidence for one
positive relation among the supplied candidate pairs.

Return only positive MODEL_PROPOSED_ONLY proposals. Allowed relations are SAME_SEMANTIC_REPEAT
and CONTINUATION_OF. Omit every pair for which evidence is insufficient; omission means NO_CLAIM,
not DISTINCT. For CONTINUATION_OF, return exact from/to direction using the pair's U handles.
Use only supplied pair and occurrence handles. Do not merge nodes, build connected components,
apply transitivity, or return Gold, historical predictions, parent, ROOT, level, or pair labels
for omitted pairs. autoCollapse is false. Return exactly the requested JSON.
""";

    private static V7AValidation ValidateResponse(JsonElement request, JsonElement response)
    {
        var errors = new List<string>();
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("positiveProposals", out var proposals) || proposals.ValueKind != JsonValueKind.Array) return V7AValidation.Invalid("V7A_REQUIRED_FIELDS");
        var candidateMap = request.GetProperty("candidatePairs").EnumerateArray().ToDictionary(x => x.GetProperty("pairId").GetString()!, x => (Left: x.GetProperty("left").GetString()!, Right: x.GetProperty("right").GetString()!), StringComparer.Ordinal);
        var occurrenceRefs = request.GetProperty("occurrences").EnumerateArray().Select(x => x.GetProperty("ref").GetString()!).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal); var touched = new HashSet<string>(StringComparer.Ordinal); var unknown = 0; var self = 0; var directionConflict = 0; var continuation = 0;
        foreach (var item in proposals.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("pairId", out var pairId) || !item.TryGetProperty("left", out var left) || !item.TryGetProperty("right", out var right) || !item.TryGetProperty("relation", out var relation) || pairId.ValueKind != JsonValueKind.String || left.ValueKind != JsonValueKind.String || right.ValueKind != JsonValueKind.String || relation.ValueKind != JsonValueKind.String) { errors.Add("V7A_PROPOSAL_SHAPE"); continue; }
            var id = pairId.GetString()!; var l = left.GetString()!; var r = right.GetString()!; var rel = relation.GetString()!;
            if (!candidateMap.TryGetValue(id, out var candidate)) { unknown++; continue; }
            if (!seen.Add(id)) { errors.Add("V7A_DUPLICATE_PAIR"); continue; }
            if (!occurrenceRefs.Contains(l) || !occurrenceRefs.Contains(r)) { unknown++; continue; }
            if (l == r) { self++; continue; }
            if (!((l == candidate.Left && r == candidate.Right) || (l == candidate.Right && r == candidate.Left))) { errors.Add("V7A_PAIR_ENDPOINT_MISMATCH"); continue; }
            touched.Add(l); touched.Add(r);
            if (rel is not ("SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF")) { errors.Add("V7A_INVALID_RELATION"); continue; }
            if (rel == "CONTINUATION_OF")
            {
                continuation++;
                if (!item.TryGetProperty("from", out var from) || !item.TryGetProperty("to", out var to) || from.ValueKind != JsonValueKind.String || to.ValueKind != JsonValueKind.String) { directionConflict++; continue; }
                var f = from.GetString()!; var t = to.GetString()!;
                if (f == t || !((f == l && t == r) || (f == r && t == l))) directionConflict++;
            }
            else if (item.TryGetProperty("from", out _) || item.TryGetProperty("to", out _)) directionConflict++;
        }
        if (unknown > 0) errors.Add("V7A_UNKNOWN_HANDLE_OR_PAIR");
        if (self > 0) errors.Add("V7A_SELF_EDGE");
        if (directionConflict > 0) errors.Add("V7A_DIRECTION_CONFLICT");
        return new V7AValidation(errors.Count == 0, proposals.GetArrayLength(), continuation, touched.Count, unknown, seen.Count != proposals.GetArrayLength() ? proposals.GetArrayLength() - seen.Count : 0, self, directionConflict, errors.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static int CountComponents(IReadOnlyList<(string Left, string Right)> edges)
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string x) { if (!parent.ContainsKey(x)) parent[x] = x; if (parent[x] != x) parent[x] = Find(parent[x]); return parent[x]; }
        void Union(string a, string b) { var x = Find(a); var y = Find(b); if (x != y) parent[y] = x; }
        foreach (var edge in edges) Union(edge.Left, edge.Right);
        return parent.Values.Select(Find).Distinct(StringComparer.Ordinal).Count();
    }

    private static int LargestComponent(IReadOnlyList<(string Left, string Right)> edges)
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string x) { if (!parent.ContainsKey(x)) parent[x] = x; if (parent[x] != x) parent[x] = Find(parent[x]); return parent[x]; }
        void Union(string a, string b) { var x = Find(a); var y = Find(b); if (x != y) parent[y] = x; }
        foreach (var edge in edges) Union(edge.Left, edge.Right);
        return parent.Values.Select(Find).GroupBy(x => x, StringComparer.Ordinal).Select(x => x.Count()).DefaultIfEmpty(0).Max();
    }

    private static object BuildRequest(string documentId, JsonElement[] packets)
    {
        var ordered = packets.OrderBy(x => x.GetProperty("documentOrder").GetInt32()).ThenBy(x => x.GetProperty("occurrenceId").GetString(), StringComparer.Ordinal).ToArray();
        var handles = ordered.Select((x, i) => new { packet = x, handle = $"U{i + 1:000}" }).ToArray();
        var occurrences = handles.Select(x => new
        {
            @ref = x.handle,
            sourceOccurrenceId = x.packet.GetProperty("occurrenceId").GetString(),
            text = x.packet.GetProperty("text").GetString(),
            documentOrder = x.packet.GetProperty("documentOrder").GetInt32(),
            sourceContainerIdentity = x.packet.GetProperty("sourceContainerIdentity").GetString(),
            sourceUnitKind = x.packet.GetProperty("sourceUnitKind").GetString(),
            previousSourceOccurrences = x.packet.GetProperty("previousSourceOccurrences"),
            nextSourceOccurrences = x.packet.GetProperty("nextSourceOccurrences"),
            parserEvidence = new { packetClasses = x.packet.GetProperty("packetClasses"), sourceContainerIdentity = x.packet.GetProperty("sourceContainerIdentity"), sourceUnitKind = x.packet.GetProperty("sourceUnitKind") },
        }).ToArray();
        var pairs = new List<object>();
        for (var i = 0; i < handles.Length; i++)
        for (var j = i + 1; j < handles.Length; j++)
        {
            var left = handles[i].packet; var right = handles[j].packet;
            var reasons = new List<string>();
            var distance = Math.Abs(left.GetProperty("documentOrder").GetInt32() - right.GetProperty("documentOrder").GetInt32());
            var sameScope = left.GetProperty("sourceContainerIdentity").GetString() == right.GetProperty("sourceContainerIdentity").GetString();
            if (sameScope && distance <= 4) reasons.Add("ADJACENT_SAME_SCOPE");
            if (sameScope && distance <= 12 && left.GetProperty("sourceUnitKind").GetString() == right.GetProperty("sourceUnitKind").GetString()) reasons.Add("NEARBY_SAME_SOURCE_UNIT_KIND");
            if (Normalize(left.GetProperty("text").GetString()) == Normalize(right.GetProperty("text").GetString()) && Normalize(left.GetProperty("text").GetString()).Length >= 3) reasons.Add("NORMALIZED_TEXT_EQUAL");
            if (left.GetProperty("text").GetString()!.Contains("cont'd", StringComparison.OrdinalIgnoreCase) || right.GetProperty("text").GetString()!.Contains("cont'd", StringComparison.OrdinalIgnoreCase)) reasons.Add("EXPLICIT_CONTINUATION_TEXT_SIGNAL");
            if (reasons.Count > 0) pairs.Add(new { pairId = $"P{pairs.Count + 1:0000}", left = handles[i].handle, right = handles[j].handle, reasons = reasons.ToArray() });
        }
        return new
        {
            schemaVersion = "a99-v7a-sparse-positive-identity-proposal-v1", documentId, identityDefault = "EACH_OCCURRENCE_SEPARATE_UNLESS_PROMOTION_LATER_ACCEPTS_PROPOSAL",
            occurrences, candidatePairs = pairs.ToArray(), outputContract = new { positiveProposals = "MODEL_PROPOSED_ONLY", unresolvedPairIds = "optional", relations = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF" }, continuationDirection = "required_for_CONTINUATION_OF", autoCollapse = false, parentOrHierarchyRequested = false },
            goldDerivedInput = false, v4hEvaluationIncluded = false, v5PredictionIncluded = false, v6dPredictionIncluded = false,
        };
    }

    private static void ValidateSource(JsonElement manifest, JsonElement packets)
    {
        Require(manifest.GetProperty("status").GetString() == "SOURCE_ONLY_OWNER_EVIDENCE_FROZEN", "V7A_SOURCE_STATUS");
        Require(manifest.GetProperty("goldReadCount").GetInt32() == 0 && manifest.GetProperty("providerCalls").GetInt32() == 0, "V7A_SOURCE_FIREWALL");
        Require(packets.GetArrayLength() == ExpectedOccurrences, "V7A_OCCURRENCE_COUNT");
    }

    private static int CountCandidatePairs(object request)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request, JsonOptions));
        return doc.RootElement.GetProperty("candidatePairs").GetArrayLength();
    }

    private static string Normalize(string? value) => new string((value ?? string.Empty).Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private sealed record FrozenRequest(string DocumentId, string RequestHash, JsonElement Request, int OccurrenceCount, int CandidatePairCount, IReadOnlyList<string> CandidateIds);
    private sealed record ComponentDiagnostic(string DocumentId, int CandidatePairs, int PositiveEdges, int NoClaimPairs, int TouchedOccurrences, int ConnectedComponentCount, int LargestProposedComponent);
    private sealed record V7AValidation(bool Accepted, int ProposedPositiveEdges, int ProposedContinuationEdges, int TouchedOccurrences, int UnknownHandleCount, int DuplicateEdgeCount, int SelfEdgeCount, int DirectionConflictCount, IReadOnlyList<string> Errors)
    {
        public static V7AValidation Invalid(string error) => new(false, 0, 0, 0, 0, 0, 0, 0, [error]);
    }
}
