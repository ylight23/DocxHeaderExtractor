using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Core.Pipeline;

internal sealed record PdfLineBlockAnnotation(
    PdfLine Line,
    bool Repeated,
    bool HeaderFooterZone,
    bool TableLike,
    bool PageNumber,
    string Reason)
{
    public bool ExcludeFromSemanticSamples =>
        PageNumber ||
        TableLike ||
        (Repeated && (HeaderFooterZone || PdfTextUtilities.Readable(Line.Text).Length <= 60));

    // Geometric/table-like evidence remains excluded from style learning above, but is not by
    // itself proof that the source fact is table body. A non-repeated structural marker may enter
    // grouping for later scope-aware validation; ordinary table cells remain out of the pool.
    public bool ExcludeFromCandidateGrouping =>
        PageNumber ||
        (Repeated && HeaderFooterZone) ||
        (TableLike && (Repeated || !HasStructuralMarker(Line.Text)));

    private static bool HasStructuralMarker(string text)
    {
        var repaired = PdfTextUtilities.HeadingReadable(text);
        return PdfMarkerFactsParser.Parse(text) is not null ||
               PdfMarkerFactsParser.Parse(repaired) is not null ||
               Regex.IsMatch(repaired, @"^\s*\p{L}{2,24}\s*\d{1,3}\s*[:.)-]", RegexOptions.CultureInvariant) ||
               Regex.IsMatch(text, @"^\s*\p{Lu}\p{Ll}{1,5}\s+\p{Ll}{1,5}\s+\d{1,3}\s*[:.)-]",
                   RegexOptions.CultureInvariant);
    }
}

internal sealed record PdfLineFilterSummary(
    int TotalLines,
    int SemanticCandidateLines,
    int RepeatedLines,
    int HeaderFooterZoneLines,
    int TableLikeLines,
    int PageNumberLines);

internal static class PdfLineBlockFilter
{
    private static readonly Regex NonAlphaNumRx = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Annotates each line. <paramref name="withheldTableLikeLines"/> is evaluation-only: the indexes
    /// of lines whose table-like mark is withheld, so a counterfactual can remove the mark from a
    /// reviewed occurrence and change nothing else. Lines are addressed by index into this list, not
    /// by their text, because two lines can read identically and only one of them is the occurrence
    /// under study. It is empty in production.
    /// </summary>
    public static IReadOnlyList<PdfLineBlockAnnotation> Analyze(
        IReadOnlyList<PdfLine> lines,
        IReadOnlySet<int>? withheldTableLikeLines = null)
    {
        if (lines.Count == 0) return [];

        var pages = lines.Select(l => l.Page).Distinct().Count();
        var repeatedKeys = lines
            .Select(l => (Line: l, Key: RepeatKey(l.Text)))
            .Where(x => x.Key.Length >= 6)
            .GroupBy(x => x.Key)
            .Where(g => g.Select(x => x.Line.Page).Distinct().Count() >= Math.Min(Math.Max(3, pages / 3), Math.Max(3, pages - 1)))
            .Select(g => g.Key)
            .ToHashSet();

        var minY = lines.Min(l => l.Y);
        var maxY = lines.Max(l => l.Y);
        var span = Math.Max(1, maxY - minY);

        return lines.Select((line, index) =>
        {
            var repeated = repeatedKeys.Contains(RepeatKey(line.Text));
            var headerFooter = IsHeaderFooterZone(line, minY, span);
            var pageNumber = IsPageNumber(line.Text);
            var tableLikeRule = ClassifyTableLine(line.Text);
            var tableLike = tableLikeRule is not null &&
                            withheldTableLikeLines?.Contains(index) != true;
            var reasons = new List<string>();
            if (pageNumber) reasons.Add("page-number");
            if (repeated) reasons.Add("repeated");
            if (headerFooter) reasons.Add("header-footer-zone");
            if (tableLike) reasons.Add("table-like");
            return new PdfLineBlockAnnotation(
                line,
                repeated,
                headerFooter,
                tableLike,
                pageNumber,
                reasons.Count == 0 ? "semantic-candidate" : string.Join(",", reasons));
        }).ToList();
    }

    public static PdfLineFilterSummary Summarize(IEnumerable<PdfLineBlockAnnotation> annotations)
    {
        var list = annotations.ToList();
        return new PdfLineFilterSummary(
            list.Count,
            list.Count(a => !a.ExcludeFromSemanticSamples),
            list.Count(a => a.Repeated),
            list.Count(a => a.HeaderFooterZone),
            list.Count(a => a.TableLike),
            list.Count(a => a.PageNumber));
    }

    private static string RepeatKey(string text)
    {
        var readable = PdfTextUtilities.Readable(text).ToLowerInvariant();
        readable = Regex.Replace(readable, @"\b\d{1,4}\b", "#");
        return NonAlphaNumRx.Replace(readable, "");
    }

    private static bool IsHeaderFooterZone(PdfLine line, double minY, double span)
    {
        var relative = (line.Y - minY) / span;
        return relative <= 0.08 || relative >= 0.92;
    }

    private static bool IsPageNumber(string text) =>
        Regex.IsMatch(text.Trim(), @"^(?:page\s*)?\d{1,4}$", RegexOptions.IgnoreCase);

    private static bool LooksLikeTableLine(string text) => ClassifyTableLine(text) is not null;

    /// <summary>
    /// Names which of the rule's branches fired, or null when none did. Same conditions in the same
    /// order as the predicate above, which delegates here - the branch name is recorded, never
    /// recomputed, so an audit of why a line was called table-like cannot answer from a second copy
    /// of the rule. The names describe the branch, not a verdict about whether it was right.
    /// </summary>
    internal static string? ClassifyTableLine(string text)
    {
        var t = PdfTextUtilities.Readable(text);
        if (t.Length == 0) return "empty_readable";

        var alnum = t.Count(char.IsLetterOrDigit);
        if (alnum == 0) return "no_alphanumeric";
        var numeric = t.Count(char.IsDigit) + t.Count(c => c is '$' or '%' or ',' or '(' or ')');
        if (numeric / (double)alnum >= 0.35) return "numeric_density";

        var words = Regex.Matches(t, @"\p{L}+").Count;
        if (t.Length <= 32 && words <= 4 && Regex.IsMatch(t, @"\b\d+\b") && !Regex.IsMatch(t, @"[.!?]\s*$"))
            return "short_numbered";

        return null;
    }
}
