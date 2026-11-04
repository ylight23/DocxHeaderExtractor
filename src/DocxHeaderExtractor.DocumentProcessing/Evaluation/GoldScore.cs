using System.Text.Json;

namespace DocxHeaderExtractor.DocumentProcessing.Evaluation;

/// <summary>Heading identity score of one run against one approved Gold document. Evaluation only: never an input to a run.</summary>
public sealed record GoldScore(string GoldDocument, int TruePositive, int FalsePositive, int FalseNegative,
    double? Precision, double? Recall, double? F1);

/// <summary>
/// Scores predicted heading identities against the Gold occurrence claims of the document with the same source SHA-256.
/// Returns null when no root is configured, no Gold file matches the source, or the file has no occurrence claims.
/// </summary>
public static class GoldHeadingScorer
{
    public static GoldScore? Evaluate(string? goldRoot, string sourceSha256, IEnumerable<string> predictedIds)
    {
        if (string.IsNullOrWhiteSpace(goldRoot) || !Directory.Exists(goldRoot) || string.IsNullOrWhiteSpace(sourceSha256)) return null;
        var predicted = predictedIds.ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(goldRoot, "*.gold.v1.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            if (!root.TryGetProperty("source", out var source) || !source.TryGetProperty("sourceSha256", out var sha)
                || !string.Equals(sha.GetString(), sourceSha256, StringComparison.OrdinalIgnoreCase)) continue;
            if (!root.TryGetProperty("occurrence", out var occurrence) || !occurrence.TryGetProperty("claims", out var claims)) return null;
            var gold = claims.EnumerateArray()
                .Where(c => c.TryGetProperty("sourceId", out _))
                .Select(c => c.GetProperty("sourceId").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var tp = gold.Count(predicted.Contains);
            var fp = predicted.Count - tp;
            var fn = gold.Count - tp;
            return new GoldScore(Path.GetFileName(file), tp, fp, fn,
                tp + fp == 0 ? null : (double)tp / (tp + fp),
                tp + fn == 0 ? null : (double)tp / (tp + fn),
                2 * tp + fp + fn == 0 ? null : 2.0 * tp / (2 * tp + fp + fn));
        }
        return null;
    }
}
