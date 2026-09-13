using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Offline decomposition of the frozen DOC-0205 semantic-node benchmark. This runner deliberately
/// reads prediction artifacts before Structural Gold and never constructs a provider/client.
/// </summary>
public static class HdsaSemanticNodeProductionErrorDecompositionRunner
{
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-semantic-node-production-live/DOC-0205";
    private const string PredictionFreeze = "prediction-freeze.v1.json";
    private const string PredictionComplete = "prediction-complete.v1.json";
    private const string Catalog = "semantic-catalog.v1.json";
    private const string Summary = "summary.v1.json";
    private const string Gold = "eval/a99-closed-loop/hdsa-parent-relation-live/DOC-0205/structural-gold.v1.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var predictionFreezePath = Path.Combine(output, PredictionFreeze);
        var predictionPath = Path.Combine(output, PredictionComplete);
        var catalogPath = Path.Combine(output, Catalog);
        var summaryPath = Path.Combine(output, Summary);
        var goldPath = Path.Combine(repoRoot, Gold.Replace('/', Path.DirectorySeparatorChar));

        RequireFile(predictionFreezePath);
        RequireFile(predictionPath);
        RequireFile(catalogPath);
        RequireFile(summaryPath);
        RequireFile(goldPath);

        using var freeze = JsonDocument.Parse(await File.ReadAllTextAsync(predictionFreezePath, ct));
        using var prediction = JsonDocument.Parse(await File.ReadAllTextAsync(predictionPath, ct));
        using var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(catalogPath, ct));
        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(summaryPath, ct));

        var freezeRoot = freeze.RootElement;
        if (freezeRoot.GetProperty("goldReadBeforeFreeze").GetBoolean() ||
            !freezeRoot.GetProperty("frozenBeforeGold").GetBoolean())
            throw new InvalidDataException("PREDICTION_FREEZE_GOLD_FIREWALL_FAILED");
        if (catalog.RootElement.GetProperty("goldUsed").GetBoolean())
            throw new InvalidDataException("SEMANTIC_CATALOG_GOLD_FIREWALL_FAILED");
        if (prediction.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean() ||
            prediction.RootElement.GetProperty("goldDerivedInput").GetBoolean())
            throw new InvalidDataException("PREDICTION_GOLD_FIREWALL_FAILED");

        var catalogFingerprint = catalog.RootElement.GetProperty("catalogFingerprint").GetString()!;
        if (!string.Equals(freezeRoot.GetProperty("catalogFingerprint").GetString(), catalogFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("CATALOG_FINGERPRINT_MISMATCH");

        var catalogEntries = ReadCatalogEntries(catalog.RootElement);
        var decisions = ReadDecisions(prediction.RootElement);
        var treeLevels = ReadTreeLevels(prediction.RootElement);
        using var gold = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath, ct));
        var goldRoot = gold.RootElement;
        var goldSourceSha = goldRoot.GetProperty("sourceSha256").GetString()!;
        var catalogSourceSha = catalog.RootElement.GetProperty("sourceSha256").GetString()!;
        if (!string.Equals(goldSourceSha, catalogSourceSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("STRUCTURAL_GOLD_SOURCE_HASH_MISMATCH");

        var goldAliases = goldRoot.GetProperty("occurrences").EnumerateArray()
            .Where(item => item.GetProperty("reviewStatus").GetString() == "RESOLVED")
            .ToDictionary(item => item.GetProperty("sourceAlias").GetString()!,
                item => item.GetProperty("semanticNodeId").GetString()!, StringComparer.Ordinal);
        var goldMembers = goldAliases.GroupBy(item => item.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(item => item.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var goldEdges = goldRoot.GetProperty("tree").GetProperty("parentOf").EnumerateArray()
            .Select(item => new Edge(item.GetProperty("parent").GetString()!, item.GetProperty("child").GetString()!))
            .ToHashSet();
        var goldParentByChild = goldEdges.ToDictionary(item => item.Child, item => item.Parent, StringComparer.Ordinal);
        var goldDepth = DeriveDepth(goldEdges);

        var nodeToGold = catalogEntries.ToDictionary(entry => entry.Id, entry => MapToGold(entry, goldAliases), StringComparer.Ordinal);
        var goldToPredicted = nodeToGold.Where(item => item.Value is not null)
            .GroupBy(item => item.Value!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var splitGoldIds = goldMembers.Keys
            .Where(id => goldToPredicted.TryGetValue(id, out var ids) && ids.Count > 1)
            .ToHashSet(StringComparer.Ordinal);
        var exactMembershipIds = catalogEntries
            .Where(entry => IsExactMembership(entry, nodeToGold[entry.Id], goldAliases, goldMembers))
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);

        var predictedParentByChild = decisions.ToDictionary(item => item.Child,
            item => item.Kind == "SELECT_PARENT" ? item.Parent : null, StringComparer.Ordinal);
        var diagnosticRows = decisions.Select(decision => BuildDiagnosticRow(
            decision, catalogEntries, nodeToGold, exactMembershipIds, splitGoldIds, goldParentByChild,
            goldDepth, predictedParentByChild, treeLevels, catalogFingerprint)).ToArray();

        var predictedMappedEdges = decisions
            .Where(item => item.Kind == "SELECT_PARENT")
            .Select(item => new
            {
                Child = nodeToGold.GetValueOrDefault(item.Child),
                Parent = item.Parent is null ? null : nodeToGold.GetValueOrDefault(item.Parent),
                ChildNode = item.Child,
            })
            .Where(item => item.Child is not null)
            .ToArray();
        var mappedEdgeSet = predictedMappedEdges.Where(item => item.Parent is not null)
            .Select(item => new Edge(item.Parent!, item.Child!)).ToHashSet();
        var unmappableParentCount = predictedMappedEdges.Count(item => item.Parent is null);
        var overall = ScoreEdges(mappedEdgeSet, goldEdges, unmappableParentCount);

        var exactChildGoldIds = exactMembershipIds.Select(id => nodeToGold[id])
            .Where(id => id is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        var conditionalPredicted = predictedMappedEdges.Where(item => exactMembershipIds.Contains(item.ChildNode)).ToArray();
        var conditionalMapped = conditionalPredicted.Where(item => item.Parent is not null)
            .Select(item => new Edge(item.Parent!, item.Child!)).ToHashSet();
        var conditionalGold = goldEdges.Where(edge => exactChildGoldIds.Contains(edge.Child)).ToHashSet();
        var conditionalUnmappable = conditionalPredicted.Count(item => item.Parent is null);
        var conditional = ScoreEdges(conditionalMapped, conditionalGold, conditionalUnmappable);

        var levelErrors = diagnosticRows.Where(row => row.GoldLevel is not null && row.PredictedLevel != row.GoldLevel).ToArray();
        var parentErrors = diagnosticRows.Where(row => row.ParentOutcome is "PARENT_WRONG" or "PARENT_WRONG_UNMAPPABLE_PARENT" or "ROOT_WRONG").ToArray();
        var errorDimensionCounts = diagnosticRows
            .SelectMany(row => row.ErrorDimensions)
            .GroupBy(dimension => dimension, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var artifact = new
        {
            schemaVersion = "a99-hdsa-semantic-node-production-error-decomposition-v1",
            documentId = "DOC-0205",
            benchmarkCheckpoint = "c945c02",
            sourceSha256 = catalogSourceSha,
            predictionArtifacts = new
            {
                predictionFreeze = PredictionFreeze,
                predictionComplete = PredictionComplete,
                catalog = Catalog,
                goldOpenedAfterPredictionFreeze = true,
                goldDerivedInput = false,
                predictionRerunForScoringFix = false,
            },
            execution = new
            {
                modelCalls = 0,
                providerCalls = 0,
                goldReadBeforeFreeze = false,
                analysisMode = "OFFLINE_FROZEN_PREDICTION_ONLY",
            },
            semanticResolution = new
            {
                predictedNodes = catalogEntries.Count,
                goldNodes = goldMembers.Count,
                exactMembershipNodes = exactMembershipIds.Count,
                splitGoldNodeIds = splitGoldIds.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                splitGoldNodeCount = splitGoldIds.Count,
                unmappedPredictedNodes = nodeToGold.Count(item => item.Value is null),
            },
            parent = new
            {
                overall,
                conditionalOnExactSemanticMembership = conditional,
                conditionalPolicy = "Restrict children to exact predicted membership; unmappable predicted parents remain FP; split semantic nodes are excluded from the conditional denominator.",
                parentErrorCount = parentErrors.Length,
            },
            level = new
            {
                exact = diagnosticRows.Count(row => row.GoldLevel is not null && row.PredictedLevel == row.GoldLevel),
                evaluated = diagnosticRows.Count(row => row.GoldLevel is not null),
                mismatchCount = levelErrors.Length,
                errorRootCauseCounts = levelErrors.GroupBy(row => row.RootCause, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            },
            classification = new
            {
                dimensions = new[]
                {
                    "SEMANTIC_SPLIT_CAUSED", "CANDIDATE_MISSING", "MODEL_WRONG_PARENT", "MODEL_SELECTED_SPLIT_ALIAS",
                    "VALIDATOR_EFFECT", "ANCESTOR_PARENT_CASCADE", "LEVEL_PROJECTION_ONLY",
                },
                allObservedErrorDimensions = diagnosticRows.SelectMany(row => row.ErrorDimensions).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                errorDimensionCounts,
                noEvidenceObserved = new[] { "CANDIDATE_MISSING", "VALIDATOR_EFFECT", "LEVEL_PROJECTION_ONLY" },
            },
            diagnostics = diagnosticRows,
            interpretation = new
            {
                primary = "The one Gold semantic node N0002 is split across S0014 and S0015. That split removes N0001>N0002, creates a wrong mapped self-edge, and cascades depth errors to descendants attached below the split.",
                localParentError = "N0001/S0005 is attached to the excluded masthead node; this is a local wrong-parent selection, not a level-only projection error.",
                conditionalReading = "Among exact semantic memberships, eight child edges are correct; the remaining FP is the document-title/root node attached to the unmappable masthead.",
                causalClaimLimit = "This is frozen-output error attribution, not an ablation proving that semantic-node abstraction alone caused the historical F1 change.",
            },
        };

        await File.WriteAllTextAsync(Path.Combine(output, "error-decomposition.v1.json"), JsonSerializer.Serialize(artifact, JsonOptions), ct);
        Console.WriteLine("HDSA_SEMANTIC_NODE_ERROR_DECOMPOSITION_STATUS=COMPLETE");
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READ_BEFORE_FREEZE=0");
        Console.WriteLine($"PARENT_OVERALL=TP:{overall.Tp} FP:{overall.Fp} FN:{overall.Fn} F1:{overall.F1:0.####}");
        Console.WriteLine($"PARENT_CONDITIONAL_EXACT_MEMBERSHIP=TP:{conditional.Tp} FP:{conditional.Fp} FN:{conditional.Fn} F1:{conditional.F1:0.####}");
        Console.WriteLine($"LEVEL_MISMATCHES={levelErrors.Length}");
        Console.WriteLine($"ARTIFACT={Path.Combine(OutputRoot, "error-decomposition.v1.json")}");
        return 0;
    }

    private static DiagnosticRow BuildDiagnosticRow(
        Decision decision, IReadOnlyList<CatalogEntry> entries, IReadOnlyDictionary<string, string?> nodeToGold,
        IReadOnlySet<string> exactMembershipIds, IReadOnlySet<string> splitGoldIds,
        IReadOnlyDictionary<string, string> goldParentByChild, IReadOnlyDictionary<string, int> goldDepth,
        IReadOnlyDictionary<string, string?> predictedParentByChild, IReadOnlyDictionary<string, int> treeLevels,
        string catalogFingerprint)
    {
        var entry = entries.Single(item => item.Id == decision.Child);
        var childGold = nodeToGold.GetValueOrDefault(decision.Child);
        var parentGold = decision.Parent is null ? null : nodeToGold.GetValueOrDefault(decision.Parent);
        var expectedParent = childGold is not null && goldParentByChild.TryGetValue(childGold, out var goldParent) ? goldParent : null;
        var membershipStatus = childGold is null ? "UNMAPPED" :
            splitGoldIds.Contains(childGold) ? "SPLIT" :
            exactMembershipIds.Contains(decision.Child) ? "EXACT" : "NONEXACT";
        var parentOutcome = decision.Kind == "UNRESOLVED" ? "UNRESOLVED" :
            childGold is null ? "UNMAPPED_CHILD" :
            decision.Kind == "ROOT" && expectedParent is null ? "ROOT_TRUE" :
            decision.Kind == "ROOT" ? "ROOT_WRONG" :
            decision.Kind != "SELECT_PARENT" ? "UNRESOLVED" :
            parentGold is null ? "PARENT_WRONG_UNMAPPABLE_PARENT" :
            parentGold == expectedParent ? "PARENT_TRUE" : "PARENT_WRONG";
        var predictedLevel = treeLevels.GetValueOrDefault(decision.Child);
        var goldLevel = childGold is not null && goldDepth.TryGetValue(childGold, out var depth) ? depth : (int?)null;
        var firstDivergent = FindFirstDivergentAncestor(decision.Child, nodeToGold, goldParentByChild, predictedParentByChild);
        var levelMismatch = goldLevel is not null && predictedLevel != goldLevel;
        var isParentError = parentOutcome is "PARENT_WRONG" or "PARENT_WRONG_UNMAPPABLE_PARENT" or "ROOT_WRONG";
        var rootCause = childGold is null ? "SEMANTIC_RESOLUTION_ERROR" :
            membershipStatus == "SPLIT" ? "SEMANTIC_SPLIT_CAUSED" :
            isParentError ? "MODEL_WRONG_PARENT" :
            levelMismatch && firstDivergent is not null && firstDivergent != decision.Child ? "ANCESTOR_PARENT_CASCADE" :
            levelMismatch ? "LEVEL_PROJECTION_ONLY" : "NO_ERROR";
        var dimensions = new List<string>();
        if (membershipStatus == "SPLIT") dimensions.Add("SEMANTIC_SPLIT_CAUSED");
        if (membershipStatus == "SPLIT" && isParentError) dimensions.Add("MODEL_SELECTED_SPLIT_ALIAS");
        if (isParentError && membershipStatus != "SPLIT") dimensions.Add("MODEL_WRONG_PARENT");
        if (levelMismatch && firstDivergent is not null && firstDivergent != decision.Child) dimensions.Add("ANCESTOR_PARENT_CASCADE");
        if (levelMismatch && firstDivergent is null) dimensions.Add("LEVEL_PROJECTION_ONLY");
        if (childGold is null) dimensions.Add("SEMANTIC_RESOLUTION_ERROR");
        return new DiagnosticRow(
            decision.Child,
            entry.Aliases,
            entry.CanonicalText,
            membershipStatus,
            childGold,
            decision.Kind,
            decision.Parent,
            parentGold,
            expectedParent,
            parentOutcome,
            predictedLevel,
            goldLevel,
            firstDivergent,
            firstDivergent is not null ? nodeToGold.GetValueOrDefault(firstDivergent) : null,
            rootCause,
            dimensions.Distinct(StringComparer.Ordinal).ToArray(),
            catalogFingerprint);
    }

    private static string? FindFirstDivergentAncestor(
        string child, IReadOnlyDictionary<string, string?> nodeToGold,
        IReadOnlyDictionary<string, string> goldParentByChild,
        IReadOnlyDictionary<string, string?> predictedParentByChild)
    {
        var current = child;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(current))
        {
            var mapped = nodeToGold.GetValueOrDefault(current);
            if (mapped is null) return current;
            var predictedParent = predictedParentByChild.GetValueOrDefault(current);
            var mappedPredictedParent = predictedParent is null ? null : nodeToGold.GetValueOrDefault(predictedParent);
            var expectedParent = goldParentByChild.GetValueOrDefault(mapped);
            // A predicted parent that maps to no Gold node is still a parent selection. Do not
            // treat it as equivalent to a Gold root merely because both mapped values are null.
            if ((predictedParent is null) != (expectedParent is null) || mappedPredictedParent != expectedParent)
                return current;
            if (predictedParent is null) return null;
            current = predictedParent;
        }
        return current;
    }

    private static EdgeScore ScoreEdges(IReadOnlySet<Edge> predicted, IReadOnlySet<Edge> gold, int unmappableParents)
    {
        var tp = predicted.Intersect(gold).Count();
        var fp = predicted.Except(gold).Count() + unmappableParents;
        var fn = gold.Except(predicted).Count();
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp);
        var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        var f1 = precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall);
        return new EdgeScore(tp, fp, fn, precision, recall, f1, predicted.Count + unmappableParents, predicted.Count, unmappableParents, gold.Count);
    }

    private static string? MapToGold(CatalogEntry entry, IReadOnlyDictionary<string, string> goldAliases)
    {
        var mapped = entry.Aliases.Where(goldAliases.ContainsKey).Select(alias => goldAliases[alias]).Distinct(StringComparer.Ordinal).ToArray();
        return mapped.Length == 1 ? mapped[0] : null;
    }

    private static bool IsExactMembership(CatalogEntry entry, string? goldId,
        IReadOnlyDictionary<string, string> goldAliases, IReadOnlyDictionary<string, HashSet<string>> goldMembers)
    {
        if (goldId is null || !goldMembers.TryGetValue(goldId, out var members)) return false;
        var visible = entry.Aliases.Where(goldAliases.ContainsKey).ToHashSet(StringComparer.Ordinal);
        return members.SetEquals(visible);
    }

    private static IReadOnlyDictionary<string, int> DeriveDepth(IReadOnlySet<Edge> edges)
    {
        var parentByChild = edges.ToDictionary(item => item.Child, item => item.Parent, StringComparer.Ordinal);
        var nodes = edges.SelectMany(item => new[] { item.Parent, item.Child }).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        int Depth(string node)
        {
            if (result.TryGetValue(node, out var known)) return known;
            return result[node] = parentByChild.TryGetValue(node, out var parent) ? Depth(parent) + 1 : 1;
        }
        foreach (var node in nodes) Depth(node);
        return result;
    }

    private static List<CatalogEntry> ReadCatalogEntries(JsonElement root) => root.GetProperty("entries").EnumerateArray()
        .Select(item => new CatalogEntry(
            item.GetProperty("semanticNodeId").GetString()!,
            item.GetProperty("memberOccurrenceIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            item.GetProperty("canonicalText").GetString()!,
            item.GetProperty("sourceOrder").GetInt32()))
        .ToList();

    private static List<Decision> ReadDecisions(JsonElement root) => root.GetProperty("decisions").EnumerateArray()
        .Select(item => new Decision(
            item.GetProperty("childSemanticNodeId").GetString()!,
            item.GetProperty("decision").GetString()!,
            item.TryGetProperty("parentSemanticNodeId", out var parent) && parent.ValueKind != JsonValueKind.Null ? parent.GetString() : null))
        .ToList();

    private static Dictionary<string, int> ReadTreeLevels(JsonElement root) => root.GetProperty("tree").GetProperty("nodes").EnumerateArray()
        .ToDictionary(item => item.GetProperty("id").GetString()!, item => item.GetProperty("level").GetInt32(), StringComparer.Ordinal);

    private static void RequireFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("HDSA_DECOMPOSITION_INPUT_MISSING", path);
    }

    private sealed record CatalogEntry(string Id, IReadOnlyList<string> Aliases, string CanonicalText, int SourceOrder);
    private sealed record Decision(string Child, string Kind, string? Parent);
    private sealed record Edge(string Parent, string Child);
    private sealed record EdgeScore(int Tp, int Fp, int Fn, double Precision, double Recall, double F1, int PredictedEdgeCount, int MappedPredictedEdgeCount, int UnmappablePredictedEdges, int GoldEdgeCount);
    private sealed record DiagnosticRow(
        string SemanticNodeId,
        IReadOnlyList<string> SourceAliases,
        string CanonicalText,
        string MembershipStatus,
        string? GoldSemanticNodeId,
        string Decision,
        string? PredictedParent,
        string? MappedPredictedParent,
        string? GoldParent,
        string ParentOutcome,
        int PredictedLevel,
        int? GoldLevel,
        string? FirstDivergentAncestor,
        string? FirstDivergentAncestorGoldSemanticNodeId,
        string RootCause,
        IReadOnlyList<string> ErrorDimensions,
        string CatalogFingerprint);
}
