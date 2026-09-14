using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityPromotionBenchmarkV4C;

internal static class Program
{
    private const string V1Root = "artifacts/identity-benchmark/v1";
    private const string V2Root = "artifacts/identity-benchmark/v2";
    private const string V4Root = "artifacts/identity-benchmark/v4/challenger";
    private const string GoldPath = "artifacts/identity-gold/semantic-identity-gold.user-reviewed.v2.json";
    private const string BindingV2Path = "artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v2.json";
    private const string BindingV4Path = "artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v4.json";
    private const int ExpectedV3Broad = 95_999;
    private const int ExpectedV4Broad = 96_069;
    private const int ExpectedV4Additions = 70;

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

            // This call deliberately occurs before any Gold file is opened.
            var freeze = VerifyV4BFrozen(root);

            var gold = ReadJson(root, GoldPath);
            var bindingV2 = ReadJson(root, BindingV2Path);
            var bindingV4 = ReadJson(root, BindingV4Path);
            var source = ReadJson(root, V2Root + "/source-catalog.json");
            var v3 = ReadJson(root, V1Root + "/candidate-set.json");
            var v4 = ReadJson(root, V4Root + "/broad-candidate-set.json");

            var sourceNodes = ReadSourceNodes(source);
            var v3Candidates = ReadCandidates(v3, false);
            var v4Candidates = ReadCandidates(v4, false);
            var v3Pairs = v3Candidates.Select(item => PairKey(item.DocumentId, item.Left, item.Right))
                .ToHashSet(StringComparer.Ordinal);
            var cases = BuildGoldCases(gold, bindingV2, bindingV4);
            ValidateSourceBackedEndpoints(cases, sourceNodes);

            var reports = cases.Select(item => EvaluateCase(item, v4Candidates, v3Pairs, sourceNodes)).ToArray();
            var positives = reports.Where(item => item.IsPositive).ToArray();
            var retrieved = positives.Count(item => item.BroadPresent);
            var gate = retrieved == positives.Length
                ? "READY_FOR_V4_PRUNING_EVALUATION"
                : "V4_BROAD_RETRIEVAL_RECALL_FAILURE";

            var output = Full(root, "artifacts/identity-benchmark/v4/evaluation");
            Directory.CreateDirectory(output);
            var goldSha = Sha256File(Full(root, GoldPath));
            var bindingV2Sha = Sha256File(Full(root, BindingV2Path));
            var bindingV4Sha = Sha256File(Full(root, BindingV4Path));
            var broadSha = Sha256File(Full(root, V4Root + "/broad-candidate-set.json"));

            WriteJson(Path.Combine(output, "broad-retrieval-evaluation.json"), new
            {
                artifactKind = "a99_identity_benchmark_v4c_broad_retrieval_evaluation",
                schemaVersion = "a99-identity-benchmark-v4c-broad-retrieval-evaluation-v1",
                status = gate,
                phase = "V4C_FROZEN_V4_BROAD_RETRIEVAL_EVALUATION",
                developmentStatus = "DEV_EXPOSED_CHALLENGER",
                independentGeneralizationClaim = false,
                integrityBeforeGold = freeze,
                goldAuthority = "USER_REVIEWED_IDENTITY_GOLD_FROZEN",
                independentABGold = false,
                goldOpenedAfterFreeze = true,
                goldSha256 = goldSha,
                bindingArtifacts = new { v2Sha256 = bindingV2Sha, v4Sha256 = bindingV4Sha, exactEndpointEvidence = true },
                metrics = new
                {
                    totalCases = reports.Length,
                    positiveDenominator = positives.Length,
                    positiveRetrieved = retrieved,
                    positiveRecall = positives.Length == 0 ? 0d : retrieved / (double)positives.Length,
                    absoluteImprovementOverV3 = retrieved / (double)positives.Length - 1d / 3d,
                    percentagePointImprovementOverV3 = (retrieved / (double)positives.Length - 1d / 3d) * 100d,
                    continuation = new { denominator = reports.Count(item => item.Relation == "CONTINUATION_OF"), retrieved = reports.Count(item => item.Relation == "CONTINUATION_OF" && item.BroadPresent) },
                    sameRepeat = new { denominator = reports.Count(item => item.Relation == "SAME_SEMANTIC_REPEAT"), retrieved = reports.Count(item => item.Relation == "SAME_SEMANTIC_REPEAT" && item.BroadPresent) },
                    distinctDiagnostics = reports.Where(item => !item.IsPositive).Select(item => new { item.ItemId, item.BroadPresent, item.V3BroadPresent, item.V4Only, item.Outcome }).ToArray(),
                    candidateVolume = new { v3Broad = ExpectedV3Broad, v4Broad = ExpectedV4Broad, added = ExpectedV4Additions, increasePercent = ExpectedV4Additions / (double)ExpectedV3Broad * 100d },
                },
                signalContribution = new
                {
                    structuralKey = reports.Where(item => item.IsPositive && item.V4Only && item.Reasons.Contains("SHARED_STRUCTURAL_HEADING_KEY", StringComparer.Ordinal)).Select(item => item.ItemId).ToArray(),
                    terminalAcronym = reports.Where(item => item.IsPositive && item.V4Only && item.Reasons.Contains("TERMINAL_ACRONYM_VARIANT", StringComparer.Ordinal)).Select(item => item.ItemId).ToArray(),
                    continuationMarker = reports.Where(item => item.IsPositive && item.V4Only && item.Reasons.Contains("EXPLICIT_CONTINUATION_VARIANT", StringComparer.Ordinal)).Select(item => item.ItemId).ToArray(),
                    causalAblationPerformed = false,
                },
                firewall = new { providerCalls = 0, modelCalls = 0, goldUsedForGeneration = false, goldUsedForPruning = false, requestsCreated = false, predictionsCreated = false, v4bArtifactsMutated = false, broadCandidateSha256 = broadSha },
                nextGate = gate,
            });

            WriteJson(Path.Combine(output, "case-report.json"), new
            {
                artifactKind = "a99_identity_benchmark_v4c_case_report",
                schemaVersion = "a99-identity-benchmark-v4c-case-report-v1",
                status = gate,
                goldAuthority = "USER_REVIEWED_IDENTITY_GOLD_FROZEN",
                independentABGold = false,
                cases = reports,
                providerCalls = 0,
                modelCalls = 0,
            });

            WriteJson(Path.Combine(output, "failure-attribution.json"), new
            {
                artifactKind = "a99_identity_benchmark_v4c_failure_attribution",
                schemaVersion = "a99-identity-benchmark-v4c-failure-attribution-v1",
                goldDerivedInput = false,
                rows = reports.Where(item => item.IsPositive).Select(item => new
                {
                    itemId = item.ItemId,
                    relation = item.Relation,
                    outcome = item.Outcome,
                    attribution = item.Attribution,
                    broadPresent = item.BroadPresent,
                    v3BroadPresent = item.V3BroadPresent,
                    v4Only = item.V4Only,
                    candidateId = item.CandidateId,
                    reasons = item.Reasons,
                }).ToArray(),
                providerCalls = 0,
            });

            File.WriteAllText(Path.Combine(output, "report.md"), BuildMarkdown(gate, freeze, reports, goldSha), new UTF8Encoding(false));
            Console.WriteLine($"V4C_STATUS={gate};V3_RECALL=1/3;V4_RECALL={retrieved}/{positives.Length};V4_BROAD={ExpectedV4Broad};ADDED={ExpectedV4Additions};PROVIDER_CALLS=0;GOLD_READS=1");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V4C_ERROR={ex}");
            return 2;
        }
    }

    private static FreezeCheck VerifyV4BFrozen(string root)
    {
        var manifest = ReadJson(root, V4Root + "/manifest.json");
        var sourceReference = ReadJson(root, V4Root + "/source-reference.json");
        var contract = ReadJson(root, V4Root + "/retrieval-contract.json");
        var firewall = ReadJson(root, V4Root + "/firewall.json");
        var broad = ReadJson(root, V4Root + "/broad-candidate-set.json");
        var additions = ReadJson(root, V4Root + "/candidate-additions.json");
        var source = ReadJson(root, V2Root + "/source-catalog.json");
        var v3 = ReadJson(root, V1Root + "/candidate-set.json");

        var broadCandidates = ReadCandidates(broad, false);
        var v3Candidates = ReadCandidates(v3, false);
        var additionCandidates = ReadCandidates(additions, true);
        var failures = new List<string>();
        var sourceSha = Sha256File(Full(root, V2Root + "/source-catalog.json"));
        var v3Sha = Sha256File(Full(root, V1Root + "/candidate-set.json"));
        var broadSha = Sha256File(Full(root, V4Root + "/broad-candidate-set.json"));
        var sourceFingerprint = source.RootElement.GetProperty("catalogFingerprint").GetString();

        Check(manifest.RootElement.GetProperty("status").GetString() == "READY_FOR_V4_RETRIEVAL_EVALUATION", "manifest.status", failures);
        Check(manifest.RootElement.GetProperty("developmentStatus").GetString() == "DEV_EXPOSED_CHALLENGER", "manifest.developmentStatus", failures);
        Check(!manifest.RootElement.GetProperty("independentGeneralizationClaim").GetBoolean(), "manifest.independentGeneralizationClaim", failures);
        Check(manifest.RootElement.GetProperty("sourceDocuments").GetInt32() == 3 && manifest.RootElement.GetProperty("sourceUnits").GetInt32() == 6538, "manifest.sourceUniverse", failures);
        Check(manifest.RootElement.GetProperty("v3BroadCandidateCount").GetInt32() == ExpectedV3Broad && v3Candidates.Length == ExpectedV3Broad, "v3.broadCount", failures);
        Check(manifest.RootElement.GetProperty("v4AddedCandidateCount").GetInt32() == ExpectedV4Additions && additionCandidates.Length == ExpectedV4Additions, "v4.additionCount", failures);
        Check(manifest.RootElement.GetProperty("v4BroadCandidateCount").GetInt32() == ExpectedV4Broad && broadCandidates.Length == ExpectedV4Broad, "v4.broadCount", failures);
        Check(manifest.RootElement.GetProperty("sourceCatalogSha256").GetString() == sourceSha, "sourceCatalogSha256", failures);
        Check(manifest.RootElement.GetProperty("sourceCatalogFingerprint").GetString() == sourceFingerprint, "sourceCatalogFingerprint", failures);
        Check(manifest.RootElement.GetProperty("v3BroadCandidateSetSha256").GetString() == v3Sha, "v3BroadCandidateSetSha256", failures);
        Check(manifest.RootElement.GetProperty("v4BroadCandidateSetSha256").GetString() == broadSha, "v4BroadCandidateSetSha256", failures);
        Check(sourceReference.RootElement.GetProperty("sha256").GetString() == sourceSha && sourceReference.RootElement.GetProperty("catalogFingerprint").GetString() == sourceFingerprint, "sourceReference", failures);
        Check(manifest.RootElement.GetProperty("providerCalls").GetInt32() == 0 && manifest.RootElement.GetProperty("modelCalls").GetInt32() == 0 && manifest.RootElement.GetProperty("goldReadCount").GetInt32() == 0, "manifest.firewallCounts", failures);
        Check(firewall.RootElement.GetProperty("providerCalls").GetInt32() == 0 && firewall.RootElement.GetProperty("modelCalls").GetInt32() == 0 && firewall.RootElement.GetProperty("goldReadCount").GetInt32() == 0, "firewall.counts", failures);
        Check(!firewall.RootElement.GetProperty("goldUsedForGeneration").GetBoolean() && !firewall.RootElement.GetProperty("goldUsedForRanking").GetBoolean() && !firewall.RootElement.GetProperty("goldUsedForPruning").GetBoolean(), "firewall.gold", failures);
        Check(!firewall.RootElement.GetProperty("requestArtifactsCreated").GetBoolean() && !firewall.RootElement.GetProperty("predictionArtifactsCreated").GetBoolean(), "firewall.artifacts", failures);
        Check(broad.RootElement.GetProperty("v3BroadCandidateCount").GetInt32() == ExpectedV3Broad && broad.RootElement.GetProperty("addedCandidateCount").GetInt32() == ExpectedV4Additions, "broad.metadata", failures);
        Check(additionCandidates.All(item => item.V4Only), "additions.v4Only", failures);
        Check(broadCandidates.Select(item => PairKey(item.DocumentId, item.Left, item.Right)).Distinct(StringComparer.Ordinal).Count() == broadCandidates.Length, "broad.uniquePairs", failures);
        Check(contract.RootElement.GetProperty("goldReadCount").GetInt32() == 0 && contract.RootElement.GetProperty("providerCalls").GetInt32() == 0, "contract.firewall", failures);
        if (failures.Count > 0)
            throw new InvalidDataException("BLOCKED_ON_V4_FREEZE_INTEGRITY: " + string.Join(",", failures));

        return new FreezeCheck(
            "PASS",
            Sha256File(Full(root, V4Root + "/manifest.json")),
            broadSha,
            Sha256File(Full(root, V4Root + "/source-reference.json")),
            Sha256File(Full(root, V4Root + "/retrieval-contract.json")),
            Sha256File(Full(root, V4Root + "/firewall.json")),
            Sha256File(Full(root, V4Root + "/candidate-additions.json")),
            ExpectedV4Broad,
            ExpectedV4Additions,
            0,
            0);
    }

    private static GoldCase[] BuildGoldCases(JsonDocument gold, JsonDocument bindingV2, JsonDocument bindingV4)
    {
        var authority = gold.RootElement;
        if (authority.GetProperty("status").GetString() != "USER_REVIEWED_IDENTITY_GOLD_FROZEN" || authority.GetProperty("independentABGold").GetBoolean())
            throw new InvalidDataException("GOLD_AUTHORITY_INVALID");

        var v2 = bindingV2.RootElement.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var v4 = bindingV4.RootElement.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var result = new List<GoldCase>();
        foreach (var item in authority.GetProperty("decisions").EnumerateArray().Where(item => item.GetProperty("itemId").GetString() is "IR-018" or "IR-019" or "IR-020" or "IR-021" or "IR-022"))
        {
            var id = item.GetProperty("itemId").GetString()!;
            var relation = item.GetProperty("relation").GetString()!;
            var binding = id == "IR-018" ? v4[id] : v2[id];
            var left = id == "IR-018" ? binding.GetProperty("outer") : binding.GetProperty("leftOccurrence");
            var right = id == "IR-018" ? binding.GetProperty("inner") : binding.GetProperty("rightOccurrence");
            var documentId = GetOptionalString(left, "documentId") ?? GetOptionalString(item, "documentId");
            var leftOccurrence = GetOptionalString(left, "sourceOccurrenceId");
            var rightOccurrence = GetOptionalString(right, "sourceOccurrenceId");
            if (string.IsNullOrWhiteSpace(documentId) || string.IsNullOrWhiteSpace(leftOccurrence) || string.IsNullOrWhiteSpace(rightOccurrence))
                throw new InvalidDataException($"GOLD_ENDPOINT_INCOMPLETE:{id}");
            result.Add(new GoldCase(id, documentId!, relation, relation is "CONTINUATION_OF" or "SAME_SEMANTIC_REPEAT", Endpoint(documentId!, leftOccurrence!), Endpoint(documentId!, rightOccurrence!)));
        }
        if (result.Count != 5)
            throw new InvalidDataException("GOLD_CASE_COUNT_INVALID");
        return result.OrderBy(item => item.ItemId, StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, SourceNode> ReadSourceNodes(JsonDocument source)
        => source.RootElement.GetProperty("sourceOccurrences").EnumerateArray()
            .ToDictionary(item => item.GetProperty("nodeId").GetString()!, item => new SourceNode(item.GetProperty("nodeId").GetString()!, item.GetProperty("text").GetString()!, item.GetProperty("documentOrder").GetInt32()), StringComparer.Ordinal);

    private static Candidate[] ReadCandidates(JsonDocument document, bool defaultV4Only)
        => document.RootElement.GetProperty("candidates").EnumerateArray().Select(item => new Candidate(
            item.TryGetProperty("candidateId", out var candidateId) ? candidateId.GetString()! : item.GetProperty("pairId").GetString()!,
            item.GetProperty("documentId").GetString()!,
            item.TryGetProperty("leftOccurrenceId", out var leftOccurrence) ? leftOccurrence.GetString()! : item.GetProperty("left").GetString()!,
            item.TryGetProperty("rightOccurrenceId", out var rightOccurrence) ? rightOccurrence.GetString()! : item.GetProperty("right").GetString()!,
            item.TryGetProperty("reasons", out var reasons) ? reasons.EnumerateArray().Select(reason => reason.GetString()!).ToArray() : Array.Empty<string>(),
            item.TryGetProperty("v4Only", out var v4Only) ? v4Only.GetBoolean() : defaultV4Only,
            item.TryGetProperty("v4Evidence", out var evidence) ? evidence : default)).ToArray();

    private static CaseReport EvaluateCase(GoldCase gold, IReadOnlyList<Candidate> candidates, IReadOnlySet<string> v3Pairs, IReadOnlyDictionary<string, SourceNode> nodes)
    {
        var matches = candidates.Where(item => SamePair(item, gold.Left, gold.Right)).ToArray();
        var v3Present = v3Pairs.Contains(PairKey(gold.DocumentId, gold.Left, gold.Right));
        if (matches.Length == 0)
            return MakeCase(gold, nodes, v3Present, false, false, null, Array.Empty<string>(), "V4_BROAD_RETRIEVAL_MISS", "V4_BROAD_RETRIEVAL");
        if (matches.Length != 1)
            return MakeCase(gold, nodes, v3Present, true, false, null, matches.SelectMany(item => item.Reasons).Distinct(StringComparer.Ordinal).ToArray(), "BINDING_OR_CATALOG_MISMATCH", "BINDING_OR_CATALOG_MISMATCH");

        var candidate = matches[0];
        var outcome = !candidate.V4Only ? "RETRIEVED_V3_EXISTING" : candidate.Reasons.Count > 1 ? "RETRIEVED_V4_MULTI_SIGNAL" : candidate.Reasons.SingleOrDefault() switch
        {
            "SHARED_STRUCTURAL_HEADING_KEY" => "RETRIEVED_V4_STRUCTURAL_KEY",
            "TERMINAL_ACRONYM_VARIANT" => "RETRIEVED_V4_ACRONYM_VARIANT",
            "EXPLICIT_CONTINUATION_VARIANT" => "RETRIEVED_V4_CONTINUATION_VARIANT",
            _ => "BINDING_OR_CATALOG_MISMATCH",
        };
        return MakeCase(gold, nodes, v3Present, true, candidate.V4Only, candidate.PairId, candidate.Reasons, outcome, "NONE");
    }

    private static CaseReport MakeCase(GoldCase gold, IReadOnlyDictionary<string, SourceNode> nodes, bool v3Present, bool broadPresent, bool v4Only, string? candidateId, IReadOnlyList<string> reasons, string outcome, string attribution)
    {
        var left = nodes[gold.Left];
        var right = nodes[gold.Right];
        return new CaseReport(gold.ItemId, gold.DocumentId, gold.Relation, gold.IsPositive, gold.Left, gold.Right,
            new { sourceOccurrenceId = left.NodeId, text = left.Text, documentOrder = left.DocumentOrder },
            new { sourceOccurrenceId = right.NodeId, text = right.Text, documentOrder = right.DocumentOrder },
            v3Present, broadPresent, v4Only, candidateId, reasons, outcome, attribution);
    }

    private static void ValidateSourceBackedEndpoints(IEnumerable<GoldCase> cases, IReadOnlyDictionary<string, SourceNode> nodes)
    {
        foreach (var item in cases)
            if (!nodes.ContainsKey(item.Left) || !nodes.ContainsKey(item.Right))
                throw new InvalidDataException($"BINDING_OR_CATALOG_MISMATCH:{item.ItemId}");
    }

    private static string BuildMarkdown(string gate, FreezeCheck freeze, IReadOnlyList<CaseReport> reports, string goldSha)
    {
        var positives = reports.Where(item => item.IsPositive).ToArray();
        var retrieved = positives.Count(item => item.BroadPresent);
        var lines = new List<string>
        {
            "# A99 Identity Retrieval V4C — frozen V4 broad evaluation",
            "",
            $"Status: **`{gate}`**",
            "",
            "This is evaluation-only. V4B freeze integrity passed before the user-reviewed Gold was opened. No V3 pruning, request, prediction, model, or provider execution was used.",
            "",
            "## Freeze integrity",
            "",
            $"- Integrity before Gold: `{freeze.Integrity}`.",
            $"- V4 manifest SHA256: `{freeze.ManifestSha256}`.",
            $"- V4 broad SHA256: `{freeze.BroadSha256}`.",
            $"- V4 broad count: `{freeze.BroadCount}`; V4 additions: `{freeze.AdditionCount}`.",
            $"- Gold SHA256: `{goldSha}`.",
            "",
            "## Retrieval comparison",
            "",
            "| Lane | Retrieved | Denominator | Recall |",
            "| --- | ---: | ---: | ---: |",
            $"| V3 broad | 1 | 3 | 33.33% |",
            $"| V4 broad | {retrieved} | {positives.Length} | {retrieved / (double)positives.Length:P2} |",
            "",
            $"Broad volume: `95,999 → 96,069` (`+70`, `{70d / 95_999d:P4}`). No retrieval precision is calculated because unlabeled candidates are not negatives.",
            "",
            "## Cases",
            "",
            "| Case | Relation | V3 broad | V4 broad | V4-only | Outcome | Reasons |",
            "| --- | --- | --- | --- | --- | --- | --- |",
        };
        lines.AddRange(reports.Select(item => $"| {item.ItemId} | {item.Relation} | {item.V3BroadPresent} | {item.BroadPresent} | {item.V4Only} | {item.Outcome} | {string.Join(", ", item.Reasons)} |"));
        lines.AddRange(new[]
        {
            "",
            "IR-018 and IR-022 are DISTINCT diagnostics only; they are excluded from positive recall and no precision is inferred.",
            "",
            "## Signal contribution",
            "",
            "Counts are descriptive participation of frozen reasons and are not causal ablations. Overlapping reasons are not double-counted as recovered cases.",
            "",
            $"- Structural key: {reports.Count(item => item.IsPositive && item.V4Only && item.Reasons.Contains("SHARED_STRUCTURAL_HEADING_KEY", StringComparer.Ordinal))} positive case(s).",
            $"- Terminal acronym: {reports.Count(item => item.IsPositive && item.V4Only && item.Reasons.Contains("TERMINAL_ACRONYM_VARIANT", StringComparer.Ordinal))} positive case(s).",
            $"- Continuation marker: {reports.Count(item => item.IsPositive && item.V4Only && item.Reasons.Contains("EXPLICIT_CONTINUATION_VARIANT", StringComparer.Ordinal))} positive case(s).",
            "",
            "## Firewall",
            "",
            "- `GoldOpenedAfterFreeze=true`.",
            "- `MODEL_CALLS=0`; `PROVIDER_CALLS=0`.",
            "- No pruning, request, prediction, threshold, or V4B artifact mutation.",
            "",
            $"## Gate\n\n`{gate}`",
        });
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string Endpoint(string documentId, string occurrence)
        => occurrence.StartsWith(documentId + ":", StringComparison.Ordinal) ? occurrence : documentId + ":" + occurrence;

    private static bool SamePair(Candidate item, string left, string right)
        => (item.Left == left && item.Right == right) || (item.Left == right && item.Right == left);

    private static string PairKey(string documentId, string left, string right)
        => string.CompareOrdinal(left, right) < 0 ? $"{documentId}|{left}|{right}" : $"{documentId}|{right}|{left}";

    private static JsonDocument ReadJson(string root, string relativePath) => JsonDocument.Parse(File.ReadAllText(Full(root, relativePath)));
    private static string Full(string root, string relativePath) => Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static string? GetOptionalString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static void Check(bool condition, string name, ICollection<string> failures) { if (!condition) failures.Add(name); }

    private sealed record FreezeCheck(string Integrity, string ManifestSha256, string BroadSha256, string SourceReferenceSha256, string ContractSha256, string FirewallSha256, string AdditionsSha256, int BroadCount, int AdditionCount, int ProviderCalls, int GoldReadCount);
    private sealed record GoldCase(string ItemId, string DocumentId, string Relation, bool IsPositive, string Left, string Right);
    private sealed record SourceNode(string NodeId, string Text, int DocumentOrder);
    private sealed record Candidate(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons, bool V4Only, JsonElement Evidence);
    private sealed record CaseReport(string ItemId, string DocumentId, string Relation, bool IsPositive, string Left, string Right, object LeftSource, object RightSource, bool V3BroadPresent, bool BroadPresent, bool V4Only, string? CandidateId, IReadOnlyList<string> Reasons, string Outcome, string Attribution);
}
