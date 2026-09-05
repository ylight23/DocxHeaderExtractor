using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.Accuracy99;

public sealed record A99PositivePrediction(
    string SourceId,
    Accuracy99Span? Span,
    int? Level,
    string? Role = null,
    string? ParentSourceId = null,
    bool ResolvedWithoutHuman = true);

public sealed record A99PositiveSetMetrics
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("goldHeadingCount")] public int GoldHeadingCount { get; init; }
    [JsonPropertyName("predictionCount")] public int PredictionCount { get; init; }
    [JsonPropertyName("truePositives")] public int TruePositives { get; init; }
    [JsonPropertyName("falsePositives")] public int FalsePositives { get; init; }
    [JsonPropertyName("falseNegatives")] public int FalseNegatives { get; init; }
    [JsonPropertyName("precision")] public double? Precision { get; init; }
    [JsonPropertyName("recall")] public double? Recall { get; init; }
    [JsonPropertyName("f1")] public double? F1 { get; init; }
    [JsonPropertyName("roleCorrect")] public int RoleCorrect { get; init; }
    [JsonPropertyName("roleEvaluated")] public int RoleEvaluated { get; init; }
    [JsonPropertyName("levelCorrect")] public int LevelCorrect { get; init; }
    [JsonPropertyName("levelEvaluated")] public int LevelEvaluated { get; init; }
    [JsonPropertyName("exactSpanMatches")] public int ExactSpanMatches { get; init; }
    [JsonPropertyName("spanEvaluated")] public int SpanEvaluated { get; init; }
    [JsonPropertyName("parentCorrect")] public int ParentCorrect { get; init; }
    [JsonPropertyName("parentEvaluated")] public int ParentEvaluated { get; init; }
    [JsonPropertyName("hierarchyCorrect")] public int HierarchyCorrect { get; init; }
    [JsonPropertyName("hierarchyEvaluated")] public int HierarchyEvaluated { get; init; }
}

public sealed record A99AutonomyMetrics
{
    [JsonPropertyName("documentAutoCompletionRate")] public double? DocumentAutoCompletionRate { get; init; }
    [JsonPropertyName("headingAutoCoverage")] public double? HeadingAutoCoverage { get; init; }
    [JsonPropertyName("evaluatedDocuments")] public int EvaluatedDocuments { get; init; }
    [JsonPropertyName("completedWithoutHuman")] public int CompletedWithoutHuman { get; init; }
    [JsonPropertyName("goldHeadingOccurrences")] public int GoldHeadingOccurrences { get; init; }
    [JsonPropertyName("goldHeadingsResolvedWithoutHuman")] public int GoldHeadingsResolvedWithoutHuman { get; init; }
    [JsonPropertyName("abstainedDocuments")] public int AbstainedDocuments { get; init; }
    [JsonPropertyName("reviewEscalatedDocuments")] public int ReviewEscalatedDocuments { get; init; }
    [JsonPropertyName("headingFieldsEscalated")] public int HeadingFieldsEscalated { get; init; }
}

public static class A99PositiveSetEvaluator
{
    public static A99PositiveSetMetrics Evaluate(
        A99HumanGoldV2Document gold,
        IEnumerable<A99PositivePrediction> predictions)
    {
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentNullException.ThrowIfNull(predictions);
        var candidates = predictions.ToArray();
        var goldById = gold.Rows.ToDictionary(x => x.SourceId, StringComparer.Ordinal);
        var matched = new Dictionary<string, A99PositivePrediction>(StringComparer.Ordinal);
        var falsePositives = 0;

        foreach (var prediction in candidates)
        {
            if (string.IsNullOrWhiteSpace(prediction.SourceId) ||
                !goldById.ContainsKey(prediction.SourceId) ||
                !matched.TryAdd(prediction.SourceId, prediction))
                falsePositives++;
        }

        var falseNegatives = gold.Rows.Count(row => !matched.ContainsKey(row.SourceId));
        var roleCorrect = 0;
        var roleEvaluated = 0;
        var levelCorrect = 0;
        var levelEvaluated = 0;
        var exactSpanMatches = 0;
        var spanEvaluated = 0;
        var parentCorrect = 0;
        var parentEvaluated = 0;
        var hierarchyCorrect = 0;
        var hierarchyEvaluated = 0;
        var predictionParents = matched.ToDictionary(x => x.Key, x => x.Value.ParentSourceId, StringComparer.Ordinal);
        var goldParents = gold.Rows.ToDictionary(x => x.SourceId, x => x.ParentOccurrenceId, StringComparer.Ordinal);

        foreach (var pair in matched)
        {
            var expected = goldById[pair.Key];
            var prediction = pair.Value;
            if (prediction.Role is not null)
            {
                roleEvaluated++;
                if (string.Equals(prediction.Role, expected.Role, StringComparison.OrdinalIgnoreCase)) roleCorrect++;
            }
            if (prediction.Level is not null)
            {
                levelEvaluated++;
                if (prediction.Level == expected.Level) levelCorrect++;
            }
            if (prediction.Span is not null)
            {
                spanEvaluated++;
                if (prediction.Span == new Accuracy99Span(expected.HeadingSpan.Start, expected.HeadingSpan.End)) exactSpanMatches++;
            }
            if (prediction.ParentSourceId is not null)
            {
                parentEvaluated++;
                if (string.Equals(prediction.ParentSourceId, expected.ParentOccurrenceId, StringComparison.Ordinal)) parentCorrect++;
                hierarchyEvaluated++;
                if (string.Equals(BuildPath(pair.Key, prediction.ParentSourceId, predictionParents), BuildPath(pair.Key, expected.ParentOccurrenceId, goldParents), StringComparison.Ordinal))
                    hierarchyCorrect++;
            }
        }

        var precision = Rate(matched.Count, matched.Count + falsePositives);
        var recall = Rate(matched.Count, matched.Count + falseNegatives);
        return new A99PositiveSetMetrics
        {
            DocumentId = gold.DocumentId,
            GoldHeadingCount = gold.Rows.Count,
            PredictionCount = candidates.Length,
            TruePositives = matched.Count,
            FalsePositives = falsePositives,
            FalseNegatives = falseNegatives,
            Precision = precision,
            Recall = recall,
            F1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall),
            RoleCorrect = roleCorrect,
            RoleEvaluated = roleEvaluated,
            LevelCorrect = levelCorrect,
            LevelEvaluated = levelEvaluated,
            ExactSpanMatches = exactSpanMatches,
            SpanEvaluated = spanEvaluated,
            ParentCorrect = parentCorrect,
            ParentEvaluated = parentEvaluated,
            HierarchyCorrect = hierarchyCorrect,
            HierarchyEvaluated = hierarchyEvaluated,
        };
    }

    public static A99AutonomyMetrics ComputeAutonomy(
        IEnumerable<(bool CompletedWithoutHuman, bool Abstained, bool ReviewEscalated, int HeadingFieldsEscalated)> documents,
        IEnumerable<(A99HumanGoldV2Document Gold, IReadOnlyList<A99PositivePrediction> Predictions)> evaluated)
    {
        var documentRows = documents.ToArray();
        var evaluatedRows = evaluated.ToArray();
        var goldHeadingCount = evaluatedRows.Sum(x => x.Gold.Rows.Count);
        var resolved = evaluatedRows.Sum(x => x.Gold.Rows.Count(gold =>
            x.Predictions.Count(prediction => prediction.ResolvedWithoutHuman && prediction.SourceId == gold.SourceId) > 0));
        return new A99AutonomyMetrics
        {
            EvaluatedDocuments = documentRows.Length,
            CompletedWithoutHuman = documentRows.Count(x => x.CompletedWithoutHuman),
            DocumentAutoCompletionRate = Rate(documentRows.Count(x => x.CompletedWithoutHuman), documentRows.Length),
            GoldHeadingOccurrences = goldHeadingCount,
            GoldHeadingsResolvedWithoutHuman = resolved,
            HeadingAutoCoverage = Rate(resolved, goldHeadingCount),
            AbstainedDocuments = documentRows.Count(x => x.Abstained),
            ReviewEscalatedDocuments = documentRows.Count(x => x.ReviewEscalated),
            HeadingFieldsEscalated = documentRows.Sum(x => x.HeadingFieldsEscalated),
        };
    }

    private static double Rate(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;

    private static string BuildPath(
        string sourceId,
        string? parentSourceId,
        IReadOnlyDictionary<string, string?> parents)
    {
        var path = new List<string> { sourceId };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cursor = parentSourceId;
        while (cursor is not null && seen.Add(cursor))
        {
            path.Add(cursor);
            if (!parents.TryGetValue(cursor, out cursor)) break;
        }
        path.Reverse();
        return string.Join("/", path);
    }
}
