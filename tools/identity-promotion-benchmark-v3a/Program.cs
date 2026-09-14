using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace IdentityPromotionBenchmarkV3A;

internal static class Program
{
    private const string Version = "a99-identity-promotion-benchmark-v3a";
    private const string V1Root = "artifacts/identity-benchmark/v1";
    private const string V2Root = "artifacts/identity-benchmark/v2";
    private const string V3Root = "artifacts/identity-benchmark/v3";
    private const int ExpectedBroadCandidateCount = 95_999;
    private const int MaxCandidatesPerOccurrence = 8;
    private const int MaxCandidatesPerDocument = 12_000;
    private const int MaxCandidatesTotal = 18_000;
    private const string BuilderSource = "src/DocxHeaderExtractor.Core/Models/HdsaCanonicalPairVerifierRequestBuilder.cs";
    private const string GuardSource = "src/DocxHeaderExtractor.Core/Models/HdsaCanonicalPairVerifierRequestGuard.cs";

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
            Build(root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V3A_ERROR={ex}");
            return 2;
        }
    }

    private static void Build(string root)
    {
        var v2Root = Full(root, V2Root);
        var output = Full(root, V3Root);
        Directory.CreateDirectory(output);

        var v2Manifest = Read<V2Manifest>(Path.Combine(v2Root, "manifest.json"));
        var candidatePath = Full(root, V1Root + "/candidate-set.json");
        var sourcePath = Full(root, V2Root + "/source-catalog.json");
        var candidates = Read<CandidateSet>(candidatePath);
        var source = Read<SourceCatalog>(sourcePath);
        var candidateSha = Sha256File(candidatePath);
        var sourceSha = Sha256File(sourcePath);
        if (candidates.CandidateCount != ExpectedBroadCandidateCount || candidates.Candidates.Count != ExpectedBroadCandidateCount ||
            v2Manifest.CandidateCount != ExpectedBroadCandidateCount || !string.Equals(v2Manifest.CandidateSetSha256, candidateSha, StringComparison.Ordinal))
            throw new InvalidDataException("BLOCKED_ON_V3_INPUT_DRIFT");
        if (candidates.GoldUsed || candidates.GoldDerivedInput || v2Manifest.ProviderCalls != 0 || v2Manifest.GoldReadCountBeforePredictionFreeze != 0)
            throw new InvalidDataException("V3A_INPUT_FIREWALL_FAILED");

        var nodesByDocument = source.SourceOccurrences
            .GroupBy(item => DocumentOf(item.NodeId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(item => item.DocumentOrder).ThenBy(item => item.NodeId, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var candidatesByDocument = candidates.Candidates
            .GroupBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(item => item.PairId, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        var inventory = BuildFeatureInventory(source, candidates);
        WriteJson(Path.Combine(output, "source-only-feature-inventory.json"), inventory);

        WriteJson(Path.Combine(output, "broad-candidate-reference.json"), new
        {
            artifactKind = "a99_identity_benchmark_v3a_broad_candidate_reference",
            schemaVersion = "a99-identity-benchmark-v3a-broad-candidate-reference",
            source = "../v1/candidate-set.json",
            v2Reference = "../v2/candidate-set-reference.json",
            sha256 = candidateSha,
            candidateCount = candidates.CandidateCount,
            sourceCatalogFingerprint = source.CatalogFingerprint,
            goldUsed = false,
            modelUsed = false,
            candidateGenerationChanged = false,
        });

        var rankingConfig = BuildRankingConfig(root, candidateSha, sourceSha, inventory.FeatureContractSha256);
        WriteJson(Path.Combine(output, "candidate-ranking-config.json"), rankingConfig);

        var ranked = new List<RankedCandidate>(candidates.CandidateCount);
        foreach (var document in source.SourceDocuments.OrderBy(item => item.DocumentId, StringComparer.Ordinal))
        {
            var nodes = nodesByDocument[document.DocumentId].ToDictionary(item => item.NodeId, StringComparer.Ordinal);
            var documentCandidates = candidatesByDocument[document.DocumentId]
                .Select(candidate => Rank(candidate, nodes))
                .OrderBy(item => item, RankedCandidateComparer.Instance)
                .Select((item, index) => item with { DocumentRank = index + 1 })
                .ToArray();
            ranked.AddRange(documentCandidates);
        }

        var dominance = ComputeDominance(ranked);
        var eligible = ranked.Where(item => !dominance.DominatedPairIds.Contains(item.Candidate.PairId)).ToArray();
        var selected = SelectBoundedShortlist(eligible, out var budgetDisposition);
        var selectedIds = selected.Select(item => item.Candidate.PairId).ToHashSet(StringComparer.Ordinal);

        var decisions = ranked.Select(item =>
        {
            var isSelected = selectedIds.Contains(item.Candidate.PairId);
            var dominated = dominance.DominatedPairIds.Contains(item.Candidate.PairId);
            var decision = isSelected ? "SHORTLISTED" : dominated ? "PRUNED_DOMINATED" : "PRUNED_BUDGET";
            var reason = isSelected ? "WITHIN_SOURCE_ONLY_BOUNDED_SELECTION" :
                dominated ? "DOMINATED_FOR_BOTH_OCCURRENCES" : budgetDisposition.GetValueOrDefault(item.Candidate.PairId, "OPERATIONAL_BUDGET");
            return new PruningDecision(
                item.Candidate.PairId, item.Candidate.DocumentId, item.Candidate.Left, item.Candidate.Right,
                item.Candidate.Reasons, item.Features, item.DocumentRank, decision, reason,
                dominance.DominatedAtLeft.Contains(item.Candidate.PairId),
                dominance.DominatedAtRight.Contains(item.Candidate.PairId));
        }).OrderBy(item => item.DocumentId, StringComparer.Ordinal).ThenBy(item => item.Rank).ThenBy(item => item.CandidateId, StringComparer.Ordinal).ToArray();
        WriteJson(Path.Combine(output, "candidate-pruning-decisions.json"), new
        {
            artifactKind = "a99_identity_benchmark_v3a_candidate_pruning_decisions",
            schemaVersion = "a99-identity-benchmark-v3a-candidate-pruning-decisions",
            goldUsed = false,
            decisions,
        });

        var shortlist = new
        {
            artifactKind = "a99_identity_benchmark_v3a_shortlist",
            schemaVersion = "a99-identity-benchmark-v3a-shortlist",
            sourceCatalogFingerprint = source.CatalogFingerprint,
            candidateSetSha256 = candidateSha,
            candidateGenerationUnchanged = true,
            candidates = selected.Select(item => new
            {
                item.Candidate.PairId,
                item.Candidate.DocumentId,
                item.Candidate.Left,
                item.Candidate.Right,
                item.Candidate.Reasons,
                item.Features,
                rank = item.DocumentRank,
            }).OrderBy(item => item.DocumentId, StringComparer.Ordinal).ThenBy(item => item.rank).ThenBy(item => item.PairId, StringComparer.Ordinal).ToArray(),
        };
        var shortlistPath = Path.Combine(output, "shortlist.json");
        WriteJson(shortlistPath, shortlist);
        var shortlistSha = Sha256File(shortlistPath);

        var shortlistManifest = new
        {
            artifactKind = "a99_identity_benchmark_v3a_shortlist_manifest",
            schemaVersion = "a99-identity-benchmark-v3a-shortlist-manifest",
            sourceCatalogFingerprint = source.CatalogFingerprint,
            broadCandidateSetSha256 = candidateSha,
            shortlistSha256 = shortlistSha,
            broadCandidateCount = candidates.CandidateCount,
            afterDominanceCount = eligible.Length,
            shortlistedCount = selected.Count,
            maxCandidatesPerOccurrence = MaxCandidatesPerOccurrence,
            maxCandidatesPerDocument = MaxCandidatesPerDocument,
            maxCandidatesTotal = MaxCandidatesTotal,
            goldUsed = false,
            providerCalls = 0,
        };
        WriteJson(Path.Combine(output, "shortlist-manifest.json"), shortlistManifest);

        var requestManifest = BuildRequestManifest(root, output, source, selected, nodesByDocument, candidateSha, shortlistSha);
        var requestManifestPath = Path.Combine(output, "request-manifest.json");
        WriteJson(requestManifestPath, requestManifest.Manifest);
        var requestManifestSha = Sha256File(requestManifestPath);

        var dryRun = ExecuteManifestDryRun(root, output, source, candidates, selected, nodesByDocument, requestManifest.Manifest.Requests);
        var broadReasonCounts = candidates.Candidates.SelectMany(item => item.Reasons).GroupBy(item => item, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var selectedReasonCounts = selected.SelectMany(item => item.Candidate.Reasons).GroupBy(item => item, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var degreesBefore = CandidateDegrees(candidates.Candidates);
        var degreesAfter = CandidateDegrees(selected.Select(item => item.Candidate).ToArray());
        var scalability = BuildScalabilityReport(source, candidates, eligible, selected, broadReasonCounts, selectedReasonCounts, degreesBefore, degreesAfter, requestManifest.LogicalBytes, requestManifest.EstimatedTokens, dominance);
        WriteJson(Path.Combine(output, "scalability-report.json"), scalability);

        var builderHash = Sha256File(Full(root, BuilderSource));
        var guardHash = Sha256File(Full(root, GuardSource));
        var firewall = new
        {
            artifactKind = "a99_identity_benchmark_v3a_firewall",
            schemaVersion = "a99-identity-benchmark-v3a-firewall",
            goldReadCount = 0,
            providerCalls = 0,
            goldArtifactLoaded = false,
            goldDerivedInput = false,
            modelDerivedFeatures = false,
            goldDerivedFeatures = false,
            candidateGenerationChanged = false,
            pruningAdded = true,
            contextProjectionChanged = false,
            identityPromotionGateChanged = false,
            everyBroadCandidateHasDisposition = decisions.Length == candidates.CandidateCount,
            shortlistBudgetExceeded = degreesAfter.Values.Any(item => item > MaxCandidatesPerOccurrence) ||
                selected.Count > MaxCandidatesTotal || selected.GroupBy(item => item.Candidate.DocumentId, StringComparer.Ordinal).Any(group => group.Count() > MaxCandidatesPerDocument),
            requestShaCheckedBeforeNetwork = dryRun.AllEntriesAccepted,
            dryRunProviderCalls = dryRun.ProviderCalls,
            canonicalBuilderSourceSha256 = builderHash,
            requestGuardSourceSha256 = guardHash,
        };
        WriteJson(Path.Combine(output, "firewall.json"), firewall);

        var manifest = new
        {
            artifactKind = "a99_identity_benchmark_manifest_v3a",
            schemaVersion = Version,
            status = "READY_FOR_V3_RETRIEVAL_EVALUATION",
            execution = "PROVIDER_PROHIBITED_UNTIL_V3B_GOLD_EVALUATION",
            sourceDocuments = source.SourceDocuments.Count,
            sourceUnits = source.SourceOccurrences.Count,
            broadCandidateCount = candidates.CandidateCount,
            broadCandidateSetSha256 = candidateSha,
            afterDominanceCount = eligible.Length,
            shortlistedCount = selected.Count,
            shortlistSha256 = shortlistSha,
            requestCount = requestManifest.Manifest.Requests.Count,
            requestManifestSha256 = requestManifestSha,
            sourceCatalogFingerprint = source.CatalogFingerprint,
            sourceCatalogSha256 = sourceSha,
            candidateRankingConfigSha256 = Sha256File(Path.Combine(output, "candidate-ranking-config.json")),
            featureInventorySha256 = Sha256File(Path.Combine(output, "source-only-feature-inventory.json")),
            canonicalRequestBuilderVersion = HdsaCanonicalPairVerifierRequestBuilder.Version,
            canonicalRequestBuilderSourceSha256 = builderHash,
            requestGuardSourceSha256 = guardHash,
            exactBytesPersisted = false,
            exactBytesDeterministicallyReconstructible = true,
            goldReadCount = 0,
            providerCalls = 0,
            nextGate = "READY_FOR_V3_RETRIEVAL_EVALUATION",
        };
        WriteJson(Path.Combine(output, "manifest.json"), manifest);

        var report = BuildMarkdown(source, candidates, eligible, selected, candidateSha, shortlistSha, requestManifestSha, requestManifest, scalability, dryRun, dominance);
        File.WriteAllText(Path.Combine(output, "report.md"), report, new UTF8Encoding(false));
        Console.WriteLine($"V3A_STATUS=READY_FOR_V3_RETRIEVAL_EVALUATION;BROAD={candidates.CandidateCount};AFTER_DOMINANCE={eligible.Length};SHORTLIST={selected.Count};REQUESTS={requestManifest.Manifest.Requests.Count};PROVIDER_CALLS=0;GOLD_READS=0");
    }

    private static FeatureInventory BuildFeatureInventory(SourceCatalog source, CandidateSet candidates)
    {
        var features = new[]
        {
            new FeatureDefinition("candidateGenerationReasons", "SOURCE_DIRECT", "existing frozen broad candidate artifact"),
            new FeatureDefinition("normalizedTextAffinity", "SOURCE_DERIVED", "existing generator reason; retained as signal, never blanket veto"),
            new FeatureDefinition("adjacentOrder", "SOURCE_DERIVED", "existing generator reason"),
            new FeatureDefinition("documentOrderDistance", "SOURCE_DERIVED", "absolute distance between source-owned documentOrder values"),
            new FeatureDefinition("sourceTextSha256", "SOURCE_DERIVED", "hash of source-owned text for diagnostics"),
            new FeatureDefinition("occurrenceDegree", "SOURCE_DERIVED", "degree in frozen broad candidate graph"),
            new FeatureDefinition("pageDistance", "UNAVAILABLE", "not present in frozen identity source catalog"),
            new FeatureDefinition("styleLayoutCompatibility", "UNAVAILABLE", "not present in frozen identity source catalog"),
            new FeatureDefinition("sectionOwnership", "UNAVAILABLE", "not present in frozen identity source catalog"),
            new FeatureDefinition("modelScore", "UNAVAILABLE", "forbidden in v3A"),
            new FeatureDefinition("goldRelationLabel", "UNAVAILABLE", "forbidden in v3A"),
        };
        var json = JsonSerializer.Serialize(features, JsonOptions);
        return new("a99_identity_benchmark_v3a_source_only_feature_inventory",
            "a99-identity-benchmark-v3a-source-only-feature-inventory", source.CatalogFingerprint,
            candidates.CandidateCount, false, false, features, Sha256Text(json));
    }

    private static object BuildRankingConfig(string root, string candidateSha, string sourceSha, string featureSha)
        => new
        {
            artifactKind = "a99_identity_benchmark_v3a_ranking_config",
            schemaVersion = "a99-identity-benchmark-v3a-ranking-config",
            algorithmVersion = "lexicographic-source-only-v1",
            dimensions = new[] { "evidenceTierDescending", "documentOrderDistanceAscending", "pairIdAscending" },
            evidenceTier = new
            {
                tier3 = "ADJACENT_ORDER + NORMALIZED_TEXT_AFFINITY",
                tier2 = "ADJACENT_ORDER",
                tier1 = "NORMALIZED_TEXT_AFFINITY",
                blanketNormalizedTextAffinityVeto = false,
            },
            dominance = new
            {
                dimensions = new[] { "hasNormalizedTextAffinity", "hasAdjacentOrder", "documentOrderDistance" },
                rule = "A dominates B only within a shared occurrence when A is no worse on every dimension and strictly better on one; pair-level pruning requires domination at both endpoints.",
                goldIndependent = true,
            },
            budgets = new
            {
                maxCandidatesPerOccurrence = MaxCandidatesPerOccurrence,
                maxCandidatesPerDocument = MaxCandidatesPerDocument,
                maxCandidatesTotal = MaxCandidatesTotal,
                selectionRule = "fixed operational caps declared before V3B; no Gold search",
            },
            direction = new
            {
                pairCanonicalization = "frozen broad candidate left/right order",
                symmetricRepeat = "one canonical pair request",
                continuation = "direction remains verifier response field; no reverse duplicate request",
            },
            inputCandidateSetSha256 = candidateSha,
            inputSourceCatalogSha256 = sourceSha,
            featureContractSha256 = featureSha,
            modelUsed = false,
            goldUsed = false,
        };

    private static RankedCandidate Rank(Candidate candidate, IReadOnlyDictionary<string, SourceOccurrence> nodes)
    {
        if (!nodes.TryGetValue(candidate.Left, out var left) || !nodes.TryGetValue(candidate.Right, out var right))
            throw new InvalidDataException("V3A_UNKNOWN_SOURCE_OCCURRENCE");
        var hasAdjacent = candidate.Reasons.Contains("ADJACENT_ORDER", StringComparer.Ordinal);
        var hasAffinity = candidate.Reasons.Contains("NORMALIZED_TEXT_AFFINITY", StringComparer.Ordinal);
        var distance = Math.Abs(right.DocumentOrder - left.DocumentOrder);
        var tier = hasAdjacent && hasAffinity ? 3 : hasAdjacent ? 2 : hasAffinity ? 1 : 0;
        if (tier == 0) throw new InvalidDataException("V3A_UNRANKABLE_CANDIDATE");
        return new(candidate, new RankFeatures(tier, hasAdjacent, hasAffinity, distance), 0);
    }

    private static DominanceResult ComputeDominance(IReadOnlyList<RankedCandidate> ranked)
    {
        var byOccurrence = ranked.SelectMany(item => new[]
            {
                (Occurrence: item.Candidate.Left, IsLeft: true, Item: item),
                (Occurrence: item.Candidate.Right, IsLeft: false, Item: item),
            })
            .GroupBy(item => item.Occurrence, StringComparer.Ordinal);
        var leftDominated = new HashSet<string>(StringComparer.Ordinal);
        var rightDominated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in byOccurrence)
        {
            var items = group.Select(item => item.Item).DistinctBy(item => item.Candidate.PairId).ToArray();
            var minDistance = new int[2, 2];
            for (var affinity = 0; affinity <= 1; affinity++)
                for (var adjacent = 0; adjacent <= 1; adjacent++)
                    minDistance[affinity, adjacent] = int.MaxValue;
            foreach (var item in items)
            {
                var a = item.Features.HasNormalizedTextAffinity ? 1 : 0;
                var s = item.Features.HasAdjacentOrder ? 1 : 0;
                minDistance[a, s] = Math.Min(minDistance[a, s], item.Features.DocumentOrderDistance);
            }
            foreach (var item in items)
            {
                var a = item.Features.HasNormalizedTextAffinity ? 1 : 0;
                var s = item.Features.HasAdjacentOrder ? 1 : 0;
                var dominated = false;
                for (var otherA = a; otherA <= 1 && !dominated; otherA++)
                    for (var otherS = s; otherS <= 1 && !dominated; otherS++)
                    {
                        var distance = minDistance[otherA, otherS];
                        if (distance == int.MaxValue || distance > item.Features.DocumentOrderDistance)
                            continue;
                        var strict = otherA > a || otherS > s || distance < item.Features.DocumentOrderDistance;
                        if (strict) dominated = true;
                    }
                if (!dominated) continue;
                foreach (var occurrence in group.Where(entry => entry.Item.Candidate.PairId == item.Candidate.PairId))
                {
                    if (occurrence.IsLeft) leftDominated.Add(item.Candidate.PairId);
                    else rightDominated.Add(item.Candidate.PairId);
                }
            }
        }
        var pairDominated = ranked.Where(item => leftDominated.Contains(item.Candidate.PairId) && rightDominated.Contains(item.Candidate.PairId))
            .Select(item => item.Candidate.PairId).ToHashSet(StringComparer.Ordinal);
        return new(leftDominated, rightDominated, pairDominated);
    }

    private static IReadOnlyList<RankedCandidate> SelectBoundedShortlist(
        IReadOnlyList<RankedCandidate> eligible,
        out Dictionary<string, string> budgetDisposition)
    {
        budgetDisposition = new(StringComparer.Ordinal);
        var degrees = new Dictionary<string, int>(StringComparer.Ordinal);
        var documentCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var selected = new List<RankedCandidate>();
        foreach (var item in eligible.OrderBy(item => item.DocumentId, StringComparer.Ordinal).ThenBy(item => item, RankedCandidateComparer.Instance))
        {
            var leftDegree = degrees.GetValueOrDefault(item.Candidate.Left);
            var rightDegree = degrees.GetValueOrDefault(item.Candidate.Right);
            var docCount = documentCounts.GetValueOrDefault(item.Candidate.DocumentId);
            if (selected.Count >= MaxCandidatesTotal || docCount >= MaxCandidatesPerDocument ||
                leftDegree >= MaxCandidatesPerOccurrence || rightDegree >= MaxCandidatesPerOccurrence)
            {
                budgetDisposition[item.Candidate.PairId] = leftDegree >= MaxCandidatesPerOccurrence || rightDegree >= MaxCandidatesPerOccurrence
                    ? "OCCURRENCE_FANOUT_BUDGET"
                    : docCount >= MaxCandidatesPerDocument ? "DOCUMENT_CALL_BUDGET" : "TOTAL_CALL_BUDGET";
                continue;
            }
            selected.Add(item);
            degrees[item.Candidate.Left] = leftDegree + 1;
            degrees[item.Candidate.Right] = rightDegree + 1;
            documentCounts[item.Candidate.DocumentId] = docCount + 1;
        }
        return selected;
    }

    private static RequestBuildResult BuildRequestManifest(
        string root,
        string output,
        SourceCatalog source,
        IReadOnlyList<RankedCandidate> selected,
        IReadOnlyDictionary<string, SourceOccurrence[]> nodesByDocument,
        string candidateSha,
        string shortlistSha)
    {
        var templates = source.SourceDocuments.ToDictionary(
            item => item.DocumentId,
            item => HdsaCanonicalPairVerifierRequestBuilder.CreateTemplate(
                source.CatalogFingerprint,
                nodesByDocument[item.DocumentId].Select(node => new HdsaIdentityRoleNodeInput(
                    node.NodeId, [node.NodeId], node.Text, node.DocumentOrder, "UNAVAILABLE", false)).ToArray()),
            StringComparer.Ordinal);
        var entries = new List<RequestEntry>(selected.Count);
        long logicalBytes = 0;
        foreach (var item in selected.OrderBy(item => item.DocumentId, StringComparer.Ordinal).ThenBy(item => item.DocumentRank).ThenBy(item => item.Candidate.PairId, StringComparer.Ordinal))
        {
            var target = new HdsaIdentityCandidatePair(item.Candidate.PairId, item.Candidate.Left, item.Candidate.Right);
            var built = templates[item.Candidate.DocumentId].Build(target);
            var guard = HdsaCanonicalPairVerifierRequestGuard.Verify(built, built.Sha256, built.Utf8Bytes.Length);
            if (!guard.Accepted) throw new InvalidDataException("V3A_REQUEST_GUARD_FAILED");
            logicalBytes += built.Utf8Bytes.Length;
            entries.Add(new(
                item.Candidate.PairId, item.Candidate.PairId, item.Candidate.DocumentId, item.Candidate.Left, item.Candidate.Right,
                built.Sha256, built.Utf8Bytes.Length,
                new RequestReconstruction("../v2/source-catalog.json", source.CatalogFingerprint,
                    HdsaCanonicalPairVerifierRequestBuilder.Version, shortlistSha, false, false)));
        }
        var manifest = new RequestManifest(
            "a99_identity_promotion_request_manifest_v3a", Version, source.CatalogFingerprint, candidateSha, shortlistSha,
            HdsaGlobalIdentityRetrieveVerifyContract.Version, HdsaCanonicalPairVerifierRequestBuilder.Version,
            false, false, entries.Count, entries);
        return new(manifest, logicalBytes, logicalBytes / 4);
    }

    private static DryRunResult ExecuteManifestDryRun(
        string root,
        string output,
        SourceCatalog source,
        CandidateSet candidates,
        IReadOnlyList<RankedCandidate> selected,
        IReadOnlyDictionary<string, SourceOccurrence[]> nodesByDocument,
        IReadOnlyList<RequestEntry> entries)
    {
        var manifest = Read<RequestManifest>(Path.Combine(output, "request-manifest.json"));
        if (manifest.Requests.Count != selected.Count || manifest.GoldDerivedInput || manifest.ExactBytesPersisted)
            throw new InvalidDataException("V3A_REQUEST_MANIFEST_INVALID");
        var templates = source.SourceDocuments.ToDictionary(
            item => item.DocumentId,
            item => HdsaCanonicalPairVerifierRequestBuilder.CreateTemplate(
                source.CatalogFingerprint,
                nodesByDocument[item.DocumentId].Select(node => new HdsaIdentityRoleNodeInput(
                    node.NodeId, [node.NodeId], node.Text, node.DocumentOrder, "UNAVAILABLE", false)).ToArray()),
            StringComparer.Ordinal);
        var candidateById = candidates.Candidates.ToDictionary(item => item.PairId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.CandidatePairId) || !candidateById.TryGetValue(entry.CandidatePairId, out var candidate) ||
                !string.Equals(entry.DocumentId, candidate.DocumentId, StringComparison.Ordinal) ||
                !string.Equals(entry.Left, candidate.Left, StringComparison.Ordinal) ||
                !string.Equals(entry.Right, candidate.Right, StringComparison.Ordinal))
                throw new InvalidDataException("V3A_REQUEST_MANIFEST_INPUT_MISMATCH");
            var built = templates[entry.DocumentId].Build(new HdsaIdentityCandidatePair(candidate.PairId, candidate.Left, candidate.Right));
            var guard = HdsaCanonicalPairVerifierRequestGuard.Verify(built, entry.RequestSha256, entry.RequestBytes);
            if (!guard.Accepted) throw new InvalidDataException("V3A_REQUEST_MANIFEST_HASH_MISMATCH");
        }
        return new(entries.Count, 0, true);
    }

    private static object BuildScalabilityReport(SourceCatalog source, CandidateSet candidates, IReadOnlyList<RankedCandidate> eligible,
        IReadOnlyList<RankedCandidate> selected, IReadOnlyDictionary<string, int> broadReasons, IReadOnlyDictionary<string, int> selectedReasons,
        IReadOnlyDictionary<string, int> degreesBefore, IReadOnlyDictionary<string, int> degreesAfter, long logicalBytes, long tokens, DominanceResult dominance)
        => new
        {
            artifactKind = "a99_identity_benchmark_v3a_scalability_report",
            broadCandidates = candidates.CandidateCount,
            afterDominance = eligible.Count,
            shortlisted = selected.Count,
            reductionPercent = Math.Round(100d * (1d - selected.Count / (double)candidates.CandidateCount), 4),
            estimatedFutureProviderCalls = selected.Count,
            logicalRequestBytes = logicalBytes,
            estimatedInputTokens = tokens,
            candidateDegrees = new
            {
                before = DegreeStats(degreesBefore),
                after = DegreeStats(degreesAfter),
                beforeMax = degreesBefore.Values.DefaultIfEmpty(0).Max(),
                afterMax = degreesAfter.Values.DefaultIfEmpty(0).Max(),
            },
            perDocument = source.SourceDocuments.OrderBy(item => item.DocumentId, StringComparer.Ordinal).Select(document => new
            {
                documentId = document.DocumentId,
                sourceUnits = document.OccurrenceCount,
                broadCandidates = candidates.Candidates.Count(item => item.DocumentId == document.DocumentId),
                shortlisted = selected.Count(item => item.Candidate.DocumentId == document.DocumentId),
            }).ToArray(),
            reasonBreakdown = new { broad = broadReasons, shortlisted = selectedReasons },
            dominance = new { before = candidates.CandidateCount, after = eligible.Count, dominated = candidates.CandidateCount - eligible.Count, pairDominated = dominance.DominatedPairIds.Count },
            goldUsed = false,
            modelUsed = false,
        };

    private static object DegreeStats(IReadOnlyDictionary<string, int> values)
    {
        var sorted = values.Values.OrderBy(item => item).ToArray();
        return new
        {
            p50 = Percentile(sorted, .50), p90 = Percentile(sorted, .90), p95 = Percentile(sorted, .95),
            p99 = Percentile(sorted, .99), max = sorted.DefaultIfEmpty(0).Max(),
        };
    }

    private static int Percentile(int[] sorted, double percentile) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(percentile * (sorted.Length - 1)))];

    private static IReadOnlyDictionary<string, int> CandidateDegrees(IEnumerable<Candidate> candidates)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            result[candidate.Left] = result.GetValueOrDefault(candidate.Left) + 1;
            result[candidate.Right] = result.GetValueOrDefault(candidate.Right) + 1;
        }
        return result;
    }

    private static string BuildMarkdown(SourceCatalog source, CandidateSet candidates, IReadOnlyList<RankedCandidate> eligible,
        IReadOnlyList<RankedCandidate> selected, string candidateSha, string shortlistSha, string requestSha,
        RequestBuildResult request, object scalability, DryRunResult dryRun, DominanceResult dominance)
    {
        var lines = new List<string>
        {
            "# A99 Identity Benchmark v3A — Source-only shortlist freeze",
            "",
            "Status: **`READY_FOR_V3_RETRIEVAL_EVALUATION`**",
            "",
            "This phase is source-only. It read the frozen v2 broad candidate lineage, made 0 provider calls, opened 0 Gold files, and did not change request context or verifier semantics.",
            "",
            "## Input",
            "",
            $"- Source units: `{source.SourceOccurrences.Count:N0}` across `{source.SourceDocuments.Count}` documents.",
            $"- Broad candidates: `{candidates.CandidateCount:N0}`; SHA256 `{candidateSha}` (same as v2).",
            "- Candidate generation/configuration: unchanged; no Gold/model output was read.",
            "",
            "## Source-only ranking contract",
            "",
            "- Lexicographic order: evidence tier descending, source document-order distance ascending, stable pair ID ascending.",
            "- Tier 3: adjacency plus normalized-text affinity; tier 2: adjacency; tier 1: normalized-text affinity.",
            "- `NORMALIZED_TEXT_AFFINITY` is retained as a signal, never used as a blanket veto.",
            "- Dominance is source-only Pareto dominance within shared occurrence dimensions; pair-level pruning requires domination at both endpoints.",
            $"- Fixed operational budgets: `{MaxCandidatesPerOccurrence}` per occurrence, `{MaxCandidatesPerDocument:N0}` per document, `{MaxCandidatesTotal:N0}` total.",
            "- Canonical pair ordering is retained; symmetric identity pairs are requested once and continuation direction remains a verifier response field.",
            "",
            "## Reduction",
            "",
            $"- After dominance: `{eligible.Count:N0}`; dominated: `{candidates.CandidateCount - eligible.Count:N0}` (pair-dominated: `{dominance.DominatedPairIds.Count:N0}`).",
            $"- Shortlisted: `{selected.Count:N0}`; reduction: `{100d * (1d - selected.Count / (double)candidates.CandidateCount):F4}%`.",
            $"- Logical request bytes: `{request.LogicalBytes:N0}`; estimated input tokens at 4 bytes/token: `{request.EstimatedTokens:N0}`.",
            $"- Manifest dry-run: `{dryRun.CheckedEntries:N0}` entries, rejected `{dryRun.RejectedEntries}`, provider calls `{dryRun.ProviderCalls}`.",
            "- Detailed degree/reason/per-document statistics are in `scalability-report.json`.",
            "",
            "## Request freeze",
            "",
            $"- Shortlist SHA256: `{shortlistSha}`; request manifest SHA256: `{requestSha}`.",
            "- Requests use `HdsaCanonicalPairVerifierRequestBuilder` from v2; exact bodies are not persisted and are deterministically reconstructible.",
            "- Reconstructed SHA and byte length are checked before any hypothetical transport.",
            "",
            "## Firewall",
            "",
            "- Gold: not loaded; relation labels/confidence/diagnostics are not inputs.",
            "- Model: not used for features or ranking.",
            "- Context projection: unchanged.",
            "- Every broad candidate has an explicit disposition: `SHORTLISTED`, `PRUNED_DOMINATED`, or `PRUNED_BUDGET`.",
            "",
            "## Gate",
            "",
            "`READY_FOR_V3_RETRIEVAL_EVALUATION`",
            "",
            "Next step: open the frozen user-reviewed identity Gold only in a separate v3B task and score candidate recall. Do not tune this frozen v3A artifact during that evaluation.",
        };
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private sealed class RankedCandidateComparer : IComparer<RankedCandidate>
    {
        public static readonly RankedCandidateComparer Instance = new();
        public int Compare(RankedCandidate? x, RankedCandidate? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var result = y.Features.EvidenceTier.CompareTo(x.Features.EvidenceTier);
            if (result != 0) return result;
            result = x.Features.DocumentOrderDistance.CompareTo(y.Features.DocumentOrderDistance);
            if (result != 0) return result;
            return string.Compare(x.Candidate.PairId, y.Candidate.PairId, StringComparison.Ordinal);
        }
    }

    private sealed record V2Manifest(int CandidateCount, string CandidateSetSha256, int ProviderCalls, int GoldReadCountBeforePredictionFreeze);
    private sealed record SourceCatalog(IReadOnlyList<SourceDocument> SourceDocuments, IReadOnlyList<SourceOccurrence> SourceOccurrences, string CatalogFingerprint);
    private sealed record SourceDocument(string DocumentId, string SourceSha256, int OccurrenceCount);
    private sealed record SourceOccurrence(string NodeId, string Text, int DocumentOrder);
    private sealed record CandidateSet(int CandidateCount, IReadOnlyList<Candidate> Candidates, bool GoldUsed, bool GoldDerivedInput);
    private sealed record Candidate(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons);
    private sealed record FeatureDefinition(string Name, string Availability, string Derivation);
    private sealed record FeatureInventory(string ArtifactKind, string SchemaVersion, string SourceCatalogFingerprint,
        int CandidateCount, bool GoldUsed, bool ModelUsed, IReadOnlyList<FeatureDefinition> Features, string FeatureContractSha256);
    private sealed record RankFeatures(int EvidenceTier, bool HasAdjacentOrder, bool HasNormalizedTextAffinity, int DocumentOrderDistance);
    private sealed record RankedCandidate(Candidate Candidate, RankFeatures Features, int DocumentRank)
    {
        public string DocumentId => Candidate.DocumentId;
    }
    private sealed record DominanceResult(HashSet<string> DominatedAtLeft, HashSet<string> DominatedAtRight, HashSet<string> DominatedPairIds);
    private sealed record PruningDecision(string CandidateId, string DocumentId, string Left, string Right, IReadOnlyList<string> BroadGenerationReasons,
        RankFeatures RankFeatures, int Rank, string Decision, string DecisionReason, bool DominatedAtLeft, bool DominatedAtRight);
    private sealed record RequestManifest(string ArtifactKind, string BenchmarkVersion, string SourceCatalogFingerprint, string CandidateSetSha256,
        string ShortlistSha256, string RequestContract, string CanonicalBuilderVersion, bool GoldDerivedInput, bool ExactBytesPersisted,
        int RequestCount, IReadOnlyList<RequestEntry> Requests);
    private sealed record RequestEntry(string RequestId, string CandidatePairId, string DocumentId, string Left, string Right, string RequestSha256, int RequestBytes, RequestReconstruction Reconstruction);
    private sealed record RequestReconstruction(string SourceCatalogPath, string SourceCatalogFingerprint, string CanonicalBuilderVersion, string ShortlistSha256, bool GoldDerivedInput, bool ExactBytesPersisted);
    private sealed record RequestBuildResult(RequestManifest Manifest, long LogicalBytes, long EstimatedTokens);
    private sealed record DryRunResult(int CheckedEntries, int ProviderCalls, bool AllEntriesAccepted)
    {
        public int RejectedEntries => AllEntriesAccepted ? 0 : 1;
    }
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string DocumentOf(string nodeId) => nodeId.Split(':', 2)[0];
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException(path);
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
}
