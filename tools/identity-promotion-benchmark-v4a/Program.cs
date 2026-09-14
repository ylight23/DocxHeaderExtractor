using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IdentityPromotionBenchmarkV4A;

internal static class Program
{
    private const string V1Root = "artifacts/identity-benchmark/v1";
    private const string V2Root = "artifacts/identity-benchmark/v2";
    private const string V3Root = "artifacts/identity-benchmark/v3";
    private const string V3Evaluation = V3Root + "/evaluation";
    private const string CurrentGenerator = "src/DocxHeaderExtractor.Core/Models/HdsaDeterministicIdentityCandidateGenerator.cs";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Regex StructuralKeyRegex = new(
        @"^\s*(SESSION|SECTION)\s+([IVXLCDM]+|\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ContinuationMarkerRegex = new(
        @"\(\s*cont(?:['’]?d|inued)\s*\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FinalAcronymParentheticalRegex = new(
        @"\(\s*[A-Z][A-Z0-9&./ -]{1,15}\s*\)\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
            Console.Error.WriteLine($"V4A_ERROR={ex}");
            return 2;
        }
    }

    private static void Run(string root)
    {
        var source = ReadJson(root, V2Root + "/source-catalog.json");
        var broad = ReadJson(root, V1Root + "/candidate-set.json");
        var v3Evaluation = ReadJson(root, V3Evaluation + "/case-report.json");
        var nodesByDocument = ReadSourceNodes(source);
        var candidates = ReadCandidates(broad);
        var candidatePairs = candidates.Select(item => PairKey(item.DocumentId, item.Left, item.Right)).ToHashSet(StringComparer.Ordinal);
        var targets = ReadTargets(v3Evaluation);
        var generated = candidates.SelectMany(item => item.Reasons.Select(reason => new { item.DocumentId, item.PairId, reason }))
            .GroupBy(item => item.reason, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var ruleCoverage = new[]
        {
            BuildRuleCoverage("ADJACENT_ORDER", candidates.Where(item => item.Reasons.Contains("ADJACENT_ORDER", StringComparer.Ordinal)).ToArray(), nodesByDocument, targets),
            BuildRuleCoverage("NORMALIZED_TEXT_AFFINITY", candidates.Where(item => item.Reasons.Contains("NORMALIZED_TEXT_AFFINITY", StringComparer.Ordinal)).ToArray(), nodesByDocument, targets),
        };

        var traces = targets.Select(target => BuildTrace(target, nodesByDocument, candidates, candidatePairs)).ToArray();
        var counterfactuals = new[]
        {
            BuildCounterfactual(
                "FINAL_ACRONYM_PARENTHETICAL_EQUIVALENCE",
                "Remove only one terminal parenthetical whose content matches a bounded uppercase acronym grammar; preserve all other text and do not strip arbitrary punctuation.",
                nodesByDocument,
                candidatePairs,
                targets,
                item => StripFinalAcronym(item.Text)),
            BuildCounterfactual(
                "SHARED_STRUCTURAL_SESSION_SECTION_KEY",
                "Use only a parser-independent leading SESSION/SECTION Roman-or-decimal key as a retrieval hint; it never labels or merges a relation.",
                nodesByDocument,
                candidatePairs,
                targets,
                item => StructuralKey(item.Text)),
            BuildCounterfactual(
                "CONTINUATION_MARKER_WITH_SHARED_STRUCTURAL_KEY",
                "Retrieve a pair when one source unit has a terminal Cont’d/Contd/Continued marker and both units share the same leading SESSION/SECTION key.",
                nodesByDocument,
                candidatePairs,
                targets,
                item => StructuralKey(item.Text),
                (left, right) => ContinuationMarkerRegex.IsMatch(left.Text) || ContinuationMarkerRegex.IsMatch(right.Text)),
        };

        var v3Manifest = ReadJson(root, V3Root + "/manifest.json");
        var v3bEvaluation = ReadJson(root, V3Evaluation + "/retrieval-evaluation.json");
        var output = Full(root, "artifacts/identity-benchmark/v4");
        Directory.CreateDirectory(output);

        var metadata = new
        {
            artifactKind = "a99_identity_benchmark_v4a_retrieval_failure_forensics",
            schemaVersion = "a99-identity-benchmark-v4a-retrieval-failure-forensics-v1",
            status = "READY_FOR_V4_RETRIEVAL_CHALLENGER_DESIGN",
            phase = "V4A_BROAD_RETRIEVAL_FAILURE_FORENSICS",
            devExposed = true,
            sourceCatalogPath = V2Root + "/source-catalog.json",
            sourceCatalogFingerprint = source.RootElement.GetString("catalogFingerprint"),
            sourceCatalogSha256 = Sha256File(Full(root, V2Root + "/source-catalog.json")),
            v3aManifestSha256 = Sha256File(Full(root, V3Root + "/manifest.json")),
            v3bEvaluationSha256 = Sha256File(Full(root, V3Evaluation + "/retrieval-evaluation.json")),
            currentGenerator = CurrentGenerator,
            currentGeneratorSha256 = Sha256File(Full(root, CurrentGenerator)),
            broadCandidateCount = candidates.Length,
            normalizedTextAffinityIncidences = generated.GetValueOrDefault("NORMALIZED_TEXT_AFFINITY"),
            goldUsedForGeneration = false,
            goldDerivedTargetReferences = true,
            modelUsed = false,
            providerCalls = 0,
            modelCalls = 0,
            productionBehaviorChanged = false,
            v1V2V3ArtifactsModified = false,
            knownV3Gate = v3bEvaluation.RootElement.GetString("status"),
            knownV3AStatus = v3Manifest.RootElement.GetString("status"),
        };

        WriteJson(Path.Combine(output, "retrieval-failure-forensics.v1.json"), new
        {
            metadata,
            targetTrace = traces,
            ruleCoverage,
            architecturalDiagnosis = "CURRENT_BROAD_RETRIEVAL_MULTI_FACTOR_GAP",
            diagnosisRationale = new[]
            {
                "The current generator has only adjacency and exact normalized-text equality; neither rule sees the two missed endpoint pairs.",
                "IR-019 needs a source-only structural/continuation retrieval signal because parser text fragmentation prevents exact normalized equality and the endpoints are non-adjacent.",
                "IR-020 needs a conservative lexical-variant or structural-key retrieval signal because the terminal acronym makes exact normalized equality fail and the endpoints are non-adjacent.",
                "This is retrieval diagnosis only; no relation label, merge, promotion, or Gold-derived candidate was created.",
            },
            firewall = new
            {
            goldUsedForGeneration = false,
            goldUsedForRuleSelection = false,
            goldUsedToChooseThreshold = false,
            goldDerivedTargetReferences = true,
            modelOutputsUsed = false,
                providerCalls = 0,
                modelCalls = 0,
                devExposed = true,
            },
        });

        WriteJson(Path.Combine(output, "candidate-rule-coverage.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4a_candidate_rule_coverage",
            schemaVersion = "a99-identity-benchmark-v4a-candidate-rule-coverage-v1",
            rules = ruleCoverage,
            totalBroadCandidates = candidates.Length,
            sourceDocuments = nodesByDocument.Count,
            generatedReasonCounts = generated,
            generatorContract = "HdsaDeterministicIdentityCandidateGenerator v1: adjacent index OR exact normalized text equality",
            providerCalls = 0,
            goldUsedForGeneration = false,
        });

        WriteJson(Path.Combine(output, "counterfactual-source-only-signals.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4a_counterfactual_source_only_signals",
            schemaVersion = "a99-identity-benchmark-v4a-counterfactual-source-only-signals-v1",
            status = "COUNTERFACTUAL_ONLY_NO_RULE_IMPLEMENTED",
            declaredSignalsBeforeMeasurement = counterfactuals.Select(item => item.Signal).ToArray(),
            signals = counterfactuals,
            noGoldOptimizedThresholdSearch = true,
            goldUsedForGeneration = false,
            devExposed = true,
            providerCalls = 0,
        });

        File.WriteAllText(Path.Combine(output, "retrieval-failure-forensics.v1.md"), BuildMarkdown(metadata, traces, ruleCoverage, counterfactuals), new UTF8Encoding(false));
        Console.WriteLine($"V4A_STATUS=READY_FOR_V4_RETRIEVAL_CHALLENGER_DESIGN;BROAD={candidates.Length};NORMALIZED_TEXT_AFFINITY={generated.GetValueOrDefault("NORMALIZED_TEXT_AFFINITY")};PROVIDER_CALLS=0;MODEL_CALLS=0");
    }

    private static object BuildRuleCoverage(string rule, IReadOnlyList<Candidate> matching, IReadOnlyDictionary<string, SourceNode[]> nodesByDocument, IReadOnlyList<Target> targets)
    {
        var degrees = matching.SelectMany(item => new[] { item.Left, item.Right }).GroupBy(item => item, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var docs = matching.GroupBy(item => item.DocumentId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new
        {
            rule,
            candidateCount = matching.Count,
            documentsAffected = docs.Count,
            perDocumentCandidateCount = docs,
            maximumEndpointFanOut = degrees.Values.DefaultIfEmpty(0).Max(),
            canTheoreticallyRetrieveIr019 = matching.Any(item => SamePair(item, targets.Single(x => x.ItemId == "IR-019"))),
            canTheoreticallyRetrieveIr020 = matching.Any(item => SamePair(item, targets.Single(x => x.ItemId == "IR-020"))),
            canTheoreticallyRetrieveIr021 = matching.Any(item => SamePair(item, targets.Single(x => x.ItemId == "IR-021"))),
            interpretation = rule == "ADJACENT_ORDER"
                ? "Generator emits only adjacent sorted source-array indices; it is not a broad distance window."
                : "Generator emits only exact equality after Unicode normalization, whitespace collapse, and uppercasing.",
        };
    }

    private static TargetTrace BuildTrace(Target target, IReadOnlyDictionary<string, SourceNode[]> nodesByDocument,
        IReadOnlyList<Candidate> candidates, IReadOnlySet<string> candidatePairs)
    {
        var nodes = nodesByDocument[target.DocumentId];
        var left = nodes.Single(item => item.NodeId == target.Left);
        var right = nodes.Single(item => item.NodeId == target.Right);
        var leftIndex = Array.IndexOf(nodes, left);
        var rightIndex = Array.IndexOf(nodes, right);
        var indexDistance = Math.Abs(leftIndex - rightIndex);
        var normalizedLeft = Normalize(left.Text);
        var normalizedRight = Normalize(right.Text);
        var matchingCandidate = candidates.FirstOrDefault(item => SamePair(item, target));
        return new TargetTrace(
            target.ItemId, target.DocumentId, target.Relation, target.IsPositive, target.Left, target.Right,
            new SourceFacts(left.NodeId, left.Text, left.DocumentOrder, leftIndex, normalizedLeft, StructuralKey(left.Text), ContinuationMarkerRegex.IsMatch(left.Text)),
            new SourceFacts(right.NodeId, right.Text, right.DocumentOrder, rightIndex, normalizedRight, StructuralKey(right.Text), ContinuationMarkerRegex.IsMatch(right.Text)),
            new
            {
                sourceArrayIndexDistance = indexDistance,
                interveningSourceUnits = Math.Max(0, indexDistance - 1),
                documentOrderDistance = Math.Abs(left.DocumentOrder - right.DocumentOrder),
                sameNormalizedText = normalizedLeft == normalizedRight,
                tokenOverlap = TokenOverlap(left.Text, right.Text),
                sourcePageDistance = "UNAVAILABLE_IN_FROZEN_CATALOG",
                styleLayoutCompatibility = "UNAVAILABLE_IN_FROZEN_CATALOG",
                numberingIdentity = "UNAVAILABLE_IN_FROZEN_CATALOG",
                scopeIdentity = "UNAVAILABLE_IN_FROZEN_CATALOG",
            },
            new RuleTrace("ADJACENT_ORDER", indexDistance == 1 ? "ELIGIBLE" : "DISTANCE_FAILED", indexDistance == 1,
                "The v1 generator tests sorted source-array adjacency (j == i + 1); no broader distance rule exists."),
            new RuleTrace("NORMALIZED_TEXT_AFFINITY", normalizedLeft == normalizedRight ? "ELIGIBLE" : "TEXT_SIGNAL_FAILED", normalizedLeft == normalizedRight,
                "The v1 generator requires exact equality after Normalize(), whitespace collapse, and ToUpperInvariant()."),
            new RuleTrace("SCOPE", "UNAVAILABLE", false, "No scope blocker is implemented by the current generator."),
            new RuleTrace("PARSER_STRUCTURAL_SIGNAL", "NOT_IMPLEMENTED", false, "No numbering/style/page/continuation structural rule is implemented."),
            matchingCandidate is not null,
            matchingCandidate?.PairId,
            matchingCandidate?.Reasons ?? Array.Empty<string>(),
            matchingCandidate is null ? "FILTERED_NO_ACTIVE_RULE" : "EMITTED",
            target.IsPositive ? "DEV_EXPOSED_TARGET" : "DEV_EXPOSED_CONTRAST_ONLY");
    }

    private static Counterfactual BuildCounterfactual(string signal, string definition, IReadOnlyDictionary<string, SourceNode[]> nodesByDocument,
        IReadOnlySet<string> existingPairs, IReadOnlyList<Target> targets, Func<SourceNode, string?> keySelector,
        Func<SourceNode, SourceNode, bool>? pairPredicate = null)
    {
        var additions = new List<AddedPair>();
        foreach (var document in nodesByDocument.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var groups = document.Value.Select(item => (Node: item, Key: keySelector(item)))
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .GroupBy(item => item.Key!, StringComparer.Ordinal);
            foreach (var group in groups)
            {
                var items = group.Select(item => item.Node).OrderBy(item => item.DocumentOrder).ThenBy(item => item.NodeId, StringComparer.Ordinal).ToArray();
                for (var i = 0; i < items.Length; i++)
                for (var j = i + 1; j < items.Length; j++)
                {
                    if (pairPredicate is not null && !pairPredicate(items[i], items[j]))
                        continue;
                    var pairKey = PairKey(document.Key, items[i].NodeId, items[j].NodeId);
                    if (!existingPairs.Contains(pairKey))
                        additions.Add(new AddedPair(document.Key, items[i].NodeId, items[j].NodeId, group.Key));
                }
            }
        }

        var additionsByDocument = additions.GroupBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var additionalDegree = additions.SelectMany(item => new[] { item.Left, item.Right }).GroupBy(item => item, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var additionPairs = additions.Select(item => PairKey(item.DocumentId, item.Left, item.Right)).ToHashSet(StringComparer.Ordinal);
        var affectedTargets = targets.ToDictionary(
            item => item.ItemId,
            item => existingPairs.Contains(PairKey(item.DocumentId, item.Left, item.Right)) ? "ALREADY_EXISTING" :
                additionPairs.Contains(PairKey(item.DocumentId, item.Left, item.Right)) ? "ADDED_COUNTERFACTUAL" : "NOT_EXPOSED",
            StringComparer.Ordinal);
        return new Counterfactual(signal, definition, additions.Count, additionsByDocument, additionalDegree.Values.DefaultIfEmpty(0).Max(),
            additions.Take(20).ToArray(), affectedTargets, "Source-only grouping estimate; no production rule was changed or selected.");
    }

    private static Target[] ReadTargets(JsonDocument document)
        => document.RootElement.GetProperty("cases").EnumerateArray().Where(item =>
                item.GetString("itemId") is "IR-019" or "IR-020" or "IR-021")
            .Select(item => new Target(item.GetString("itemId")!, item.GetString("documentId")!, item.GetString("relation")!, item.GetBoolean("isPositive"),
                item.GetString("left")!, item.GetString("right")!)).ToArray();

    private static Dictionary<string, SourceNode[]> ReadSourceNodes(JsonDocument source)
        => source.RootElement.GetProperty("sourceOccurrences").EnumerateArray()
            .Select(item => new SourceNode(item.GetString("nodeId")!, item.GetString("text")!, item.GetInt32("documentOrder")))
            .GroupBy(item => DocumentOf(item.NodeId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.DocumentOrder).ThenBy(item => item.NodeId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

    private static Candidate[] ReadCandidates(JsonDocument document)
        => document.RootElement.GetProperty("candidates").EnumerateArray().Select(item => new Candidate(
            item.GetString("pairId")!, item.GetString("documentId")!, item.GetString("left")!, item.GetString("right")!,
            item.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString()!).ToArray())).ToArray();

    private static string StripFinalAcronym(string text)
        => Normalize(FinalAcronymParentheticalRegex.IsMatch(text) ? FinalAcronymParentheticalRegex.Replace(text, string.Empty).TrimEnd() : text);

    private static string? StructuralKey(string text)
    {
        var match = StructuralKeyRegex.Match(text);
        return match.Success ? $"{match.Groups[1].Value.ToUpperInvariant()} {match.Groups[2].Value.ToUpperInvariant()}" : null;
    }

    private static string Normalize(string value)
        => string.Join(' ', value.Normalize().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static double TokenOverlap(string left, string right)
    {
        var a = Normalize(left).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var b = Normalize(right).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return a.Count == 0 && b.Count == 0 ? 1d : a.Count == 0 || b.Count == 0 ? 0d : a.Intersect(b, StringComparer.Ordinal).Count() / (double)a.Union(b, StringComparer.Ordinal).Count();
    }

    private static bool SamePair(Candidate item, Target target) => SamePair(item.DocumentId, item.Left, item.Right, target.DocumentId, target.Left, target.Right);
    private static bool SamePair(Candidate item, string left, string right) => item.Left == left && item.Right == right || item.Left == right && item.Right == left;
    private static bool SamePair(string documentA, string leftA, string rightA, string documentB, string leftB, string rightB)
        => documentA == documentB && (leftA == leftB && rightA == rightB || leftA == rightB && rightA == leftB);
    private static string PairKey(string documentId, string left, string right) => documentId + "|" + string.Join("|", new[] { left, right }.Order(StringComparer.Ordinal));
    private static string DocumentOf(string nodeId) => nodeId.Split(':', 2)[0];

    private static string BuildMarkdown(object metadata, IReadOnlyList<TargetTrace> traces, IReadOnlyList<object> ruleCoverage, IReadOnlyList<Counterfactual> counterfactuals)
    {
        var lines = new List<string>
        {
            "# A99 Identity Retrieval v4A — Broad-retrieval failure forensics",
            "",
            "Status: `READY_FOR_V4_RETRIEVAL_CHALLENGER_DESIGN`",
            "",
            "This is a DEV-exposed, offline forensic artifact. It does not implement a retrieval rule, create a v4 shortlist, build requests, call a provider, or modify V1/V2/V3 artifacts.",
            "",
            "## Frozen current behavior",
            "",
            "- Generator: `HdsaDeterministicIdentityCandidateGenerator v1`.",
            "- Active rules: sorted-array adjacency OR exact normalized-text equality.",
            "- Broad candidates: `95,999`.",
            "- `NORMALIZED_TEXT_AFFINITY` incidences: `89,476`.",
            "- `MODEL_CALLS=0`; `PROVIDER_CALLS=0`.",
            "- Gold/model output was not an input to candidate generation; target endpoints are DEV-exposed forensic references.",
            "",
            "## Target traces",
            "",
            "| Case | Relation | Adjacent rule | Text affinity | Broad candidate | Outcome |",
            "|---|---|---|---|---|---|",
        };
        lines.AddRange(traces.Select(item => $"| {item.ItemId} | {item.Relation} | {item.AdjacentRule.Status} | {item.TextAffinityRule.Status} | {(item.CurrentCandidatePresent ? "yes" : "no")} | {item.FinalOutcome} |"));
        lines.AddRange(new[] { "", "## Diagnosis", "", "- IR-019 is a broad miss because its endpoints are non-adjacent and parser-preserved text is not exactly equal after the current normalization. The frozen source contains a terminal continuation marker and a shared `SESSION V` key, but the current generator has no such rule.", "- IR-020 is a broad miss because its endpoints are non-adjacent and `(ITP)` makes exact normalized equality fail. The current generator has no bounded terminal-acronym or structural-key rule.", "- IR-021 is retrieved because its endpoints are non-adjacent but their normalized texts are exactly equal, so `NORMALIZED_TEXT_AFFINITY` emits the pair.", "- Narrowest architectural classification: `CURRENT_BROAD_RETRIEVAL_MULTI_FACTOR_GAP`.", "", "## Current rule coverage", "" });
        lines.AddRange(ruleCoverage.Select(item => "- " + JsonSerializer.Serialize(item, JsonOptions)));
        lines.AddRange(new[] { "", "## Counterfactual signals", "", "The following generic source-only transforms were declared before corpus-wide measurement. Their counts are impact estimates only; no signal was selected and no production behavior changed." });
        lines.AddRange(counterfactuals.Select(item => $"- `{item.Signal}`: +{item.AdditionalCandidatePairs:N0} deduplicated pairs; max added endpoint fan-out `{item.AdditionalMaximumEndpointFanOut}`."));
        lines.AddRange(new[] { "", "## Gate", "", "`READY_FOR_V4_RETRIEVAL_CHALLENGER_DESIGN`", "", "Next single recommendation: design one Gold-independent deterministic high-recall retrieval challenger using a bounded structural/lexical signal set, then freeze its broad candidate set before any verifier/provider call. Do not tune thresholds against these five DEV-exposed cases." });
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static JsonDocument ReadJson(string root, string relative) => JsonDocument.Parse(File.ReadAllText(Full(root, relative)));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));

    private static string? GetString(this JsonElement element, string property) => element.GetProperty(property).GetString();
    private static int GetInt32(this JsonElement element, string property) => element.GetProperty(property).GetInt32();
    private static bool GetBoolean(this JsonElement element, string property) => element.GetProperty(property).GetBoolean();
    private static string? GetString(this JsonDocument document, string property) => document.RootElement.GetProperty(property).GetString();

    private sealed record SourceNode(string NodeId, string Text, int DocumentOrder);
    private sealed record Candidate(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons);
    private sealed record Target(string ItemId, string DocumentId, string Relation, bool IsPositive, string Left, string Right);
    private sealed record RuleTrace(string Rule, string Status, bool Eligible, string Explanation);
    private sealed record SourceFacts(string SourceOccurrenceId, string Text, int DocumentOrder, int SourceArrayIndex, string NormalizedText, string? StructuralKey, bool HasContinuationMarker);
    private sealed record TargetTrace(string ItemId, string DocumentId, string Relation, bool IsPositive, string Left, string Right,
        SourceFacts LeftSource, SourceFacts RightSource, object SourceOnlyComparison, RuleTrace AdjacentRule, RuleTrace TextAffinityRule,
        RuleTrace ScopeRule, RuleTrace StructuralRule, bool CurrentCandidatePresent, string? CurrentCandidateId,
        IReadOnlyList<string> CurrentCandidateReasons, string FinalOutcome, string Exposure);
    private sealed record RuleCoverage(string Rule, int CandidateCount, int DocumentsAffected, IReadOnlyDictionary<string, int> PerDocumentCandidateCount,
        int MaximumEndpointFanOut, bool CanTheoreticallyRetrieveIr019, bool CanTheoreticallyRetrieveIr020, bool CanTheoreticallyRetrieveIr021, string Interpretation);
    private sealed record AddedPair(string DocumentId, string Left, string Right, string GroupKey);
    private sealed record Counterfactual(string Signal, string Definition, int AdditionalCandidatePairs,
        IReadOnlyDictionary<string, int> AdditionalPairsByDocument, int AdditionalMaximumEndpointFanOut,
        IReadOnlyList<AddedPair> SampleAddedPairs, IReadOnlyDictionary<string, string> TargetDiagnosticStatus, string SafetyNote);
}
