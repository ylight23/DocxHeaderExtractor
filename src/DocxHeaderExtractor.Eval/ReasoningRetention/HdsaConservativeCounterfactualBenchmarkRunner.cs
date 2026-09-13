using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Rebuilds the conservative V4 catalog from the frozen promotion replay and reuses the frozen
/// v3 parent decisions. It is intentionally zero-call and opens Structural Gold only after the
/// counterfactual prediction has been persisted and frozen.
/// </summary>
public static class HdsaConservativeCounterfactualBenchmarkRunner
{
    private const string V3Root = "eval/a99-closed-loop/hdsa-semantic-node-production-live/DOC-0205";
    private const string ReplayPath = "eval/a99-closed-loop/hdsa-semantic-merge-isolation-replay/DOC-0205/summary.v1.json";
    private const string GoldPath = "eval/a99-closed-loop/hdsa-parent-relation-live/DOC-0205/structural-gold.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-v4-conservative-counterfactual/DOC-0205";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var v3 = Path.Combine(repoRoot, V3Root.Replace('/', Path.DirectorySeparatorChar));
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);

        var replayPath = Path.Combine(repoRoot, ReplayPath.Replace('/', Path.DirectorySeparatorChar));
        Require(replayPath);
        using var replayDocument = JsonDocument.Parse(await File.ReadAllTextAsync(replayPath, ct));
        var replayRoot = replayDocument.RootElement;
        if (!string.Equals(replayRoot.GetProperty("conclusion").GetProperty("status").GetString(),
            "CONSERVATIVE_PROMOTION_REPLAY_PASS", StringComparison.Ordinal) ||
            replayRoot.GetProperty("execution").GetProperty("goldUsed").GetBoolean() ||
            replayRoot.GetProperty("execution").GetProperty("providerCalls").GetInt32() != 0 ||
            replayRoot.GetProperty("requestImpact").GetProperty("unexpectedChangedTargets").GetArrayLength() != 0)
            throw new InvalidDataException("CONSERVATIVE_PROMOTION_REPLAY_NOT_AUTHORIZED");

        var catalog = ReadV3Catalog(Path.Combine(v3, "semantic-catalog.v1.json"));
        var parentFiles = catalog.Entries.Select(entry => ReadFrozenV3Parent(v3, catalog, entry)).ToArray();
        if (parentFiles.Length != 12) throw new InvalidDataException("FROZEN_V3_PARENT_COUNT_EXPECTED_12");

        // The counterfactual deliberately reuses the frozen v3 request payloads.  The current
        // ROOT-v2 contract adds decisionOptions, so its newly serialized request is not expected
        // to have the historical v3 byte hash.  The guard below proves the historical bytes and
        // separately reports that intentional schema delta rather than silently weakening it.
        var requests = parentFiles.Select(item => item.FrozenRequest).ToArray();
        var decisions = parentFiles.Select(item => item.Decision).ToArray();
        var validations = parentFiles.Select(item => item.Validation).ToArray();
        if (validations.Any(item => !item.Accepted)) throw new InvalidDataException("FROZEN_V3_PARENT_VALIDATION_FAILED");
        var decisionByChild = decisions.ToDictionary(item => item.ChildSemanticNodeId, StringComparer.Ordinal);
        var hierarchy = HdsaSemanticHierarchyPipeline.Run(catalog,
            request => decisionByChild.TryGetValue(request.ChildSemanticNodeId, out var decision)
                ? decision
                : throw new InvalidDataException("FROZEN_V3_DECISION_MISSING:" + request.ChildSemanticNodeId));

        var predictionPath = Path.Combine(output, "prediction-complete.v1.json");
        await WriteJsonAsync(predictionPath, new
        {
            schemaVersion = "a99-hdsa-v4-conservative-counterfactual-prediction-v1",
            documentId = "DOC-0205",
            identitySource = "FROZEN_CONSERVATIVE_PROMOTION_REPLAY",
            parentSource = "FROZEN_V3_PARENT_PREDICTIONS",
            catalogFingerprint = catalog.CatalogFingerprint,
            requests,
            currentContractRequests = parentFiles.Select(item => item.CurrentRequest).ToArray(),
            requestCompatibility = parentFiles.Select(item => new
            {
                childSemanticNodeId = item.FrozenRequest.ChildSemanticNodeId,
                frozenV3RequestSha256 = item.FrozenRequestHash,
                currentContractRequestSha256 = item.CurrentRequestHash,
                historicalV3RequestHashVerified = item.HistoricalRequestHashMatches,
                commonRequestFieldsEquivalent = item.CommonRequestFieldsEquivalent,
                intentionalCurrentContractDelta = new[] { "decisionOptions (ROOT-v2)" },
            }).ToArray(),
            decisions,
            validations,
            hierarchy.Tree,
            hierarchy.GraphValidation,
            modelCalls = 0,
            providerCalls = 0,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
        }, ct);
        var freezePath = Path.Combine(output, "prediction-freeze.v1.json");
        await WriteJsonAsync(freezePath, new
        {
            schemaVersion = "a99-hdsa-v4-conservative-counterfactual-freeze-v1",
            documentId = "DOC-0205",
            catalogFingerprint = catalog.CatalogFingerprint,
            predictionSha256 = Sha256File(predictionPath),
            parentDecisionCount = decisions.Length,
            modelCalls = 0,
            providerCalls = 0,
            goldReadBeforeFreeze = false,
            frozenBeforeGold = true,
        }, ct);

        var evaluation = EvaluateAfterFreeze(repoRoot, catalog, hierarchy, freezePath);
        var summary = new
        {
            schemaVersion = "a99-hdsa-v4-conservative-counterfactual-summary-v1",
            status = "COMPLETE",
            documentId = "DOC-0205",
            identitySource = "FROZEN_CONSERVATIVE_PROMOTION_REPLAY",
            parentSource = "FROZEN_V3_PARENT_PREDICTIONS",
            catalogFingerprint = catalog.CatalogFingerprint,
            catalogEquivalentToV3 = replayRoot.GetProperty("replayCatalog").GetProperty("catalogEquivalentToV3").GetBoolean(),
            requestsEquivalentToFrozenV3 = parentFiles.All(item => item.HistoricalRequestHashMatches),
            currentContractRequestBytesEquivalentToFrozenV3 = parentFiles.All(item => item.CurrentRequestHash == item.FrozenRequestHash),
            commonRequestFieldsEquivalent = parentFiles.All(item => item.CommonRequestFieldsEquivalent),
            currentContractRequestDelta = "decisionOptions (ROOT-v2) is present in current requests but absent from frozen v3 request schema",
            parentDecisionCount = decisions.Length,
            treeValid = hierarchy.Tree.IsValid,
            levelSource = "HDSA_TREE_DEPTH",
            evaluation,
            modelCalls = 0,
            providerCalls = 0,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
        };
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), summary, ct);

        Console.WriteLine("HDSA_V4_CONSERVATIVE_COUNTERFACTUAL_STATUS=COMPLETE");
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine($"PARENT_F1={evaluation.Parent.F1:F4}");
        Console.WriteLine($"LEVEL_EXACT={evaluation.Level.Exact}/{evaluation.Level.Evaluated}");
        Console.WriteLine($"TREE_VALID={hierarchy.Tree.IsValid}");
        Console.WriteLine($"SUMMARY={Path.Combine(OutputRoot, "summary.v1.json")}");
        return 0;
    }

    private static HdsaFrozenSemanticNodeCatalog ReadV3Catalog(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var sourceSha = root.GetProperty("sourceSha256").GetString()!;
        var snapshot = root.GetProperty("preprocessingSnapshotHash").GetString()!;
        var resolverVersion = root.GetProperty("resolverVersion").GetString()!;
        var entries = root.GetProperty("entries").EnumerateArray().Select(item => new
        {
            Id = item.GetProperty("semanticNodeId").GetString()!,
            Aliases = item.GetProperty("memberOccurrenceIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            Text = item.GetProperty("canonicalText").GetString()!,
            Order = item.GetProperty("sourceOrder").GetInt32(),
        }).ToArray();
        var input = new HdsaSemanticNodeResolutionInput(sourceSha, snapshot,
            entries.Select(item => new HdsaSemanticNodeSourceOccurrence(item.Aliases.Single(), item.Order, item.Text)).ToArray(), false);
        var result = new HdsaSemanticNodeResolutionV3Result(sourceSha, snapshot,
            entries.Select(item => new HdsaSemanticNodePrediction(item.Id, item.Aliases, item.Text, "FROZEN_V3_CATALOG", resolverVersion, false)).ToArray(),
            [], [], [], resolverVersion, false);
        var catalog = HdsaSemanticNodeCatalogBuilder.Build(input, result);
        if (!string.Equals(catalog.CatalogFingerprint, root.GetProperty("catalogFingerprint").GetString(), StringComparison.Ordinal))
            throw new InvalidDataException("V3_CATALOG_FINGERPRINT_RECONSTRUCTION_MISMATCH");
        return catalog;
    }

    private static FrozenParent ReadFrozenV3Parent(string v3, HdsaFrozenSemanticNodeCatalog catalog, HdsaSemanticNodeCatalogEntry entry)
    {
        var dir = Path.Combine(v3, entry.SemanticNodeId);
        var requestPath = Path.Combine(dir, "request.v1.json");
        var predictionPath = Path.Combine(dir, "prediction.v1.json");
        var freezePath = Path.Combine(dir, "freeze.v1.json");
        Require(requestPath); Require(predictionPath); Require(freezePath);
        using var requestDocument = JsonDocument.Parse(File.ReadAllText(requestPath));
        using var predictionDocument = JsonDocument.Parse(File.ReadAllText(predictionPath));
        using var freezeDocument = JsonDocument.Parse(File.ReadAllText(freezePath));
        var requestRoot = requestDocument.RootElement;
        var predictionRoot = predictionDocument.RootElement;
        var freezeRoot = freezeDocument.RootElement;
        if (GetBooleanOrDefault(requestRoot, "goldReadBeforeFreeze") ||
            GetBooleanOrDefault(predictionRoot, "goldReadBeforeFreeze") ||
            GetBooleanOrDefault(freezeRoot, "goldReadBeforeFreeze") ||
            (freezeRoot.TryGetProperty("frozenBeforeGold", out var frozenBeforeGold) && !frozenBeforeGold.GetBoolean()))
            throw new InvalidDataException("FROZEN_V3_PARENT_GOLD_FIREWALL_FAILED:" + entry.SemanticNodeId);
        var request = JsonSerializer.Deserialize<HdsaSemanticNodeParentReasoningRequest>(
            requestRoot.GetProperty("request").GetRawText(), JsonOptions)
            ?? throw new InvalidDataException("FROZEN_V3_PARENT_REQUEST_INVALID:" + entry.SemanticNodeId);
        var generated = HdsaSemanticNodeParentReasoningContract.CreateRequest(catalog, entry.SemanticNodeId);
        var generatedJson = JsonSerializer.Serialize(generated, JsonOptions);
        var generatedHash = Sha256Text(generatedJson);
        var storedHash = requestRoot.GetProperty("requestSha256").GetString()!;
        var historical = new HistoricalV3ParentRequest(
            request.CatalogFingerprint,
            request.ChildSemanticNodeId,
            request.CandidateParentSemanticNodeIds,
            request.AuthoritativeParentUniverse,
            request.Evidence,
            request.GoldDerivedInput);
        var historicalHash = Sha256Text(JsonSerializer.Serialize(historical, JsonOptions));
        var freezeHash = freezeRoot.GetProperty("requestSha256").GetString()!;
        var commonEquivalent = CommonRequestFieldsEquivalent(request, generated);
        if (!string.Equals(request.CatalogFingerprint, catalog.CatalogFingerprint, StringComparison.Ordinal) ||
            !string.Equals(storedHash, historicalHash, StringComparison.Ordinal) ||
            !string.Equals(freezeHash, historicalHash, StringComparison.Ordinal) ||
            !commonEquivalent)
            throw new InvalidDataException($"FROZEN_V3_PARENT_REQUEST_DRIFT:{entry.SemanticNodeId};stored={storedHash};historical={historicalHash};current={generatedHash};freeze={freezeHash};commonEquivalent={commonEquivalent};catalog={request.CatalogFingerprint}/{catalog.CatalogFingerprint}");
        var decision = HdsaSemanticNodeParentReasoningContract.Parse(predictionRoot.GetProperty("rawResponse").GetString()!);
        var validation = HdsaSemanticNodeParentReasoningContract.Validate(generated, decision, catalog);
        return new(request, generated, decision, validation, string.Equals(storedHash, historicalHash, StringComparison.Ordinal),
            commonEquivalent, historicalHash, generatedHash);
    }

    private static bool CommonRequestFieldsEquivalent(
        HdsaSemanticNodeParentReasoningRequest frozen,
        HdsaSemanticNodeParentReasoningRequest current) =>
        string.Equals(frozen.CatalogFingerprint, current.CatalogFingerprint, StringComparison.Ordinal) &&
        string.Equals(frozen.ChildSemanticNodeId, current.ChildSemanticNodeId, StringComparison.Ordinal) &&
        frozen.CandidateParentSemanticNodeIds.SequenceEqual(current.CandidateParentSemanticNodeIds, StringComparer.Ordinal) &&
        frozen.AuthoritativeParentUniverse.Count == current.AuthoritativeParentUniverse.Count &&
        frozen.AuthoritativeParentUniverse.Zip(current.AuthoritativeParentUniverse).All(pair =>
            string.Equals(pair.First.SemanticNodeId, pair.Second.SemanticNodeId, StringComparison.Ordinal) &&
            pair.First.MemberOccurrenceIds.SequenceEqual(pair.Second.MemberOccurrenceIds, StringComparer.Ordinal) &&
            string.Equals(pair.First.CanonicalText, pair.Second.CanonicalText, StringComparison.Ordinal) &&
            pair.First.SourceOrder == pair.Second.SourceOrder) &&
        string.Equals(frozen.Evidence.DocumentOrder, current.Evidence.DocumentOrder, StringComparison.Ordinal) &&
        string.Equals(frozen.Evidence.Numbering, current.Evidence.Numbering, StringComparison.Ordinal) &&
        string.Equals(frozen.Evidence.Styles, current.Evidence.Styles, StringComparison.Ordinal) &&
        string.Equals(frozen.Evidence.SemanticRoles, current.Evidence.SemanticRoles, StringComparison.Ordinal) &&
        string.Equals(frozen.Evidence.LocalContext, current.Evidence.LocalContext, StringComparison.Ordinal) &&
        string.Equals(frozen.Evidence.GlobalContext, current.Evidence.GlobalContext, StringComparison.Ordinal) &&
        frozen.GoldDerivedInput == current.GoldDerivedInput;

    private static CounterfactualEvaluation EvaluateAfterFreeze(string repoRoot, HdsaFrozenSemanticNodeCatalog catalog,
        HdsaSemanticHierarchyRunResult hierarchy, string freezePath)
    {
        using var freeze = JsonDocument.Parse(File.ReadAllText(freezePath));
        if (freeze.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean())
            throw new InvalidDataException("GOLD_OPEN_BEFORE_COUNTERFACTUAL_FREEZE");
        var goldPath = Path.Combine(repoRoot, GoldPath.Replace('/', Path.DirectorySeparatorChar));
        using var gold = JsonDocument.Parse(File.ReadAllText(goldPath));
        var root = gold.RootElement;
        if (!string.Equals(root.GetProperty("sourceSha256").GetString(), catalog.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("COUNTERFACTUAL_GOLD_SOURCE_MISMATCH");
        var aliases = root.GetProperty("occurrences").EnumerateArray()
            .Where(item => item.GetProperty("reviewStatus").GetString() == "RESOLVED")
            .ToDictionary(item => item.GetProperty("sourceAlias").GetString()!, item => item.GetProperty("semanticNodeId").GetString()!, StringComparer.Ordinal);
        var goldMembers = aliases.GroupBy(item => item.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var nodeMap = catalog.Entries.ToDictionary(entry => entry.SemanticNodeId, entry =>
        {
            var ids = entry.MemberOccurrenceIds.Where(aliases.ContainsKey).Select(alias => aliases[alias]).Distinct(StringComparer.Ordinal).ToArray();
            return ids.Length == 1 ? ids[0] : null;
        }, StringComparer.Ordinal);
        var goldEdges = root.GetProperty("tree").GetProperty("parentOf").EnumerateArray()
            .Select(item => item.GetProperty("parent").GetString() + ">" + item.GetProperty("child").GetString())
            .ToHashSet(StringComparer.Ordinal);
        var predictedEdges = hierarchy.GraphValidation.AcceptedRelations
            .Where(item => item.Relation == HdsaRelationType.ParentOf)
            .Select(item => (parent: nodeMap.GetValueOrDefault(item.From), child: nodeMap.GetValueOrDefault(item.To)))
            .Where(item => item.child is not null)
            .ToArray();
        var mappedPredictedEdges = predictedEdges
            .Where(item => item.parent is not null)
            .Select(item => item.parent + ">" + item.child)
            .ToHashSet(StringComparer.Ordinal);
        // Match the historical v3 metrics policy: a predicted edge whose evaluated child is
        // known but whose parent is outside the evaluated semantic universe is a false positive,
        // not silently discarded as an unmappable edge.
        var unmappablePredictedEdges = predictedEdges.Count(item => item.parent is null);
        var tp = mappedPredictedEdges.Intersect(goldEdges, StringComparer.Ordinal).Count();
        var fp = mappedPredictedEdges.Except(goldEdges, StringComparer.Ordinal).Count() + unmappablePredictedEdges;
        var fn = goldEdges.Except(mappedPredictedEdges, StringComparer.Ordinal).Count();
        var precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
        var recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        var predictedDepth = hierarchy.Tree.Nodes.ToDictionary(item => item.Id, item => item.Level, StringComparer.Ordinal);
        var goldDepth = GoldDepth(goldEdges);
        var levelRows = catalog.Entries.Where(entry => nodeMap[entry.SemanticNodeId] is not null)
            .Select(entry => new
            {
                semanticNodeId = entry.SemanticNodeId,
                predictedLevel = predictedDepth.GetValueOrDefault(entry.SemanticNodeId),
                goldLevel = goldDepth[nodeMap[entry.SemanticNodeId]!],
                exact = predictedDepth.GetValueOrDefault(entry.SemanticNodeId) == goldDepth[nodeMap[entry.SemanticNodeId]!],
            }).ToArray();
        return new CounterfactualEvaluation(
            true,
            catalog.Entries.Count,
            goldMembers.Count,
            new ParentMetrics(tp, fp, fn, precision, recall, f1, goldEdges.Count,
                mappedPredictedEdges.Count, unmappablePredictedEdges),
            new LevelMetrics(levelRows.Count(item => item.exact), levelRows.Length,
                levelRows.Length == 0 ? 0 : (double)levelRows.Count(item => item.exact) / levelRows.Length, levelRows),
            hierarchy.Tree.IsValid,
            hierarchy.CatalogWasMutated);
    }

    private static IReadOnlyDictionary<string, int> GoldDepth(IReadOnlySet<string> edges)
    {
        var parent = edges.ToDictionary(edge => edge[(edge.IndexOf('>') + 1)..], edge => edge[..edge.IndexOf('>')], StringComparer.Ordinal);
        var all = parent.Keys.Concat(parent.Values).ToHashSet(StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        int D(string id) => depth.TryGetValue(id, out var value) ? value : depth[id] = parent.TryGetValue(id, out var p) ? D(p) + 1 : 1;
        foreach (var id in all) _ = D(id);
        return depth;
    }

    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static bool GetBooleanOrDefault(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.GetBoolean();
    private static string Sha256File(string path) => Sha256Text(File.ReadAllText(path));
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), ct);
    private static void Require(string path) { if (!File.Exists(path)) throw new FileNotFoundException("COUNTERFACTUAL_INPUT_MISSING", path); }

    private sealed record FrozenParent(HdsaSemanticNodeParentReasoningRequest FrozenRequest,
        HdsaSemanticNodeParentReasoningRequest CurrentRequest,
        HdsaSemanticNodeParentDecision Decision, HdsaSemanticNodeParentDecisionValidation Validation,
        bool HistoricalRequestHashMatches,
        bool CommonRequestFieldsEquivalent,
        string FrozenRequestHash,
        string CurrentRequestHash);

    private sealed record HistoricalV3ParentRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("catalogFingerprint")] string CatalogFingerprint,
        [property: System.Text.Json.Serialization.JsonPropertyName("childSemanticNodeId")] string ChildSemanticNodeId,
        [property: System.Text.Json.Serialization.JsonPropertyName("candidateParentSemanticNodeIds")] IReadOnlyList<string> CandidateParentSemanticNodeIds,
        [property: System.Text.Json.Serialization.JsonPropertyName("authoritativeParentUniverse")] IReadOnlyList<HdsaSemanticParentUniverseEntry> AuthoritativeParentUniverse,
        [property: System.Text.Json.Serialization.JsonPropertyName("evidence")] HdsaRelationEvidence Evidence,
        [property: System.Text.Json.Serialization.JsonPropertyName("goldDerivedInput")] bool GoldDerivedInput);

    private sealed record CounterfactualEvaluation(
        bool GoldOpenedAfterPredictionFreeze,
        int PredictedSemanticNodes,
        int GoldSemanticNodes,
        ParentMetrics Parent,
        LevelMetrics Level,
        bool TreeValid,
        bool CatalogMutated);

    private sealed record ParentMetrics(int TP, int FP, int FN, double Precision, double Recall, double F1,
        int GoldEdgeCount, int MappedPredictedEdgeCount, int UnmappablePredictedEdges);
    private sealed record LevelMetrics(int Exact, int Evaluated, double Accuracy, object Rows);
}
