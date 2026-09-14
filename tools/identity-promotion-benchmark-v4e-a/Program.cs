using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace IdentityPromotionBenchmarkV4EA;

internal static class Program
{
    private const string V1Root = "artifacts/identity-benchmark/v1";
    private const string V2Root = "artifacts/identity-benchmark/v2";
    private const string V3Root = "artifacts/identity-benchmark/v3";
    private const string V4Root = "artifacts/identity-benchmark/v4/challenger";
    private const string OutputRoot = "artifacts/identity-benchmark/v4/pruning-challenger";
    private const int ExpectedV3Broad = 95_999;
    private const int ExpectedV3Shortlist = 7_659;
    private const int ExpectedV4Broad = 96_069;
    private const int ExpectedV4Only = 70;
    private const int MaxCandidatesPerOccurrence = 8;
    private const int MaxCandidatesPerDocument = 12_000;
    private const int MaxCandidatesTotal = 18_000;

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
            Console.Error.WriteLine($"V4E_A_ERROR={ex}");
            return 2;
        }
    }

    private static void Run(string root)
    {
        // No Gold path, V4C evaluation, relation label, model or provider artifact is read here.
        var source = Read(root, V2Root + "/source-catalog.json");
        var v3Broad = Read(root, V1Root + "/candidate-set.json");
        var v4Manifest = Read(root, V4Root + "/manifest.json");
        var v4Broad = Read(root, V4Root + "/broad-candidate-set.json");
        var v4SourceReference = Read(root, V4Root + "/source-reference.json");
        var v4Contract = Read(root, V4Root + "/retrieval-contract.json");
        var v4Firewall = Read(root, V4Root + "/firewall.json");
        var v3Manifest = Read(root, V3Root + "/manifest.json");
        var v3Ranking = Read(root, V3Root + "/candidate-ranking-config.json");
        var v3Features = Read(root, V3Root + "/source-only-feature-inventory.json");
        var v3ShortlistManifest = Read(root, V3Root + "/shortlist-manifest.json");
        var v3ShortlistBytes = File.ReadAllBytes(Full(root, V3Root + "/shortlist.json"));

        var sourceCatalog = ReadSourceCatalog(source);
        var v3Candidates = ReadCandidates(v3Broad, false);
        var v4Candidates = ReadCandidates(v4Broad, false);
        var freeze = VerifyInputs(root, source, v3Broad, v4Manifest, v4Broad, v4SourceReference, v4Contract, v4Firewall,
            v3Manifest, v3Ranking, v3Features, v3ShortlistManifest, v3Candidates, v4Candidates);

        var v3Run = Prune(v3Candidates, sourceCatalog, sourceCatalog.CatalogFingerprint);
        var v3ShortlistJson = BuildShortlistJson(sourceCatalog.CatalogFingerprint, Sha256File(Full(root, V1Root + "/candidate-set.json")), v3Run.Selected);
        var baselineByteIdentical = Encoding.UTF8.GetBytes(v3ShortlistJson).SequenceEqual(v3ShortlistBytes);
        var baselineSha = Sha256Text(v3ShortlistJson);
        var expectedBaselineSha = Sha256File(Full(root, V3Root + "/shortlist.json"));
        if (v3Run.Selected.Count != ExpectedV3Shortlist || !baselineByteIdentical || baselineSha != expectedBaselineSha)
            throw new InvalidDataException("BLOCKED_ON_V4E_V3_BASELINE_REGRESSION");

        var v4Run = Prune(v4Candidates, sourceCatalog, sourceCatalog.CatalogFingerprint);
        var output = Full(root, OutputRoot);
        Directory.CreateDirectory(output);
        var v4BroadSha = Sha256File(Full(root, V4Root + "/broad-candidate-set.json"));
        var v3BroadSha = Sha256File(Full(root, V1Root + "/candidate-set.json"));
        var sourceSha = Sha256File(Full(root, V2Root + "/source-catalog.json"));
        var rankingSha = Sha256File(Full(root, V3Root + "/candidate-ranking-config.json"));
        var featuresSha = Sha256File(Full(root, V3Root + "/source-only-feature-inventory.json"));
        var shortlistJson = BuildShortlistJson(sourceCatalog.CatalogFingerprint, v4BroadSha, v4Run.Selected);
        var shortlistPath = Path.Combine(output, "shortlist.json");
        File.WriteAllText(shortlistPath, shortlistJson, new UTF8Encoding(false));
        var shortlistSha = Sha256File(shortlistPath);
        var request = BuildRequestManifest(sourceCatalog, v4Run.Selected, shortlistSha, v4BroadSha);
        var requestPath = Path.Combine(output, "request-manifest.json");
        WriteJson(requestPath, request.Manifest);
        var requestSha = Sha256File(requestPath);
        var allReasons = v4Candidates.SelectMany(item => item.Reasons).GroupBy(item => item, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var selectedReasons = v4Run.Selected.SelectMany(item => item.Candidate.Reasons).GroupBy(item => item, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var beforeDegrees = CandidateDegrees(v4Candidates);
        var afterDegrees = CandidateDegrees(v4Run.Selected.Select(item => item.Candidate).ToArray());

        WriteJson(Path.Combine(output, "development-disclosure.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4e_a_development_disclosure",
            schemaVersion = "a99-identity-benchmark-v4e-a-development-disclosure-v1",
            developmentStatus = "DEV_EXPOSED_CHALLENGER",
            informedBy = new[] { "V3B_RETRIEVAL_FAILURE", "V4A_RETRIEVAL_FORENSICS", "V4C_BROAD_RETRIEVAL_EVALUATION", "V4D_PRUNER_CONTRACT_INCOMPATIBILITY" },
            independentGeneralizationClaim = false,
            goldReadCount = 0,
            providerCalls = 0,
            v4cEvaluationReadCount = 0,
            designExposedToV4CEvaluation = true,
        });
        WriteJson(Path.Combine(output, "v3-baseline-compatibility.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4e_a_v3_baseline_compatibility",
            schemaVersion = "a99-identity-benchmark-v4e-a-v3-baseline-compatibility-v1",
            status = "PASS",
            v3BroadCandidateCount = v3Candidates.Length,
            expectedShortlistCount = ExpectedV3Shortlist,
            actualShortlistCount = v3Run.Selected.Count,
            frozenV3ShortlistSha256 = expectedBaselineSha,
            challengerReproductionSha256 = baselineSha,
            byteIdentical = baselineByteIdentical,
            candidateDispositionComparable = true,
            v3ArtifactsModified = false,
            goldUsed = false,
            providerCalls = 0,
        });
        WriteJson(Path.Combine(output, "source-evidence-contract.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4e_a_source_evidence_contract",
            schemaVersion = "a99-identity-benchmark-v4e-a-source-evidence-contract-v1",
            status = "FROZEN_SOURCE_ONLY_CHALLENGER",
            exactNormalizedTextEquivalent = "existing V3 NORMALIZED_TEXT_AFFINITY; unchanged priority and dominance behavior",
            adjacency = "existing V3 ADJACENT_ORDER; unchanged priority and dominance behavior",
            explicitLexicalVariantEquivalent = "bounded terminal acronym evidence; ranking evidence only",
            structuralHeadingKeyEquivalent = "same-document normalized SESSION/SECTION key; ranking evidence only",
            explicitContinuationVariant = "bounded terminal continuation marker; ranking evidence only",
            compositeEvidence = "one canonical candidate with independent boolean dimensions; no duplicate and no opaque bonus",
            oldSemanticsPreserved = true,
            relationInference = false,
            goldUsed = false,
            modelUsed = false,
        });
        WriteJson(Path.Combine(output, "source-evidence-distribution.json"), BuildEvidenceDistribution(v4Candidates, sourceCatalog));
        WriteJson(Path.Combine(output, "ranking-contract.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4e_a_ranking_contract",
            schemaVersion = "a99-identity-benchmark-v4e-a-ranking-contract-v1",
            algorithmVersion = "v4e-backward-compatible-evidence-vector-v1",
            primaryOrder = new[] { "legacyV3EvidenceTierDescending", "continuationEvidenceDescending", "lexicalVariantEvidenceDescending", "structuralKeyEvidenceDescending", "sourceOrderDistanceAscending", "candidateIdAscending" },
            oldEvidenceTier = new { tier3 = "ADJACENT_ORDER + NORMALIZED_TEXT_AFFINITY", tier2 = "ADJACENT_ORDER", tier1 = "NORMALIZED_TEXT_AFFINITY" },
            dominance = "A dominates B at an endpoint only when A is no worse on normalized affinity, adjacency, lexical variant, structural key, continuation and distance, with at least one strict improvement; heterogeneous evidence remains incomparable.",
            incomparableEvidence = "preserved until fixed operational budget; deterministic fallback order is documented above",
            budgets = new { maxCandidatesPerOccurrence = MaxCandidatesPerOccurrence, maxCandidatesPerDocument = MaxCandidatesPerDocument, maxCandidatesTotal = MaxCandidatesTotal },
            reasonLabelsAreScores = false,
            goldUsed = false,
            modelUsed = false,
        });
        WriteJson(Path.Combine(output, "pruning-decisions.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4e_a_pruning_decisions",
            schemaVersion = "a99-identity-benchmark-v4e-a-pruning-decisions-v1",
            status = "READY_FOR_V4E_PRUNING_GOLD_EVALUATION",
            decisions = v4Run.Decisions,
            goldUsed = false,
            providerCalls = 0,
        });
        WriteJson(Path.Combine(output, "shortlist-manifest.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4e_a_shortlist_manifest",
            schemaVersion = "a99-identity-benchmark-v4e-a-shortlist-manifest-v1",
            status = "READY_FOR_V4E_PRUNING_GOLD_EVALUATION",
            sourceCatalogFingerprint = sourceCatalog.CatalogFingerprint,
            broadCandidateSetSha256 = v4BroadSha,
            shortlistSha256 = shortlistSha,
            broadCandidateCount = v4Candidates.Length,
            afterDominanceCount = v4Run.Eligible.Count,
            shortlistedCount = v4Run.Selected.Count,
            maxCandidatesPerOccurrence = MaxCandidatesPerOccurrence,
            maxCandidatesPerDocument = MaxCandidatesPerDocument,
            maxCandidatesTotal = MaxCandidatesTotal,
            goldUsed = false,
            providerCalls = 0,
        });
        WriteJson(Path.Combine(output, "shortlist-delta-vs-v3.json"), BuildDelta(v3Run, v4Run, v3Candidates, v4Candidates));
        WriteJson(Path.Combine(output, "scalability-report.json"), BuildScalability(v3Candidates, v3Run, v4Candidates, v4Run, allReasons, selectedReasons, beforeDegrees, afterDegrees));
        WriteJson(Path.Combine(output, "firewall.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4e_a_firewall",
            schemaVersion = "a99-identity-benchmark-v4e-a-firewall-v1",
            goldReadCount = 0,
            providerCalls = 0,
            modelCalls = 0,
            v4cEvaluationReadCount = 0,
            goldUsedForRanking = false,
            goldUsedForPruning = false,
            goldUsedForThreshold = false,
            relationLabelsConsumed = false,
            v3BaselineByteCompatibility = baselineByteIdentical,
            v3ArtifactsModified = false,
            v4bArtifactsModified = false,
            everyBroadCandidateHasDisposition = v4Run.Decisions.Count == v4Candidates.Length,
            budgetExceeded = afterDegrees.Values.Any(item => item > MaxCandidatesPerOccurrence) || v4Run.Selected.Count > MaxCandidatesTotal || v4Run.Selected.GroupBy(item => item.Candidate.DocumentId).Any(group => group.Count() > MaxCandidatesPerDocument),
            requestShaCheckedBeforeNetwork = request.AllEntriesAccepted,
            exactBytesPersisted = false,
            exactBytesDeterministicallyReconstructible = true,
        });
        WriteJson(Path.Combine(output, "manifest.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4e_a_manifest",
            schemaVersion = "a99-identity-benchmark-v4e-a-manifest-v1",
            status = "READY_FOR_V4E_PRUNING_GOLD_EVALUATION",
            developmentStatus = "DEV_EXPOSED_CHALLENGER",
            informedBy = new[] { "V3B_RETRIEVAL_FAILURE", "V4A_RETRIEVAL_FORENSICS", "V4C_BROAD_RETRIEVAL_EVALUATION", "V4D_PRUNER_CONTRACT_INCOMPATIBILITY" },
            independentGeneralizationClaim = false,
            v4BroadCandidateCount = v4Candidates.Length,
            v4BroadCandidateSetSha256 = v4BroadSha,
            afterDominanceCount = v4Run.Eligible.Count,
            shortlistedCount = v4Run.Selected.Count,
            shortlistSha256 = shortlistSha,
            requestCount = request.Manifest.RequestCount,
            requestManifestSha256 = requestSha,
            v3BroadCandidateCount = v3Candidates.Length,
            v3BaselineShortlistCount = v3Run.Selected.Count,
            v3BaselineShortlistByteIdentical = baselineByteIdentical,
            v3PruningContractHashes = new { rankingConfigSha256 = rankingSha, featureContractSha256 = featuresSha, shortlistManifestSha256 = Sha256File(Full(root, V3Root + "/shortlist-manifest.json")) },
            sourceCatalogSha256 = sourceSha,
            sourceCatalogFingerprint = sourceCatalog.CatalogFingerprint,
            goldReadCount = 0,
            providerCalls = 0,
            v4cEvaluationReadCount = 0,
            nextGate = "READY_FOR_V4E_PRUNING_GOLD_EVALUATION",
        });
        File.WriteAllText(Path.Combine(output, "report.md"), BuildMarkdown(v3Run, v4Run, v4Candidates, v4BroadSha, shortlistSha, requestSha, freeze, request), new UTF8Encoding(false));
        Console.WriteLine($"V4E_A_STATUS=READY_FOR_V4E_PRUNING_GOLD_EVALUATION;V3_BASELINE=PASS;V4_BROAD={v4Candidates.Length};AFTER_DOMINANCE={v4Run.Eligible.Count};SHORTLIST={v4Run.Selected.Count};REQUESTS={request.Manifest.RequestCount};PROVIDER_CALLS=0;GOLD_READS=0;V4C_READS=0");
    }

    private static FreezeCheck VerifyInputs(string root, JsonDocument source, JsonDocument v3Broad, JsonDocument v4Manifest, JsonDocument v4Broad, JsonDocument v4SourceReference, JsonDocument v4Contract, JsonDocument v4Firewall,
        JsonDocument v3Manifest, JsonDocument v3Ranking, JsonDocument v3Features, JsonDocument v3ShortlistManifest, IReadOnlyList<Candidate> v3Candidates, IReadOnlyList<Candidate> v4Candidates)
    {
        var failures = new List<string>();
        var sourceSha = Sha256File(Full(root, V2Root + "/source-catalog.json"));
        var v3Sha = Sha256File(Full(root, V1Root + "/candidate-set.json"));
        var v4Sha = Sha256File(Full(root, V4Root + "/broad-candidate-set.json"));
        Check(v4Manifest.RootElement.GetProperty("status").GetString() == "READY_FOR_V4_RETRIEVAL_EVALUATION", "v4.status", failures);
        Check(v4Manifest.RootElement.GetProperty("v4BroadCandidateCount").GetInt32() == ExpectedV4Broad && v4Candidates.Count == ExpectedV4Broad, "v4.count", failures);
        Check(v4Manifest.RootElement.GetProperty("v4AddedCandidateCount").GetInt32() == ExpectedV4Only && v4Candidates.Count(item => item.V4Only) == ExpectedV4Only, "v4.additions", failures);
        Check(v4Manifest.RootElement.GetProperty("v4BroadCandidateSetSha256").GetString() == v4Sha, "v4.broad.sha", failures);
        Check(v4Manifest.RootElement.GetProperty("sourceCatalogSha256").GetString() == sourceSha && v4SourceReference.RootElement.GetProperty("sha256").GetString() == sourceSha, "v4.source.sha", failures);
        Check(v4Manifest.RootElement.GetProperty("v3BroadCandidateSetSha256").GetString() == v3Sha && v3Candidates.Count == ExpectedV3Broad, "v3.broad.sha", failures);
        Check(v4Contract.RootElement.GetProperty("goldReadCount").GetInt32() == 0 && v4Contract.RootElement.GetProperty("providerCalls").GetInt32() == 0, "v4.contract.firewall", failures);
        Check(v4Firewall.RootElement.GetProperty("goldReadCount").GetInt32() == 0 && v4Firewall.RootElement.GetProperty("providerCalls").GetInt32() == 0, "v4.firewall", failures);
        Check(v3Manifest.RootElement.GetProperty("broadCandidateCount").GetInt32() == ExpectedV3Broad && v3Manifest.RootElement.GetProperty("shortlistedCount").GetInt32() == ExpectedV3Shortlist, "v3.manifest", failures);
        Check(v3Manifest.RootElement.GetProperty("goldReadCount").GetInt32() == 0 && v3Manifest.RootElement.GetProperty("providerCalls").GetInt32() == 0, "v3.firewall", failures);
        Check(v3Ranking.RootElement.GetProperty("algorithmVersion").GetString() == "lexicographic-source-only-v1" && !v3Ranking.RootElement.GetProperty("goldUsed").GetBoolean() && !v3Ranking.RootElement.GetProperty("modelUsed").GetBoolean(), "v3.ranking", failures);
        Check(!v3Features.RootElement.GetProperty("goldUsed").GetBoolean() && !v3Features.RootElement.GetProperty("modelUsed").GetBoolean(), "v3.features", failures);
        Check(v3ShortlistManifest.RootElement.GetProperty("maxCandidatesPerOccurrence").GetInt32() == MaxCandidatesPerOccurrence && v3ShortlistManifest.RootElement.GetProperty("maxCandidatesPerDocument").GetInt32() == MaxCandidatesPerDocument && v3ShortlistManifest.RootElement.GetProperty("maxCandidatesTotal").GetInt32() == MaxCandidatesTotal, "v3.budgets", failures);
        if (failures.Count > 0) throw new InvalidDataException("BLOCKED_ON_V4E_INPUT_FREEZE_INTEGRITY: " + string.Join(",", failures));
        return new FreezeCheck("PASS", Sha256File(Full(root, V4Root + "/manifest.json")), v4Sha, v3Sha);
    }

    private static PruneResult Prune(IReadOnlyList<Candidate> candidates, SourceCatalog source, string sourceFingerprint)
    {
        var byDoc = candidates.GroupBy(item => item.DocumentId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var ranked = new List<RankedCandidate>(candidates.Count);
        foreach (var doc in byDoc.Keys.Order(StringComparer.Ordinal))
        {
            var nodes = source.NodesByDocument[doc].ToDictionary(item => item.NodeId, StringComparer.Ordinal);
            var local = byDoc[doc].Select(candidate => Rank(candidate, nodes)).OrderBy(item => item, RankedComparer.Instance).Select((item, index) => item with { DocumentRank = index + 1 }).ToArray();
            ranked.AddRange(local);
        }
        var dominance = ComputeDominance(ranked);
        var eligible = ranked.Where(item => !dominance.DominatedPairIds.Contains(item.Candidate.PairId)).ToArray();
        var selected = SelectBounded(eligible, out var budgetReasons);
        var selectedIds = selected.Select(item => item.Candidate.PairId).ToHashSet(StringComparer.Ordinal);
        var decisions = ranked.Select(item =>
        {
            var selectedHere = selectedIds.Contains(item.Candidate.PairId);
            var dominated = dominance.DominatedPairIds.Contains(item.Candidate.PairId);
            var decision = selectedHere ? "SHORTLISTED" : dominated ? "PRUNED_DOMINATED" : "PRUNED_BUDGET";
            var reason = selectedHere ? "WITHIN_SOURCE_ONLY_BOUNDED_SELECTION" : dominated ? "DOMINATED_FOR_BOTH_OCCURRENCES" : budgetReasons.GetValueOrDefault(item.Candidate.PairId, "OPERATIONAL_BUDGET");
            return new PruningDecision(item.Candidate.PairId, item.Candidate.DocumentId, item.Candidate.Left, item.Candidate.Right, item.Candidate.Reasons, item.Features, item.DocumentRank, decision, reason, dominance.DominatedAtLeft.Contains(item.Candidate.PairId), dominance.DominatedAtRight.Contains(item.Candidate.PairId));
        }).OrderBy(item => item.DocumentId, StringComparer.Ordinal).ThenBy(item => item.Rank).ThenBy(item => item.CandidateId, StringComparer.Ordinal).ToArray();
        return new(ranked, eligible, selected, decisions);
    }

    private static RankedCandidate Rank(Candidate candidate, IReadOnlyDictionary<string, SourceNode> nodes)
    {
        if (!nodes.TryGetValue(candidate.Left, out var left) || !nodes.TryGetValue(candidate.Right, out var right)) throw new InvalidDataException("V4E_UNKNOWN_SOURCE_OCCURRENCE");
        var adjacent = candidate.Reasons.Contains("ADJACENT_ORDER", StringComparer.Ordinal);
        var normalized = candidate.Reasons.Contains("NORMALIZED_TEXT_AFFINITY", StringComparer.Ordinal);
        var lexical = candidate.Reasons.Contains("TERMINAL_ACRONYM_VARIANT", StringComparer.Ordinal);
        var structural = candidate.Reasons.Contains("SHARED_STRUCTURAL_HEADING_KEY", StringComparer.Ordinal);
        var continuation = candidate.Reasons.Contains("EXPLICIT_CONTINUATION_VARIANT", StringComparer.Ordinal);
        var legacyTier = adjacent && normalized ? 3 : adjacent ? 2 : normalized ? 1 : 0;
        return new(candidate,
            new RankFeatures(legacyTier, adjacent, normalized, Math.Abs(left.DocumentOrder - right.DocumentOrder)),
            new EvidenceVector(adjacent, normalized, lexical, structural, continuation, Math.Abs(left.DocumentOrder - right.DocumentOrder)), 0);
    }

    private static DominanceResult ComputeDominance(IReadOnlyList<RankedCandidate> ranked)
    {
        var left = new HashSet<string>(StringComparer.Ordinal);
        var right = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in ranked.SelectMany(item => new[] { (item.Candidate.Left, true, item), (item.Candidate.Right, false, item) }).GroupBy(item => item.Item1, StringComparer.Ordinal))
        {
            var entries = group.Select(item => item.Item3).DistinctBy(item => item.Candidate.PairId).ToArray();
            foreach (var item in entries)
            foreach (var other in entries)
            {
                if (item.Candidate.PairId == other.Candidate.PairId || !Dominates(other.Evidence, item.Evidence)) continue;
                foreach (var occurrence in group.Where(entry => entry.Item3.Candidate.PairId == item.Candidate.PairId))
                    if (occurrence.Item2) left.Add(item.Candidate.PairId); else right.Add(item.Candidate.PairId);
                break;
            }
        }
        return new(left, right, ranked.Where(item => left.Contains(item.Candidate.PairId) && right.Contains(item.Candidate.PairId)).Select(item => item.Candidate.PairId).ToHashSet(StringComparer.Ordinal));
    }

    private static bool Dominates(EvidenceVector a, EvidenceVector b)
    {
        var noWorse = (!b.HasNormalizedTextAffinity || a.HasNormalizedTextAffinity) && (!b.HasAdjacentOrder || a.HasAdjacentOrder) && (!b.HasLexicalVariant || a.HasLexicalVariant) && (!b.HasStructuralKey || a.HasStructuralKey) && (!b.HasContinuation || a.HasContinuation) && a.DocumentOrderDistance <= b.DocumentOrderDistance;
        var strict = a.HasNormalizedTextAffinity != b.HasNormalizedTextAffinity || a.HasAdjacentOrder != b.HasAdjacentOrder || a.HasLexicalVariant != b.HasLexicalVariant || a.HasStructuralKey != b.HasStructuralKey || a.HasContinuation != b.HasContinuation || a.DocumentOrderDistance < b.DocumentOrderDistance;
        return noWorse && strict;
    }

    private static IReadOnlyList<RankedCandidate> SelectBounded(IReadOnlyList<RankedCandidate> eligible, out Dictionary<string, string> reasons)
    {
        reasons = new(StringComparer.Ordinal);
        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        var docs = new Dictionary<string, int>(StringComparer.Ordinal);
        var selected = new List<RankedCandidate>();
        foreach (var item in eligible.OrderBy(item => item.Candidate.DocumentId, StringComparer.Ordinal).ThenBy(item => item, RankedComparer.Instance))
        {
            var left = degree.GetValueOrDefault(item.Candidate.Left);
            var right = degree.GetValueOrDefault(item.Candidate.Right);
            var doc = docs.GetValueOrDefault(item.Candidate.DocumentId);
            if (selected.Count >= MaxCandidatesTotal || doc >= MaxCandidatesPerDocument || left >= MaxCandidatesPerOccurrence || right >= MaxCandidatesPerOccurrence)
            {
                reasons[item.Candidate.PairId] = left >= MaxCandidatesPerOccurrence || right >= MaxCandidatesPerOccurrence ? "OCCURRENCE_FANOUT_BUDGET" : doc >= MaxCandidatesPerDocument ? "DOCUMENT_CALL_BUDGET" : "TOTAL_CALL_BUDGET";
                continue;
            }
            selected.Add(item);
            degree[item.Candidate.Left] = left + 1;
            degree[item.Candidate.Right] = right + 1;
            docs[item.Candidate.DocumentId] = doc + 1;
        }
        return selected;
    }

    private static string BuildShortlistJson(string fingerprint, string candidateSha, IReadOnlyList<RankedCandidate> selected)
        => JsonSerializer.Serialize(new
        {
            artifactKind = "a99_identity_benchmark_v3a_shortlist",
            schemaVersion = "a99-identity-benchmark-v3a-shortlist",
            sourceCatalogFingerprint = fingerprint,
            candidateSetSha256 = candidateSha,
            candidateGenerationUnchanged = true,
            candidates = selected.Select(item => new { item.Candidate.PairId, item.Candidate.DocumentId, item.Candidate.Left, item.Candidate.Right, item.Candidate.Reasons, features = item.Features, rank = item.DocumentRank }).OrderBy(item => item.DocumentId, StringComparer.Ordinal).ThenBy(item => item.rank).ThenBy(item => item.PairId, StringComparer.Ordinal).ToArray(),
        }, JsonOptions) + Environment.NewLine;

    private static RequestBuildResult BuildRequestManifest(SourceCatalog source, IReadOnlyList<RankedCandidate> selected, string shortlistSha, string candidateSha)
    {
        var templates = source.NodesByDocument.ToDictionary(item => item.Key, item => HdsaCanonicalPairVerifierRequestBuilder.CreateTemplate(source.CatalogFingerprint, item.Value.Select(node => new HdsaIdentityRoleNodeInput(node.NodeId, [node.NodeId], node.Text, node.DocumentOrder, "UNAVAILABLE", false)).ToArray()), StringComparer.Ordinal);
        var entries = new List<RequestEntry>(selected.Count);
        long logicalBytes = 0;
        foreach (var item in selected.OrderBy(item => item.Candidate.DocumentId, StringComparer.Ordinal).ThenBy(item => item.DocumentRank).ThenBy(item => item.Candidate.PairId, StringComparer.Ordinal))
        {
            var built = templates[item.Candidate.DocumentId].Build(new HdsaIdentityCandidatePair(item.Candidate.PairId, item.Candidate.Left, item.Candidate.Right));
            var guard = HdsaCanonicalPairVerifierRequestGuard.Verify(built, built.Sha256, built.Utf8Bytes.Length);
            if (!guard.Accepted) throw new InvalidDataException("V4E_REQUEST_GUARD_FAILED");
            var rebuilt = templates[item.Candidate.DocumentId].Build(new HdsaIdentityCandidatePair(item.Candidate.PairId, item.Candidate.Left, item.Candidate.Right));
            if (rebuilt.Sha256 != built.Sha256 || !rebuilt.Utf8Bytes.SequenceEqual(built.Utf8Bytes)) throw new InvalidDataException("V4E_REQUEST_RECONSTRUCTION_MISMATCH");
            logicalBytes += built.Utf8Bytes.Length;
            entries.Add(new(item.Candidate.PairId, item.Candidate.PairId, item.Candidate.DocumentId, item.Candidate.Left, item.Candidate.Right, built.Sha256, built.Utf8Bytes.Length, new RequestReconstruction("../v2/source-catalog.json", source.CatalogFingerprint, HdsaCanonicalPairVerifierRequestBuilder.Version, shortlistSha, false, false)));
        }
        return new(new RequestManifest("a99_identity_benchmark_v4e_a_request_manifest", "a99-identity-benchmark-v4e-a", source.CatalogFingerprint, candidateSha, shortlistSha, HdsaGlobalIdentityRetrieveVerifyContract.Version, HdsaCanonicalPairVerifierRequestBuilder.Version, false, false, entries.Count, entries), logicalBytes, logicalBytes / 4, true);
    }

    private static object BuildEvidenceDistribution(IReadOnlyList<Candidate> candidates, SourceCatalog source)
    {
        var configurations = candidates.GroupBy(item => string.Join("+", item.Reasons.Order(StringComparer.Ordinal)), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var newCandidates = candidates.Where(item => item.V4Only).ToArray();
        var degree = CandidateDegrees(candidates);
        return new
        {
            artifactKind = "a99_identity_benchmark_v4e_a_source_evidence_distribution",
            sourceOnly = true,
            candidateCount = candidates.Count,
            configurationCounts = configurations,
            v4OnlyConfigurationCounts = newCandidates.GroupBy(item => string.Join("+", item.Reasons.Order(StringComparer.Ordinal)), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            degree = DegreeStats(degree),
            negativeControls = new
            {
                sameStructuralKeyDifferentText = newCandidates.Count(item => item.Reasons.Contains("SHARED_STRUCTURAL_HEADING_KEY", StringComparer.Ordinal) && source.NodesByDocument[item.DocumentId].Single(node => node.NodeId == item.Left).Text != source.NodesByDocument[item.DocumentId].Single(node => node.NodeId == item.Right).Text),
                adjacentUnrelatedText = candidates.Count(item => item.Reasons.Contains("ADJACENT_ORDER", StringComparer.Ordinal) && !item.Reasons.Contains("NORMALIZED_TEXT_AFFINITY", StringComparer.Ordinal)),
                acronymEvidencePairs = newCandidates.Count(item => item.Reasons.Contains("TERMINAL_ACRONYM_VARIANT", StringComparer.Ordinal)),
            },
            goldUsed = false,
            providerCalls = 0,
        };
    }

    private static object BuildDelta(PruneResult v3, PruneResult v4, IReadOnlyList<Candidate> v3Candidates, IReadOnlyList<Candidate> v4Candidates)
    {
        var old = v3.Selected.Select(item => item.Candidate.PairId).ToHashSet(StringComparer.Ordinal);
        var current = v4.Selected.Select(item => item.Candidate.PairId).ToHashSet(StringComparer.Ordinal);
        var v4Only = v4Candidates.Where(item => item.V4Only).Select(item => item.PairId).ToHashSet(StringComparer.Ordinal);
        return new
        {
            artifactKind = "a99_identity_benchmark_v4e_a_shortlist_delta",
            schemaVersion = "a99-identity-benchmark-v4e-a-shortlist-delta-v1",
            v3ShortlistCount = v3.Selected.Count,
            v4ShortlistCount = v4.Selected.Count,
            v3CandidatesRetained = old.Intersect(current, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            v3CandidatesDisplaced = old.Except(current, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            v4OnlyCandidatesAdmitted = v4Only.Intersect(current, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            v4OnlyCandidatesRejected = v4Only.Except(current, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            displacedRankingComparisons = v3Candidates.Where(item => old.Contains(item.PairId) && !current.Contains(item.PairId)).Select(item => new { candidateId = item.PairId, reason = "V4_SOURCE_ONLY_CANDIDATES_OR_FROZEN_ORDER_DISPLACED_IT; SEE_PRUNING_DECISIONS", sourceOnly = true }).ToArray(),
            goldUsed = false,
        };
    }

    private static object BuildScalability(IReadOnlyList<Candidate> v3Candidates, PruneResult v3, IReadOnlyList<Candidate> v4Candidates, PruneResult v4, IReadOnlyDictionary<string, int> reasons, IReadOnlyDictionary<string, int> selectedReasons, IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after)
    {
        var v4OnlyIds = v4Candidates.Where(item => item.V4Only).Select(item => item.PairId).ToHashSet(StringComparer.Ordinal);
        return new
        {
            artifactKind = "a99_identity_benchmark_v4e_a_scalability_report",
            v3 = new { broad = v3Candidates.Count, afterDominance = v3.Eligible.Count, shortlist = v3.Selected.Count },
            v4 = new { broad = v4Candidates.Count, afterDominance = v4.Eligible.Count, shortlist = v4.Selected.Count, reductionPercent = Math.Round(100d * (1d - v4.Selected.Count / (double)v4Candidates.Count), 4) },
            v4Only = new { total = v4OnlyIds.Count, shortlisted = v4.Selected.Count(item => item.Candidate.V4Only), dominated = v4.Decisions.Count(item => v4OnlyIds.Contains(item.CandidateId) && item.Decision == "PRUNED_DOMINATED"), budgetPruned = v4.Decisions.Count(item => v4OnlyIds.Contains(item.CandidateId) && item.Decision == "PRUNED_BUDGET") },
            reasonBreakdown = new { broad = reasons, shortlisted = selectedReasons },
            candidateDegree = new { before = DegreeStats(before), after = DegreeStats(after), beforeMax = before.Values.DefaultIfEmpty(0).Max(), afterMax = after.Values.DefaultIfEmpty(0).Max() },
            perDocument = v4Candidates.GroupBy(item => item.DocumentId, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => new { documentId = group.Key, broad = group.Count(), shortlisted = v4.Selected.Count(item => item.Candidate.DocumentId == group.Key), afterDominance = v4.Eligible.Count(item => item.Candidate.DocumentId == group.Key) }).ToArray(),
            dominance = new { dominated = v4Candidates.Count - v4.Eligible.Count, pairDominated = v4Candidates.Count - v4.Eligible.Count },
            goldUsed = false,
            providerCalls = 0,
        };
    }

    private static string BuildMarkdown(PruneResult v3, PruneResult v4, IReadOnlyList<Candidate> v4Candidates, string broadSha, string shortlistSha, string requestSha, FreezeCheck freeze, RequestBuildResult request)
    {
        var v4OnlyIds = v4Candidates.Where(item => item.V4Only).Select(item => item.PairId).ToHashSet(StringComparer.Ordinal);
        var v4OnlyShortlisted = v4.Selected.Count(item => item.Candidate.V4Only);
        var v4OnlyDominated = v4.Decisions.Count(item => v4OnlyIds.Contains(item.CandidateId) && item.Decision == "PRUNED_DOMINATED");
        var v4OnlyBudgetPruned = v4.Decisions.Count(item => v4OnlyIds.Contains(item.CandidateId) && item.Decision == "PRUNED_BUDGET");
        return string.Join(Environment.NewLine, new[]
        {
            "# A99 Identity Retrieval V4E-A - backward-compatible pruning challenger",
            "",
            "Status: READY_FOR_V4E_PRUNING_GOLD_EVALUATION",
            "",
            "DEV-exposed source-only challenger; Gold, V4C evaluation, model output, and provider execution were not used.",
            "V3 broad: " + v3.Ranked.Count + "; reproduced shortlist: " + v3.Selected.Count + "; byte/SHA identical: true.",
            "V4 broad: " + v4Candidates.Count + "; after dominance: " + v4.Eligible.Count + "; final shortlist: " + v4.Selected.Count + ".",
            "V4 broad SHA256: " + broadSha + "; shortlist SHA256: " + shortlistSha + "; requests: " + request.Manifest.RequestCount + "; request SHA256: " + requestSha + ".",
            "V4-only: total " + v4OnlyIds.Count + "; shortlisted " + v4OnlyShortlisted + "; dominated " + v4OnlyDominated + "; budget-pruned " + v4OnlyBudgetPruned + ".",
            "Existing exact-normalized and adjacency evidence retain V3 precedence; new dimensions are source-only evidence and do not encode relation semantics.",
            "Raw request bytes were not persisted; deterministic reconstruction/parity guard passed.",
            "GoldReadCount=0; V4CEvaluationReadCount=0; ModelCalls=0; ProviderCalls=0; independentGeneralizationClaim=false.",
            "",
            "Gate: READY_FOR_V4E_PRUNING_GOLD_EVALUATION",
        }) + Environment.NewLine;
    }

    private static string BuildMarkdownLegacy(PruneResult v3, PruneResult v4, IReadOnlyList<Candidate> v4Candidates, string broadSha, string shortlistSha, string requestSha, FreezeCheck freeze, RequestBuildResult request)
    {
        var v4OnlyIds = v4Candidates.Where(item => item.V4Only).Select(item => item.PairId).ToHashSet(StringComparer.Ordinal);
        var lines = new List<string>
        {
            "# A99 Identity Retrieval V4E-A — backward-compatible pruning challenger",
            "",
            "Status: **`READY_FOR_V4E_PRUNING_GOLD_EVALUATION`**",
            "",
            "This is a DEV-exposed, source-only challenger. Gold, V4C evaluation, model output, requests from prior phases, and provider execution were not used as inputs.",
            "",
            "## Development disclosure",
            "",
            "- Informed by: `V3B_RETRIEVAL_FAILURE`, `V4A_RETRIEVAL_FORENSICS`, `V4C_BROAD_RETRIEVAL_EVALUATION`, `V4D_PRUNER_CONTRACT_INCOMPATIBILITY`.",
            "- `independentGeneralizationClaim=false`; `GoldReadCount=0`; `V4CEvaluationReadCount=0`; `ProviderCalls=0`.",
            "",
            "## V3 backward compatibility",
            "",
            $"- V3 broad: `{v3.Ranked.Count:N0}`; challenger shortlist: `{v3.Selected.Count:N0}`.",
            "- Frozen V3A shortlist reproduction: **PASS**, byte-identical and SHA-identical.",
            "- V3A semantics are preserved for old candidates; no V3 artifacts were modified.",
            "",
            "## V4E evidence vector",
            "",
            "- Existing exact-normalized and adjacency evidence retain their V3 precedence.",
            "- Terminal acronym, structural heading key, and explicit continuation are independent source-evidence dimensions only.",
            "- Composite evidence remains one candidate; heterogeneous evidence is incomparable under dominance until deterministic fallback ranking is needed.",
            "- No reason label implies a semantic relation and no Gold-specific slot/bonus exists.",
            "",
            "## V4 pruning",
            "",
            $"- Broad: `{v4Candidates.Count:N0}`; after dominance: `{v4.Eligible.Count:N0}`; final shortlist: `{v4.Selected.Count:N0}`.",
            $"- Reduction: `{100d * (1d - v4.Selected.Count / (double)v4Candidates.Count):F4}%`.",
            $"- V4 broad SHA256: `{broadSha}`; shortlist SHA256: `{shortlistSha}`.",
            $"- Request freeze: `{request.Manifest.RequestCount:N0}` deterministic entries; SHA256 `{requestSha}`; raw bytes persisted: `false`.",
            $"- Request reconstruction/parity guard: `{request.AllEntriesAccepted}`.",
            "",
           "## V4-only accounting",
           "",
        $"- Total: `{v4Candidates.Count(item => item.V4Only)}`; shortlisted: `{v4.Selected.Count(item => item.Candidate.V4Only)}`; dominated: `{v4.Decisions.Count(item => item.Decision == "PRUNED_DOMINATED" && v4Candidates.Any(candidate => candidate.PairId == item.CandidateId && candidate.V4Only))}`; budget-pruned: `{v4.Decisions.Count(item => item.Decision == "PRUNED_BUDGET" && v4Candidates.Any(candidate => candidate.PairId == item.CandidateId && candidate.V4Only))}`.",
           $"- Total: `{v4OnlyIds.Count}`; shortlisted: `{v4.Selected.Count(item => item.Candidate.V4Only)}`; dominated: `{v4.Decisions.Count(item => v4OnlyIds.Contains(item.CandidateId) && item.Decision == "PRUNED_DOMINATED")}`; budget-pruned: `{v4.Decisions.Count(item => v4OnlyIds.Contains(item.CandidateId) && item.Decision == "PRUNED_BUDGET")}`.",
            "- This source-only phase deliberately does not identify Gold cases or interpret candidate labels as relations.",
            "",
            "## Gate",
            "",
            "`READY_FOR_V4E_PRUNING_GOLD_EVALUATION`",
        };
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static SourceCatalog ReadSourceCatalog(JsonDocument document)
    {
        var nodes = document.RootElement.GetProperty("sourceOccurrences").EnumerateArray().Select(item => new SourceNode(item.GetProperty("nodeId").GetString()!, item.GetProperty("text").GetString()!, item.GetProperty("documentOrder").GetInt32())).ToArray();
        var byDoc = nodes.GroupBy(item => item.NodeId.Split(':', 2)[0], StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.OrderBy(item => item.DocumentOrder).ThenBy(item => item.NodeId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        return new(byDoc, document.RootElement.GetProperty("catalogFingerprint").GetString()!);
    }

    private static Candidate[] ReadCandidates(JsonDocument document, bool defaultV4Only)
        => document.RootElement.GetProperty("candidates").EnumerateArray().Select(item => new Candidate(
            item.TryGetProperty("candidateId", out var id) ? id.GetString()! : item.GetProperty("pairId").GetString()!,
            item.GetProperty("documentId").GetString()!,
            item.TryGetProperty("leftOccurrenceId", out var left) ? left.GetString()! : item.GetProperty("left").GetString()!,
            item.TryGetProperty("rightOccurrenceId", out var right) ? right.GetString()! : item.GetProperty("right").GetString()!,
            item.TryGetProperty("reasons", out var reasons) ? reasons.EnumerateArray().Select(reason => reason.GetString()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : Array.Empty<string>(),
            item.TryGetProperty("v4Only", out var v4Only) ? v4Only.GetBoolean() : defaultV4Only)).ToArray();

    private static IReadOnlyDictionary<string, int> CandidateDegrees(IEnumerable<Candidate> candidates)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in candidates) { result[item.Left] = result.GetValueOrDefault(item.Left) + 1; result[item.Right] = result.GetValueOrDefault(item.Right) + 1; }
        return result;
    }

    private static object DegreeStats(IReadOnlyDictionary<string, int> values)
    {
        var sorted = values.Values.OrderBy(item => item).ToArray();
        return new { p50 = Percentile(sorted, .5), p90 = Percentile(sorted, .9), p95 = Percentile(sorted, .95), p99 = Percentile(sorted, .99), max = sorted.DefaultIfEmpty(0).Max() };
    }

    private static int Percentile(int[] values, double p) => values.Length == 0 ? 0 : values[Math.Min(values.Length - 1, (int)Math.Floor(p * (values.Length - 1)))];
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static JsonDocument Read(string root, string relative) => JsonDocument.Parse(File.ReadAllText(Full(root, relative)));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static void Check(bool ok, string name, ICollection<string> failures) { if (!ok) failures.Add(name); }

    private sealed class RankedComparer : IComparer<RankedCandidate>
    {
        public static readonly RankedComparer Instance = new();
        public int Compare(RankedCandidate? x, RankedCandidate? y)
        {
            if (ReferenceEquals(x, y)) return 0; if (x is null) return -1; if (y is null) return 1;
            var result = y.Features.EvidenceTier.CompareTo(x.Features.EvidenceTier); if (result != 0) return result;
            result = y.Evidence.HasContinuation.CompareTo(x.Evidence.HasContinuation); if (result != 0) return result;
            result = y.Evidence.HasLexicalVariant.CompareTo(x.Evidence.HasLexicalVariant); if (result != 0) return result;
            result = y.Evidence.HasStructuralKey.CompareTo(x.Evidence.HasStructuralKey); if (result != 0) return result;
            result = x.Evidence.DocumentOrderDistance.CompareTo(y.Evidence.DocumentOrderDistance); if (result != 0) return result;
            return string.Compare(x.Candidate.PairId, y.Candidate.PairId, StringComparison.Ordinal);
        }
    }

    private sealed record SourceCatalog(IReadOnlyDictionary<string, SourceNode[]> NodesByDocument, string CatalogFingerprint);
    private sealed record SourceNode(string NodeId, string Text, int DocumentOrder);
    private sealed record Candidate(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons, bool V4Only);
    private sealed record RankFeatures(int EvidenceTier, bool HasAdjacentOrder, bool HasNormalizedTextAffinity, int DocumentOrderDistance);
    private sealed record EvidenceVector(bool HasAdjacentOrder, bool HasNormalizedTextAffinity, bool HasLexicalVariant, bool HasStructuralKey, bool HasContinuation, int DocumentOrderDistance);
    private sealed record RankedCandidate(Candidate Candidate, RankFeatures Features, EvidenceVector Evidence, int DocumentRank);
    private sealed record DominanceResult(HashSet<string> DominatedAtLeft, HashSet<string> DominatedAtRight, HashSet<string> DominatedPairIds);
    private sealed record PruningDecision(string CandidateId, string DocumentId, string Left, string Right, IReadOnlyList<string> BroadGenerationReasons, RankFeatures RankFeatures, int Rank, string Decision, string DecisionReason, bool DominatedAtLeft, bool DominatedAtRight);
    private sealed record PruneResult(IReadOnlyList<RankedCandidate> Ranked, IReadOnlyList<RankedCandidate> Eligible, IReadOnlyList<RankedCandidate> Selected, IReadOnlyList<PruningDecision> Decisions);
    private sealed record FreezeCheck(string Integrity, string V4ManifestSha256, string V4BroadSha256, string V3BroadSha256);
    private sealed record RequestManifest(string ArtifactKind, string BenchmarkVersion, string SourceCatalogFingerprint, string CandidateSetSha256, string ShortlistSha256, string RequestContract, string CanonicalBuilderVersion, bool GoldDerivedInput, bool ExactBytesPersisted, int RequestCount, IReadOnlyList<RequestEntry> Requests);
    private sealed record RequestEntry(string RequestId, string CandidatePairId, string DocumentId, string Left, string Right, string RequestSha256, int RequestBytes, RequestReconstruction Reconstruction);
    private sealed record RequestReconstruction(string SourceCatalogPath, string SourceCatalogFingerprint, string CanonicalBuilderVersion, string ShortlistSha256, bool GoldDerivedInput, bool ExactBytesPersisted);
    private sealed record RequestBuildResult(RequestManifest Manifest, long LogicalBytes, long EstimatedTokens, bool AllEntriesAccepted);
}
