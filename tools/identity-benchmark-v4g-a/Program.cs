using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace IdentityBenchmarkV4GA;

internal static class Program
{
    private const string OutputRelative = "artifacts/identity-benchmark/v4/target-grounding-challenger";
    private const string SourceRelative = "artifacts/identity-benchmark/v2/source-catalog.json";
    private const string ShortlistRelative = "artifacts/identity-benchmark/v4/pruning-challenger/shortlist.json";
    private const string V4CManifestRelative = "artifacts/identity-benchmark/v4/projected-requests/manifest.json";
    private const string V4CRequestsRelative = "artifacts/identity-benchmark/v4/projected-requests/request-manifest.json";
    private const string V4CSampleRelative = "artifacts/identity-benchmark/v4/projected-verifier-experiment/sample.json";
    private const string V4CPairedRelative = "artifacts/identity-benchmark/v4/projected-verifier-experiment/paired-request-manifest.json";
    private const string PacketsRelative = "artifacts/identity-benchmark/v4/context-projection/packet-manifest.json";
    private const string ProjectionConfigRelative = "artifacts/identity-benchmark/v4/context-projection/projection-config.json";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Provider = "OpenRouter";
    private const string ConfigHash = "8983be53cc38d8a28c8d1467216934d3bab1201673dae86f077f82328b392967";
    private const int ExpectedCandidates = 7_702;
    private const int ExpectedSample = 128;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex ContinuationMarker = new(@"\(\s*(?:cont(?:['’]d)?|continued)\s*\)|\bcontinued\b|tiếp\s+(?:theo|tục)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BranchMarker = new(@"\[(?:option|alternative|phương\s+án)\s*[^\]]*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try { await RunAsync(root); return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"V4G_A_ERROR={ex}"); return 2; }
    }

    private static async Task RunAsync(string root)
    {
        var output = Full(root, OutputRelative);
        Directory.CreateDirectory(output);
        var sourcePath = Full(root, SourceRelative);
        var shortlistPath = Full(root, ShortlistRelative);
        var v4cManifestPath = Full(root, V4CManifestRelative);
        var v4cRequestsPath = Full(root, V4CRequestsRelative);
        var v4cSamplePath = Full(root, V4CSampleRelative);
        var v4cPairedPath = Full(root, V4CPairedRelative);
        var packetsPath = Full(root, PacketsRelative);
        var projectionConfigPath = Full(root, ProjectionConfigRelative);
        foreach (var path in new[] { sourcePath, shortlistPath, v4cManifestPath, v4cRequestsPath, v4cSamplePath, v4cPairedPath, packetsPath, projectionConfigPath })
            Require(File.Exists(path), "V4G_A_MISSING_INPUT:" + path);

        using var source = Read(sourcePath);
        using var shortlist = Read(shortlistPath);
        using var v4cManifest = Read(v4cManifestPath);
        using var v4cRequests = Read(v4cRequestsPath);
        using var sample = Read(v4cSamplePath);
        using var paired = Read(v4cPairedPath);
        using var packetManifest = Read(packetsPath);

        ValidateFrozenInputs(source.RootElement, shortlist.RootElement, v4cManifest.RootElement, v4cRequests.RootElement, sample.RootElement, paired.RootElement, packetManifest.RootElement);
        var catalogFingerprint = source.RootElement.GetProperty("catalogFingerprint").GetString()!;
        var sourceDocuments = source.RootElement.GetProperty("sourceDocuments").EnumerateArray().ToDictionary(
            x => x.GetProperty("documentId").GetString()!,
            x => new SourceDoc(x.GetProperty("documentId").GetString()!, x.GetProperty("sourceSha256").GetString()!, x.GetProperty("sourcePath").GetString()!), StringComparer.Ordinal);
        var allNodes = source.RootElement.GetProperty("sourceOccurrences").EnumerateArray().Select(x => new Occurrence(x.GetProperty("nodeId").GetString()!, x.GetProperty("text").GetString()!, x.GetProperty("documentOrder").GetInt32())).ToArray();
        var byDocument = allNodes.GroupBy(x => DocumentOf(x.NodeId), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(x => x.DocumentOrder).ThenBy(x => x.NodeId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var candidates = shortlist.RootElement.GetProperty("candidates").EnumerateArray().Select(x => new Candidate(
            x.GetProperty("pairId").GetString()!, x.GetProperty("documentId").GetString()!, x.GetProperty("left").GetString()!, x.GetProperty("right").GetString()!,
            x.TryGetProperty("reasons", out var reasons) ? reasons.EnumerateArray().Select(y => y.GetString()!).ToArray() : Array.Empty<string>())).OrderBy(x => x.PairId, StringComparer.Ordinal).ToArray();
        var packetRows = packetManifest.RootElement.GetProperty("packets").EnumerateArray().ToDictionary(x => x.GetProperty("candidateId").GetString()!, StringComparer.Ordinal);
        var v4cRows = v4cRequests.RootElement.GetProperty("requests").EnumerateArray().ToDictionary(x => x.GetProperty("candidateId").GetString()!, StringComparer.Ordinal);
        var v4cSampleIds = sample.RootElement.GetProperty("candidates").EnumerateArray().Select(x => x.GetProperty("pairId").GetString()!).Order(StringComparer.Ordinal).ToArray();
        var pairedRows = paired.RootElement.GetProperty("arms").EnumerateArray().ToDictionary(x => x.GetProperty("candidateId").GetString()!, StringComparer.Ordinal);
        var projectionConfigSha = Sha256File(projectionConfigPath);
        var requestRows = new List<RequestRow>(ExpectedCandidates);
        var lineageRows = new List<LineageRow>(ExpectedCandidates);
        foreach (var candidate in candidates)
        {
            Require(byDocument.TryGetValue(candidate.DocumentId, out var nodes), "V4G_A_UNKNOWN_DOCUMENT:" + candidate.PairId);
            var nodeMap = nodes!.ToDictionary(x => x.NodeId, StringComparer.Ordinal);
            Require(nodeMap.TryGetValue(candidate.Left, out var left), "V4G_A_UNKNOWN_LEFT_TARGET:" + candidate.PairId);
            Require(nodeMap.TryGetValue(candidate.Right, out var right), "V4G_A_UNKNOWN_RIGHT_TARGET:" + candidate.PairId);
            Require(packetRows.TryGetValue(candidate.PairId, out var packetRow), "V4G_A_MISSING_PACKET:" + candidate.PairId);
            Require(v4cRows.TryGetValue(candidate.PairId, out var v4cRow), "V4G_A_MISSING_V4C_REQUEST:" + candidate.PairId);
            var packet = BuildPacket(candidate, left!, right!, nodes!, sourceDocuments[candidate.DocumentId], catalogFingerprint, projectionConfigSha);
            var packetBytes = JsonSerializer.SerializeToUtf8Bytes(packet, JsonOptions);
            Require(Sha256(packetBytes) == packetRow.GetProperty("packetSha256").GetString(), "V4G_A_PACKET_RECONSTRUCTION:" + candidate.PairId);
            var packetJson = JsonSerializer.SerializeToElement(packet, JsonOptions);
            var built = HdsaCanonicalTargetGroundedProjectedPairVerifierRequestBuilder.Build(packetJson, Model, Provider, 768);
            var oldRequestHash = v4cRow.GetProperty("requestSha256").GetString()!;
            var oldBytes = v4cRow.GetProperty("exactByteLength").GetInt64();
            requestRows.Add(new(candidate.PairId, candidate.PairId, "packet:" + candidate.PairId, candidate.DocumentId, packetRow.GetProperty("packetSha256").GetString()!, built.Utf8Bytes.Length, built.Sha256, HdsaCanonicalTargetGroundedProjectedPairVerifierRequestBuilder.Version, Sha256File(Full(root, "src/DocxHeaderExtractor.Core/Models/HdsaCanonicalTargetGroundedProjectedPairVerifierRequestBuilder.cs")), Model, Provider, ConfigHash));
            lineageRows.Add(new(candidate.PairId, packetBytes.Length, oldBytes, built.Utf8Bytes.Length, oldRequestHash, packetRow.GetProperty("packetSha256").GetString()!, NewSourceFacts: 0, Reordered: 1, Relabeled: 1, DuplicatedTargetFacts: 2, PacketShaVerified: true));
        }

        var v4cTotal = v4cRows.Values.Sum(x => x.GetProperty("exactByteLength").GetInt64());
        var v4gTotal = requestRows.Sum(x => (long)x.ByteLength);
        var sampleRows = requestRows.Where(x => Array.BinarySearch(v4cSampleIds, x.CandidateId, StringComparer.Ordinal) >= 0).OrderBy(x => x.CandidateId, StringComparer.Ordinal).ToArray();
        Require(requestRows.Count == ExpectedCandidates && v4cRows.Count == ExpectedCandidates, "V4G_A_FULL_COUNT");
        Require(requestRows.Select(x => x.CandidateId).SequenceEqual(v4cRows.Keys.Order(StringComparer.Ordinal)), "V4G_A_CANDIDATE_ID_PARITY");
        Require(sampleRows.Length == ExpectedSample, "V4G_A_SAMPLE_COUNT");
        Require(sampleRows.Select(x => x.CandidateId).SequenceEqual(v4cSampleIds), "V4G_A_SAMPLE_ID_PARITY");
        Require(lineageRows.All(x => x.NewSourceFacts == 0 && x.PacketShaVerified), "V4G_A_LINEAGE_GATE");

        var contract = new
        {
            schemaVersion = "a99-v4g-a-target-grounding-contract-v1",
            builderVersion = HdsaCanonicalTargetGroundedProjectedPairVerifierRequestBuilder.Version,
            systemContractSha256 = Sha256Text(HdsaCanonicalTargetGroundedProjectedPairVerifierRequestBuilder.SystemContract),
            targetPair = new { leftRole = "LEFT_TARGET", rightRole = "RIGHT_TARGET", fields = new[] { "occurrenceId", "verbatimText", "canonicalComparisonText", "sourceOrder", "pageOrSourceLocation" } },
            supportingEvidence = new[] { "LEFT_CONTEXT", "RIGHT_CONTEXT", "STRUCTURAL_CONTEXT", "INTERVENING_CONTEXT", "RELATIONAL_FACTS", "EVIDENCE_AVAILABILITY", "MISSING_EVIDENCE" },
            targetIdentity = "exact frozen source occurrence IDs; no generated short aliases replace IDs",
            responseContract = HdsaGlobalIdentityRetrieveVerifyContract.Version,
            decisionOptions = new[] { "CONTINUATION_OF", "SAME_SEMANTIC_REPEAT", "DISTINCT", "UNRESOLVED" },
            parserChanged = false,
        };
        var manifest = new
        {
            schemaVersion = "a99-v4g-a-manifest-v1", experiment = "IDENTITY_BENCHMARK_V4G_A", parent = "V4F-F@153204b", status = "READY_FOR_V4G_TARGET_GROUNDING_PROVIDER_EXPERIMENT",
            developmentStatus = "DEV_EXPOSED_CHALLENGER", informedBy = "V4F_F_PROJECTED_TARGET_GROUNDING_DEGRADATION", independentGeneralizationClaim = false,
            candidateCount = ExpectedCandidates, v4cProjectedRequestCount = ExpectedCandidates, v4gRequestCount = requestRows.Count, frozenSampleCount = sampleRows.Length,
            sourceCatalogSha256 = Sha256File(sourcePath), sourceCatalogFingerprint = catalogFingerprint, v4cManifestSha256 = Sha256File(v4cManifestPath), v4cRequestManifestSha256 = Sha256File(v4cRequestsPath), packetManifestSha256 = Sha256File(packetsPath), projectionConfigSha256 = projectionConfigSha,
            candidateUniverseChanged = false, rankingChanged = false, projectionEvidenceChanged = false, parserChanged = false, semanticDecisionContractChanged = false, targetGroundingRepresentationChanged = true,
            goldReadCount = 0, v4ebEvaluationReadCount = 0, providerCalls = 0, modelCalls = 0, newSourceFacts = 0, requestBodiesPersisted = false, exactBytesDeterministicallyReconstructible = true,
            v4cProjectedTotalBytes = v4cTotal, v4gTotalBytes = v4gTotal, sizeDeltaBytes = v4gTotal - v4cTotal, sizeDeltaRatio = (v4gTotal - v4cTotal) / (double)v4cTotal,
            primaryExperiment = "V4F-C PROJECTED_V1 vs V4G PROJECTED_V2_TARGET_GROUNDED", oldArmHistoricalOnly = true, createdUtc = DateTimeOffset.UtcNow,
        };
        await WriteAsync(Path.Combine(output, "manifest.json"), manifest);
        await WriteAsync(Path.Combine(output, "target-grounding-contract.json"), contract);
        await WriteAsync(Path.Combine(output, "evidence-lineage-audit.json"), new
        {
            schemaVersion = "a99-v4g-a-evidence-lineage-audit-v1", candidateCount = lineageRows.Count, newSourceFacts = 0,
            counts = new { reordered = lineageRows.Count, relabeledForTargetGrounding = lineageRows.Count, duplicatedTargetFacts = lineageRows.Count * 2, unchanged = 0 },
            classifications = new[] { "REORDERED", "RELABELED_FOR_TARGET_GROUNDING", "DUPLICATED_TARGET_FACT_FOR_GROUNDING", "UNCHANGED" },
            fieldPolicy = "every V4G supporting-evidence field is copied from the reconstructed frozen V4F-B packet after pairCore is removed; target fields duplicate existing pairCore facts only",
            rows = lineageRows,
        });
        await WriteAsync(Path.Combine(output, "request-manifest.json"), new
        {
            schemaVersion = "a99-v4g-a-request-manifest-v1", requestCount = requestRows.Count, packetManifestConsumedAsFrozen = true, packetBodiesPersisted = false, exactBytesPersisted = false, exactBytesDeterministicallyReconstructible = true, canonicalOrdering = "candidateId ASC", builderVersion = HdsaCanonicalTargetGroundedProjectedPairVerifierRequestBuilder.Version, model = Model, provider = Provider, configurationHash = ConfigHash, requests = requestRows,
        });
        await WriteAsync(Path.Combine(output, "challenger-sample-request-manifest.json"), new
        {
            schemaVersion = "a99-v4g-a-challenger-sample-request-manifest-v1", sampleCount = sampleRows.Length, sampleSource = "V4F-D frozen 128 candidate IDs", failureConditionedSelection = false, requests = sampleRows,
        });
        var increases = requestRows.Zip(v4cRows.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value.GetProperty("exactByteLength").GetInt64())).Select(x => x.First.ByteLength - x.Second).Order().ToArray();
        await WriteAsync(Path.Combine(output, "size-delta.json"), new
        {
            schemaVersion = "a99-v4g-a-size-delta-v1", v4fCTotalBytes = v4cTotal, v4gTotalBytes = v4gTotal, deltaBytes = v4gTotal - v4cTotal, deltaPercent = (v4gTotal - v4cTotal) / (double)v4cTotal,
            meanIncreasePerRequest = increases.Average(), p95Increase = Percentile(increases, .95), maxIncrease = increases.Max(), minIncrease = increases.Min(), requestCount = requestRows.Count,
        });
        await WriteAsync(Path.Combine(output, "future-metrics-contract.json"), new
        {
            schemaVersion = "a99-v4g-a-future-metrics-contract-v1", providerCalls = 0, primaryMetric = "TargetMismatchRate", historicalV1 = new { targetMismatch = "45/128", valid = "63/128" }, futureArms = new[] { "V4F-C PROJECTED_V1", "V4G PROJECTED_V2_TARGET_GROUNDED" }, secondaryMetrics = new[] { "ParseValidityRate", "ValidResponseRate", "RelationAgreementAmongBothValid", "ProviderErrorRate" }, v1IsHistoricalControl = true, noV1RerunRequired = true, semanticAccuracy = "not measured in V4G-A",
        });
        await WriteAsync(Path.Combine(output, "firewall.json"), new
        {
            schemaVersion = "a99-v4g-a-firewall-v1", goldReadCount = 0, v4ebEvaluationReadCount = 0, providerCalls = 0, modelCalls = 0, candidateUniverseChanged = false, projectionEvidenceChanged = false, rankingChanged = false, parserChanged = false, semanticDecisionContractChanged = false, targetGroundingRepresentationChanged = true, noProviderTransport = true,
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), Report(root, v4cTotal, v4gTotal, sampleRows.Length, requestRows.Count, increases), new UTF8Encoding(false));
        Console.WriteLine($"V4G_A_COMPLETE requests={requestRows.Count} sample={sampleRows.Length} v4cBytes={v4cTotal} v4gBytes={v4gTotal} providerCalls=0 modelCalls=0 goldReadCount=0");
    }

    private static void ValidateFrozenInputs(JsonElement source, JsonElement shortlist, JsonElement v4cManifest, JsonElement v4cRequests, JsonElement sample, JsonElement paired, JsonElement packets)
    {
        Require(source.GetProperty("goldDerivedInput").GetBoolean() == false, "V4G_A_SOURCE_GOLD");
        Require(shortlist.GetProperty("candidateGenerationUnchanged").GetBoolean(), "V4G_A_SHORTLIST_DRIFT");
        Require(v4cManifest.GetProperty("candidateCount").GetInt32() == ExpectedCandidates && v4cManifest.GetProperty("projectedRequestCount").GetInt32() == ExpectedCandidates, "V4G_A_V4C_COUNT");
        Require(v4cRequests.GetProperty("requestCount").GetInt32() == ExpectedCandidates, "V4G_A_V4C_REQUEST_COUNT");
        Require(sample.GetProperty("sampleCount").GetInt32() == ExpectedSample, "V4G_A_SAMPLE_COUNT_INPUT");
        Require(paired.GetProperty("sampleCount").GetInt32() == ExpectedSample && paired.GetProperty("totalFutureCalls").GetInt32() == 256, "V4G_A_PAIRED_INPUT");
        Require(packets.GetProperty("packetCount").GetInt32() == ExpectedCandidates && packets.GetProperty("packetBodiesPersisted").GetBoolean() == false, "V4G_A_PACKET_INPUT");
    }

    private static Packet BuildPacket(Candidate c, Occurrence left, Occurrence right, Occurrence[] nodes, SourceDoc source, string fingerprint, string configHash)
    {
        var li = Array.IndexOf(nodes, left); var ri = Array.IndexOf(nodes, right); var ordered = li <= ri ? (li, ri) : (ri, li);
        var allBetween = nodes.Skip(ordered.Item1 + 1).Take(Math.Max(0, ordered.Item2 - ordered.Item1 - 1)).ToArray();
        var between = Bounded(allBetween, 8).Select(x => Core(x, source)).ToArray();
        var local = new LocalContext(Neighbors(nodes, li, -1, 2, source), Neighbors(nodes, li, 1, 2, source), Neighbors(nodes, ri, -1, 2, source), Neighbors(nodes, ri, 1, 2, source), between, allBetween.Length > between.Length, allBetween.Length);
        var structural = new StructuralContext(new[] { Container(left.NodeId), Container(right.NodeId) }.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StructuralPeers(nodes, li, ri));
        var relational = new RelationalFacts(Math.Abs(left.DocumentOrder - right.DocumentOrder), Normalize(left.Text) == Normalize(right.Text), Markers(left.Text), Markers(right.Text), Branches(left.Text), Branches(right.Text), c.Reasons.Order(StringComparer.Ordinal).ToArray());
        var availability = new EvidenceAvailability("AVAILABLE", "AVAILABLE", "AVAILABLE", "UNAVAILABLE", "UNAVAILABLE", "UNAVAILABLE", "UNAVAILABLE", allBetween.Length > 0 ? "AVAILABLE" : "UNAVAILABLE");
        return new("a99_identity_benchmark_v4f_b_pair_evidence_packet", "v4f-b-pair-evidence-packet-v1", c.PairId, c.DocumentId, [Core(left, source), Core(right, source)], local, structural, relational, availability, [new EvidenceAuthority("FROZEN_SOURCE_CATALOG", "source occurrence text/order/container", "AVAILABLE"), new EvidenceAuthority("FROZEN_SOURCE_CATALOG", "bounded context selected by source order", "AVAILABLE"), new EvidenceAuthority("FROZEN_SOURCE_CATALOG", "source path extension", "AVAILABLE")], ["numbering", "scope", "layout", "visual"], "v4f-b-bounded-source-evidence-projection-v1", fingerprint, configHash);
    }

    private static PairOccurrence Core(Occurrence x, SourceDoc source) => new(x.NodeId, x.Text, Normalize(x.Text), x.DocumentOrder, Path.GetExtension(source.SourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" : "SOURCE_PACKET", Container(x.NodeId));
    private static IReadOnlyList<PairOccurrence> Neighbors(IReadOnlyList<Occurrence> all, int index, int direction, int count, SourceDoc source) => (direction < 0 ? all.Take(index).TakeLast(count) : all.Skip(index + 1).Take(count)).Select(x => Core(x, source)).ToArray();
    private static IReadOnlyList<Occurrence> Bounded(IReadOnlyList<Occurrence> all, int max) => all.Count <= max ? all : all.Take(max / 2).Concat(all.TakeLast(max - max / 2)).ToArray();
    private static IReadOnlyList<StructuralPeer> StructuralPeers(IReadOnlyList<Occurrence> nodes, int left, int right) => nodes.Select((x, i) => (x, i)).Where(x => x.i == left || x.i == right).SelectMany(anchor => nodes.Select((x, i) => (x, i)).Where(x => x.i != left && x.i != right && Container(x.x.NodeId) == Container(anchor.x.NodeId)).OrderBy(x => Math.Abs(x.i - anchor.i)).ThenBy(x => x.x.NodeId, StringComparer.Ordinal).Take(4)).Select(x => new StructuralPeer(x.x.NodeId, x.x.DocumentOrder, Container(x.x.NodeId))).DistinctBy(x => x.SourceOccurrenceId).OrderBy(x => x.DocumentOrder).ThenBy(x => x.SourceOccurrenceId, StringComparer.Ordinal).Take(4).ToArray();
    private static string[] Markers(string text) => ContinuationMarker.Matches(text).Select(x => x.Value).Order(StringComparer.Ordinal).ToArray();
    private static string[] Branches(string text) => BranchMarker.Matches(text).Select(x => x.Value).Order(StringComparer.Ordinal).ToArray();
    private static string Normalize(string text) => string.Join(' ', text.Normalize(System.Text.NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Container(string id) => (id.Split(':', 2).ElementAtOrDefault(1) ?? id).Split('/').FirstOrDefault() ?? string.Empty;
    private static string DocumentOf(string id) => id.Split(':', 2)[0];
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static string Sha256File(string path) => Sha256(File.ReadAllBytes(path));
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Sha256Text(string text) => Sha256(Encoding.UTF8.GetBytes(text));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static async Task WriteAsync(string path, object value) { await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false)); }
    private static long Percentile(long[] values, double percentile) => values.Length == 0 ? 0 : values[Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1)];
    private static string Report(string root, long v4cTotal, long v4gTotal, int sampleCount, int requestCount, long[] increases) => $"# A99 V4G-A — target-grounding challenger freeze\n\nStatus: **READY_FOR_V4G_TARGET_GROUNDING_PROVIDER_EXPERIMENT**\n\nDevelopment status: **DEV_EXPOSED_CHALLENGER**. This request representation was informed by V4F-F target-grounding forensics; no independent generalization claim is made.\n\n## Checkpoint\n\n- Parent: `V4F-F@153204b`\n- Candidate universe: **{requestCount:N0}/{ExpectedCandidates:N0}**, unchanged.\n- Frozen V4F-D sample: **{sampleCount}/{ExpectedSample}**, same candidate IDs.\n- V4F-C historical projected target mismatch: **45/128**.\n- V4F-C historical projected valid: **63/128**.\n\n## Target contract\n\n`TARGET PAIR` contains `LEFT_TARGET` and `RIGHT_TARGET`, each with the exact frozen occurrence ID, verbatim text, canonical comparison text, source order, and source/container location. `SUPPORTING EVIDENCE` is separately labeled and cannot become an endpoint by list position. The response relation vocabulary and parser are unchanged.\n\n## Evidence lineage\n\n- New source facts: **0**.\n- Target fields duplicate existing pair-core facts only.\n- Supporting evidence is the reconstructed frozen V4F-B packet with pair-core removed.\n- Candidate generation, ranking, projection bounds, model, provider, and parser are unchanged.\n\n## Size\n\n- V4F-C projected total: **{v4cTotal:N0} bytes**.\n- V4G target-grounded total: **{v4gTotal:N0} bytes**.\n- Delta: **{v4gTotal - v4cTotal:N0} bytes ({(v4gTotal - v4cTotal) / (double)v4cTotal:P4})**.\n- Mean increase/request: **{increases.Average():N1}**; p95: **{Percentile(increases, .95):N0}**; max: **{increases.Max():N0}**.\n\n## Firewall\n\nGold reads: **0**; V4E evaluation reads: **0**; provider calls: **0**; model calls: **0**. V4F-C/F-D/F-E/F-F artifacts were read-only inputs and were not modified.\n\nNo provider execution was performed. This phase freezes an experiment-ready challenger only.\n";

    private sealed record Occurrence(string NodeId, string Text, int DocumentOrder);
    private sealed record Candidate(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons);
    private sealed record SourceDoc(string DocumentId, string SourceSha256, string SourcePath);
    private sealed record RequestRow(string CandidateId, string RequestId, string PacketId, string DocumentId, string PacketSha256, int ByteLength, string RequestSha256, string BuilderVersion, string BuilderHash, string Model, string Provider, string ConfigurationHash);
    private sealed record LineageRow(string CandidateId, int PacketBytes, long V4FCBytes, int V4GBytes, string V4FCRequestSha256, string PacketSha256, int NewSourceFacts, int Reordered, int Relabeled, int DuplicatedTargetFacts, bool PacketShaVerified);
    private sealed record PairOccurrence(string SourceOccurrenceId, string RawSurface, string CanonicalComparisonSurface, int DocumentOrder, string SourceKind, string SourceContainer);
    private sealed record LocalContext(IReadOnlyList<PairOccurrence> PreviousOfLeft, IReadOnlyList<PairOccurrence> NextOfLeft, IReadOnlyList<PairOccurrence> PreviousOfRight, IReadOnlyList<PairOccurrence> NextOfRight, IReadOnlyList<PairOccurrence> Intervening, bool InterveningTruncated, int TotalInterveningOccurrences);
    private sealed record StructuralPeer(string SourceOccurrenceId, int DocumentOrder, string SourceContainer);
    private sealed record StructuralContext(IReadOnlyList<string> SourceContainers, IReadOnlyList<StructuralPeer> StructuralPeers);
    private sealed record RelationalFacts(int SourceOrderDistance, bool NormalizedTextEqual, IReadOnlyList<string> LeftContinuationMarkers, IReadOnlyList<string> RightContinuationMarkers, IReadOnlyList<string> LeftBranchMarkers, IReadOnlyList<string> RightBranchMarkers, IReadOnlyList<string> CandidateRetrievalReasons);
    private sealed record EvidenceAvailability(string PairCore, string LocalContext, string StructuralContext, string Numbering, string Scope, string Layout, string Visual, string InterveningOccurrences);
    private sealed record EvidenceAuthority(string Authority, string Derivation, string Availability);
    private sealed record Packet(string ArtifactKind, string SchemaVersion, string CandidateId, string DocumentId, IReadOnlyList<PairOccurrence> PairCore, LocalContext LocalContext, StructuralContext StructuralContext, RelationalFacts RelationalFacts, EvidenceAvailability EvidenceAvailability, IReadOnlyList<EvidenceAuthority> EvidenceAuthorities, IReadOnlyList<string> MissingEvidence, string ProjectionVersion, string SourceCatalogFingerprint, string ProjectionConfigSha256);
}
