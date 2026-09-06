namespace DocxHeaderExtractor.Eval.Accuracy99;

/// <summary>Strict v3 evaluator: exact source+span identity is the only official join.</summary>
public static class A99PositiveSetEvaluatorV3
{
    public static A99PositiveSetMetrics Evaluate(
        A99HumanGoldV3Document gold,
        IEnumerable<A99PositivePrediction> predictions)
    {
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentNullException.ThrowIfNull(predictions);
        var candidates = predictions.ToArray();
        var goldById = gold.Rows.ToDictionary(x => x.HeadingOccurrenceId, StringComparer.Ordinal);
        var matched = new Dictionary<string, A99PositivePrediction>(StringComparer.Ordinal);
        var falsePositives = 0;
        foreach (var prediction in candidates)
        {
            var id = prediction.HeadingOccurrenceId;
            if (string.IsNullOrWhiteSpace(id) || !goldById.ContainsKey(id) || !matched.TryAdd(id, prediction)) falsePositives++;
        }

        var falseNegatives = gold.Rows.Count(row => !matched.ContainsKey(row.HeadingOccurrenceId));
        var goldParents = gold.Rows.ToDictionary(row => row.HeadingOccurrenceId, row => (string?)row.ParentHeadingOccurrenceId, StringComparer.Ordinal);
        var predictionParents = matched.ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ParentOccurrenceId, StringComparer.Ordinal);
        var roleCorrect = 0; var roleEvaluated = 0;
        var levelCorrect = 0; var levelEvaluated = 0;
        var exactSpanMatches = 0; var spanEvaluated = 0;
        var parentCorrect = 0; var parentEvaluated = 0;
        var hierarchyCorrect = 0; var hierarchyEvaluated = 0;

        foreach (var pair in matched)
        {
            var expected = goldById[pair.Key];
            var prediction = pair.Value;
            if (prediction.Role is not null) { roleEvaluated++; if (string.Equals(prediction.Role, expected.Role, StringComparison.OrdinalIgnoreCase)) roleCorrect++; }
            if (prediction.Level is not null) { levelEvaluated++; if (prediction.Level == expected.Level) levelCorrect++; }
            if (prediction.Span is not null) { spanEvaluated++; if (prediction.Span == new Accuracy99Span(expected.HeadingSpan.Start, expected.HeadingSpan.End)) exactSpanMatches++; }
            if (prediction.ParentOccurrenceId is not null)
            {
                parentEvaluated++;
                if (string.Equals(prediction.ParentOccurrenceId, expected.ParentHeadingOccurrenceId, StringComparison.Ordinal)) parentCorrect++;
                hierarchyEvaluated++;
                if (string.Equals(BuildPath(pair.Key, prediction.ParentOccurrenceId, predictionParents), BuildPath(pair.Key, expected.ParentHeadingOccurrenceId, goldParents), StringComparison.Ordinal)) hierarchyCorrect++;
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
            RoleCorrect = roleCorrect, RoleEvaluated = roleEvaluated,
            LevelCorrect = levelCorrect, LevelEvaluated = levelEvaluated,
            ExactSpanMatches = exactSpanMatches, SpanEvaluated = spanEvaluated,
            ParentCorrect = parentCorrect, ParentEvaluated = parentEvaluated,
            HierarchyCorrect = hierarchyCorrect, HierarchyEvaluated = hierarchyEvaluated,
        };
    }

    public static A99AutonomyMetrics ComputeAutonomy(
        IEnumerable<(bool CompletedWithoutHuman, bool Abstained, bool ReviewEscalated, int HeadingFieldsEscalated)> documents,
        IEnumerable<(A99HumanGoldV3Document Gold, IReadOnlyList<A99PositivePrediction> Predictions)> evaluated)
    {
        var documentRows = documents.ToArray();
        var evaluatedRows = evaluated.ToArray();
        var goldHeadingCount = evaluatedRows.Sum(x => x.Gold.Rows.Count);
        var resolved = evaluatedRows.Sum(x => x.Gold.Rows.Count(gold =>
            x.Predictions.Count(prediction => prediction.ResolvedWithoutHuman && prediction.HeadingOccurrenceId == gold.HeadingOccurrenceId) == 1));
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

    private static string BuildPath(string id, string? parent, IReadOnlyDictionary<string, string?> parents)
    {
        var path = new List<string> { id };
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        var cursor = parent;
        while (cursor is not null && !string.Equals(cursor, "ROOT", StringComparison.OrdinalIgnoreCase) && seen.Add(cursor))
        {
            path.Add(cursor);
            if (!parents.TryGetValue(cursor, out cursor)) break;
        }
        path.Reverse();
        return string.Join("/", path);
    }
}
