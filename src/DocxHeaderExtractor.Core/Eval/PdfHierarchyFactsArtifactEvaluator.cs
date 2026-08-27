using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Core.Eval;

/// <summary>
/// M8.1b replay evaluator. It consumes frozen hierarchy facts and reviewed hierarchy gold only;
/// it never invokes inventory, a hierarchy resolver, a PDF parser, or a model.
/// </summary>
public static class PdfHierarchyFactsArtifactEvaluator
{
    public static PdfHierarchyFactsEvaluation Evaluate(string artifactJson, string hierarchyGoldJson)
    {
        var facts = ReadFacts(artifactJson);
        var gold = PdfHierarchyGold.Load(hierarchyGoldJson);
        var goldBySourceFactId = gold.Headings
            .Where(item => item.SourceFactId is not null)
            .ToDictionary(item => item.SourceFactId!, StringComparer.Ordinal);
        var goldByHeadingId = gold.Headings.ToDictionary(item => item.HeadingId, StringComparer.Ordinal);
        var inventorySourceIds = facts.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var evaluated = new List<PdfHierarchyFactItemEvaluation>(facts.Count + gold.Headings.Count);

        var levelCorrect = 0;
        var resolvedLevels = facts.Count(item => item.ResolvedLevel is not null);
        var resolvedLevelsGoldMatched = 0;
        var parentResolved = 0;
        var parentCorrect = 0;
        var predictedEdges = new List<(string Parent, string Child)>();

        foreach (var fact in facts)
        {
            if (!goldBySourceFactId.TryGetValue(fact.Id, out var goldItem))
            {
                evaluated.Add(PdfHierarchyFactItemEvaluation.GoldMissing(fact));
                continue;
            }

            var levelOutcome = fact.ResolvedLevel is null
                ? "unresolved"
                : fact.ResolvedLevel == goldItem.GoldLevel ? "correct" : "incorrect";
            if (fact.ResolvedLevel is not null)
            {
                resolvedLevelsGoldMatched++;
                if (levelOutcome == "correct") levelCorrect++;
            }

            var expectedParentSourceId = goldItem.GoldParentId is null
                ? null
                : goldByHeadingId.TryGetValue(goldItem.GoldParentId, out var parentGold)
                    ? parentGold.SourceFactId
                    : throw new InvalidOperationException($"Hierarchy gold parent không tồn tại: {goldItem.GoldParentId}.");
            var parentOutcome = fact.MarkerPrefixParentCandidate is null
                ? "unresolved"
                : string.Equals(fact.MarkerPrefixParentCandidate, expectedParentSourceId, StringComparison.Ordinal)
                    ? "correct" : "incorrect";
            if (fact.MarkerPrefixParentCandidate is not null)
            {
                parentResolved++;
                if (parentOutcome == "correct") parentCorrect++;
                predictedEdges.Add((fact.MarkerPrefixParentCandidate, fact.Id));
            }

            evaluated.Add(new PdfHierarchyFactItemEvaluation(
                fact.Id, fact.SourceOrder, fact.Page, goldItem.HeadingId, goldItem.SourceAnchor,
                goldItem.GoldLevel, fact.ResolvedLevel, levelOutcome,
                goldItem.GoldParentId, expectedParentSourceId, fact.MarkerPrefixParentCandidate,
                parentOutcome, fact.Evidence));
        }

        foreach (var missingGold in gold.Headings.Where(item => item.SourceFactId is null || !inventorySourceIds.Contains(item.SourceFactId)))
        {
            evaluated.Add(new PdfHierarchyFactItemEvaluation(
                missingGold.SourceFactId ?? $"gold-only:{missingGold.HeadingId}", null, null, missingGold.HeadingId, missingGold.SourceAnchor,
                missingGold.GoldLevel, null, "gold_missing", missingGold.GoldParentId,
                missingGold.GoldParentId is null ? null : goldByHeadingId[missingGold.GoldParentId].SourceFactId,
                null, "gold_missing", []));
        }

        var goldEdges = gold.Headings
            .Where(item => item.GoldParentId is not null)
            .Select(item => (Parent: item.GoldParentId!, Child: item.HeadingId))
            .ToHashSet();
        var truePositiveEdges = predictedEdges.Count(edge =>
            goldBySourceFactId.TryGetValue(edge.Parent, out var parentGold) &&
            goldBySourceFactId.TryGetValue(edge.Child, out var childGold) &&
            goldEdges.Contains((parentGold.HeadingId, childGold.HeadingId)));
        var edgePrecision = Ratio(truePositiveEdges, predictedEdges.Count);
        var edgeRecall = Ratio(truePositiveEdges, goldEdges.Count);
        var goldParented = gold.Headings.Count(item => item.GoldParentId is not null);
        var goldRoots = gold.Headings.Count - goldParented;
        var forestInvariant = goldRoots + goldEdges.Count == gold.Headings.Count;

        return new PdfHierarchyFactsEvaluation(
            true,
            gold.GoldVersion,
            facts.Count,
            gold.Headings.Count,
            facts.Count(item => goldBySourceFactId.ContainsKey(item.Id)),
            gold.Headings.Count(item => item.SourceFactId is null || !inventorySourceIds.Contains(item.SourceFactId)),
            Ratio(facts.Count(item => goldBySourceFactId.ContainsKey(item.Id)), gold.Headings.Count),
            facts.Count(item => item.MarkerPath is not null),
            Ratio(facts.Count(item => item.MarkerPath is not null), facts.Count),
            resolvedLevels,
            resolvedLevelsGoldMatched,
            resolvedLevels - resolvedLevelsGoldMatched,
            levelCorrect,
            Ratio(levelCorrect, resolvedLevelsGoldMatched),
            parentResolved,
            Ratio(parentResolved, facts.Count),
            parentCorrect,
            Ratio(parentCorrect, parentResolved),
            new PdfHierarchyEdgeEvaluation(
                predictedEdges.Count, goldEdges.Count, truePositiveEdges, edgePrecision, edgeRecall,
                edgePrecision is null || edgeRecall is null ? null : HarmonicMean(edgePrecision.Value, edgeRecall.Value),
                predictedEdges.Count == 0 ? "no_predicted_edges" : "measured"),
            new PdfHierarchyGoldGraphEvaluation(goldRoots, goldParented, goldEdges.Count, forestInvariant,
                forestInvariant ? "valid_forest" : "invalid_forest"),
            evaluated.Count(item => item.LevelOutcome == "unresolved" || item.ParentOutcome == "unresolved"),
            "not_measured",
            evaluated);
    }

    private static IReadOnlyList<PdfHierarchyFactAudit> ReadFacts(string artifactJson)
    {
        using var document = JsonDocument.Parse(artifactJson);
        var root = RootObject(document.RootElement);
        if (!root.TryGetProperty("hierarchyFacts", out var hierarchyFacts) ||
            !hierarchyFacts.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Frozen artifact không có hierarchyFacts.items.");
        return JsonSerializer.Deserialize<List<PdfHierarchyFactAudit>>(items.GetRawText())
            ?? throw new InvalidOperationException("Không đọc được hierarchy facts frozen.");
    }

    private static JsonElement RootObject(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            if (rows.GetArrayLength() != 1)
                throw new InvalidOperationException("Hierarchy evaluator cần artifact có đúng một row.");
            return rows[0];
        }
        if (root.ValueKind == JsonValueKind.Object) return root;
        throw new InvalidOperationException("Artifact JSON phải là object hoặc wrapper có đúng một row.");
    }

    private static double? Ratio(int numerator, int denominator) => denominator == 0 ? null : numerator / (double)denominator;
    private static double HarmonicMean(double left, double right) => left + right == 0 ? 0 : 2 * left * right / (left + right);
}

public sealed record PdfHierarchyGold(string GoldVersion, IReadOnlyList<PdfHierarchyGoldHeading> Headings)
{
    public static PdfHierarchyGold Load(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("evaluationOnly", out var evaluationOnly) || evaluationOnly.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Hierarchy gold phải được đánh dấu evaluationOnly=true.");
        var version = root.GetProperty("goldVersion").GetString();
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("Hierarchy gold thiếu goldVersion.");
        var headings = JsonSerializer.Deserialize<List<PdfHierarchyGoldHeading>>(root.GetProperty("headings").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (headings.Count == 0) throw new InvalidOperationException("Hierarchy gold không có heading nào.");
        if (headings.Any(item => string.IsNullOrWhiteSpace(item.HeadingId) || string.IsNullOrWhiteSpace(item.SourceAnchor)))
            throw new InvalidOperationException("Mỗi hierarchy gold heading cần headingId và sourceAnchor.");
        if (headings.GroupBy(item => item.HeadingId, StringComparer.Ordinal).Any(group => group.Count() > 1) ||
            headings.Where(item => item.SourceFactId is not null).GroupBy(item => item.SourceFactId!, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Hierarchy gold có headingId hoặc sourceFactId trùng.");
        var byHeadingId = headings.ToDictionary(item => item.HeadingId, StringComparer.Ordinal);
        foreach (var heading in headings.Where(item => item.GoldParentId is not null))
        {
            if (!byHeadingId.ContainsKey(heading.GoldParentId!))
                throw new InvalidOperationException($"Hierarchy gold parent không tồn tại: {heading.GoldParentId}.");
            if (string.Equals(heading.HeadingId, heading.GoldParentId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Hierarchy gold không thể tự làm cha: {heading.HeadingId}.");
        }
        foreach (var heading in headings)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { heading.HeadingId };
            var current = heading;
            while (current.GoldParentId is not null)
            {
                current = byHeadingId[current.GoldParentId];
                if (!seen.Add(current.HeadingId))
                    throw new InvalidOperationException($"Hierarchy gold có cycle tại: {heading.HeadingId}.");
            }
        }
        return new PdfHierarchyGold(version, headings);
    }
}

public sealed record PdfHierarchyGoldHeading(string HeadingId, string? SourceFactId, string SourceAnchor, int GoldLevel, string? GoldParentId);

public sealed record PdfHierarchyEdgeEvaluation(
    int PredictedEdges,
    int GoldEdges,
    int TruePositiveEdges,
    double? Precision,
    double? Recall,
    double? F1,
    string Status);

public sealed record PdfHierarchyGoldGraphEvaluation(
    int Roots,
    int ParentedHeadings,
    int Edges,
    bool ForestInvariant,
    string Status);

public sealed record PdfHierarchyFactItemEvaluation(
    string SourceFactId,
    int? SourceOrder,
    int? Page,
    string? GoldHeadingId,
    string? GoldSourceAnchor,
    int? GoldLevel,
    int? ResolvedLevel,
    string LevelOutcome,
    string? GoldParentId,
    string? GoldParentSourceFactId,
    string? ResolvedParentSourceFactId,
    string ParentOutcome,
    IReadOnlyList<string> Evidence)
{
    public static PdfHierarchyFactItemEvaluation GoldMissing(PdfHierarchyFactAudit fact) => new(
        fact.Id, fact.SourceOrder, fact.Page, null, null, null, fact.ResolvedLevel, "gold_missing",
        null, null, fact.MarkerPrefixParentCandidate, "gold_missing", fact.Evidence);
}

public sealed record PdfHierarchyFactsEvaluation(
    bool EvaluationOnly,
    string GoldVersion,
    int InventoryHeadings,
    int GoldHeadings,
    int GoldIdentityResolved,
    int GoldIdentityUnresolved,
    double? BridgeResolvedGoldHeadings,
    int MarkerEvidence,
    double? MarkerEvidenceCoverage,
    int ResolvedLevels,
    int ResolvedLevelsGoldMatched,
    int ResolvedLevelsNotGoldMatched,
    int CorrectResolvedLevels,
    double? LevelAccuracyGivenResolvedGold,
    int DeterministicParentResolved,
    double? DeterministicParentCoverage,
    int ParentCorrect,
    double? ParentAccuracyGivenResolved,
    PdfHierarchyEdgeEvaluation EdgeMetrics,
    PdfHierarchyGoldGraphEvaluation GoldGraph,
    int UnresolvedCount,
    string Conflicts,
    IReadOnlyList<PdfHierarchyFactItemEvaluation> Items);
