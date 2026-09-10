namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Pure, Gold-only offline math for the model-omission stability audit. This type is
/// deliberately separate from runtime transformation code.</summary>
public static class SemanticTextStabilityDiagnostics
{
    public static string Classify(IReadOnlyList<string> statuses)
    {
        if (statuses.Any(x => x == "SYSTEM_LOSS")) return "SYSTEM_AFFECTED";
        if (statuses.Any(x => x == "UNRESOLVED")) return "UNRESOLVED";
        if (statuses.Contains("MODEL_OMISSION") && statuses.Contains("MODEL_WRONG_SPAN")) return "MIXED_SPAN_OR_OMISSION";
        var omissions = statuses.Count(x => x == "MODEL_OMISSION");
        if (omissions == 3) return "PERSISTENT_3_OF_3_MISS";
        if (omissions == 2) return "STOCHASTIC_2_OF_3_MISS";
        if (omissions == 1 && statuses.Count(x => x == "EXACT_TP") == 2) return "STOCHASTIC_1_OF_3_MISS";
        if (statuses.All(x => x == "EXACT_TP")) return "ALWAYS_FOUND";
        return "UNRESOLVED";
    }

    public static StabilityDiagnosticScore Score(IReadOnlyList<IReadOnlySet<string>> repeatPredictions, IReadOnlySet<string> gold, int minimumRepeats)
    {
        var prediction = repeatPredictions.SelectMany(x => x)
            .GroupBy(x => x, StringComparer.Ordinal)
            .Where(x => x.Count() >= minimumRepeats)
            .Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var tp = prediction.Intersect(gold, StringComparer.Ordinal).Count();
        var fp = prediction.Except(gold, StringComparer.Ordinal).Count();
        var fn = gold.Except(prediction, StringComparer.Ordinal).Count();
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp);
        var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        var f1 = precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall);
        return new(tp, fp, fn, precision, recall, f1);
    }
}

public sealed record StabilityDiagnosticScore(int Tp, int Fp, int Fn, double Precision, double Recall, double F1);
