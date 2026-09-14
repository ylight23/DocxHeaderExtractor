using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityPromotionBenchmarkV4EB;

internal static class Program
{
    private const string V1Root = "artifacts/identity-benchmark/v1";
    private const string V2Root = "artifacts/identity-benchmark/v2";
    private const string V4Root = "artifacts/identity-benchmark/v4/challenger";
    private const string V4E = "artifacts/identity-benchmark/v4/pruning-challenger";
    private const string GoldPath = "artifacts/identity-gold/semantic-identity-gold.user-reviewed.v2.json";
    private const string BindingV2Path = "artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v2.json";
    private const string BindingV4Path = "artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v4.json";
    private const int ExpectedV3Broad = 95_999;
    private const int ExpectedV3Shortlist = 7_659;
    private const int ExpectedV4Broad = 96_069;
    private const int ExpectedV4Shortlist = 7_702;
    private const int ExpectedPositiveDenominator = 3;

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

            // This is intentionally the only operation before Gold is opened.
            var integrity = VerifyV4EFrozen(root);

            var gold = ReadJson(root, GoldPath);
            var bindingV2 = ReadJson(root, BindingV2Path);
            var bindingV4 = ReadJson(root, BindingV4Path);
            var source = ReadJson(root, V2Root + "/source-catalog.json");
            var v3 = ReadJson(root, V1Root + "/candidate-set.json");
            var v4 = ReadJson(root, V4Root + "/broad-candidate-set.json");
            var shortlist = ReadJson(root, V4E + "/shortlist.json");
            var decisions = ReadJson(root, V4E + "/pruning-decisions.json");

            var sourceNodes = ReadSourceNodes(source);
            var v3Candidates = ReadCandidates(v3);
            var v4Candidates = ReadCandidates(v4);
            var v4Shortlist = ReadCandidates(shortlist);
            var v3Pairs = v3Candidates.Select(item => PairKey(item.DocumentId, item.Left, item.Right))
                .ToHashSet(StringComparer.Ordinal);
            var cases = BuildGoldCases(gold, bindingV2, bindingV4);
            ValidateSourceBackedEndpoints(cases, sourceNodes);
            var pruningById = decisions.RootElement.GetProperty("decisions")
                .EnumerateArray()
                .ToDictionary(item => item.GetProperty("candidateId").GetString()!, StringComparer.Ordinal);

            var reports = cases.Select(item => EvaluateCase(item, v3Candidates, v4Candidates, v4Shortlist, pruningById, v3Pairs, sourceNodes)).ToArray();
            var positives = reports.Where(item => item.IsPositive).ToArray();
            var retained = positives.Count(item => item.ShortlistPresent);
            var gate = retained == ExpectedPositiveDenominator
                ? "READY_FOR_V4_PROVIDER_SCALE_DECISION"
                : "V4E_PRUNING_RETENTION_FAILURE";

            var output = Full(root, V4E + "/evaluation");
            Directory.CreateDirectory(output);
            var goldSha = Sha256File(Full(root, GoldPath));
            var bindingV2Sha = Sha256File(Full(root, BindingV2Path));
            var bindingV4Sha = Sha256File(Full(root, BindingV4Path));
            var shortlistSha = Sha256File(Full(root, V4E + "/shortlist.json"));
            var decisionsSha = Sha256File(Full(root, V4E + "/pruning-decisions.json"));

            WriteJson(Path.Combine(output, "pruning-retention-evaluation.json"), new
            {
                artifactKind = "a99_identity_benchmark_v4e_b_pruning_retention_evaluation",
                schemaVersion = "a99-identity-benchmark-v4e-b-pruning-retention-evaluation-v1",
                status = gate,
                phase = "V4E_B_FROZEN_V4E_SHORTLIST_GOLD_EVALUATION",
                developmentStatus = "DEV_EXPOSED_CHALLENGER",
                independentGeneralizationClaim = false,
                integrityBeforeGold = integrity,
                goldAuthority = "USER_REVIEWED_IDENTITY_GOLD_FROZEN",
                independentABGold = false,
                goldOpenedAfterFreeze = true,
                goldSha256 = goldSha,
                bindingArtifacts = new { v2Sha256 = bindingV2Sha, v4Sha256 = bindingV4Sha },
                v4eArtifacts = new { manifestSha256 = integrity.V4EManifestSha256, shortlistSha256 = shortlistSha, pruningDecisionsSha256 = decisionsSha },
                metrics = new
                {
                    v4BroadPositiveRecall = new { numerator = positives.Count(item => item.BroadPresent), denominator = positives.Length, value = positives.Length == 0 ? 0d : positives.Count(item => item.BroadPresent) / (double)positives.Length },
                    v4ePositiveRetention = new { numerator = retained, denominator = positives.Length, value = positives.Length == 0 ? 0d : retained / (double)positives.Length },
                    continuationRetention = new { numerator = reports.Count(item => item.Relation == "CONTINUATION_OF" && item.ShortlistPresent), denominator = reports.Count(item => item.Relation == "CONTINUATION_OF") },
                    sameRepeatRetention = new { numerator = reports.Count(item => item.Relation == "SAME_SEMANTIC_REPEAT" && item.ShortlistPresent), denominator = reports.Count(item => item.Relation == "SAME_SEMANTIC_REPEAT") },
                    distinctDiagnosticCoverage = reports.Where(item => !item.IsPositive).Select(item => new { item.ItemId, item.BroadPresent, item.ShortlistPresent, item.Disposition }).ToArray(),
                    candidateVolume = new { v3Broad = ExpectedV3Broad, v3PositiveRecall = "1/3", v4Broad = ExpectedV4Broad, v4eShortlist = ExpectedV4Shortlist },
                },
                firewall = new { providerCalls = 0, modelCalls = 0, promotionExecution = false, v4eArtifactsMutated = false },
                nextGate = gate,
            });

            WriteJson(Path.Combine(output, "case-report.json"), new
            {
                artifactKind = "a99_identity_benchmark_v4e_b_case_report",
                schemaVersion = "a99-identity-benchmark-v4e-b-case-report-v1",
                status = gate,
                goldAuthority = "USER_REVIEWED_IDENTITY_GOLD_FROZEN",
                independentABGold = false,
                cases = reports,
                providerCalls = 0,
                modelCalls = 0,
            });

            WriteJson(Path.Combine(output, "failure-attribution.json"), new
            {
                artifactKind = "a99_identity_benchmark_v4e_b_failure_attribution",
                schemaVersion = "a99-identity-benchmark-v4e-b-failure-attribution-v1",
                goldDerivedInput = false,
                rows = reports.Where(item => item.IsPositive && !item.ShortlistPresent).Select(item => new
                {
                    itemId = item.ItemId,
                    relation = item.Relation,
                    disposition = item.Disposition,
                    attribution = item.Attribution,
                    broadPresent = item.BroadPresent,
                    v4Only = item.V4Only,
                    candidateId = item.CandidateId,
                }).ToArray(),
                providerCalls = 0,
            });

            File.WriteAllText(Path.Combine(output, "report.md"), BuildMarkdown(gate, integrity, reports, goldSha), new UTF8Encoding(false));
            Console.WriteLine($"V4E_B_STATUS={gate};V3_RECALL=1/3;V4_BROAD_RECALL={positives.Count(item => item.BroadPresent)}/{positives.Length};V4E_RETENTION={retained}/{positives.Length};V4E_SHORTLIST={ExpectedV4Shortlist};PROVIDER_CALLS=0;GOLD_READS=1");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V4E_B_ERROR={ex}");
            return 2;
        }
    }

    private static FreezeIntegrity VerifyV4EFrozen(string root)
    {
        var manifest = ReadJson(root, V4E + "/manifest.json");
        var shortlistManifest = ReadJson(root, V4E + "/shortlist-manifest.json");
        var ranking = ReadJson(root, V4E + "/ranking-contract.json");
        var sourceEvidence = ReadJson(root, V4E + "/source-evidence-contract.json");
        var request = ReadJson(root, V4E + "/request-manifest.json");
        var firewall = ReadJson(root, V4E + "/firewall.json");
        var compatibility = ReadJson(root, V4E + "/v3-baseline-compatibility.json");
        var broad = ReadJson(root, V4Root + "/broad-candidate-set.json");
        var v4Shortlist = ReadJson(root, V4E + "/shortlist.json");
        var source = ReadJson(root, V2Root + "/source-catalog.json");
        var v3 = ReadJson(root, V1Root + "/candidate-set.json");

        var failures = new List<string>();
        var manifestSha = Sha256File(Full(root, V4E + "/manifest.json"));
        var shortlistSha = Sha256File(Full(root, V4E + "/shortlist.json"));
        var rankingSha = Sha256File(Full(root, V4E + "/ranking-contract.json"));
        var sourceEvidenceSha = Sha256File(Full(root, V4E + "/source-evidence-contract.json"));
        var requestSha = Sha256File(Full(root, V4E + "/request-manifest.json"));
        var firewallSha = Sha256File(Full(root, V4E + "/firewall.json"));
        var compatibilitySha = Sha256File(Full(root, V4E + "/v3-baseline-compatibility.json"));
        var broadSha = Sha256File(Full(root, V4Root + "/broad-candidate-set.json"));
        var sourceSha = Sha256File(Full(root, V2Root + "/source-catalog.json"));
        var v3Sha = Sha256File(Full(root, V1Root + "/candidate-set.json"));
        var v4Candidates = ReadCandidates(broad);
        var v3Candidates = ReadCandidates(v3);
        var shortlistCandidates = ReadCandidates(v4Shortlist);

        Check(manifest.RootElement.GetProperty("status").GetString() == "READY_FOR_V4E_PRUNING_GOLD_EVALUATION", "manifest.status", failures);
        Check(manifest.RootElement.GetProperty("developmentStatus").GetString() == "DEV_EXPOSED_CHALLENGER" && !manifest.RootElement.GetProperty("independentGeneralizationClaim").GetBoolean(), "manifest.disclosure", failures);
        Check(manifest.RootElement.GetProperty("v3BroadCandidateCount").GetInt32() == ExpectedV3Broad && manifest.RootElement.GetProperty("v3BaselineShortlistCount").GetInt32() == ExpectedV3Shortlist, "manifest.v3.counts", failures);
        Check(manifest.RootElement.GetProperty("v4BroadCandidateCount").GetInt32() == ExpectedV4Broad && manifest.RootElement.GetProperty("shortlistedCount").GetInt32() == ExpectedV4Shortlist, "manifest.v4.counts", failures);
        Check(manifest.RootElement.GetProperty("shortlistSha256").GetString() == shortlistSha && manifest.RootElement.GetProperty("requestManifestSha256").GetString() == requestSha, "manifest.artifact.hashes", failures);
        Check(manifest.RootElement.GetProperty("sourceCatalogSha256").GetString() == sourceSha && v4Candidates.Length == ExpectedV4Broad && v3Candidates.Length == ExpectedV3Broad, "source-and-broad.integrity", failures);
        Check(shortlistCandidates.Length == ExpectedV4Shortlist && v4Shortlist.RootElement.GetProperty("candidateSetSha256").GetString() == broadSha, "shortlist.integrity", failures);
        Check(shortlistManifest.RootElement.GetProperty("shortlistSha256").GetString() == shortlistSha && shortlistManifest.RootElement.GetProperty("shortlistedCount").GetInt32() == ExpectedV4Shortlist, "shortlist-manifest.integrity", failures);
        Check(compatibility.RootElement.GetProperty("status").GetString() == "PASS" && compatibility.RootElement.GetProperty("byteIdentical").GetBoolean() && compatibility.RootElement.GetProperty("actualShortlistCount").GetInt32() == ExpectedV3Shortlist, "v3.compatibility", failures);
        Check(compatibility.RootElement.GetProperty("frozenV3ShortlistSha256").GetString() == compatibility.RootElement.GetProperty("challengerReproductionSha256").GetString(), "v3.compatibility.sha", failures);
        Check(ranking.RootElement.GetProperty("goldUsed").GetBoolean() == false && ranking.RootElement.GetProperty("modelUsed").GetBoolean() == false && sourceEvidence.RootElement.GetProperty("goldUsed").GetBoolean() == false, "source-only.contracts", failures);
        Check(request.RootElement.GetProperty("goldDerivedInput").GetBoolean() == false && request.RootElement.GetProperty("exactBytesPersisted").GetBoolean() == false && request.RootElement.GetProperty("requestCount").GetInt32() == ExpectedV4Shortlist, "request.integrity", failures);
        Check(firewall.RootElement.GetProperty("goldReadCount").GetInt32() == 0 && firewall.RootElement.GetProperty("v4cEvaluationReadCount").GetInt32() == 0 && firewall.RootElement.GetProperty("providerCalls").GetInt32() == 0 && firewall.RootElement.GetProperty("modelCalls").GetInt32() == 0, "firewall.counts", failures);
        Check(firewall.RootElement.GetProperty("v3BaselineByteCompatibility").GetBoolean() && firewall.RootElement.GetProperty("everyBroadCandidateHasDisposition").GetBoolean() && !firewall.RootElement.GetProperty("budgetExceeded").GetBoolean(), "firewall.invariants", failures);
        Check(firewall.RootElement.GetProperty("requestShaCheckedBeforeNetwork").GetBoolean() && firewall.RootElement.GetProperty("exactBytesDeterministicallyReconstructible").GetBoolean(), "firewall.request", failures);
        Check(v3Sha == ReadTextHash(root, V1Root + "/candidate-set.json"), "v3.sha.recomputed", failures);
        if (failures.Count > 0)
            throw new InvalidDataException("BLOCKED_ON_V4E_FREEZE_INTEGRITY: " + string.Join(",", failures));

        return new FreezeIntegrity("PASS", manifestSha, shortlistSha, rankingSha, sourceEvidenceSha, requestSha, firewallSha, compatibilitySha, broadSha, sourceSha, ExpectedV4Shortlist);
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

    private static CaseReport EvaluateCase(GoldCase gold, IReadOnlyList<Candidate> v3, IReadOnlyList<Candidate> broad, IReadOnlyList<Candidate> shortlist, IReadOnlyDictionary<string, JsonElement> decisions, IReadOnlySet<string> v3Pairs, IReadOnlyDictionary<string, SourceNode> nodes)
    {
        var broadMatches = broad.Where(item => SamePair(item, gold.Left, gold.Right)).ToArray();
        var shortlistMatches = shortlist.Where(item => SamePair(item, gold.Left, gold.Right)).ToArray();
        var v3Present = v3Pairs.Contains(PairKey(gold.DocumentId, gold.Left, gold.Right));
        if (broadMatches.Length == 0)
            return MakeCase(gold, nodes, v3Present, false, false, null, Array.Empty<string>(), null, "V4_BROAD_RETRIEVAL_MISS", "NONE");
        if (broadMatches.Length != 1 || shortlistMatches.Length > 1)
            return MakeCase(gold, nodes, v3Present, true, false, null, broadMatches.SelectMany(item => item.Reasons).Distinct(StringComparer.Ordinal).ToArray(), null, "BINDING_OR_CATALOG_MISMATCH", "BINDING_OR_CATALOG_MISMATCH");

        var candidate = broadMatches[0];
        var retained = shortlistMatches.Length == 1;
        var disposition = retained ? "SHORTLISTED" : decisions.TryGetValue(candidate.PairId, out var decision) ? decision.GetProperty("decision").GetString()! : "BINDING_OR_CATALOG_MISMATCH";
        var evidence = decisions.TryGetValue(candidate.PairId, out var detail) && detail.TryGetProperty("rankFeatures", out var features) ? features : default;
        var outcome = retained
            ? candidate.V4Only ? "RETAINED_V4_ONLY" : "RETAINED_V3_EXISTING"
            : disposition switch
            {
                "PRUNED_DOMINATED" => "PRUNED_DOMINATED",
                "PRUNED_BUDGET" => "PRUNED_BUDGET",
                _ => "BINDING_OR_CATALOG_MISMATCH",
            };
        var attribution = retained || outcome.StartsWith("RETAINED", StringComparison.Ordinal) ? "NONE" : outcome switch
        {
            "PRUNED_DOMINATED" => "V4E_DOMINANCE_FALSE_NEGATIVE",
            "PRUNED_BUDGET" => "V4E_BUDGET_FALSE_NEGATIVE",
            _ => "BINDING_OR_CATALOG_MISMATCH",
        };
        return MakeCase(gold, nodes, v3Present, true, candidate.V4Only, candidate.PairId, candidate.Reasons, evidence, outcome, attribution, retained);
    }

    private static CaseReport MakeCase(GoldCase gold, IReadOnlyDictionary<string, SourceNode> nodes, bool v3Present, bool broadPresent, bool v4Only, string? candidateId, IReadOnlyList<string> reasons, JsonElement? evidence, string outcome, string attribution, bool shortlistPresent = false)
    {
        var left = nodes[gold.Left];
        var right = nodes[gold.Right];
        return new CaseReport(gold.ItemId, gold.DocumentId, gold.Relation, gold.IsPositive, gold.Left, gold.Right,
            new { sourceOccurrenceId = left.NodeId, text = left.Text, documentOrder = left.DocumentOrder },
            new { sourceOccurrenceId = right.NodeId, text = right.Text, documentOrder = right.DocumentOrder },
            v3Present, broadPresent, v4Only, candidateId, reasons, evidence, shortlistPresent, outcome, attribution);
    }

    private static void ValidateSourceBackedEndpoints(IEnumerable<GoldCase> cases, IReadOnlyDictionary<string, SourceNode> nodes)
    {
        foreach (var item in cases)
            if (!nodes.ContainsKey(item.Left) || !nodes.ContainsKey(item.Right))
                throw new InvalidDataException($"BINDING_OR_CATALOG_MISMATCH:{item.ItemId}");
    }

    private static string BuildMarkdown(string gate, FreezeIntegrity integrity, IReadOnlyList<CaseReport> reports, string goldSha)
    {
        var positives = reports.Where(item => item.IsPositive).ToArray();
        var retained = positives.Count(item => item.ShortlistPresent);
        var lines = new List<string>
        {
            "# A99 Identity Retrieval V4E-B - frozen pruning retention evaluation",
            "",
            "Status: **" + gate + "**",
            "",
            "V4E-A integrity passed before the user-reviewed Gold was opened. This phase is evaluation-only: no retriever, pruner, ranking, request, promotion, model, or provider execution was changed.",
            "",
            "## Freeze integrity",
            "",
            "- V4E manifest SHA256: " + integrity.V4EManifestSha256 + ".",
            "- V4E shortlist SHA256: " + integrity.ShortlistSha256 + "; count: " + integrity.ShortlistCount + " (expected 7702).",
            "- V3 compatibility: PASS; frozen V3 shortlist reproduced byte/SHA-identically.",
            "- Gold SHA256: " + goldSha + "; Gold opened after integrity: true.",
            "",
            "## Pipeline comparison",
            "",
            "| Lane | Candidate volume | Positive result |",
            "| --- | ---: | ---: |",
            "| V3 broad | 95,999 | 1/3 recall |",
            "| V4 broad | 96,069 | " + positives.Count(item => item.BroadPresent) + "/3 recall |",
            "| V4E shortlist | 7,702 | " + retained + "/3 retention |",
            "",
            "## Cases",
            "",
            "| Case | Relation | V3 broad | V4 broad | V4-only | Disposition | Shortlisted | Outcome |",
            "| --- | --- | --- | --- | --- | --- | --- | --- |",
        };
        lines.AddRange(reports.Select(item => "| " + item.ItemId + " | " + item.Relation + " | " + item.V3BroadPresent + " | " + item.BroadPresent + " | " + item.V4Only + " | " + item.Disposition + " | " + item.ShortlistPresent + " | " + item.Outcome + " |"));
        lines.AddRange(new[]
        {
            "",
            "IR-018 and IR-022 are DISTINCT_DIAGNOSTIC_COVERAGE only; they are excluded from the positive denominator and are not treated as negatives.",
            "",
            "## Firewall",
            "",
            "GoldOpenedAfterFreeze=true; MODEL_CALLS=0; PROVIDER_CALLS=0; V4E artifacts mutated=false.",
            "",
            "## Gate",
            "",
            gate,
        });
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static IReadOnlyDictionary<string, SourceNode> ReadSourceNodes(JsonDocument source)
        => source.RootElement.GetProperty("sourceOccurrences").EnumerateArray()
            .Select(item => new SourceNode(item.GetProperty("nodeId").GetString()!, item.GetProperty("text").GetString()!, item.GetProperty("documentOrder").GetInt32()))
            .ToDictionary(item => item.NodeId, StringComparer.Ordinal);

    private static Candidate[] ReadCandidates(JsonDocument document)
        => document.RootElement.GetProperty("candidates").EnumerateArray().Select(item => new Candidate(
            item.TryGetProperty("pairId", out var pairId) ? pairId.GetString()! : item.GetProperty("candidateId").GetString()!,
            item.GetProperty("documentId").GetString()!,
            item.TryGetProperty("left", out var left) ? left.GetString()! : item.GetProperty("leftOccurrenceId").GetString()!,
            item.TryGetProperty("right", out var right) ? right.GetString()! : item.GetProperty("rightOccurrenceId").GetString()!,
            item.TryGetProperty("reasons", out var reasons) ? reasons.EnumerateArray().Select(reason => reason.GetString()!).ToArray() : Array.Empty<string>(),
            item.TryGetProperty("v4Only", out var v4Only) && v4Only.GetBoolean())).ToArray();

    private static bool SamePair(Candidate item, string left, string right)
        => (item.Left == left && item.Right == right) || (item.Left == right && item.Right == left);

    private static string PairKey(string documentId, string left, string right)
        => string.CompareOrdinal(left, right) < 0 ? documentId + "|" + left + "|" + right : documentId + "|" + right + "|" + left;

    private static string Endpoint(string documentId, string occurrence)
        => occurrence.StartsWith(documentId + ":", StringComparison.Ordinal) ? occurrence : documentId + ":" + occurrence;

    private static JsonDocument ReadJson(string root, string relativePath) => JsonDocument.Parse(File.ReadAllText(Full(root, relativePath)));
    private static string Full(string root, string relativePath) => Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string ReadTextHash(string root, string path) => Sha256File(Full(root, path));
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static string? GetOptionalString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static void Check(bool condition, string name, ICollection<string> failures) { if (!condition) failures.Add(name); }

    private sealed record FreezeIntegrity(string Integrity, string V4EManifestSha256, string ShortlistSha256, string RankingSha256, string SourceEvidenceSha256, string RequestManifestSha256, string FirewallSha256, string V3CompatibilitySha256, string V4BroadSha256, string SourceSha256, int ShortlistCount);
    private sealed record GoldCase(string ItemId, string DocumentId, string Relation, bool IsPositive, string Left, string Right);
    private sealed record SourceNode(string NodeId, string Text, int DocumentOrder);
    private sealed record Candidate(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons, bool V4Only);
    private sealed record CaseReport(string ItemId, string DocumentId, string Relation, bool IsPositive, string Left, string Right, object LeftSource, object RightSource, bool V3BroadPresent, bool BroadPresent, bool V4Only, string? CandidateId, IReadOnlyList<string> Reasons, JsonElement? Evidence, bool ShortlistPresent, string Outcome, string Attribution)
    {
        public string Disposition => ShortlistPresent ? "SHORTLISTED" : Outcome;
    }
}
