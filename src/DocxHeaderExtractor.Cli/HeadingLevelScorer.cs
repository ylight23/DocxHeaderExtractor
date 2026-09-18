using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Cli;

/// <summary>One reference heading: the text a human approved, and the level they gave it.</summary>
public sealed record ScoredHeading(string Text, int? Level);

/// <summary>
/// Level accuracy of a prediction against an approved reference.
/// <para>
/// Two measurement rules are deliberate. Occurrences are joined on VERBATIM TEXT, never on
/// sourceId: references in this repository carry at least two paragraph-id schemes
/// (<c>paragraph[N]</c> and <c>body[1]/p[N]</c>) and some point at the wrong paragraph
/// outright, so a sourceId join silently reports a recall failure that is really a join failure.
/// </para>
/// <para>
/// Level is reported twice. The absolute figure compares the numbers as they stand. The
/// structural figure first removes the single constant offset that fits the matched set best,
/// because whether the document title counts as an ancestor shifts every level by the same
/// amount without changing which headings are siblings. Only the structural figure says whether
/// the hierarchy is right; the offset itself says whether the two sides share a root convention.
/// </para>
/// </summary>
public sealed record HeadingLevelScore(
    int GoldCount,
    int PredictionCount,
    int Matched,
    int LevelComparable,
    int AbsoluteExact,
    int StructuralExact,
    int LevelOffset,
    int Unresolved,
    IReadOnlyList<string> MissingFromPrediction,
    IReadOnlyList<(string Text, int GoldLevel, int PredictedLevel)> StructuralMismatches)
{
    public double Recall => GoldCount == 0 ? 0 : (double)Matched / GoldCount;
    public double Precision => PredictionCount == 0 ? 0 : (double)Matched / PredictionCount;

    /// <summary>Never a bare percentage: an accuracy without its denominator hides the sample.</summary>
    public string Describe(string documentId) =>
        $"""
         {documentId}
           reference headings : {GoldCount}
           predicted headings : {PredictionCount}
           matched on text    : {Matched}  (recall {Recall:P0}, precision {Precision:P0})
           missing            : {MissingFromPrediction.Count}
           level comparable   : {LevelComparable}   unresolved: {Unresolved}
           level absolute     : {AbsoluteExact}/{LevelComparable}
           level structural   : {StructuralExact}/{LevelComparable}   (root offset {LevelOffset:+#;-#;0})
         """;
}

public static class HeadingLevelScorer
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    internal static string Normalize(string? text) =>
        Whitespace.Replace(text ?? string.Empty, " ").Trim().ToLowerInvariant();

    public static HeadingLevelScore Score(
        IReadOnlyList<ScoredHeading> gold,
        IReadOnlyList<ScoredHeading> predicted)
    {
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentNullException.ThrowIfNull(predicted);

        var goldByText = new Dictionary<string, ScoredHeading>(StringComparer.Ordinal);
        foreach (var item in gold)
            goldByText.TryAdd(Normalize(item.Text), item);
        var predictedByText = new Dictionary<string, ScoredHeading>(StringComparer.Ordinal);
        foreach (var item in predicted)
            predictedByText.TryAdd(Normalize(item.Text), item);

        var pairs = new List<(string Text, int GoldLevel, int PredictedLevel)>();
        var unresolved = 0;
        var matched = 0;
        var missing = new List<string>();
        foreach (var (key, goldItem) in goldByText)
        {
            if (!predictedByText.TryGetValue(key, out var predictedItem))
            {
                missing.Add(goldItem.Text);
                continue;
            }
            matched++;
            if (predictedItem.Level is null || goldItem.Level is null) unresolved++;
            else pairs.Add((goldItem.Text, goldItem.Level.Value, predictedItem.Level.Value));
        }

        var absolute = pairs.Count(pair => pair.GoldLevel == pair.PredictedLevel);
        var offset = BestOffset(pairs);
        var structural = pairs.Count(pair => pair.PredictedLevel - offset == pair.GoldLevel);
        var mismatches = pairs
            .Where(pair => pair.PredictedLevel - offset != pair.GoldLevel)
            .ToArray();

        return new HeadingLevelScore(
            goldByText.Count, predictedByText.Count, matched, pairs.Count,
            absolute, structural, offset, unresolved, missing, mismatches);
    }

    /// <summary>
    /// The single shift that best aligns the two level scales. Searched over the offsets actually
    /// present in the data rather than a guessed window, so no document can silently fall outside it.
    /// </summary>
    private static int BestOffset(IReadOnlyList<(string Text, int GoldLevel, int PredictedLevel)> pairs)
    {
        if (pairs.Count == 0) return 0;
        return pairs
            .Select(pair => pair.PredictedLevel - pair.GoldLevel)
            .Distinct()
            .OrderByDescending(candidate => pairs.Count(pair => pair.PredictedLevel - candidate == pair.GoldLevel))
            .ThenBy(int.Abs)
            .First();
    }
}
