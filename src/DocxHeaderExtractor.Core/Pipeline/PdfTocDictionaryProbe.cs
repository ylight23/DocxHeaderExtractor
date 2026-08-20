using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Core.Pipeline;

internal sealed record PdfTocDictionaryProbeResult(
    int TocPage,
    int Entries,
    int ExactPageAnchors,
    int RelaxedPageAnchors,
    int AtOrAfterPageAnchors,
    IReadOnlyList<PdfTocDictionaryEntry> Items)
{
    public static PdfTocDictionaryProbeResult Empty { get; } = new(0, 0, 0, 0, 0, []);
}

internal sealed record PdfTocDictionaryEntry(
    string Title,
    int Page,
    string CanonicalText,
    int? ExactAnchorPage,
    int? RelaxedAnchorPage,
    int? AtOrAfterAnchorPage);

/// <summary>
/// Diagnostic-only PDF TOC dictionary probe. It uses dot-leader TOC entries as clean titles and
/// canonical matching against body pages as anchors. It does not rewrite PDF source text and does
/// not emit production headings.
/// </summary>
internal static class PdfTocDictionaryProbe
{
    private static readonly Regex DotLeaderEntryRx = new(
        @"^(?<title>.+?)\s*\.{5,}\s*(?<page>[\d\s]{1,8})$",
        RegexOptions.Compiled);
    private static readonly Regex LooseTocEntryRx = new(
        @"^(?<title>.+?)\s+(?<page>(?:\d\s*){1,3})$",
        RegexOptions.Compiled);

    public static PdfTocDictionaryProbeResult Analyze(IReadOnlyList<PdfLine> lines)
    {
        if (lines.Count == 0) return PdfTocDictionaryProbeResult.Empty;

        var byPage = lines
            .GroupBy(l => l.Page)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(l => l.Y).ThenBy(l => l.Left).ToList());

        var toc = byPage
            .Select(kv => (Page: kv.Key, Entries: ParseEntries(kv.Value, HasTocHeader(kv.Value))))
            .Where(x => x.Entries.Count >= 5)
            .OrderByDescending(x => x.Entries.Count)
            .ThenBy(x => x.Page)
            .FirstOrDefault();

        if (toc.Entries is not { Count: >= 5 }) return PdfTocDictionaryProbeResult.Empty;

        var pageCanonical = byPage
            .Where(kv => kv.Key != toc.Page)
            .ToDictionary(
                kv => kv.Key,
                kv => PdfTextUtilities.CanonicalForMatch(string.Join(" ", kv.Value.Select(l => l.Text))));

        var items = toc.Entries.Select(entry =>
        {
            var exact = pageCanonical.TryGetValue(entry.Page, out var exactPageText) &&
                        exactPageText.Contains(entry.CanonicalText, StringComparison.Ordinal)
                ? entry.Page
                : (int?)null;
            var relaxed = exact ?? RelaxedCanonicalTexts(entry.Title)
                .Where(c => pageCanonical.TryGetValue(entry.Page, out var pageText) &&
                            pageText.Contains(c, StringComparison.Ordinal))
                .Select(_ => (int?)entry.Page)
                .FirstOrDefault();
            var atOrAfter = pageCanonical
                .Where(kv => kv.Key >= entry.Page &&
                             (kv.Value.Contains(entry.CanonicalText, StringComparison.Ordinal) ||
                              RelaxedCanonicalTexts(entry.Title).Any(c => kv.Value.Contains(c, StringComparison.Ordinal))))
                .OrderBy(kv => kv.Key)
                .Select(kv => (int?)kv.Key)
                .FirstOrDefault();
            return entry with
            {
                ExactAnchorPage = exact,
                RelaxedAnchorPage = relaxed,
                AtOrAfterAnchorPage = atOrAfter,
            };
        }).ToArray();

        return new PdfTocDictionaryProbeResult(
            toc.Page,
            items.Length,
            items.Count(i => i.ExactAnchorPage is not null),
            items.Count(i => i.RelaxedAnchorPage is not null),
            items.Count(i => i.AtOrAfterAnchorPage is not null),
            items);
    }

    private static bool HasTocHeader(IReadOnlyList<PdfLine> pageLines) =>
        pageLines.Any(l => PdfTextUtilities.CanonicalForMatch(l.Text).Contains("tableofcontents", StringComparison.Ordinal));

    private static List<PdfTocDictionaryEntry> ParseEntries(IReadOnlyList<PdfLine> pageLines, bool allowLoose)
    {
        var result = new List<PdfTocDictionaryEntry>();
        foreach (var line in pageLines)
        {
            var text = PdfTextUtilities.Readable(line.Text);
            var match = DotLeaderEntryRx.Match(text);
            if (!match.Success && allowLoose)
                match = LooseTocEntryRx.Match(text);
            if (!match.Success) continue;

            var title = CleanTitle(match.Groups["title"].Value);
            if (title.Length is < 3 or > 180) continue;

            var pageText = Regex.Replace(match.Groups["page"].Value, @"\s+", "");
            if (!int.TryParse(pageText, out var page) || page <= 0) continue;

            result.Add(new PdfTocDictionaryEntry(
                title,
                page,
                PdfTextUtilities.CanonicalForMatch(title),
                null,
                null,
                null));
        }

        return result;
    }

    private static string CleanTitle(string title)
    {
        title = PdfTextUtilities.HeadingReadable(title);
        title = Regex.Replace(title, @"\s+", " ").Trim(' ', '.');
        return title;
    }

    private static IEnumerable<string> RelaxedCanonicalTexts(string title)
    {
        // These relaxed variants are only safe when checked on the page declared by the TOC entry.
        // Used globally, prefix matching such as "A - B" => "A" would merge distinct headings.
        foreach (var tail in RemoveLeadingNavigationPhrase(title))
            yield return PdfTextUtilities.CanonicalForMatch(tail);

        foreach (var prefix in PrefixBeforeQualifier(title))
            yield return PdfTextUtilities.CanonicalForMatch(prefix);
    }

    private static IEnumerable<string> RemoveLeadingNavigationPhrase(string title)
    {
        var match = Regex.Match(title, @"^\s*[\p{L}\s]{3,40}?\s+(?:to|of|about|về)\s+(?<tail>.+)$",
            RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var tail = match.Groups["tail"].Value.Trim();
            if (tail.Length >= 6) yield return PdfTextUtilities.CanonicalForMatch(tail);
        }
    }

    private static IEnumerable<string> PrefixBeforeQualifier(string title)
    {
        var parts = Regex.Split(title, @"\s*(?:—|–|-|:)\s+");
        if (parts.Length < 2) yield break;

        var prefix = parts[0].Trim();
        if (prefix.Length < 6) yield break;
        if (Regex.Matches(prefix, @"\p{L}+").Count < 2) yield break;

        yield return prefix;
    }
}
