using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityPromotionBenchmarkV4DA;

internal static class Program
{
    private const string V1Root = "artifacts/identity-benchmark/v1";
    private const string V2Root = "artifacts/identity-benchmark/v2";
    private const string V3Root = "artifacts/identity-benchmark/v3";
    private const string V4Root = "artifacts/identity-benchmark/v4/challenger";
    private const string OutputRoot = "artifacts/identity-benchmark/v4/pruning";
    private const int ExpectedV3Broad = 95_999;
    private const int ExpectedV4Broad = 96_069;
    private const int ExpectedV4Only = 70;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args.Length == 1 ? args[0] : Directory.GetCurrentDirectory());
            Run(root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V4D_A_ERROR={ex}");
            return 2;
        }
    }

    private static void Run(string root)
    {
        // V4D-A intentionally never opens the Gold path or V4C evaluation output.
        var v4Manifest = Read(root, V4Root + "/manifest.json");
        var v4Broad = Read(root, V4Root + "/broad-candidate-set.json");
        var v4SourceReference = Read(root, V4Root + "/source-reference.json");
        var v4Contract = Read(root, V4Root + "/retrieval-contract.json");
        var v4Firewall = Read(root, V4Root + "/firewall.json");
        var v3Manifest = Read(root, V3Root + "/manifest.json");
        var v3Ranking = Read(root, V3Root + "/candidate-ranking-config.json");
        var v3Features = Read(root, V3Root + "/source-only-feature-inventory.json");
        var v3ShortlistManifest = Read(root, V3Root + "/shortlist-manifest.json");
        var v3CandidateSet = Read(root, V1Root + "/candidate-set.json");

        var v4Candidates = ReadCandidates(v4Broad);
        var v3Candidates = ReadCandidates(v3CandidateSet);
        var freeze = VerifyInputs(root, v4Manifest, v4Broad, v4SourceReference, v4Contract, v4Firewall,
            v3Manifest, v3Ranking, v3Features, v3ShortlistManifest, v3Candidates, v4Candidates);

        var supported = new HashSet<string>(StringComparer.Ordinal) { "ADJACENT_ORDER", "NORMALIZED_TEXT_AFFINITY" };
        var v4Only = v4Candidates.Where(item => item.V4Only).ToArray();
        var unsupported = v4Only
            .SelectMany(item => item.Reasons.Where(reason => !supported.Contains(reason)).Select(reason => new UnsupportedReason(item, reason)))
            .ToArray();
        var unsupportedNames = unsupported.Select(item => item.Reason).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var gate = unsupportedNames.Length == 0 ? "READY_FOR_V4_PRUNING_GOLD_EVALUATION" : "BLOCKED_ON_V3_PRUNER_CONTRACT_INCOMPATIBILITY";
        var output = Full(root, OutputRoot);
        Directory.CreateDirectory(output);
        var v4ManifestSha = Sha256File(Full(root, V4Root + "/manifest.json"));
        var v4BroadSha = Sha256File(Full(root, V4Root + "/broad-candidate-set.json"));
        var v3RankingSha = Sha256File(Full(root, V3Root + "/candidate-ranking-config.json"));
        var v3FeatureSha = Sha256File(Full(root, V3Root + "/source-only-feature-inventory.json"));
        var v3ShortlistManifestSha = Sha256File(Full(root, V3Root + "/shortlist-manifest.json"));
        var v3CandidateSha = Sha256File(Full(root, V1Root + "/candidate-set.json"));

        WriteJson(Path.Combine(output, "manifest.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4d_a_manifest",
            schemaVersion = "a99-identity-benchmark-v4d-a-manifest-v1",
            status = gate,
            developmentStatus = "DEV_EXPOSED_CHALLENGER",
            independentGeneralizationClaim = false,
            phase = "V4D_A_SOURCE_ONLY_V3_PRUNING_COMPATIBILITY_AND_FREEZE",
            input = new { v4BroadCandidateCount = v4Candidates.Length, v4OnlyCandidateCount = v4Only.Length, v4BroadSha256 = v4BroadSha },
            v3PruningContract = new { rankingConfigSha256 = v3RankingSha, featureContractSha256 = v3FeatureSha, shortlistManifestSha256 = v3ShortlistManifestSha, candidateSetSha256 = v3CandidateSha },
            newReasonLabels = unsupportedNames,
            newReasonLabelsHaveFrozenV3Interpretation = unsupportedNames.Length == 0,
            pruningApplied = false,
            shortlistCreated = false,
            requestArtifactsCreated = false,
            goldReadCount = 0,
            providerCalls = 0,
            modelCalls = 0,
            v4bManifestSha256 = v4ManifestSha,
            freezeIntegrity = freeze,
            nextGate = gate,
        });

        WriteJson(Path.Combine(output, "v3-pruning-contract-reference.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4d_a_v3_pruning_contract_reference",
            schemaVersion = "a99-identity-benchmark-v4d-a-v3-pruning-contract-reference-v1",
            status = gate,
            algorithmVersion = v3Ranking.RootElement.GetProperty("algorithmVersion").GetString(),
            dimensions = v3Ranking.RootElement.GetProperty("dimensions"),
            evidenceTier = v3Ranking.RootElement.GetProperty("evidenceTier"),
            dominance = v3Ranking.RootElement.GetProperty("dominance"),
            budgets = v3Ranking.RootElement.GetProperty("budgets"),
            inputHashes = new { rankingConfigSha256 = v3RankingSha, featureContractSha256 = v3FeatureSha, shortlistManifestSha256 = v3ShortlistManifestSha },
            supportedReasonLabels = supported.Order(StringComparer.Ordinal).ToArray(),
            unsupportedV4ReasonLabels = unsupportedNames,
            compatibility = unsupportedNames.Length == 0 ? "REUSABLE_UNCHANGED" : "NEW_REASON_LABELS_HAVE_NO_FROZEN_TIER_OR_FEATURE_MAPPING",
            goldUsed = false,
            providerCalls = 0,
        });

        var blockedRows = unsupported.GroupBy(item => item.Candidate.PairId, StringComparer.Ordinal).Select(group => new
        {
            candidateId = group.Key,
            documentId = group.First().Candidate.DocumentId,
            left = group.First().Candidate.Left,
            right = group.First().Candidate.Right,
            v4Reasons = group.First().Candidate.Reasons,
            unsupportedReasons = group.Select(item => item.Reason).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            disposition = "BLOCKED_UNRANKABLE_UNDER_FROZEN_V3_CONTRACT",
        }).OrderBy(item => item.documentId, StringComparer.Ordinal).ThenBy(item => item.candidateId, StringComparer.Ordinal).ToArray();

        WriteJson(Path.Combine(output, "pruning-decisions.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4d_a_pruning_decisions",
            schemaVersion = "a99-identity-benchmark-v4d-a-pruning-decisions-v1",
            status = gate,
            pruningApplied = false,
            everyBroadCandidateHasDisposition = false,
            blockedBeforeDisposition = gate == "BLOCKED_ON_V3_PRUNER_CONTRACT_INCOMPATIBILITY",
            unsupportedV4OnlyCandidates = blockedRows,
            decisions = Array.Empty<object>(),
            goldUsed = false,
            providerCalls = 0,
        });
        WriteJson(Path.Combine(output, "shortlist.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4d_a_shortlist",
            schemaVersion = "a99-identity-benchmark-v4d-a-shortlist-v1",
            status = gate,
            candidates = Array.Empty<object>(),
            blockedBeforePruning = gate == "BLOCKED_ON_V3_PRUNER_CONTRACT_INCOMPATIBILITY",
        });
        WriteJson(Path.Combine(output, "shortlist-manifest.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4d_a_shortlist_manifest",
            schemaVersion = "a99-identity-benchmark-v4d-a-shortlist-manifest-v1",
            status = gate,
            v4BroadCandidateCount = v4Candidates.Length,
            afterDominanceCount = (int?)null,
            shortlistedCount = 0,
            pruningApplied = false,
            goldUsed = false,
            providerCalls = 0,
        });
        WriteJson(Path.Combine(output, "request-manifest.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4d_a_request_manifest",
            schemaVersion = "a99-identity-benchmark-v4d-a-request-manifest-v1",
            status = gate,
            requestCount = 0,
            exactBytesPersisted = false,
            exactBytesDeterministicallyReconstructible = false,
            requestArtifactsCreated = false,
            providerCalls = 0,
        });
        WriteJson(Path.Combine(output, "shortlist-delta-vs-v3.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4d_a_shortlist_delta",
            schemaVersion = "a99-identity-benchmark-v4d-a-shortlist-delta-v1",
            status = gate,
            computed = false,
            reason = "PRUNER_CONTRACT_INCOMPATIBILITY_BEFORE_SHORTLIST",
            v3ShortlistCount = v3ShortlistManifest.RootElement.GetProperty("shortlistedCount").GetInt32(),
            v3CandidatesRetained = Array.Empty<string>(),
            v3CandidatesDisplaced = Array.Empty<string>(),
            v4OnlyCandidatesAdmitted = Array.Empty<string>(),
        });
        WriteJson(Path.Combine(output, "scalability-report.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4d_a_scalability_report",
            schemaVersion = "a99-identity-benchmark-v4d-a-scalability-report-v1",
            status = gate,
            v3BroadCandidates = ExpectedV3Broad,
            v4BroadCandidates = ExpectedV4Broad,
            afterDominance = (int?)null,
            finalShortlist = 0,
            maxDegreeBefore = (int?)null,
            maxDegreeAfter = (int?)null,
            notComputed = "PRUNER_CONTRACT_INCOMPATIBILITY",
            goldUsed = false,
            providerCalls = 0,
        });
        WriteJson(Path.Combine(output, "firewall.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4d_a_firewall",
            schemaVersion = "a99-identity-benchmark-v4d-a-firewall-v1",
            status = gate,
            goldReadCount = 0,
            providerCalls = 0,
            modelCalls = 0,
            goldUsedForPruning = false,
            goldUsedForRanking = false,
            v4cEvaluationRead = false,
            v4bArtifactsModified = false,
            v3ArtifactsModified = false,
            pruningApplied = false,
            requestArtifactsCreated = false,
            predictionArtifactsCreated = false,
        });
        var report = new StringBuilder()
            .AppendLine("# A99 Identity Retrieval V4D-A — V3 pruning compatibility")
            .AppendLine()
            .AppendLine($"Status: **`{gate}`**")
            .AppendLine()
            .AppendLine("This phase is source-only and deliberately does not open Gold, V4C evaluation, requests, predictions, or provider execution.")
            .AppendLine()
            .AppendLine("## Freeze integrity")
            .AppendLine()
            .AppendLine($"- V4B integrity: `{freeze.Integrity}`.")
            .AppendLine($"- V4 broad: `{v4Candidates.Length:N0}`; SHA256 `{v4BroadSha}`.")
            .AppendLine($"- V3 pruning ranking config SHA256: `{v3RankingSha}`.")
            .AppendLine($"- V3 feature contract SHA256: `{v3FeatureSha}`.")
            .AppendLine()
            .AppendLine("## Compatibility")
            .AppendLine()
            .AppendLine("The frozen V3 ranker explicitly maps candidate reason labels to evidence tiers. The V4-only candidates carry new reason labels without a frozen V3 interpretation.")
            .AppendLine($"- Frozen V3-supported labels: `{string.Join(", ", supported.Order(StringComparer.Ordinal))}`.")
            .AppendLine($"- Unsupported V4 labels: `{string.Join(", ", unsupportedNames)}`.")
            .AppendLine("- No new tier, weight, feature, or bonus was invented.")
            .AppendLine()
            .AppendLine("## V4D-A result")
            .AppendLine()
            .AppendLine("Pruning, dominance, ranking, budgets, shortlist, and request freeze were not applied because the frozen V3 contract cannot rank the new V4 representation without changed semantics.")
            .AppendLine()
            .AppendLine("## Firewall")
            .AppendLine()
            .AppendLine("- GoldReadCount: `0`.")
            .AppendLine("- ProviderCalls: `0`; ModelCalls: `0`.")
            .AppendLine("- V4C evaluation was not read; V4B/V3 artifacts were not modified.")
            .AppendLine()
            .AppendLine($"## Gate\n\n`{gate}`")
            .ToString();
        File.WriteAllText(Path.Combine(output, "report.md"), report, new UTF8Encoding(false));
        Console.WriteLine($"V4D_A_STATUS={gate};V4_BROAD={v4Candidates.Length};V4_ONLY={v4Only.Length};UNSUPPORTED_LABELS={unsupportedNames.Length};PROVIDER_CALLS=0;GOLD_READS=0");
    }

    private static FreezeCheck VerifyInputs(string root, JsonDocument v4Manifest, JsonDocument v4Broad, JsonDocument v4SourceReference, JsonDocument v4Contract, JsonDocument v4Firewall,
        JsonDocument v3Manifest, JsonDocument v3Ranking, JsonDocument v3Features, JsonDocument v3ShortlistManifest, IReadOnlyList<Candidate> v3Candidates, IReadOnlyList<Candidate> v4Candidates)
    {
        var failures = new List<string>();
        var v4BroadSha = Sha256File(Full(root, V4Root + "/broad-candidate-set.json"));
        var v4SourceSha = Sha256File(Full(root, V2Root + "/source-catalog.json"));
        var v3Sha = Sha256File(Full(root, V1Root + "/candidate-set.json"));
        Check(v4Manifest.RootElement.GetProperty("status").GetString() == "READY_FOR_V4_RETRIEVAL_EVALUATION", "v4.status", failures);
        Check(v4Manifest.RootElement.GetProperty("developmentStatus").GetString() == "DEV_EXPOSED_CHALLENGER" && !v4Manifest.RootElement.GetProperty("independentGeneralizationClaim").GetBoolean(), "v4.claims", failures);
        Check(v4Manifest.RootElement.GetProperty("v4BroadCandidateCount").GetInt32() == ExpectedV4Broad && v4Candidates.Count == ExpectedV4Broad, "v4.count", failures);
        Check(v4Manifest.RootElement.GetProperty("v4AddedCandidateCount").GetInt32() == ExpectedV4Only && v4Candidates.Count(item => item.V4Only) == ExpectedV4Only, "v4.additions", failures);
        Check(v4Manifest.RootElement.GetProperty("v4BroadCandidateSetSha256").GetString() == v4BroadSha, "v4.broadSha", failures);
        Check(v4Manifest.RootElement.GetProperty("sourceCatalogSha256").GetString() == v4SourceSha, "v4.sourceSha", failures);
        Check(v4Manifest.RootElement.GetProperty("v3BroadCandidateSetSha256").GetString() == v3Sha && v3Candidates.Count == ExpectedV3Broad, "v3.broadSha", failures);
        Check(v4SourceReference.RootElement.GetProperty("sha256").GetString() == v4SourceSha, "v4.sourceReference", failures);
        Check(v4Contract.RootElement.GetProperty("providerCalls").GetInt32() == 0 && v4Contract.RootElement.GetProperty("goldReadCount").GetInt32() == 0, "v4.contract.firewall", failures);
        Check(v4Firewall.RootElement.GetProperty("providerCalls").GetInt32() == 0 && v4Firewall.RootElement.GetProperty("goldReadCount").GetInt32() == 0, "v4.firewall.counts", failures);
        Check(v3Manifest.RootElement.GetProperty("broadCandidateCount").GetInt32() == ExpectedV3Broad && v3Manifest.RootElement.GetProperty("afterDominanceCount").GetInt32() == 7659 && v3Manifest.RootElement.GetProperty("shortlistedCount").GetInt32() == 7659, "v3.manifest.contract", failures);
        Check(v3Manifest.RootElement.GetProperty("goldReadCount").GetInt32() == 0 && v3Manifest.RootElement.GetProperty("providerCalls").GetInt32() == 0, "v3.manifest.firewall", failures);
        Check(v3Ranking.RootElement.GetProperty("algorithmVersion").GetString() == "lexicographic-source-only-v1" && !v3Ranking.RootElement.GetProperty("goldUsed").GetBoolean() && !v3Ranking.RootElement.GetProperty("modelUsed").GetBoolean(), "v3.ranking", failures);
        Check(!v3Features.RootElement.GetProperty("goldUsed").GetBoolean() && !v3Features.RootElement.GetProperty("modelUsed").GetBoolean(), "v3.features", failures);
        Check(v3ShortlistManifest.RootElement.GetProperty("maxCandidatesPerOccurrence").GetInt32() == 8 && v3ShortlistManifest.RootElement.GetProperty("maxCandidatesPerDocument").GetInt32() == 12000 && v3ShortlistManifest.RootElement.GetProperty("maxCandidatesTotal").GetInt32() == 18000, "v3.budgets", failures);
        if (failures.Count > 0)
            throw new InvalidDataException("BLOCKED_ON_V4D_A_FREEZE_INTEGRITY: " + string.Join(",", failures));
        return new FreezeCheck("PASS", v4Manifest.RootElement.GetProperty("status").GetString()!, Sha256File(Full(root, V4Root + "/manifest.json")), v4BroadSha, v3Sha);
    }

    private static Candidate[] ReadCandidates(JsonDocument document)
        => document.RootElement.GetProperty("candidates").EnumerateArray().Select(item => new Candidate(
            item.TryGetProperty("candidateId", out var id) ? id.GetString()! : item.GetProperty("pairId").GetString()!,
            item.GetProperty("documentId").GetString()!,
            item.TryGetProperty("leftOccurrenceId", out var left) ? left.GetString()! : item.GetProperty("left").GetString()!,
            item.TryGetProperty("rightOccurrenceId", out var right) ? right.GetString()! : item.GetProperty("right").GetString()!,
            item.TryGetProperty("reasons", out var reasons) ? reasons.EnumerateArray().Select(reason => reason.GetString()!).ToArray() : Array.Empty<string>(),
            item.TryGetProperty("v4Only", out var v4Only) && v4Only.GetBoolean())).ToArray();

    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static JsonDocument Read(string root, string relative) => JsonDocument.Parse(File.ReadAllText(Full(root, relative)));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static void Check(bool ok, string name, ICollection<string> failures) { if (!ok) failures.Add(name); }

    private sealed record Candidate(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons, bool V4Only);
    private sealed record UnsupportedReason(Candidate Candidate, string Reason);
    private sealed record FreezeCheck(string Integrity, string V4ManifestStatus, string V4ManifestSha256, string V4BroadSha256, string V3BroadSha256);
}
