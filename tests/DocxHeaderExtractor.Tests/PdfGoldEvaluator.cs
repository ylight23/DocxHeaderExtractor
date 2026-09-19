using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Tests;

/// <summary>One predicted heading, in the same address space Gold uses.</summary>
public sealed record PdfPredictedHeading(
    string SourceAlias,
    string SelectionMode,
    string? VerbatimText = null,
    int? Occurrence = null,
    string? ParentSourceAlias = null);

/// <summary>
/// Two measurements, deliberately kept apart.
/// <para>
/// Semantic membership asks whether the right occurrences were identified as headings. Structural
/// relation asks whether the ones both sides agree on were placed under the right parent. Folding
/// them together makes a correct heading with a wrong parent read as a missed heading, which sends
/// the next investigation after a recall problem that does not exist. A hierarchy question can only
/// be asked about a heading both sides already found.
/// </para>
/// <para>
/// Relations are scored only over headings Gold actually adjudicated a parent for. Gold with no
/// parent on a row is silent about that relation, not asserting it has none, so a prediction is
/// neither right nor wrong there and the row is excluded rather than counted as a failure.
/// </para>
/// </summary>
public sealed record PdfGoldEvaluation(
    [property: JsonPropertyName("semantic")] PdfMembershipScore Semantic,
    [property: JsonPropertyName("structural")] PdfRelationScore Structural)
{
    [property: JsonPropertyName("goldRows")] public int GoldRows { get; init; }
    [property: JsonPropertyName("predictedRows")] public int PredictedRows { get; init; }

    /// <summary>Gold rows that did not bind. Non-zero means the Gold is not measurable yet.</summary>
    [property: JsonPropertyName("goldIssues")] public IReadOnlyList<string> GoldIssues { get; init; } = [];
}

public sealed record PdfMembershipScore(
    [property: JsonPropertyName("truePositive")] int TruePositive,
    [property: JsonPropertyName("falsePositive")] int FalsePositive,
    [property: JsonPropertyName("falseNegative")] int FalseNegative)
{
    [JsonPropertyName("recall")]
    public double Recall => TruePositive + FalseNegative == 0 ? 1 : (double)TruePositive / (TruePositive + FalseNegative);

    [JsonPropertyName("precision")]
    public double Precision => TruePositive + FalsePositive == 0 ? 1 : (double)TruePositive / (TruePositive + FalsePositive);

    [JsonPropertyName("missedAliases")] public IReadOnlyList<string> MissedAliases { get; init; } = [];
    [JsonPropertyName("spuriousAliases")] public IReadOnlyList<string> SpuriousAliases { get; init; } = [];
}

public sealed record PdfRelationScore(
    [property: JsonPropertyName("adjudicated")] int Adjudicated,
    [property: JsonPropertyName("agreed")] int Agreed,
    [property: JsonPropertyName("disagreed")] int Disagreed,
    [property: JsonPropertyName("notProposed")] int NotProposed)
{
    /// <summary>Over adjudicated relations only; a document Gold said nothing about scores 1 of 0.</summary>
    [JsonPropertyName("accuracy")]
    public double Accuracy => Adjudicated == 0 ? 1 : (double)Agreed / Adjudicated;

    [JsonPropertyName("disagreements")] public IReadOnlyList<string> Disagreements { get; init; } = [];
}

public static class PdfGoldEvaluator
{
    /// <summary>
    /// Compares a prediction with Gold in the source address space both share. Identity is
    /// (alias, selection, text, occurrence) - never a character offset, and never a text match
    /// across occurrences, because the same words can legitimately appear in several.
    /// </summary>
    public static PdfGoldEvaluation Evaluate(
        PdfGoldDocument gold,
        IReadOnlyList<PdfPredictedHeading> predicted,
        IReadOnlyList<PdfGoldIssue>? goldIssues = null)
    {
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentNullException.ThrowIfNull(predicted);

        var goldByKey = gold.Headings.ToDictionary(Key, StringComparer.Ordinal);
        var predictedByKey = new Dictionary<string, PdfPredictedHeading>(StringComparer.Ordinal);
        foreach (var row in predicted) predictedByKey[Key(row)] = row;

        var matched = goldByKey.Keys.Where(predictedByKey.ContainsKey).ToArray();
        var missed = goldByKey.Keys.Except(predictedByKey.Keys, StringComparer.Ordinal)
            .Select(key => goldByKey[key].SourceAlias).Order(StringComparer.Ordinal).ToArray();
        var spurious = predictedByKey.Keys.Except(goldByKey.Keys, StringComparer.Ordinal)
            .Select(key => predictedByKey[key].SourceAlias).Order(StringComparer.Ordinal).ToArray();

        var membership = new PdfMembershipScore(matched.Length, spurious.Length, missed.Length)
        {
            MissedAliases = missed,
            SpuriousAliases = spurious,
        };

        // Only headings both sides found, and only where Gold adjudicated a parent.
        var adjudicated = matched
            .Where(key => goldByKey[key].ParentSourceAlias is { Length: > 0 })
            .ToArray();
        var agreed = 0;
        var notProposed = 0;
        var disagreements = new List<string>();
        foreach (var key in adjudicated)
        {
            var expected = goldByKey[key].ParentSourceAlias!;
            var actual = predictedByKey[key].ParentSourceAlias;
            if (actual is null or "") { notProposed++; disagreements.Add($"{goldByKey[key].SourceAlias}: expected {expected}, none proposed"); }
            else if (string.Equals(actual, expected, StringComparison.Ordinal)) agreed++;
            else disagreements.Add($"{goldByKey[key].SourceAlias}: expected {expected}, got {actual}");
        }

        var relations = new PdfRelationScore(
            adjudicated.Length, agreed, adjudicated.Length - agreed - notProposed, notProposed)
        {
            Disagreements = disagreements,
        };

        return new PdfGoldEvaluation(membership, relations)
        {
            GoldRows = gold.Headings.Count,
            PredictedRows = predicted.Count,
            GoldIssues = (goldIssues ?? []).Select(issue => $"{issue.Code}:{issue.SourceAlias}").ToArray(),
        };
    }

    private static string Key(PdfGoldHeading row) =>
        $"{row.SourceAlias}|{row.SelectionMode}|{row.VerbatimText}|{row.Occurrence}";

    private static string Key(PdfPredictedHeading row) =>
        $"{row.SourceAlias}|{row.SelectionMode}|{row.VerbatimText}|{row.Occurrence}";
}
