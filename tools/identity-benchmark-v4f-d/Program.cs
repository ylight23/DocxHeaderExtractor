using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace IdentityBenchmarkV4FD;

internal static class Program
{
    private const string SourceRelative = "artifacts/identity-benchmark/v2/source-catalog.json";
    private const string V4Root = "artifacts/identity-benchmark/v4/pruning-challenger";
    private const string ProjectionRoot = "artifacts/identity-benchmark/v4/context-projection";
    private const string ProjectedRoot = "artifacts/identity-benchmark/v4/projected-requests";
    private const string OutputRelative = "artifacts/identity-benchmark/v4/projected-verifier-experiment";
    private const int ExpectedPopulation = 7702;
    private const int TargetSample = 128;
    private const string SelectionSeed = "a99-v4f-d-source-only-stratified-sha256-v1";
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static int Main(string[] args)
    {
        try { Run(Path.GetFullPath(args.Length == 1 ? args[0] : Directory.GetCurrentDirectory())); return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"V4F_D_ERROR={ex}"); return 2; }
    }

    private static void Run(string root)
    {
        var sourcePath = Full(root, SourceRelative);
        var v4ManifestPath = Full(root, V4Root + "/manifest.json");
        var shortlistPath = Full(root, V4Root + "/shortlist.json");
        var oldRequestPath = Full(root, V4Root + "/request-manifest.json");
        var packetManifestPath = Full(root, ProjectionRoot + "/packet-manifest.json");
        var projectedManifestPath = Full(root, ProjectedRoot + "/manifest.json");
        var projectedRequestPath = Full(root, ProjectedRoot + "/request-manifest.json");
        using var source = Read(sourcePath); using var v4 = Read(v4ManifestPath); using var shortlist = Read(shortlistPath); using var oldRequests = Read(oldRequestPath); using var packets = Read(packetManifestPath); using var projected = Read(projectedManifestPath); using var projectedRequests = Read(projectedRequestPath);
        var failures = VerifyInputs(source, v4, shortlist, oldRequests, packets, projected, projectedRequests, sourcePath, shortlistPath, oldRequestPath, packetManifestPath, projectedManifestPath);
        if (failures.Count != 0) throw new InvalidDataException("BLOCKED_ON_V4F_INPUT_DRIFT: " + string.Join(", ", failures));

        var nodes = source.RootElement.GetProperty("sourceOccurrences").EnumerateArray().Select(x => new Occurrence(x.GetProperty("nodeId").GetString()!, x.GetProperty("text").GetString()!, x.GetProperty("documentOrder").GetInt32())).ToDictionary(x => x.NodeId, StringComparer.Ordinal);
        var candidates = shortlist.RootElement.GetProperty("candidates").EnumerateArray().Select(ReadCandidate).ToArray();
        var allCandidates = candidates.ToDictionary(x => x.PairId, StringComparer.Ordinal);
        var degrees = candidates.SelectMany(x => new[] { x.Left, x.Right }).GroupBy(x => x, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var packetInfo = packets.RootElement.GetProperty("packets").EnumerateArray().ToDictionary(x => x.GetProperty("candidateId").GetString()!, x => new PacketInfo(x.GetProperty("packetClass").GetString()!, x.GetProperty("packetSha256").GetString()!), StringComparer.Ordinal);
        var oldInfo = oldRequests.RootElement.GetProperty("requests").EnumerateArray().ToDictionary(x => x.GetProperty("requestId").GetString()!, x => new ArmInfo("ARM_A", "OLD_FULL_CONTEXT", x.GetProperty("requestBytes").GetInt64(), x.GetProperty("requestSha256").GetString()!, oldRequests.RootElement.GetProperty("canonicalBuilderVersion").GetString()!), StringComparer.Ordinal);
        var newInfo = projectedRequests.RootElement.GetProperty("requests").EnumerateArray().ToDictionary(x => x.GetProperty("requestId").GetString()!, x => new ArmInfo("ARM_B", "PROJECTED_CONTEXT", x.GetProperty("exactByteLength").GetInt64(), x.GetProperty("requestSha256").GetString()!, x.GetProperty("builderVersion").GetString()!), StringComparer.Ordinal);
        var records = candidates.Select(c => ToRecord(c, nodes, degrees, packetInfo[c.PairId], oldInfo[c.PairId], newInfo[c.PairId])).ToArray();
        var sampled = Select(records);
        if (sampled.Length != TargetSample || sampled.Select(x => x.DocumentId).Distinct(StringComparer.Ordinal).Count() != 3 || sampled.Select(x => x.PacketClass).Distinct(StringComparer.Ordinal).Count() != 2) throw new InvalidDataException("V4F_D_SAMPLE_COVERAGE_FAILED");
        var output = Full(root, OutputRelative); Directory.CreateDirectory(output);
        var schemaHash = Hash(Encoding.UTF8.GetBytes(oldRequests.RootElement.GetProperty("requestContract").GetString()! + "|" + string.Join('|', new[] { "CONTINUATION_OF", "SAME_SEMANTIC_REPEAT", "DISTINCT", "UNRESOLVED" }) + "|" + HdsaGlobalIdentityRetrieveVerifyContract.VerificationSchema()));
        var configHash = projectedRequests.RootElement.GetProperty("requests").EnumerateArray().First().GetProperty("configurationHash").GetString()!;
        var model = "qwen/qwen3.7-flash"; var provider = "OpenRouter"; var options = new[] { "CONTINUATION_OF", "SAME_SEMANTIC_REPEAT", "DISTINCT", "UNRESOLVED" };
        Write(output + "/sampling-contract.json", new { artifactKind = "a99_identity_benchmark_v4f_d_sampling_contract", schemaVersion = "a99-v4f-d-sampling-contract-v1", population = ExpectedPopulation, targetSample = TargetSample, selectionSeed = SelectionSeed, selectionInputs = new[] { "documentId", "packetClass", "retrievalReasons", "normalizedTextEquality", "sourceOrderDistanceBucket", "endpointDegreeBucket", "requestSizeBucket" }, forbiddenInputs = new[] { "IR_ID", "GOLD_RELATION", "V4E_B_OUTCOME", "MODEL_OUTPUT", "HUMAN_DIAGNOSTIC" }, goldUsedForSelection = false, knownIrUsedForSelection = false, deterministic = true, sourceOnly = true });
        Write(output + "/population-strata.json", new { artifactKind = "a99_identity_benchmark_v4f_d_population_strata", schemaVersion = "a99-v4f-d-population-strata-v1", populationCount = records.Length, grouping = "documentId|packetClass|evidenceConfiguration|sourceOrderDistanceBucket", strata = Strata(records, sampled) });
        Write(output + "/sample.json", new { artifactKind = "a99_identity_benchmark_v4f_d_sample", schemaVersion = "a99-v4f-d-sample-v1", sampleCount = sampled.Length, selectionSeed = SelectionSeed, canonicalOrdering = "stableSha256(pairId|seed) ASC", documents = sampled.GroupBy(x => x.DocumentId, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new { documentId = x.Key, count = x.Count() }), packetClasses = sampled.GroupBy(x => x.PacketClass, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new { packetClass = x.Key, count = x.Count() }), candidates = sampled.OrderBy(x => x.StableRank, StringComparer.Ordinal).Select(x => new { x.PairId, x.DocumentId, x.Left, x.Right, x.PacketClass, x.EvidenceConfiguration, x.SourceOrderDistanceBucket, x.EndpointDegreeBucket, x.RequestSizeBucket, x.StableRank, packetSha256 = x.PacketSha256, reasons = x.Reasons }) });
        Write(output + "/paired-request-manifest.json", new { artifactKind = "a99_identity_benchmark_v4f_d_paired_request_manifest", schemaVersion = "a99-v4f-d-paired-request-manifest-v1", sampleCount = sampled.Length, totalFutureCalls = sampled.Length * 2, candidateIdentityParity = true, model = model, provider = provider, configurationHash = configHash, decisionOptions = options, responseSchemaHash = schemaHash, parserSchemaIdentical = true, onlyChangedVariable = "EVIDENCE_REPRESENTATION", arms = sampled.OrderBy(x => x.StableRank, StringComparer.Ordinal).Select(x => new { candidateId = x.PairId, oldRequest = new { armId = "ARM_A", requestId = x.PairId, requestBytes = x.OldBytes, requestSha256 = x.OldRequestSha, builderVersion = x.OldBuilder, model, provider, configurationHash = configHash }, projectedRequest = new { armId = "ARM_B", requestId = x.PairId, requestBytes = x.NewBytes, requestSha256 = x.NewRequestSha, builderVersion = x.NewBuilder, model, provider, configurationHash = configHash }, packetSha256 = x.PacketSha256 }) });
        Write(output + "/execution-order.json", new { artifactKind = "a99_identity_benchmark_v4f_d_execution_order", schemaVersion = "a99-v4f-d-execution-order-v1", ordering = "candidate stable SHA ASC; per-candidate arm order balanced by SHA parity", providerExecution = false, entries = Execution(sampled) });
        Write(output + "/metrics-contract.json", new { artifactKind = "a99_identity_benchmark_v4f_d_metrics_contract", schemaVersion = "a99-v4f-d-metrics-contract-v1", behavioralPreservation = new[] { "PairwiseRelationAgreement", "ProjectedChangeRate", "ParseValidityRateByArm", "InvalidResponseRateByArm", "AbstentionRateByArm" }, semanticAccuracy = "SEPARATE; requires independent labels; ARM_A is not oracle", agreementIsAccuracy = false, freezeBeforeExecution = true });
        Write(output + "/cost-preview.json", new { artifactKind = "a99_identity_benchmark_v4f_d_cost_preview", schemaVersion = "a99-v4f-d-cost-preview-v1", pricing = "COST_UNKNOWN", oldArm = Distribution(sampled.Select(x => x.OldBytes)), projectedArm = Distribution(sampled.Select(x => x.NewBytes)), combined = new { calls = sampled.Length * 2, estimatedInputTokens = sampled.Sum(x => Estimate(x.OldBytes) + Estimate(x.NewBytes)), bytes = sampled.Sum(x => x.OldBytes + x.NewBytes) }, outputTokensExcluded = true, providerNotQueried = true });
        Write(output + "/firewall.json", new { artifactKind = "a99_identity_benchmark_v4f_d_firewall", schemaVersion = "a99-v4f-d-firewall-v1", providerCalls = 0, modelCalls = 0, goldReadCountForSampling = 0, v4ebEvaluationReadCountForSampling = 0, knownIrUsedForSelection = false, candidateUniverseChanged = false, rankingChanged = false, projectionChanged = false, requestBytesPersisted = false, requestHashesOnly = true, noProviderTransport = true, armLabelsNeutralInternally = true, goldAccuracyUnavailableUntilIndependentLabels = true });
        Write(output + "/manifest.json", new { artifactKind = "a99_identity_benchmark_v4f_d_manifest", schemaVersion = "a99-v4f-d-manifest-v1", status = "READY_FOR_V4F_PAIRED_PROVIDER_EXECUTION", parent = "V4F-C@7874911", populationCount = records.Length, sampleCount = sampled.Length, totalFutureCalls = sampled.Length * 2, documents = sampled.Select(x => x.DocumentId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), packetClasses = sampled.Select(x => x.PacketClass).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), candidateUniverseChanged = false, rankingChanged = false, projectionChanged = false, oldRequestCount = sampled.Length, projectedRequestCount = sampled.Length, candidateIdentityParity = true, model = model, provider = provider, configurationHash = configHash, decisionOptions = options, responseSchemaHash = schemaHash, onlyChangedVariable = "EVIDENCE_REPRESENTATION", providerCalls = 0, modelCalls = 0, goldReadCountForSampling = 0, v4ebEvaluationReadCountForSampling = 0, knownIrUsedForSelection = false, nextGate = "READY_FOR_V4F_PAIRED_PROVIDER_EXECUTION" });
        File.WriteAllText(output + "/report.md", Report(records, sampled, configHash), new UTF8Encoding(false));
        Console.WriteLine($"V4F_D_COMPLETE population={records.Length} sample={sampled.Length} futureCalls={sampled.Length * 2} oldTokens={sampled.Sum(x => Estimate(x.OldBytes))} projectedTokens={sampled.Sum(x => Estimate(x.NewBytes))}");
    }

    private static CandidateRecord ToRecord(Candidate c, IReadOnlyDictionary<string, Occurrence> nodes, IReadOnlyDictionary<string, int> degrees, PacketInfo packet, ArmInfo old, ArmInfo newer)
    {
        var left = nodes[c.Left]; var right = nodes[c.Right]; var distance = Math.Abs(left.DocumentOrder - right.DocumentOrder); var equality = Normalize(left.Text) == Normalize(right.Text); var reasons = c.Reasons.Order(StringComparer.Ordinal).ToArray(); var config = string.Join('+', reasons);
        return new(c.PairId, c.DocumentId, c.Left, c.Right, packet.PacketClass, config, equality ? "EQUAL" : "NON_EQUAL", DistanceBucket(distance), DegreeBucket(Math.Max(degrees[c.Left], degrees[c.Right])), SizeBucket(old.Bytes), string.Join('|', reasons), StableRank(c.PairId), packet.PacketSha256, old.Bytes, old.RequestSha, old.BuilderVersion, newer.Bytes, newer.RequestSha, newer.BuilderVersion);
    }

    private static CandidateRecord[] Select(CandidateRecord[] records)
    {
        var groups = records.GroupBy(x => x.StratumKey, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.OrderBy(y => y.StableRank, StringComparer.Ordinal).ToList()).ToList(); var result = new List<CandidateRecord>(TargetSample); var index = 0;
        while (result.Count < TargetSample) { var added = false; foreach (var group in groups) { if (index < group.Count && result.Count < TargetSample) { result.Add(group[index]); added = true; } } if (!added) break; index++; }
        return result.OrderBy(x => x.StableRank, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<object> Strata(CandidateRecord[] all, CandidateRecord[] sampled) => all.GroupBy(x => x.StratumKey, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g => new { stratum = g.Key, populationCount = g.Count(), sampledCount = sampled.Count(x => x.StratumKey == g.Key), documentId = g.First().DocumentId, packetClass = g.First().PacketClass, evidenceConfiguration = g.First().EvidenceConfiguration, sourceOrderDistanceBucket = g.First().SourceOrderDistanceBucket });
    private static IEnumerable<object> Execution(CandidateRecord[] sample) => sample.OrderBy(x => x.StableRank, StringComparer.Ordinal).SelectMany(x => (Convert.ToByte(x.StableRank[..2], 16) % 2 == 0 ? new[] { "ARM_A", "ARM_B" } : new[] { "ARM_B", "ARM_A" }).Select((arm, i) => new { sequence = 2 * Array.IndexOf(sample.OrderBy(y => y.StableRank, StringComparer.Ordinal).ToArray(), x) + i + 1, candidateId = x.PairId, armId = arm, requestId = x.PairId }));
    private static object Distribution(IEnumerable<long> values) { var a = values.OrderBy(x => x).ToArray(); return new { count = a.Length, min = a.Min(), p50 = a[(int)Math.Floor(.5 * (a.Length - 1))], p95 = a[(int)Math.Floor(.95 * (a.Length - 1))], max = a.Max(), total = a.Sum() }; }
    private static object Distribution(IEnumerable<int> values) => Distribution(values.Select(x => (long)x));
    private static string Report(CandidateRecord[] all, CandidateRecord[] sample, string configHash) => $"# A99 V4F-D — paired old-context vs projected-context verifier experiment\n\nStatus: **READY_FOR_V4F_PAIRED_PROVIDER_EXECUTION**\n\nPopulation: **{all.Length:N0}** frozen candidates. Source-only deterministic sample: **{sample.Length:N0}** candidates across **{sample.Select(x => x.DocumentId).Distinct(StringComparer.Ordinal).Count()}** documents and both packet classes.\n\n## Paired design\n\nEach sampled candidate has ARM_A (frozen old full-context request) and ARM_B (frozen projected request). Candidate identity, model (`qwen/qwen3.7-flash`), provider (`OpenRouter`), decision options, response schema and configuration are held constant. The only changed variable is evidence representation. Request bodies are not persisted; SHA/length/builder metadata are frozen and reconstructible.\n\nFuture calls: **{sample.Length * 2}**; old estimated input tokens: **{sample.Sum(x => Estimate(x.OldBytes)):N0}**; projected estimated input tokens: **{sample.Sum(x => Estimate(x.NewBytes)):N0}**; combined estimated input tokens: **{sample.Sum(x => Estimate(x.OldBytes) + Estimate(x.NewBytes)):N0}**. Pricing: **COST_UNKNOWN**.\n\n## Sampling\n\nSelection seed: `{SelectionSeed}`. Strata use document, packet class, natural retrieval-reason configuration and source-only distance bucket; stable SHA ranking makes the sample reproducible. Population/sample counts are in `population-strata.json`. No Gold, IR, V4E-B outcome or model output was read.\n\n## Metrics frozen\n\nBehavioral preservation is separate from semantic accuracy: pairwise relation agreement, projected change rate, parse validity, invalid response and abstention rates. Semantic accuracy requires independent labels; ARM_A is not an oracle.\n\n## Firewall\n\n`ProviderCalls=0`; `ModelCalls=0`; `GoldReadCountForSampling=0`; `V4EBEvaluationReadCountForSampling=0`; `KnownIRUsedForSelection=false`; candidate universe/ranking/projection unchanged.\n\n**Next gate:** provider execution requires separate authorization.\n";
    private static string DistanceBucket(int distance) => distance == 1 ? "ADJACENT_1" : distance <= 4 ? "NEAR_2_4" : distance <= 16 ? "MID_5_16" : "FAR_17_PLUS";
    private static string DegreeBucket(int degree) => degree <= 1 ? "DEGREE_1" : degree <= 3 ? "DEGREE_2_3" : "DEGREE_4_PLUS";
    private static string SizeBucket(long bytes) => bytes <= 400_000 ? "SMALL_LE_400K" : bytes <= 800_000 ? "MEDIUM_400_800K" : "LARGE_GT_800K";
    private static string Normalize(string text) => string.Join(' ', text.Normalize(System.Text.NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static int Estimate(long bytes) => checked((int)((bytes + 3) / 4));
    private static string StableRank(string pairId) => Hash(Encoding.UTF8.GetBytes(SelectionSeed + "|" + pairId));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Sha256File(string path) => Hash(File.ReadAllBytes(path));
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static string Full(string root, string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, WriteOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static Candidate ReadCandidate(JsonElement x) => new(x.GetProperty("pairId").GetString()!, x.GetProperty("documentId").GetString()!, x.GetProperty("left").GetString()!, x.GetProperty("right").GetString()!, x.TryGetProperty("reasons", out var r) ? r.EnumerateArray().Select(y => y.GetString()!).ToArray() : []);
    private static List<string> VerifyInputs(JsonDocument source, JsonDocument v4, JsonDocument shortlist, JsonDocument oldRequests, JsonDocument packets, JsonDocument projected, JsonDocument projectedRequests, string sourcePath, string shortlistPath, string oldRequestPath, string packetManifestPath, string projectedManifestPath)
    {
        var f = new List<string>(); void C(bool ok, string name) { if (!ok) f.Add(name); }
        var m = v4.RootElement; var p = projected.RootElement; C(source.RootElement.GetProperty("sourceOccurrences").GetArrayLength() > 0, "source.readable"); C(m.GetProperty("shortlistedCount").GetInt32() == ExpectedPopulation, "v4e.count"); C(shortlist.RootElement.GetProperty("candidates").GetArrayLength() == ExpectedPopulation, "shortlist.count"); C(oldRequests.RootElement.GetProperty("requestCount").GetInt32() == ExpectedPopulation, "old.count"); C(packets.RootElement.GetProperty("packetCount").GetInt32() == ExpectedPopulation, "packets.count"); C(projectedRequests.RootElement.GetProperty("requestCount").GetInt32() == ExpectedPopulation, "projected.count"); C(Sha256File(sourcePath) == m.GetProperty("sourceCatalogSha256").GetString(), "source.sha"); C(Sha256File(shortlistPath) == m.GetProperty("shortlistSha256").GetString(), "shortlist.sha"); C(Sha256File(oldRequestPath) == m.GetProperty("requestManifestSha256").GetString(), "old.sha"); C(Sha256File(packetManifestPath) == p.GetProperty("v4fBPacketManifestSha256").GetString(), "packet.sha"); C(Sha256File(sourcePath) == p.GetProperty("sourceCatalogSha256").GetString(), "projected.source.sha"); C(p.GetProperty("candidateCount").GetInt32() == ExpectedPopulation && p.GetProperty("projectedRequestCount").GetInt32() == ExpectedPopulation, "projected.manifest.count"); C(oldRequests.RootElement.GetProperty("goldDerivedInput").GetBoolean() == false, "old.gold"); C(source.RootElement.GetProperty("goldDerivedInput").GetBoolean() == false, "source.gold"); C(p.GetProperty("providerCalls").GetInt32() == 0 && p.GetProperty("goldReadCount").GetInt32() == 0, "projected.firewall"); C(projectedRequests.RootElement.GetProperty("packetBodiesPersisted").GetBoolean() == false && projectedRequests.RootElement.GetProperty("exactBytesPersisted").GetBoolean() == false, "projected.hash_only"); return f;
    }

    private sealed record Occurrence(string NodeId, string Text, int DocumentOrder);
    private sealed record Candidate(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons);
    private sealed record PacketInfo(string PacketClass, string PacketSha256);
    private sealed record ArmInfo(string ArmId, string Label, long Bytes, string RequestSha, string BuilderVersion);
    private sealed record CandidateRecord(string PairId, string DocumentId, string Left, string Right, string PacketClass, string EvidenceConfiguration, string NormalizedEquality, string SourceOrderDistanceBucket, string EndpointDegreeBucket, string RequestSizeBucket, string ReasonsKey, string StableRank, string PacketSha256, long OldBytes, string OldRequestSha, string OldBuilder, long NewBytes, string NewRequestSha, string NewBuilder)
    {
        public string[] Reasons => ReasonsKey.Length == 0 ? [] : ReasonsKey.Split('|');
        public string StratumKey => string.Join('|', DocumentId, PacketClass, EvidenceConfiguration, SourceOrderDistanceBucket);
    }
}
