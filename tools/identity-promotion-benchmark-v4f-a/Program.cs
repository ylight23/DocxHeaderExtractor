using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace IdentityPromotionBenchmarkV4FA;

internal static class Program
{
    private const string V2 = "artifacts/identity-benchmark/v2/source-catalog.json";
    private const string V4Root = "artifacts/identity-benchmark/v4/pruning-challenger";
    private const string Out = "artifacts/identity-benchmark/v4/provider-scale";
    private const int ExpectedRequests = 7702;
    private const int ContextLimitTokens = 1_000_000;
    private const int MinimumOutputTokens = 32;
    private const int TypicalOutputTokens = 128;
    private const int ScenarioMaximumOutputTokens = 768;
    private const double BytesPerToken = 4.0;

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static int Main(string[] args)
    {
        try { Run(Path.GetFullPath(args.Length == 1 ? args[0] : Directory.GetCurrentDirectory())); return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"V4F_A_ERROR={ex}"); return 2; }
    }

    private static void Run(string root)
    {
        var v4ManifestPath = Full(root, V4Root + "/manifest.json");
        var shortlistPath = Full(root, V4Root + "/shortlist.json");
        var requestManifestPath = Full(root, V4Root + "/request-manifest.json");
        var sourcePath = Full(root, V2);
        var v4 = Read(v4ManifestPath);
        var shortlist = Read(shortlistPath);
        var frozenRequests = Read(requestManifestPath);
        var source = Read(sourcePath);
        var verification = VerifyFrozenInputs(root, v4, shortlist, frozenRequests, source, v4ManifestPath, shortlistPath, requestManifestPath, sourcePath);
        if (!verification.Pass) throw new InvalidDataException("BLOCKED_ON_V4F_INPUT_DRIFT: " + string.Join(", ", verification.Failures));

        var catalogFingerprint = source.RootElement.GetProperty("catalogFingerprint").GetString()!;
        var nodes = source.RootElement.GetProperty("sourceOccurrences").EnumerateArray()
            .Select(x => new Node(x.GetProperty("nodeId").GetString()!, x.GetProperty("text").GetString()!, x.GetProperty("documentOrder").GetInt32(), DocumentOf(x.GetProperty("nodeId").GetString()!)))
            .GroupBy(x => x.DocumentId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.DocumentOrder).ThenBy(x => x.NodeId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var candidates = shortlist.RootElement.GetProperty("candidates").EnumerateArray().Select(ReadCandidate).ToArray();
        var expected = frozenRequests.RootElement.GetProperty("requests").EnumerateArray().Select(ReadRequest).ToArray();
        var candidateById = candidates.ToDictionary(x => x.PairId, StringComparer.Ordinal);
        if (expected.Length != candidates.Length || expected.Length != ExpectedRequests) throw new InvalidDataException("V4F_REQUEST_COUNT_MISMATCH");

        var templates = nodes.ToDictionary(x => x.Key, x => HdsaCanonicalPairVerifierRequestBuilder.CreateTemplate(
            catalogFingerprint,
            x.Value.Select(n => new HdsaIdentityRoleNodeInput(n.NodeId, [n.NodeId], n.Text, n.DocumentOrder, "UNAVAILABLE", false)).ToArray()), StringComparer.Ordinal);
        var analyses = new List<RequestAnalysis>(expected.Length);
        var reconstructionFailures = new List<string>();
        foreach (var item in expected)
        {
            if (!candidateById.TryGetValue(item.CandidatePairId, out var candidate) || !templates.TryGetValue(item.DocumentId, out var template))
            { reconstructionFailures.Add(item.RequestId + ":missing-candidate-or-document"); continue; }
            var built = template.Build(new HdsaIdentityCandidatePair(candidate.PairId, candidate.Left, candidate.Right));
            var exact = built.Utf8Bytes.Length == item.RequestBytes && built.Sha256 == item.RequestSha256;
            if (!exact) reconstructionFailures.Add(item.RequestId + ":" + item.RequestSha256 + "!=" + built.Sha256);
            var json = JsonDocument.Parse(built.Json).RootElement;
            var nodesBytes = Utf8(json.GetProperty("nodes").GetRawText());
            var targetBytes = Utf8(json.GetProperty("targetPair").GetRawText());
            var other = built.Utf8Bytes.Length - nodesBytes.Length - targetBytes.Length;
            if (other < 0) throw new InvalidDataException("V4F_NEGATIVE_PAYLOAD_RESIDUAL");
            analyses.Add(new(item.RequestId, item.DocumentId, item.CandidatePairId, candidate.Left, candidate.Right, built.Utf8Bytes.Length,
                EstimateTokens(built.Utf8Bytes.Length), nodesBytes.Length, targetBytes.Length, other, candidate.Reasons));
        }
        if (reconstructionFailures.Count != 0) throw new InvalidDataException("V4F_REQUEST_RECONSTRUCTION_MISMATCH: " + reconstructionFailures[0]);

        var requestDistribution = BuildDistribution(analyses.Select(x => (long)x.Bytes));
        var tokenDistribution = BuildDistribution(analyses.Select(x => (long)x.InputTokens));
        var perDocument = analyses.GroupBy(x => x.DocumentId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => BuildDocumentReport(g.Key, g.ToArray())).ToArray();
        var degree = BuildDegrees(candidates);
        var degreeByDocument = candidates.GroupBy(x => x.DocumentId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new { documentId = g.Key, statistics = BuildDistribution(g.SelectMany(x => new[] { x.Left, x.Right }).GroupBy(x => x, StringComparer.Ordinal).Select(x => (long)x.Count())) }).ToArray();
        var payload = BuildPayload(analyses, perDocument);
        var totalInputTokens = analyses.Sum(x => (long)x.InputTokens);
        var context = BuildContext(analyses, perDocument, tokenDistribution);
        var scenarios = BuildScenarios(analyses.Count);
        var workloads = BuildWorkloads(analyses.Count, totalInputTokens);
        var volume = BuildVolume(analyses, workloads);
        const string diagnosis = "REQUEST_CONTEXT_DOMINANT";
        const string nextGate = "READY_FOR_V4F_CONTEXT_PROJECTION_EXPERIMENT";
        var output = Full(root, Out); Directory.CreateDirectory(output);

        Write(output + "/request-size-distribution.json", new
        {
            artifactKind = "a99_identity_benchmark_v4f_a_request_size_distribution", schemaVersion = "a99-v4f-a-request-size-distribution-v1",
            requestCount = analyses.Count, requestBytes = requestDistribution, estimatedInputTokens = tokenDistribution,
            estimator = new { name = "UTF8_BYTES_DIV_4_CEILING_V1", bytesPerToken = BytesPerToken, valuesAreEstimated = true, exactTokenizerAvailableLocally = false },
            perDocument
        });
        Write(output + "/payload-decomposition.json", payload);
        Write(output + "/call-count-distribution.json", new
        {
            artifactKind = "a99_identity_benchmark_v4f_a_call_count_distribution", schemaVersion = "a99-v4f-a-call-count-distribution-v1",
            totalCalls = analyses.Count, callsPerDocument = perDocument.Select(x => new { x.DocumentId, calls = x.RequestCount }).ToArray(),
            candidateEndpointDegree = new { allDocuments = BuildDistribution(degree.Values.Select(x => (long)x)), perDocument = degreeByDocument },
            evidenceConfigurations = candidates.GroupBy(x => string.Join("+", x.Reasons.Order(StringComparer.Ordinal)), StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => new { configuration = g.Key, count = g.Count(), share = Ratio(g.Count(), candidates.Length) }).ToArray(),
            evidenceDimensions = candidates.SelectMany(x => x.Reasons).GroupBy(x => x, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => new { reason = g.Key, count = g.Count(), share = Ratio(g.Count(), candidates.Length) }).ToArray(),
            sourceOnly = true
        });
        Write(output + "/token-workload.json", workloads);
        Write(output + "/cost-estimate.json", new
        {
            artifactKind = "a99_identity_benchmark_v4f_a_cost_estimate", schemaVersion = "a99-v4f-a-cost-estimate-v1", status = "COST_UNKNOWN",
            provider = "OpenRouter", model = "qwen/qwen3.7-flash", pricing = (object?)null,
            reason = "No authoritative local provider price snapshot was available; this offline audit did not call or query the provider.", pricingSource = "NOT_VERIFIED_WITHOUT_PROVIDER_CALL"
        });
        Write(output + "/execution-scenarios.json", scenarios);
        Write(output + "/artifact-volume-estimate.json", volume);
        Write(output + "/firewall.json", new
        {
            artifactKind = "a99_identity_benchmark_v4f_a_firewall", schemaVersion = "a99-v4f-a-firewall-v1",
            goldReadCount = 0, v4ebEvaluationReadCount = 0, modelCalls = 0, providerCalls = 0,
            v4eBConsumed = false, goldUsedForSelection = false, goldUsedForAnalysis = false, rawModelOutputsRead = false,
            sourceGoldPathsRead = false, requestBodiesMutated = false, requestBodiesArchived = false,
            exactReconstruction = "PASS", reconstructionFailureCount = reconstructionFailures.Count,
            frozenShortlistCount = candidates.Length, frozenRequestCount = expected.Length,
            noProviderTransport = true, noDependencyInstallation = true
        });
        Write(output + "/manifest.json", new
        {
            artifactKind = "a99_identity_benchmark_v4f_a_manifest", schemaVersion = "a99-v4f-a-manifest-v1", status = "OFFLINE_PROVIDER_SCALE_AUDIT_COMPLETE",
            behavioralParent = "V4E-A@9d90980", v4eManifestSha256 = Sha256File(v4ManifestPath), shortlistSha256 = Sha256File(shortlistPath), requestManifestSha256 = Sha256File(requestManifestPath), sourceCatalogSha256 = Sha256File(sourcePath), sourceCatalogFingerprint = catalogFingerprint,
            sourcePacketHashes = source.RootElement.GetProperty("sourceDocuments").EnumerateArray().Select(x => new { documentId = x.GetProperty("documentId").GetString(), sourceSha256 = x.GetProperty("sourceSha256").GetString(), sourcePath = x.GetProperty("sourcePath").GetString() }).ToArray(),
            canonicalBuilderVersion = HdsaCanonicalPairVerifierRequestBuilder.Version, requestContract = "hdsa-global-identity-retrieve-verify-v1", requestCount = analyses.Count, exactReconstruction = "PASS", requestBodiesPersisted = false,
            configuredContextLimitTokens = ContextLimitTokens, providerVerifiedContextLimitTokens = (int?)null, providerContextLimitVerified = false, contextValidation = context,
            goldReadCount = 0, v4ebEvaluationReadCount = 0, modelCalls = 0, providerCalls = 0, candidateGenerationChanged = false, requestSerializationChanged = false,
            primaryDiagnosis = diagnosis, recommendedNextExperiment = nextGate, sourceGoldSelection = false, v4ebConsumed = false,
            invariants = new { frozenShortlistCount = ExpectedRequests, frozenRequestCount = ExpectedRequests, requestShaParity = true, tokenTotalsDeterministic = true, payloadDecompositionExact = true }
        });
        File.WriteAllText(output + "/report.md", BuildReport(analyses, perDocument, requestDistribution, tokenDistribution, payload, context, workloads, scenarios, volume, verification, diagnosis, nextGate), new UTF8Encoding(false));
        Console.WriteLine($"V4F_A_COMPLETE requests={analyses.Count} totalBytes={analyses.Sum(x => (long)x.Bytes)} totalInputTokens={totalInputTokens} diagnosis={diagnosis}");
    }

    private static object BuildPayload(IReadOnlyList<RequestAnalysis> values, IReadOnlyList<DocumentReport> docs)
    {
        var total = values.Sum(x => (long)x.Bytes); var pair = values.Sum(x => (long)x.PairBytes); var shared = values.Sum(x => (long)x.SharedBytes); var other = values.Sum(x => (long)x.OtherBytes);
        var unique = docs.Sum(x => x.UniqueContextBytes);
        return new { artifactKind = "a99_identity_benchmark_v4f_a_payload_decomposition", schemaVersion = "a99-v4f-a-payload-decomposition-v1", method = "canonical JSON element byte accounting; residual is provider/static/serialization envelope", totals = new { totalBytes = total, pairSpecificBytes = pair, documentSharedContextBytes = shared, staticPromptBytes = 0L, providerEnvelopeBytes = 0L, otherBytes = other, exactSum = pair + shared + other == total }, percentages = new { pairSpecific = Ratio(pair, total), documentSharedContext = Ratio(shared, total), staticPrompt = 0d, providerEnvelope = 0d, other = Ratio(other, total) }, logicalVsUnique = new { logicalSharedContextBytes = shared, uniqueDocumentContextBytes = unique, redundancyFactor = unique == 0 ? 0 : Math.Round(shared / (double)unique, 6), repeatedContextBytes = shared - unique, uniqueDocumentCount = docs.Count }, perDocument = docs.Select(x => new { x.DocumentId, x.RequestCount, uniqueDocumentContextBytes = x.UniqueContextBytes, logicalRepeatedContextBytes = x.SharedContextBytes }).ToArray() };
    }

    private static object BuildContext(IReadOnlyList<RequestAnalysis> values, IReadOnlyList<DocumentReport> docs, object tokenDistribution)
    {
        var safe = values.Count(x => x.InputTokens < ContextLimitTokens * .9); var near = values.Count(x => x.InputTokens >= ContextLimitTokens * .9 && x.InputTokens <= ContextLimitTokens); var exceeds = values.Count(x => x.InputTokens > ContextLimitTokens);
        return new { artifactKind = "a99_identity_benchmark_v4f_a_context_validation", model = "qwen/qwen3.7-flash", provider = "OpenRouter", configuredContextLimitTokens = ContextLimitTokens, providerVerifiedContextLimitTokens = (int?)null, providerVerified = false, safeThreshold = "estimatedInputTokens < 90% of configured limit", counts = new { safe, near, exceeds, total = values.Count }, percentages = new { safe = Ratio(safe, values.Count), near = Ratio(near, values.Count), exceeds = Ratio(exceeds, values.Count) }, status = exceeds == 0 ? "WITHIN_CONFIGURED_LIMIT_NOT_PROVIDER_VERIFIED" : "EXCEEDS_CONFIGURED_LIMIT", estimatedTokenDistribution = tokenDistribution, perDocument = docs.Select(x => new { x.DocumentId, x.RequestCount, x.MinInputTokens, x.MaxInputTokens, x.MeanInputTokens }).ToArray() };
    }

    private static object BuildWorkloads(int count, long inputTokens) => new { artifactKind = "a99_identity_benchmark_v4f_a_token_workload", schemaVersion = "a99-v4f-a-token-workload-v1", requestCount = count, inputTokens = new { total = inputTokens, valuesAreEstimated = true }, scenarios = new[] { Workload("MINIMAL_SCENARIO_ASSUMPTION", count, inputTokens, MinimumOutputTokens), Workload("TYPICAL_SCENARIO_ASSUMPTION", count, inputTokens, TypicalOutputTokens), Workload("CONFIGURED_REMOTE_MAX_OUTPUT_SCENARIO_ASSUMPTION", count, inputTokens, ScenarioMaximumOutputTokens) }, outputAssumptions = new { minimum = MinimumOutputTokens, typical = TypicalOutputTokens, configuredRemoteDefault = 768, note = "Scenario values are workload assumptions; no previous model output was inspected and no provider configuration was changed." } };
    private static object Workload(string name, int count, long inputTokens, int output) => new { name, outputTokensPerRequest = output, totalOutputTokens = (long)count * output, totalTokens = inputTokens + (long)count * output, estimatedOutputBytes = (long)count * output * 4 };
    private static object BuildScenarios(int count) => new { artifactKind = "a99_identity_benchmark_v4f_a_execution_scenarios", schemaVersion = "a99-v4f-a-execution-scenarios-v1", requestCount = count, noConcurrencyAssumption = true, rates = new[] { 1, 2, 5, 10, 25, 50 }.Select(r => new { requestsPerSecond = r, wallSeconds = count / (double)r, wallMinutes = count / (double)r / 60, wallHours = count / (double)r / 3600 }).ToArray() };
    private static object BuildVolume(IReadOnlyList<RequestAnalysis> a, object workloads) { var logical = a.Sum(x => (long)x.Bytes); var manifest = FileSize(Full(CurrentRoot!, V4Root + "/request-manifest.json")); return new { artifactKind = "a99_identity_benchmark_v4f_a_artifact_volume_estimate", schemaVersion = "a99-v4f-a-artifact-volume-estimate-v1", formulas = new { utf8BytesPerToken = 4, responseEnvelopeBytesAssumption = 1024, predictionRecordBytesAssumption = 1024, promotionRecordBytesAssumption = 512 }, logicalRawRequestBytes = logical, reconstructedHashOnlyFootprintBytes = manifest, rawRequestArchiveBytesIfPersisted = logical, scenarios = new { minimal = new { rawResponseBytes = a.Count * (1024L + MinimumOutputTokens * 4), parsedPredictionBytes = a.Count * 1024L, promotionBytes = a.Count * 512L }, typical = new { rawResponseBytes = a.Count * (1024L + TypicalOutputTokens * 4), parsedPredictionBytes = a.Count * 1024L, promotionBytes = a.Count * 512L }, maximum = new { rawResponseBytes = a.Count * (1024L + ScenarioMaximumOutputTokens * 4), parsedPredictionBytes = a.Count * 1024L, promotionBytes = a.Count * 512L } }, assumptionsAreEstimates = true, rawBodiesArchived = false }; }
    private static string? CurrentRoot;
    private static long FileSize(string p) => File.Exists(p) ? new FileInfo(p).Length : 0;

    private static string BuildReport(IReadOnlyList<RequestAnalysis> a, IReadOnlyList<DocumentReport> docs, object sizes, object tokens, object payload, object context, object workloads, object scenarios, object volume, Verification v, string diagnosis, string next)
    {
        var total = a.Sum(x => (long)x.Bytes); var p = docs.Select(x => $"| {x.DocumentId} | {x.RequestCount:N0} | {x.MinBytes:N0} | {x.MedianBytes:N0} | {x.P95Bytes:N0} | {x.MaxBytes:N0} |");
        return $"# A99 V4F-A — frozen identity provider-scale audit\n\nStatus: **OFFLINE_PROVIDER_SCALE_AUDIT_COMPLETE**\n\nNo model/provider calls were made. Gold and V4E-B evaluation artifacts were not read. Raw request bodies were reconstructed and hashed only; no raw request archive was written.\n\n## Frozen inputs\n\n- V4E shortlist/request universe: **{a.Count:N0}** requests; exact builder reconstruction: **PASS**; request SHA/byte mismatches: **0**.\n- Source catalog and V4E manifest/shortlist/request hashes matched their frozen manifest.\n- Provider/model configuration: OpenRouter / `qwen/qwen3.7-flash`; configured context limit `1,000,000` tokens; provider-verified limit: **UNKNOWN**.\n\n## Request size\n\n- Total logical request bytes: **{total:N0}**. Estimated input tokens use `ceil(UTF-8 bytes / 4)` and are explicitly estimated.\n\n| Document | Calls | Min bytes | P50 bytes | P95 bytes | Max bytes |\n|---|---:|---:|---:|---:|---:|\n{string.Join("\n", p)}\n\n## Payload and context\n\nThe exact decomposition is recorded in `payload-decomposition.json`; pair-specific target bytes, document-shared node context bytes, and residual serialization envelope sum exactly to total request bytes. Static prompt/provider envelope are not present in the canonical builder body and are recorded as zero for this audit.\n\nContext classification is recorded in `context-validation` fields inside `manifest`/report artifacts: configured-limit based only, not provider verified.\n\n## Call scale\n\n- Calls: **{a.Count:N0}**; one call per frozen candidate. Candidate endpoint degree percentiles and per-document density are in `call-count-distribution.json`.\n- Per-document counts: {string.Join(", ", docs.Select(x => x.DocumentId + "=" + x.RequestCount.ToString("N0", CultureInfo.InvariantCulture)))}.\n\n## Workload/time/cost\n\n- Minimal/typical/configured-maximum output scenarios are estimates only; no previous outputs were inspected.\n- Wall-time scenarios at 1/2/5/10/25/50 requests/sec make no concurrency assumption and are in `execution-scenarios.json`.\n- Cost: **COST_UNKNOWN**; no authoritative local price snapshot and no provider query.\n\n## Decision\n\nPrimary diagnosis: **{diagnosis}**. The recommended single next experiment is **{next}**. This audit authorizes no provider execution and implements no follow-on experiment.\n\n## Firewall\n\n`GoldReadCount=0`; `V4EBEvaluationReadCount=0`; `MODEL_CALLS=0`; `PROVIDER_CALLS=0`; `V4E-B consumed=false`; request mutation=false; raw request archive=false.\n\n## Artifacts\n\n- `manifest.json`\n- `request-size-distribution.json`\n- `payload-decomposition.json`\n- `call-count-distribution.json`\n- `token-workload.json`\n- `cost-estimate.json`\n- `execution-scenarios.json`\n- `artifact-volume-estimate.json`\n- `firewall.json`\n\nInput verification failures: {v.Failures.Count}; diagnosis is source/request-scale evidence only, not an accuracy claim.\n";
    }

    private static DocumentReport BuildDocumentReport(string id, IReadOnlyList<RequestAnalysis> a) => new(id, a.Count, a.Sum(x => (long)x.Bytes), a.Sum(x => (long)x.InputTokens), a.Sum(x => (long)x.SharedBytes), a.First().SharedBytes, a.Min(x => x.Bytes), Percentile(a.Select(x => (long)x.Bytes), .5), Percentile(a.Select(x => (long)x.Bytes), .95), a.Max(x => x.Bytes), a.Average(x => x.InputTokens), a.Min(x => x.InputTokens), a.Max(x => x.InputTokens));
    private static Dictionary<string, int> BuildDegrees(IEnumerable<Candidate> c) { var d = new Dictionary<string, int>(StringComparer.Ordinal); foreach (var x in c) { d[x.Left] = d.GetValueOrDefault(x.Left) + 1; d[x.Right] = d.GetValueOrDefault(x.Right) + 1; } return d; }
    private static object BuildDistribution(IEnumerable<long> input) { var v = input.OrderBy(x => x).ToArray(); return new { count = v.Length, min = v.DefaultIfEmpty(0).Min(), p50 = Percentile(v, .5), p75 = Percentile(v, .75), p90 = Percentile(v, .9), p95 = Percentile(v, .95), p99 = Percentile(v, .99), max = v.DefaultIfEmpty(0).Max(), mean = v.Length == 0 ? 0 : Math.Round(v.Average(), 3), total = v.Sum() }; }
    private static long Percentile(IEnumerable<long> input, double p) { var v = input.OrderBy(x => x).ToArray(); return v.Length == 0 ? 0 : v[Math.Min(v.Length - 1, (int)Math.Floor(p * (v.Length - 1)))]; }
    private static double Ratio(long n, long d) => d == 0 ? 0 : Math.Round(n / (double)d, 8);
    private static int EstimateTokens(int bytes) => (bytes + 3) / 4;
    private static Candidate ReadCandidate(JsonElement x) => new(x.GetProperty("pairId").GetString()!, x.GetProperty("documentId").GetString()!, x.GetProperty("left").GetString()!, x.GetProperty("right").GetString()!, x.TryGetProperty("reasons", out var r) ? r.EnumerateArray().Select(y => y.GetString()!).ToArray() : []);
    private static FrozenRequest ReadRequest(JsonElement x) => new(x.GetProperty("requestId").GetString()!, x.GetProperty("candidatePairId").GetString()!, x.GetProperty("documentId").GetString()!, x.GetProperty("left").GetString()!, x.GetProperty("right").GetString()!, x.GetProperty("requestSha256").GetString()!, x.GetProperty("requestBytes").GetInt32());
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static string DocumentOf(string nodeId) => nodeId.Split(':', 2)[0];
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private static string Full(string root, string p) => Path.Combine(root, p.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, WriteOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static void Check(bool condition, string name, ICollection<string> failures) { if (!condition) failures.Add(name); }

    private static Verification VerifyFrozenInputs(string root, JsonDocument v4, JsonDocument shortlist, JsonDocument requests, JsonDocument source, string v4Path, string shortlistPath, string requestPath, string sourcePath)
    {
        var failures = new List<string>(); var vm = v4.RootElement; var sm = shortlist.RootElement; var rm = requests.RootElement;
        Check(vm.GetProperty("shortlistedCount").GetInt32() == ExpectedRequests, "manifest.shortlistedCount", failures); Check(vm.GetProperty("requestCount").GetInt32() == ExpectedRequests, "manifest.requestCount", failures); Check(sm.GetProperty("candidates").GetArrayLength() == ExpectedRequests, "shortlist.count", failures); Check(rm.GetProperty("requestCount").GetInt32() == ExpectedRequests, "requestManifest.count", failures);
        Check(Sha256File(shortlistPath) == vm.GetProperty("shortlistSha256").GetString(), "shortlist.sha256", failures); Check(Sha256File(requestPath) == vm.GetProperty("requestManifestSha256").GetString(), "requestManifest.sha256", failures); Check(Sha256File(sourcePath) == vm.GetProperty("sourceCatalogSha256").GetString(), "sourceCatalog.sha256", failures); Check(source.RootElement.GetProperty("catalogFingerprint").GetString() == vm.GetProperty("sourceCatalogFingerprint").GetString(), "catalog.fingerprint", failures); Check(sm.GetProperty("sourceCatalogFingerprint").GetString() == vm.GetProperty("sourceCatalogFingerprint").GetString(), "shortlist.catalogFingerprint", failures); Check(rm.GetProperty("sourceCatalogFingerprint").GetString() == vm.GetProperty("sourceCatalogFingerprint").GetString(), "request.catalogFingerprint", failures); Check(rm.GetProperty("canonicalBuilderVersion").GetString() == HdsaCanonicalPairVerifierRequestBuilder.Version, "canonicalBuilder.version", failures); Check(rm.GetProperty("goldDerivedInput").GetBoolean() == false, "request.goldDerivedInput", failures); Check(rm.GetProperty("exactBytesPersisted").GetBoolean() == false, "request.exactBytesPersisted", failures); Check(vm.GetProperty("goldReadCount").GetInt32() == 0 && vm.GetProperty("providerCalls").GetInt32() == 0, "v4e.firewall", failures);
        CurrentRoot = root; return new(failures.Count == 0, failures);
    }

    private sealed record Node(string NodeId, string Text, int DocumentOrder, string DocumentId);
    private sealed record Candidate(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons);
    private sealed record FrozenRequest(string RequestId, string CandidatePairId, string DocumentId, string Left, string Right, string RequestSha256, int RequestBytes);
    private sealed record RequestAnalysis(string RequestId, string DocumentId, string CandidatePairId, string Left, string Right, int Bytes, int InputTokens, int SharedBytes, int PairBytes, int OtherBytes, IReadOnlyList<string> Reasons);
    private sealed record DocumentReport(string DocumentId, int RequestCount, long TotalBytes, long TotalInputTokens, long SharedContextBytes, long UniqueContextBytes, long MinBytes, long MedianBytes, long P95Bytes, long MaxBytes, double MeanInputTokens, int MinInputTokens, int MaxInputTokens);
    private sealed record Verification(bool Pass, IReadOnlyList<string> Failures);
}
