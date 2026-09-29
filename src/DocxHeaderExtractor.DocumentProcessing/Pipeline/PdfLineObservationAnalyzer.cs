using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Raw layout measurements of one parser line. Positions and counts only - no "header/footer",
/// "repeated", "page number" or "table-like" verdict: what a line is belongs to the model.
/// </summary>
internal sealed record PdfLineBlockAnnotation(PdfLine Line)
{
    /// <summary>
    /// The line's vertical position within the document's own text extent: 0 at the lowest text,
    /// 1 at the highest (PDF user space grows upward). A measurement, not a band.
    /// </summary>
    public double VerticalPosition { get; init; }

    /// <summary>
    /// How many distinct pages carry a line with the same normalized text (case folded, standalone
    /// numbers masked, alphanumerics only), and the first and last of them.
    /// </summary>
    public int SameNormalizedTextPageCount { get; init; } = 1;

    public int SameNormalizedTextFirstPage { get; init; }

    public int SameNormalizedTextLastPage { get; init; }
}

/// <summary>
/// Computes position and recurrence measurements for each parser line. It does not create a
/// candidate set, discard a source occurrence, or classify a line.
/// </summary>
internal static class PdfLineObservationAnalyzer
{
    private static readonly Regex NonAlphaNumRx = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    public static IReadOnlyList<PdfLineBlockAnnotation> Analyze(IReadOnlyList<PdfLine> lines)
    {
        if (lines.Count == 0) return [];

        var pagesByKey = lines
            .Select(l => (l.Page, Key: RepeatKey(l.Text)))
            .Where(x => x.Key.Length > 0)
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Page).Distinct().Order().ToArray());

        var minY = lines.Min(l => l.Y);
        var maxY = lines.Max(l => l.Y);
        var span = Math.Max(1, maxY - minY);

        return lines.Select(line =>
        {
            var samePages = pagesByKey.TryGetValue(RepeatKey(line.Text), out var found) ? found : [line.Page];
            return new PdfLineBlockAnnotation(line)
            {
                VerticalPosition = Math.Round((line.Y - minY) / span, 3),
                SameNormalizedTextPageCount = samePages.Length,
                SameNormalizedTextFirstPage = samePages[0],
                SameNormalizedTextLastPage = samePages[^1],
            };
        }).ToList();
    }

    private static string RepeatKey(string text)
    {
        var readable = PdfTextUtilities.Readable(text).ToLowerInvariant();
        readable = Regex.Replace(readable, @"\b\d{1,4}\b", "#");
        return NonAlphaNumRx.Replace(readable, "");
    }
}
