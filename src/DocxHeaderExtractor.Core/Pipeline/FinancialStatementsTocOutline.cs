using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// World Bank financial statements converted from PDF often keep a complete text "Contents" page,
/// but DOCX/PDF visual signals otherwise look like dense numeric tables. This route uses Contents as
/// the title dictionary and body text only as anchors.
/// </summary>
public static class FinancialStatementsTocOutline
{
    public const string Basis = "financial_statement_toc_text";

    private static readonly Regex SectionMarkerRx = new(
        @"(?:^|\b)Section\s+(?<roman>[IVXLCDM]{1,8}):",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AppendixRx = new(@"\bAppendix\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PageRx = new(@"^\d{1,3}$", RegexOptions.Compiled);
    private static readonly Regex EntryWithPageRx = new(
        @"(?<title>[A-Z][A-Za-z0-9&'’(),/\- ]{2,150}?)\s+(?<page>\d{1,3})(?=\s+(?:[A-Z]|Section|Appendix)|$)",
        RegexOptions.Compiled);
    private static readonly Regex SpaceRx = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumRx = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    public static bool HasTextTocSignal(SlimDocument document) => FindTocParagraph(document) is not null;

    public static List<HeadingRecord> Build(SlimDocument document) => Analyze(document);

    private static List<HeadingRecord> Analyze(SlimDocument document)
    {
        var toc = FindTocParagraph(document);
        if (toc is null) return [];

        var entries = ParseEntries(toc.Text, toc.Index, document);
        if (entries.Count < 10) return [];

        var anchors = AnchorEntries(document, entries, toc.Index);
        var ratio = anchors.Count / (double)entries.Count;
        return anchors.Count >= 10 && ratio >= 0.55 ? anchors : [];
    }

    private static SlimParagraph? FindTocParagraph(SlimDocument document) =>
        document.Paragraphs
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Index)
            .FirstOrDefault(p =>
            {
                var text = NormalizeTocText(p.Text);
                return text.Contains("Contents", StringComparison.OrdinalIgnoreCase) &&
                       SectionMarkerRx.Matches(text).Count >= 3 &&
                       Regex.Matches(text, @"\b\d{1,3}\b").Count >= 8;
            });

    private static List<TocEntry> ParseEntries(string text, int tocIndex, SlimDocument document)
    {
        var normalized = NormalizeTocText(text);
        var entries = new List<TocEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var bodyCanon = Canon(string.Join(' ', document.Paragraphs
            .Where(p => p.Index > tocIndex && !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => p.Text)));
        var markers = SectionMarkerRx.Matches(normalized).Cast<Match>()
            .Concat(AppendixRx.Matches(normalized).Cast<Match>())
            .OrderBy(m => m.Index)
            .ToList();

        for (var i = 0; i < markers.Count; i++)
        {
            var marker = markers[i];
            var next = i + 1 < markers.Count ? markers[i + 1].Index : normalized.Length;
            var chunk = normalized[marker.Index..next];
            TocEntry? currentSection;

            if (AppendixRx.IsMatch(marker.Value) && !SectionMarkerRx.IsMatch(marker.Value))
            {
                currentSection = Add("Appendix", 1, null, ["Appendix"]);
            }
            else
            {
                var section = SectionMarkerRx.Match(chunk);
                if (!section.Success) continue;
                var roman = section.Groups["roman"].Value.ToUpperInvariant();
                var firstEntry = EntryWithPageRx.Match(chunk, section.Index + section.Length);
                var rawTail = firstEntry.Success
                    ? chunk[(section.Index + section.Length)..firstEntry.Groups["title"].Index]
                    : chunk[(section.Index + section.Length)..];
                var firstTitle = firstEntry.Success ? Clean(firstEntry.Groups["title"].Value) : "";
                var title = FindRepeatedSectionTitle(document, tocIndex, roman) ??
                            ChooseSectionTitle($"Section {roman}", Clean(rawTail), firstTitle, bodyCanon);
                var fullTitle = Clean($"Section {roman}: {title}");
                currentSection = Add(fullTitle, 1, null, [fullTitle, $"Section {roman}", title]);
            }

            foreach (Match entryMatch in EntryWithPageRx.Matches(chunk))
            {
                var title = Clean(entryMatch.Groups["title"].Value);
                if (currentSection is not null)
                    title = RemoveSectionPrefix(title, currentSection.Title);
                if (!int.TryParse(entryMatch.Groups["page"].Value, out var page)) continue;
                Add(title, currentSection is null ? 1 : 2, page, [title]);
            }
        }

        return entries;

        TocEntry? Add(string title, int level, int? page, IReadOnlyList<string> anchors)
        {
            title = Clean(title);
            if (!LooksLikeTitle(title)) return null;
            var key = Canon(title);
            if (!seen.Add(key)) return null;
            var entry = new TocEntry(title, level, page, tocIndex, anchors);
            entries.Add(entry);
            return entry;
        }
    }

    private static string ChooseSectionTitle(string marker, string rawTail, string firstEntryTitle, string bodyCanon)
    {
        if (rawTail.Length >= 4 && bodyCanon.Contains(Canon($"{marker} {rawTail}"), StringComparison.Ordinal))
            return rawTail;

        var words = firstEntryTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var take = Math.Min(words.Length, 8); take >= 1; take--)
        {
            var candidate = Clean(string.Join(' ', words.Take(take)));
            if (candidate.Length >= 4 && bodyCanon.Contains(Canon($"{marker} {candidate}"), StringComparison.Ordinal))
                return candidate;
        }

        return rawTail.Length >= 4 ? rawTail : Clean(words.FirstOrDefault() ?? "");
    }

    private static string? FindRepeatedSectionTitle(SlimDocument document, int tocIndex, string roman)
    {
        var marker = $@"Section\s+{Regex.Escape(roman)}:";
        var repeated = new Regex(
            $@"{marker}\s*(?<title>.{{3,90}}?)\s*{marker}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        foreach (var paragraph in document.Paragraphs.Where(p => p.Index > tocIndex && !string.IsNullOrWhiteSpace(p.Text)))
        {
            var match = repeated.Match(paragraph.Text);
            if (!match.Success) continue;

            var title = Clean(match.Groups["title"].Value);
            if (LooksLikeTitle(title) && !title.Contains("Table ", StringComparison.OrdinalIgnoreCase))
                return title;
        }

        return null;
    }

    private static string RemoveSectionPrefix(string title, string sectionTitle)
    {
        var cleanSection = Clean(Regex.Replace(sectionTitle, @"^Section\s+[IVXLCDM]+:\s*", "", RegexOptions.IgnoreCase));
        if (cleanSection.Length >= 4 && title.StartsWith(cleanSection, StringComparison.OrdinalIgnoreCase))
            return Clean(title[cleanSection.Length..]);
        return title;
    }

    private static string NormalizeTocText(string text)
    {
        var s = text.Replace("ContentsSection", "Contents    Section", StringComparison.OrdinalIgnoreCase);
        s = Regex.Replace(s, @"(?<=\d)(?=Section\s+[IVXLCDM]+:)", "    ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(?<=\d)(?=Appendix\b)", "    ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(?<=\d)(?=[A-Z][a-z][A-Za-z])", "    ");
        return s;
    }

    private static List<HeadingRecord> AnchorEntries(
        SlimDocument document,
        IReadOnlyList<TocEntry> entries,
        int tocIndex)
    {
        var paragraphs = document.Paragraphs
            .Where(p => p.Index > tocIndex &&
                        p.Role != ParagraphRole.Empty &&
                        !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Index)
            .Select(p => new CanonParagraph(p, CanonicalMap(p.Text)))
            .Where(p => p.Map.Canonical.Length > 0)
            .ToList();

        var result = new List<HeadingRecord>();
        var cursor = tocIndex + 1;
        foreach (var entry in entries)
        {
            var match = FindAnchor(paragraphs, entry, cursor);
            if (match is null) continue;

            result.Add(new HeadingRecord
            {
                Index = match.Value.Paragraph.Index,
                StableId = match.Value.Paragraph.StableId,
                Level = entry.Level,
                Text = entry.Title,
                OriginalText = match.Value.Paragraph.Text,
                HeadingSpan = new TextOffsetSpan(match.Value.Start, match.Value.End),
                BoundarySource = entry.Page is null ? "financial_statement_section_toc" : $"financial_statement_toc_page_{entry.Page}",
                StyleId = match.Value.Paragraph.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0.96,
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                ConfidenceBasis = Basis,
            });
            cursor = match.Value.Paragraph.Index;
        }

        return result.OrderBy(h => h.Index).ThenBy(h => h.Level).ToList();
    }

    private static MatchResult? FindAnchor(IReadOnlyList<CanonParagraph> paragraphs, TocEntry entry, int minIndex)
    {
        foreach (var anchor in entry.AnchorParts.Select(Canon).Where(a => a.Length >= 5).Distinct())
        {
            foreach (var p in paragraphs.Where(p => p.Paragraph.Index >= minIndex))
            {
                var at = 0;
                while (at >= 0 && at < p.Map.Canonical.Length)
                {
                    at = p.Map.Canonical.IndexOf(anchor, at, StringComparison.Ordinal);
                    if (at < 0) break;

                    var start = p.Map.SourceIndexes[at];
                    var end = p.Map.SourceIndexes[at + anchor.Length - 1] + 1;
                    if (start < p.Paragraph.Text.Length &&
                        (!char.IsLetter(p.Paragraph.Text[start]) || char.IsUpper(p.Paragraph.Text[start])))
                        return new MatchResult(p.Paragraph, start, end);

                    at += anchor.Length;
                }
            }
        }

        return null;
    }

    private static bool LooksLikeTitle(string text)
    {
        text = Clean(text);
        if (text.Length is < 4 or > 150) return false;
        if (!text.Any(char.IsLetter)) return false;
        if (Regex.IsMatch(text, @"^\d+$")) return false;
        if (text.Contains('$') || Regex.IsMatch(text, @"\d{4}")) return false;
        return true;
    }

    private static string Clean(string text)
    {
        var s = text.Replace('–', '-').Replace('—', '-').Replace('’', '\'');
        s = SpaceRx.Replace(s, " ").Trim(' ', '.', ':');
        s = Regex.Replace(s, @"^(?:.*\bContents)\s+", "", RegexOptions.IgnoreCase);
        return s.Trim();
    }

    private static CanonMap CanonicalMap(string text)
    {
        var canonical = new System.Text.StringBuilder(text.Length);
        var indexes = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c)) continue;
            canonical.Append(char.ToLowerInvariant(c));
            indexes.Add(i);
        }
        return new CanonMap(canonical.ToString(), indexes);
    }

    private static string Canon(string text) => NonAlphaNumRx.Replace(Clean(text).ToLowerInvariant(), "");

    private sealed record TocEntry(
        string Title,
        int Level,
        int? Page,
        int TocParagraphIndex,
        IReadOnlyList<string> AnchorParts);

    private sealed record CanonMap(string Canonical, IReadOnlyList<int> SourceIndexes);
    private sealed record CanonParagraph(SlimParagraph Paragraph, CanonMap Map);
    private readonly record struct MatchResult(SlimParagraph Paragraph, int Start, int End);
}
