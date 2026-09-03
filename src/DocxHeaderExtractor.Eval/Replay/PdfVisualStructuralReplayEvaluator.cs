using System.Globalization;
using System.Text;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Eval;

/// <summary>
/// Scores immutable visual recovery traces against a rebased evaluation key. The trace contract
/// contains a source span and role, but deliberately no inferred hierarchy, so level/parent/final
/// structural metrics remain not-measured rather than being fabricated from a visual title.
/// </summary>
public static class PdfVisualStructuralReplayEvaluator
{
    public static PdfVisualStructuralReplay Evaluate(AnswerKey key, IReadOnlyList<PdfVisualRecoveryTrace> traces)
    {
        var gold = key.PositiveEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.StableId) && !string.IsNullOrWhiteSpace(entry.Text))
            .ToDictionary(entry => entry.StableId!, StringComparer.Ordinal);
        var predicted = traces
            .Where(trace => trace.Status == "visual-ocr-canonical-map" &&
                            string.Equals(trace.Role, "HeadingTopic", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(trace.MappedStableId))
            .ToArray();
        var anchored = predicted.Where(trace => gold.ContainsKey(trace.MappedStableId!)).ToArray();
        var titleExact = anchored.Where(trace => Same(trace.MappedText, gold[trace.MappedStableId!].Text)).ToArray();
        var unmatched = predicted.Where(trace => !gold.ContainsKey(trace.MappedStableId!)).Select(trace =>
            new PdfVisualReplayUnmatched(trace.MappedStableId, trace.MappedText, "no-gold-anchor")).Concat(
            anchored.Where(trace => !Same(trace.MappedText, gold[trace.MappedStableId!].Text)).Select(trace =>
                new PdfVisualReplayUnmatched(trace.MappedStableId, trace.MappedText, "title-not-exact")))
            .ToArray();

        return new PdfVisualStructuralReplay(
            Metric(titleExact.Length, predicted.Length, gold.Count),
            Metric(anchored.Length, predicted.Length, gold.Count),
            new PdfVisualReplayNotMeasured("not-measured", "visual recovery artifact has no level or parent proposal"),
            new PdfVisualReplayNotMeasured("not-measured", "visual recovery artifact has no level or parent proposal"),
            new PdfVisualReplayNotMeasured("not-measured", "cannot compute structural F1 without replayable level and parent proposals"),
            unmatched);
    }

    private static PdfVisualReplayMetric Metric(int hits, int predicted, int gold)
    {
        var precision = predicted == 0 ? 0 : (double)hits / predicted;
        var recall = gold == 0 ? 0 : (double)hits / gold;
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return new PdfVisualReplayMetric(hits, predicted, gold, precision, recall, f1);
    }

    private static bool Same(string? left, string? right) => string.Equals(Canonical(left), Canonical(right), StringComparison.Ordinal);

    private static string Canonical(string? value)
    {
        var builder = new StringBuilder();
        foreach (var character in (value ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (char.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}

public sealed record PdfVisualStructuralReplay(
    PdfVisualReplayMetric Title,
    PdfVisualReplayMetric Anchor,
    PdfVisualReplayNotMeasured Level,
    PdfVisualReplayNotMeasured Parent,
    PdfVisualReplayNotMeasured FinalStructural,
    IReadOnlyList<PdfVisualReplayUnmatched> Unmatched);

public sealed record PdfVisualReplayMetric(int Hits, int Predicted, int Gold, double Precision, double Recall, double F1);
public sealed record PdfVisualReplayNotMeasured(string State, string Reason);
public sealed record PdfVisualReplayUnmatched(string? StableId, string? Text, string Reason);
