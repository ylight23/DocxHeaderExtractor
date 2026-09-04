using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.Eval.Accuracy99;

public static class Accuracy99Evaluator
{
    public static Accuracy99DocumentMetrics Evaluate(
        SourceDocument source,
        HumanGoldArtifact gold,
        IEnumerable<Accuracy99Prediction> predictions,
        string? sourceSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(gold);
        if (!gold.ExhaustiveSourceLabels)
            throw new InvalidDataException("accuracy99-requires-exhaustive-source-labels");
        HumanGoldValidator.EnsureValid(gold, source, sourceSha256);

        var sourceById = source.Paragraphs
            .GroupBy(p => p.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var occurrenceById = gold.SourceOccurrences.ToDictionary(x => x.SourceId, StringComparer.Ordinal);
        var headingById = gold.Headings
            .GroupBy(x => x.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var goldParents = headingById.ToDictionary(
            pair => pair.Key, pair => pair.Value.ParentSourceId, StringComparer.Ordinal);
        var goldHeadings = gold.Headings.ToArray();
        var candidates = predictions.ToArray();
        var predictionParents = candidates
            .Where(prediction => prediction.HasParent)
            .GroupBy(prediction => prediction.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().ParentSourceId, StringComparer.Ordinal);
        var matched = new Dictionary<int, int>();
        var usedGold = new HashSet<int>();
        var sourceUnjoined = 0;
        var unmeasured = 0;
        var falsePositives = 0;
        var firstLosses = new Dictionary<Accuracy99FirstLossStage, int>();

        for (var predictionIndex = 0; predictionIndex < candidates.Length; predictionIndex++)
        {
            var prediction = candidates[predictionIndex];
            if (string.IsNullOrWhiteSpace(prediction.SourceId) ||
                !sourceById.TryGetValue(prediction.SourceId, out var paragraph))
            {
                sourceUnjoined++;
                Add(firstLosses, Accuracy99FirstLossStage.SourceReading);
                continue;
            }

            if (prediction.Span is null || !prediction.Span.IsValidFor(paragraph.Text))
            {
                unmeasured++;
                Add(firstLosses, Accuracy99FirstLossStage.Span);
                continue;
            }

            var label = occurrenceById.TryGetValue(prediction.SourceId, out var occurrence)
                ? occurrence.Label
                : GoldSourceLabel.NotObservable;
            if (label is GoldSourceLabel.Ambiguous or GoldSourceLabel.NotObservable)
            {
                unmeasured++;
                continue;
            }

            var matchIndex = FindBestMatch(prediction, goldHeadings, usedGold);
            if (matchIndex >= 0)
            {
                matched[predictionIndex] = matchIndex;
                usedGold.Add(matchIndex);
            }
            else if (label == GoldSourceLabel.NonHeading)
            {
                falsePositives++;
            }
            else
            {
                falsePositives++;
            }
        }

        var falseNegatives = goldHeadings.Length - usedGold.Count;
        for (var goldIndex = 0; goldIndex < goldHeadings.Length; goldIndex++)
        {
            if (usedGold.Contains(goldIndex)) continue;
            var goldHeading = goldHeadings[goldIndex];
            var hasSameSource = candidates.Any(p =>
                string.Equals(p.SourceId, goldHeading.SourceId, StringComparison.Ordinal));
            var hasOverlappingSource = candidates.Any(p =>
                string.Equals(p.SourceId, goldHeading.SourceId, StringComparison.Ordinal) &&
                p.Span is not null &&
                p.Span.IsValidFor(sourceById[goldHeading.SourceId].Text) &&
                Overlaps(p.Span, goldHeading.HeadingSpan));
            Add(firstLosses, hasOverlappingSource
                ? Accuracy99FirstLossStage.Span
                : hasSameSource
                    ? Accuracy99FirstLossStage.Span
                    : Accuracy99FirstLossStage.CandidateGeneration);
        }

        var truePositives = usedGold.Count;
        var exactSpans = 0;
        var spanEvaluated = 0;
        var levelCorrect = 0;
        var levelEvaluated = 0;
        var parentCorrect = 0;
        var parentEvaluated = 0;
        var hierarchyCorrect = 0;
        var hierarchyEvaluated = 0;

        foreach (var pair in matched)
        {
            var prediction = candidates[pair.Key];
            var goldHeading = goldHeadings[pair.Value];
            spanEvaluated++;
            if (prediction.Span == goldHeading.HeadingSpan) exactSpans++;

            if (prediction.Level is not null)
            {
                levelEvaluated++;
                if (prediction.Level == goldHeading.Level) levelCorrect++;
            }
            else Add(firstLosses, Accuracy99FirstLossStage.Level);

            if (prediction.HasParent)
            {
                parentEvaluated++;
                if (string.Equals(prediction.ParentSourceId, goldHeading.ParentSourceId, StringComparison.Ordinal))
                    parentCorrect++;
                if (string.Equals(
                        BuildPath(prediction.SourceId, prediction.ParentSourceId, predictionParents),
                        BuildPath(goldHeading.SourceId, goldHeading.ParentSourceId, goldParents),
                        StringComparison.Ordinal))
                    hierarchyCorrect++;
                hierarchyEvaluated++;
            }
            else Add(firstLosses, Accuracy99FirstLossStage.Parent);
        }

        var precision = Rate(truePositives, truePositives + falsePositives);
        var recall = Rate(truePositives, truePositives + falseNegatives);
        return new Accuracy99DocumentMetrics
        {
            DocumentId = source.DocumentId,
            TruePositives = truePositives,
            FalsePositives = falsePositives,
            FalseNegatives = falseNegatives,
            Precision = precision,
            Recall = recall,
            F1 = F1(precision, recall),
            ExactSpanMatches = exactSpans,
            SpanEvaluated = spanEvaluated,
            LevelCorrect = levelCorrect,
            LevelEvaluated = levelEvaluated,
            ParentCorrect = parentCorrect,
            ParentEvaluated = parentEvaluated,
            HierarchyCorrect = hierarchyCorrect,
            HierarchyEvaluated = hierarchyEvaluated,
            SourceUnjoined = sourceUnjoined,
            Unmeasured = unmeasured,
            FirstLosses = firstLosses,
            DocumentExactMatch = sourceUnjoined == 0 && falsePositives == 0 && falseNegatives == 0,
        };
    }

    public static Accuracy99DocumentMetrics Evaluate(
        SourceDocument source,
        HumanGoldArtifact gold,
        DocumentOutline outline,
        string? sourceSha256 = null) =>
        Evaluate(source, gold, outline.Headings.Select(FromHeading), sourceSha256);

    public static Accuracy99Prediction FromHeading(HeadingRecord heading) =>
        new(
            heading.SourceId ?? heading.StableId ?? string.Empty,
            heading.HeadingSpan is null
                ? null
                : new Accuracy99Span(heading.HeadingSpan.Start, heading.HeadingSpan.End),
            heading.Level,
            heading.Text,
            heading.OriginalText,
            heading.BoundarySource);

    public static Accuracy99AggregateMetrics Aggregate(IEnumerable<Accuracy99DocumentMetrics> documents)
    {
        var materialized = documents.ToArray();
        var micro = new Accuracy99DocumentMetrics
        {
            DocumentId = "micro",
            TruePositives = materialized.Sum(x => x.TruePositives),
            FalsePositives = materialized.Sum(x => x.FalsePositives),
            FalseNegatives = materialized.Sum(x => x.FalseNegatives),
            ExactSpanMatches = materialized.Sum(x => x.ExactSpanMatches),
            SpanEvaluated = materialized.Sum(x => x.SpanEvaluated),
            LevelCorrect = materialized.Sum(x => x.LevelCorrect),
            LevelEvaluated = materialized.Sum(x => x.LevelEvaluated),
            ParentCorrect = materialized.Sum(x => x.ParentCorrect),
            ParentEvaluated = materialized.Sum(x => x.ParentEvaluated),
            HierarchyCorrect = materialized.Sum(x => x.HierarchyCorrect),
            HierarchyEvaluated = materialized.Sum(x => x.HierarchyEvaluated),
            SourceUnjoined = materialized.Sum(x => x.SourceUnjoined),
            Unmeasured = materialized.Sum(x => x.Unmeasured),
            FirstLosses = materialized.SelectMany(x => x.FirstLosses)
                .GroupBy(x => x.Key)
                .ToDictionary(x => x.Key, x => x.Sum(y => y.Value)),
        };
        micro = micro with
        {
            Precision = Rate(micro.TruePositives, micro.TruePositives + micro.FalsePositives),
            Recall = Rate(micro.TruePositives, micro.TruePositives + micro.FalseNegatives),
        };
        micro = micro with { F1 = F1(micro.Precision, micro.Recall) };
        return new Accuracy99AggregateMetrics
        {
            DocumentCount = materialized.Length,
            Micro = micro,
            MacroPrecision = Average(materialized.Select(x => x.Precision)),
            MacroRecall = Average(materialized.Select(x => x.Recall)),
            MacroF1 = Average(materialized.Select(x => x.F1)),
            Documents = materialized,
        };
    }

    private static int FindBestMatch(
        Accuracy99Prediction prediction,
        IReadOnlyList<HumanGoldHeading> headings,
        IReadOnlySet<int> used)
    {
        return headings
            .Select((heading, index) => (heading, index))
            .Where(x => !used.Contains(x.index) &&
                        string.Equals(x.heading.SourceId, prediction.SourceId, StringComparison.Ordinal) &&
                        Overlaps(prediction.Span!, x.heading.HeadingSpan))
            .OrderBy(x => prediction.Span == x.heading.HeadingSpan ? 0 : 1)
            .ThenBy(x => x.index)
            .Select(x => x.index)
            .FirstOrDefault(-1);
    }

    private static string BuildPath(
        string sourceId,
        string? parentSourceId,
        IReadOnlyDictionary<string, string?> parentById)
    {
        var path = new List<string> { sourceId };
        var cursor = parentSourceId;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (cursor is not null && seen.Add(cursor))
        {
            path.Add(cursor);
            if (!parentById.TryGetValue(cursor, out cursor)) break;
        }
        path.Reverse();
        return string.Join("/", path);
    }

    private static bool Overlaps(Accuracy99Span left, Accuracy99Span right) =>
        left.Start < right.End && right.Start < left.End;

    private static double? Rate(int numerator, int denominator) =>
        denominator == 0 ? null : (double)numerator / denominator;

    private static double? F1(double? precision, double? recall) =>
        precision is null || recall is null || precision + recall == 0
            ? null
            : 2 * precision * recall / (precision + recall);

    private static double? Average(IEnumerable<double?> values)
    {
        var measured = values.Where(x => x is not null).Select(x => x!.Value).ToArray();
        return measured.Length == 0 ? null : measured.Average();
    }

    private static void Add(IDictionary<Accuracy99FirstLossStage, int> values, Accuracy99FirstLossStage stage)
    {
        values.TryGetValue(stage, out var count);
        values[stage] = count + 1;
    }
}
