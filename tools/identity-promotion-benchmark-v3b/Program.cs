using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityPromotionBenchmarkV3B;

internal static class Program
{
    private const string V1Root = "artifacts/identity-benchmark/v1";
    private const string V2Root = "artifacts/identity-benchmark/v2";
    private const string V3Root = "artifacts/identity-benchmark/v3";
    private const string GoldPath = "artifacts/identity-gold/semantic-identity-gold.user-reviewed.v2.json";
    private const string BindingV2Path = "artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v2.json";
    private const string BindingV4Path = "artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v4.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args.Length == 1 ? args[0] : Directory.GetCurrentDirectory());

            // This verification deliberately occurs before ReadJson(GoldPath).
            var freeze = VerifyV3AFrozen(root);
            var gold = ReadJson(root, GoldPath);
            var bindingV2 = ReadJson(root, BindingV2Path);
            var bindingV4 = ReadJson(root, BindingV4Path);
            var source = ReadJson(root, V2Root + "/source-catalog.json");
            var broad = ReadJson(root, V1Root + "/candidate-set.json");
            var shortlist = ReadJson(root, V3Root + "/shortlist.json");
            var decisions = ReadJson(root, V3Root + "/candidate-pruning-decisions.json");

            var cases = BuildGoldCases(gold, bindingV2, bindingV4);
            var sourceNodes = ReadSourceNodes(source);
            ValidateSourceBackedEndpoints(root, cases, sourceNodes);
            var broadCandidates = ReadCandidates(broad, "candidates");
            var shortlistCandidates = ReadCandidates(shortlist, "candidates");
            var pruning = ReadPruningDecisions(decisions);

            var reports = cases.Select(item => EvaluateCase(item, broadCandidates, shortlistCandidates, pruning, sourceNodes)).ToArray();
            var positives = reports.Where(item => item.IsPositive).ToArray();
            var retrievedPositive = positives.Count(item => item.Outcome == "RETRIEVED");
            var evaluationRoot = Full(root, V3Root + "/evaluation");
            Directory.CreateDirectory(evaluationRoot);

            var gate = retrievedPositive == positives.Length
                ? "READY_FOR_V3_PROVIDER_BENCHMARK_PREPARATION"
                : "V3_RETRIEVAL_RECALL_FAILURE";
            var goldSha = Sha256File(Full(root, GoldPath));
            var bindingV2Sha = Sha256File(Full(root, BindingV2Path));
            var bindingV4Sha = Sha256File(Full(root, BindingV4Path));

            WriteJson(Path.Combine(evaluationRoot, "case-report.json"), new
            {
                artifactKind = "a99_identity_benchmark_v3b_case_report",
                schemaVersion = "a99-identity-benchmark-v3b-case-report",
                status = gate,
                goldAuthority = "USER_REVIEWED_IDENTITY_GOLD_FROZEN",
                independentABGold = false,
                cases = reports,
                providerCalls = 0,
                modelCalls = 0,
            });

            WriteJson(Path.Combine(evaluationRoot, "retrieval-failure-attribution.json"), new
            {
                artifactKind = "a99_identity_benchmark_v3b_retrieval_failure_attribution",
                schemaVersion = "a99-identity-benchmark-v3b-retrieval-failure-attribution",
                goldDerivedInput = false,
                rows = reports.Where(item => item.Outcome != "RETRIEVED").Select(item => new
                {
                    itemId = item.ItemId,
                    relation = item.Relation,
                    isPositive = item.IsPositive,
                    outcome = item.Outcome,
                    attribution = item.Attribution,
                    candidateId = item.CandidateId,
                    rank = item.DocumentRank,
                    pruningDisposition = item.PruningDisposition,
                    sourceOnlyReasons = item.CandidateReasons,
                }).ToArray(),
                providerCalls = 0,
            });

            WriteJson(Path.Combine(evaluationRoot, "retrieval-evaluation.json"), new
            {
                artifactKind = "a99_identity_benchmark_v3b_retrieval_evaluation",
                schemaVersion = "a99-identity-benchmark-v3b-retrieval-evaluation",
                status = gate,
                phase = "V3B_FROZEN_IDENTITY_SHORTLIST_RETRIEVAL_EVALUATION",
                behavioralParent = "V3A_SOURCE_ONLY_SHORTLIST_FREEZE",
                goldAuthority = "USER_REVIEWED_IDENTITY_GOLD_FROZEN",
                independentABGold = false,
                goldOpenedAfterV3AIntegrity = true,
                goldSha256 = goldSha,
                bindingArtifacts = new
                {
                    v2Sha256 = bindingV2Sha,
                    v4Sha256 = bindingV4Sha,
                    exactEndpointEvidence = true,
                },
                v3aFreeze = freeze,
                metrics = new
                {
                    totalCases = reports.Length,
                    positiveDenominator = positives.Length,
                    positiveRetrieved = retrievedPositive,
                    positiveRecall = positives.Length == 0 ? 0d : retrievedPositive / (double)positives.Length,
                    continuationCases = reports.Count(item => item.Relation == "CONTINUATION_OF"),
                    continuationRetrieved = reports.Count(item => item.Relation == "CONTINUATION_OF" && item.Outcome == "RETRIEVED"),
                    repeatCases = reports.Count(item => item.Relation == "SAME_SEMANTIC_REPEAT"),
                    repeatRetrieved = reports.Count(item => item.Relation == "SAME_SEMANTIC_REPEAT" && item.Outcome == "RETRIEVED"),
                    distinctDiagnostics = reports.Where(item => !item.IsPositive).Select(item => new
                    {
                        itemId = item.ItemId,
                        broadPresent = item.BroadCandidatePresent,
                        shortlistPresent = item.ShortlistPresent,
                        outcome = item.Outcome,
                    }).ToArray(),
                    outcomeCounts = reports.GroupBy(item => item.Outcome, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                },
                firewall = new
                {
                    providerCalls = 0,
                    modelCalls = 0,
                    goldUsedForCandidateGeneration = false,
                    goldUsedForRanking = false,
                    goldUsedForPruning = false,
                    goldUsedForRequests = false,
                    predictionRerun = false,
                    v3aArtifactsMutated = false,
                },
                nextGate = gate,
            });

            File.WriteAllText(Path.Combine(evaluationRoot, "report.md"), BuildMarkdown(gate, freeze, reports, goldSha), new UTF8Encoding(false));
            Console.WriteLine($"V3B_STATUS={gate};POSITIVE={retrievedPositive}/{positives.Length};DISTINCT_DIAGNOSTICS={reports.Length - positives.Length};PROVIDER_CALLS=0;GOLD_READS=1");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V3B_ERROR={ex}");
            return 2;
        }
    }

    private static object VerifyV3AFrozen(string root)
    {
        var manifestPath = Full(root, V3Root + "/manifest.json");
        var manifest = ReadJson(root, V3Root + "/manifest.json");
        var v2Manifest = ReadJson(root, V2Root + "/manifest.json");
        var shortlistManifest = ReadJson(root, V3Root + "/shortlist-manifest.json");
        var firewall = ReadJson(root, V3Root + "/firewall.json");

        var candidateSha = Sha256File(Full(root, V1Root + "/candidate-set.json"));
        var shortlistSha = Sha256File(Full(root, V3Root + "/shortlist.json"));
        var rankingSha = Sha256File(Full(root, V3Root + "/candidate-ranking-config.json"));
        var featureSha = Sha256File(Full(root, V3Root + "/source-only-feature-inventory.json"));
        var requestSha = Sha256File(Full(root, V3Root + "/request-manifest.json"));

        var failures = new List<string>();
        Check(manifest.GetString("status") == "READY_FOR_V3_RETRIEVAL_EVALUATION", "status", failures);
        Check(manifest.GetInt32("broadCandidateCount") == 95999, "broadCandidateCount", failures);
        Check(manifest.GetInt32("afterDominanceCount") == 7659, "afterDominanceCount", failures);
        Check(manifest.GetInt32("shortlistedCount") == 7659, "shortlistedCount", failures);
        Check(v2Manifest.GetString("candidateSetSha256") == candidateSha && manifest.GetString("broadCandidateSetSha256") == candidateSha, "candidateSha256", failures);
        Check(manifest.GetString("shortlistSha256") == shortlistSha && shortlistManifest.GetString("shortlistSha256") == shortlistSha, "shortlistSha256", failures);
        Check(manifest.GetString("candidateRankingConfigSha256") == rankingSha, "rankingConfigSha256", failures);
        Check(manifest.GetString("featureInventorySha256") == featureSha, "featureInventorySha256", failures);
        Check(manifest.GetString("requestManifestSha256") == requestSha, "requestManifestSha256", failures);
        Check(manifest.GetInt32("providerCalls") == 0 && manifest.GetInt32("goldReadCount") == 0, "manifestFirewall", failures);
        Check(firewall.GetInt32("providerCalls") == 0 && firewall.GetInt32("goldReadCount") == 0, "firewallCounts", failures);
        Check(firewall.GetBoolean("goldArtifactLoaded") == false && firewall.GetBoolean("goldDerivedFeatures") == false, "firewallGold", failures);
        if (failures.Count != 0)
            throw new InvalidDataException("BLOCKED_ON_V3A_FREEZE_INTEGRITY: " + string.Join(",", failures));

        return new
        {
            manifestSha256 = Sha256File(manifestPath),
            broadCandidateCount = 95999,
            afterDominanceCount = 7659,
            shortlistedCount = 7659,
            broadCandidateSetSha256 = candidateSha,
            shortlistSha256 = shortlistSha,
            candidateRankingConfigSha256 = rankingSha,
            featureInventorySha256 = featureSha,
            requestManifestSha256 = requestSha,
            integrity = "PASS",
            goldReadCountBeforeCheck = 0,
            providerCallsBeforeCheck = 0,
        };
    }

    private static GoldCase[] BuildGoldCases(JsonDocument gold, JsonDocument bindingV2, JsonDocument bindingV4)
    {
        var authority = gold.RootElement;
        if (authority.GetString("status") != "USER_REVIEWED_IDENTITY_GOLD_FROZEN" || authority.GetBoolean("independentABGold"))
            throw new InvalidDataException("GOLD_AUTHORITY_INVALID");

        var v2Items = bindingV2.RootElement.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetString("id")!, StringComparer.Ordinal);
        var v4Items = bindingV4.RootElement.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetString("id")!, StringComparer.Ordinal);
        var result = new List<GoldCase>();
        foreach (var item in authority.GetProperty("decisions").EnumerateArray().Where(item => item.GetString("itemId") is string id && id is "IR-018" or "IR-019" or "IR-020" or "IR-021" or "IR-022"))
        {
            var id = item.GetString("itemId")!;
            var relation = item.GetString("relation")!;
            JsonElement left;
            JsonElement right;
            if (id == "IR-018")
            {
                var binding = v4Items[id];
                left = binding.GetProperty("outer");
                right = binding.GetProperty("inner");
            }
            else
            {
                var binding = v2Items[id];
                left = binding.TryGetProperty("leftOccurrence", out var boundLeft) && boundLeft.ValueKind == JsonValueKind.Object
                    ? boundLeft : binding.GetProperty("leftQuery");
                right = binding.TryGetProperty("rightOccurrence", out var boundRight) && boundRight.ValueKind == JsonValueKind.Object
                    ? boundRight : binding.GetProperty("rightQuery");
            }

            var leftId = GetOptionalString(left, "sourceOccurrenceId");
            var rightId = GetOptionalString(right, "sourceOccurrenceId");
            var documentId = GetOptionalString(item, "documentId") ?? GetOptionalString(left, "documentId");
            if (string.IsNullOrWhiteSpace(leftId) || string.IsNullOrWhiteSpace(rightId) || string.IsNullOrWhiteSpace(documentId))
                throw new InvalidDataException($"GOLD_ENDPOINT_INCOMPLETE:{id}");
            result.Add(new GoldCase(id, documentId!, relation, relation is "CONTINUATION_OF" or "SAME_SEMANTIC_REPEAT",
                documentId + ":" + leftId, documentId + ":" + rightId));
        }
        if (result.Count != 5)
            throw new InvalidDataException("GOLD_CASE_COUNT_INVALID");
        return result.OrderBy(item => item.ItemId, StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, SourceNode> ReadSourceNodes(JsonDocument source)
        => source.RootElement.GetProperty("sourceOccurrences").EnumerateArray()
            .ToDictionary(item => item.GetString("nodeId")!, item => new SourceNode(item.GetString("nodeId")!, item.GetString("text")!, item.GetInt32("documentOrder")), StringComparer.Ordinal);

    private static void ValidateSourceBackedEndpoints(string root, IReadOnlyList<GoldCase> cases, IReadOnlyDictionary<string, SourceNode> nodes)
    {
        foreach (var item in cases)
        {
            if (!nodes.ContainsKey(item.Left) || !nodes.ContainsKey(item.Right))
                throw new InvalidDataException($"BINDING_OR_CATALOG_MISMATCH:{item.ItemId}");
        }
    }

    private static Candidate[] ReadCandidates(JsonDocument document, string property)
        => document.RootElement.GetProperty(property).EnumerateArray().Select(item => new Candidate(
            item.GetString("pairId")!, item.GetString("documentId")!, item.GetString("left")!, item.GetString("right")!,
            item.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString()!).ToArray(),
            item.TryGetProperty("features", out var features) ? features : default,
            item.TryGetProperty("rank", out var rank) ? rank.GetInt32() : 0)).ToArray();

    private static Dictionary<string, Pruning> ReadPruningDecisions(JsonDocument document)
        => document.RootElement.GetProperty("decisions").EnumerateArray().ToDictionary(
            item => item.GetString("candidateId")!,
            item => new Pruning(item.GetString("candidateId")!, item.GetString("documentId")!, item.GetString("left")!, item.GetString("right")!,
                item.GetString("decision")!, item.GetString("decisionReason")!, item.GetInt32("rank"),
                item.GetProperty("broadGenerationReasons").EnumerateArray().Select(reason => reason.GetString()!).ToArray(),
                item.GetProperty("rankFeatures")), StringComparer.Ordinal);

    private static CaseReport EvaluateCase(GoldCase gold, IReadOnlyList<Candidate> broad, IReadOnlyList<Candidate> shortlist,
        IReadOnlyDictionary<string, Pruning> pruning, IReadOnlyDictionary<string, SourceNode> nodes)
    {
        var matches = broad.Where(item => SamePair(item, gold.Left, gold.Right)).ToArray();
        var shortlistMatches = shortlist.Where(item => SamePair(item, gold.Left, gold.Right)).ToArray();
        if (matches.Length == 0)
            return MakeReport(gold, false, false, false, "BROAD_RETRIEVAL_MISS", "BROAD_RETRIEVAL", null, null, Array.Empty<string>(), Array.Empty<object>(), nodes);
        if (matches.Length > 1)
            return MakeReport(gold, true, false, false, "BINDING_OR_CATALOG_MISMATCH", "AMBIGUOUS_BROAD_PAIR", null, null, matches.SelectMany(item => item.Reasons).Distinct(StringComparer.Ordinal).ToArray(), Array.Empty<object>(), nodes);

        var candidate = matches[0];
        if (!pruning.TryGetValue(candidate.PairId, out var decision))
            return MakeReport(gold, true, false, false, "BINDING_OR_CATALOG_MISMATCH", "MISSING_V3A_DISPOSITION", candidate.PairId, null, candidate.Reasons, Array.Empty<object>(), nodes);
        var retrieved = shortlistMatches.Length == 1 && decision.Decision == "SHORTLISTED";
        var afterDominance = decision.Decision != "PRUNED_DOMINATED";
        var outcome = retrieved ? "RETRIEVED" : decision.Decision == "PRUNED_DOMINATED" ? "V3_DOMINANCE_FALSE_NEGATIVE" : "V3_BUDGET_FALSE_NEGATIVE";
        var attribution = retrieved ? "NONE" : outcome == "V3_DOMINANCE_FALSE_NEGATIVE" ? "SOURCE_ONLY_DOMINANCE_PRUNING" : "SOURCE_ONLY_OPERATIONAL_BUDGET";
        var competitors = pruning.Values.Where(item => item.DocumentId == candidate.DocumentId && item.Rank < decision.Rank)
            .OrderBy(item => item.Rank).Take(5).Select(item => (object)new
            {
                candidateId = item.CandidateId,
                rank = item.Rank,
                decision = item.Decision,
                reasons = item.Reasons,
                rankFeatures = item.RankFeatures,
            }).ToArray();
        return MakeReport(gold, afterDominance, retrieved, true, outcome, attribution, candidate.PairId, decision.Rank,
            candidate.Reasons, competitors, nodes, decision.Decision, decision.DecisionReason, candidate.DocumentId,
            nodes[gold.Left].DocumentOrder, nodes[gold.Right].DocumentOrder);
    }

    private static CaseReport MakeReport(GoldCase gold, bool broadPresent, bool shortlistPresent, bool afterDominancePresent, string outcome, string attribution,
        string? candidateId, int? rank, IReadOnlyList<string> reasons, IReadOnlyList<object> competitors,
        IReadOnlyDictionary<string, SourceNode> nodes, string? disposition = null, string? dispositionReason = null,
        string? documentId = null, int? leftOrder = null, int? rightOrder = null)
    {
        var left = nodes[gold.Left];
        var right = nodes[gold.Right];
        return new CaseReport(gold.ItemId, gold.DocumentId, gold.Relation, gold.IsPositive, gold.Left, gold.Right,
            new { sourceOccurrenceId = left.NodeId, text = left.Text, documentOrder = left.DocumentOrder },
            new { sourceOccurrenceId = right.NodeId, text = right.Text, documentOrder = right.DocumentOrder },
            broadPresent, afterDominancePresent, shortlistPresent, candidateId, rank, "V3A_DOCUMENT_RANK", reasons, disposition, dispositionReason,
            competitors, outcome, attribution);
    }

    private static bool SamePair(Candidate item, string left, string right)
        => (item.Left == left && item.Right == right) || (item.Left == right && item.Right == left);

    private static string BuildMarkdown(string gate, object freeze, IReadOnlyList<CaseReport> reports, string goldSha)
    {
        var positives = reports.Where(item => item.IsPositive).ToArray();
        var retrieved = positives.Count(item => item.Outcome == "RETRIEVED");
        var lines = new List<string>
        {
            "# A99 Identity Benchmark v3B — Frozen shortlist retrieval evaluation",
            "",
            $"Status: **`{gate}`**",
            "",
            "This is an offline retrieval evaluation. V3A integrity was verified before opening the user-reviewed Gold; no provider or model call was made, and no V3A artifact was modified.",
            "",
            "## Firewall and authority",
            "",
            "- Gold authority: `USER_REVIEWED_IDENTITY_GOLD_FROZEN`; `independentABGold=false`.",
            $"- Gold SHA256: `{goldSha}`.",
            $"- V3A integrity: `{JsonSerializer.Serialize(freeze)}`.",
            "- Gold was evaluation-only; it was not used to generate, rank, prune, or request candidates.",
            "- `MODEL_CALLS=0`; `PROVIDER_CALLS=0`.",
            "",
            "## Retrieval metric",
            "",
            $"- Positive denominator: `{positives.Length}` (IR-019 continuation + IR-020/IR-021 repeat).",
            $"- Positive retrieved: `{retrieved}/{positives.Length}`; positive recall: `{(positives.Length == 0 ? 0 : retrieved / (double)positives.Length):P2}`.",
            "- Distinct cases IR-018 and IR-022 are reported as diagnostics only; no retrieval precision is calculated from five cases.",
            "",
            "## Case outcomes",
            "",
            "| Case | Relation | Broad | Shortlist | Rank | Outcome | Attribution |",
            "|---|---|---:|---:|---:|---|---|",
        };
        lines.AddRange(reports.Select(item => $"| {item.ItemId} | {item.Relation} | {(item.BroadCandidatePresent ? "yes" : "no")} | {(item.ShortlistPresent ? "yes" : "no")} | {item.DocumentRank?.ToString() ?? "-"} | {item.Outcome} | {item.Attribution} |"));
        lines.AddRange(new[]
        {
            "",
            "## Gate",
            "",
            $"`{gate}`",
            "",
            "No provider benchmark is authorized by this artifact when positive retrieval recall is incomplete. V3A remains immutable and any failure is attributed to source-only retrieval/pruning lineage, not to a model decision.",
            "",
        });
        return string.Join(Environment.NewLine, lines);
    }

    private static void Check(bool condition, string name, ICollection<string> failures)
    {
        if (!condition) failures.Add(name);
    }

    private static JsonDocument ReadJson(string root, string relative) => JsonDocument.Parse(File.ReadAllText(Full(root, relative)));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));

    private static string? GetString(this JsonDocument document, string property) => document.RootElement.GetProperty(property).GetString();
    private static string? GetString(this JsonElement element, string property) => element.GetProperty(property).GetString();
    private static string? GetOptionalString(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetString() : null;
    private static int GetInt32(this JsonDocument document, string property) => document.RootElement.GetProperty(property).GetInt32();
    private static int GetInt32(this JsonElement element, string property) => element.GetProperty(property).GetInt32();
    private static bool GetBoolean(this JsonDocument document, string property) => document.RootElement.GetProperty(property).GetBoolean();
    private static bool GetBoolean(this JsonElement element, string property) => element.GetProperty(property).GetBoolean();

    private sealed record GoldCase(string ItemId, string DocumentId, string Relation, bool IsPositive, string Left, string Right);
    private sealed record SourceNode(string NodeId, string Text, int DocumentOrder);
    private sealed record Candidate(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons, JsonElement Features, int Rank);
    private sealed record Pruning(string CandidateId, string DocumentId, string Left, string Right, string Decision, string DecisionReason, int Rank, IReadOnlyList<string> Reasons, JsonElement RankFeatures);
    private sealed record CaseReport(string ItemId, string DocumentId, string Relation, bool IsPositive, string Left, string Right,
        object LeftSource, object RightSource, bool BroadCandidatePresent, bool AfterDominancePresent, bool ShortlistPresent,
        string? CandidateId, int? DocumentRank, string RankMeaning,
        IReadOnlyList<string> CandidateReasons, string? PruningDisposition, string? PruningReason, IReadOnlyList<object> CompetingCandidates,
        string Outcome, string Attribution);
}
