using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

public sealed record BookTocDictionaryResult(
    bool Accepted,
    IReadOnlyList<HeadingRecord> Headings,
    BookTocDictionaryDiagnostics Diagnostics);

public sealed record BookTocDictionaryDiagnostics(
    string Reason,
    int TocParagraphs,
    int DictionaryEntries,
    int BodyAnchors,
    double BodyAnchorRatio,
    int TocStartIndex,
    int TocEndIndex);

/// <summary>
/// Textbook/PDF text-layout route: TOC provides the clean title dictionary, body occurrences provide
/// DOCX anchors and spans. This avoids accepting whole PDF-converted paragraphs as headings.
/// </summary>
public static class BookTocDictionaryOutline
{
    public const string Basis = "book_toc_dictionary";

    private static readonly Regex WhitespaceRx = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex TokenRx = new(@"[A-Za-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex EntryStartRx = new(
        @"(?<![A-Za-z0-9])(?<entry>Preface|Bibliography|Index|Part\s+(?<roman>[IVXLC]+)\.?|Chapter\s+(?<chapter>\d{1,2})\.?|(?<section>\d{1,2}[a-z])\.)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StandaloneBodyStartRx = new(
        @"^(?:Part\s+[IVXLC]+|CHAPTER\s+\d{1,2})\.?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<HeadingRecord> Build(SlimDocument document) => Analyze(document).Headings;

    public static BookTocDictionaryResult Analyze(SlimDocument document)
    {
        var toc = FindTocCluster(document);
        if (toc is null)
            return Reject("no-book-toc-cluster");

        var entries = ParseEntries(toc.Text);
        if (entries.Count < 20)
            return Reject("too-few-book-toc-entries", toc, entries.Count, 0);

        var anchored = AnchorEntries(document, entries, toc.EndIndex);
        var ratio = anchored.Count / (double)entries.Count;
        if (anchored.Count < 20 || ratio < 0.65)
            return Reject("low-body-anchor-ratio", toc, entries.Count, anchored.Count);

        return new BookTocDictionaryResult(
            true,
            anchored,
            new BookTocDictionaryDiagnostics(
                "accepted",
                toc.ParagraphCount,
                entries.Count,
                anchored.Count,
                ratio,
                toc.StartIndex,
                toc.EndIndex));
    }

    private static BookTocDictionaryResult Reject(
        string reason,
        TocCluster? toc = null,
        int entries = 0,
        int anchors = 0)
    {
        var ratio = entries == 0 ? 0 : anchors / (double)entries;
        return new BookTocDictionaryResult(
            false,
            [],
            new BookTocDictionaryDiagnostics(
                reason,
                toc?.ParagraphCount ?? 0,
                entries,
                anchors,
                ratio,
                toc?.StartIndex ?? -1,
                toc?.EndIndex ?? -1));
    }

    private static TocCluster? FindTocCluster(SlimDocument document)
    {
        var paragraphs = document.Paragraphs
            .Where(p => p.Role != ParagraphRole.Empty && !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Index)
            .ToList();

        for (var i = 0; i < paragraphs.Count; i++)
        {
            var text = paragraphs[i].Text;
            if (!text.Equals("Contents", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("Table of Contents", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = new List<SlimParagraph>();
            // PDF-to-DOCX converters frequently split a visual TOC table into one paragraph per
            // cell.  The TOC label, title, and page number are then separate paragraphs, so use
            // a bounded contiguous window instead of requiring "Contents" and "Chapter" to be
            // in the same paragraph.
            for (var j = i; j < paragraphs.Count && parts.Count < 320; j++)
            {
                var p = paragraphs[j];
                if (p.Index > paragraphs[i].Index &&
                    StandaloneBodyStartRx.IsMatch(p.Text) &&
                    CountEntries(string.Join(' ', parts.Select(x => x.Text))) >= 8)
                    break;

                parts.Add(p);
                var combined = NormalizeSpace(string.Join(' ', parts.Select(x => x.Text)));
                if (CountEntries(combined) >= 20 &&
                    j + 1 < paragraphs.Count &&
                    StandaloneBodyStartRx.IsMatch(paragraphs[j + 1].Text))
                    break;
            }

            var clusterText = NormalizeSpace(string.Join(' ', parts.Select(x => x.Text)));
            if (CountEntries(clusterText) >= 20)
                return new TocCluster(
                    paragraphs[i].Index,
                    parts[^1].Index,
                    parts.Count,
                    clusterText);
        }

        return null;
    }

    private static List<BookTocEntry> ParseEntries(string text)
    {
        var entries = new List<BookTocEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var starts = EntryStartRx.Matches(text).Cast<Match>().ToList();

        for (var i = 0; i < starts.Count; i++)
        {
            var m = starts[i];
            var next = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
            var marker = CleanMarker(m.Groups["entry"].Value);
            var tailStart = m.Index + m.Length;
            var tail = CleanTocTitle(text[tailStart..next]);
            BookTocEntry? entry = null;

            if (marker.Equals("Preface", StringComparison.OrdinalIgnoreCase))
            {
                // This route is for body outline navigation. Front-matter preface entries in PDF
                // textbook TOCs are often before the body anchor window and are not part of the
                // expected outline for the current corpus slice.
                continue;
            }
            else if (marker.Equals("Bibliography", StringComparison.OrdinalIgnoreCase) ||
                     marker.Equals("Index", StringComparison.OrdinalIgnoreCase))
            {
                var title = marker.Equals("Bibliography", StringComparison.OrdinalIgnoreCase)
                    ? "Bibliography"
                    : "Index";
                entry = new BookTocEntry($"BACK:{title.ToUpperInvariant()}", 1, title, [title]);
            }
            else if (m.Groups["roman"].Success)
            {
                var roman = m.Groups["roman"].Value.ToUpperInvariant();
                var title = CleanTitle(tail);
                entry = new BookTocEntry(
                    $"PART:{roman}",
                    1,
                    $"Part {roman}. {title}",
                    [$"Part {roman}", title]);
            }
            else if (m.Groups["chapter"].Success)
            {
                var number = m.Groups["chapter"].Value;
                var title = CleanTitle(tail);
                entry = new BookTocEntry(
                    $"CH:{number}",
                    2,
                    $"Chapter {number}. {title}",
                    ["CHAPTER", number, title]);
            }
            else if (m.Groups["section"].Success)
            {
                var sectionMarker = m.Groups["section"].Value.ToLowerInvariant();
                var title = CleanTitle(tail);
                entry = new BookTocEntry(
                    $"SEC:{sectionMarker}",
                    3,
                    $"{sectionMarker}. {title}",
                    [$"{sectionMarker}.", title]);
            }

            if (entry is null || !seen.Add(entry.Key)) continue;
            if (entry.Title.Length is < 3 or > 120) continue;
            entries.Add(entry);
        }

        return entries;
    }

    private static int CountEntries(string text) =>
        EntryStartRx.Matches(text).Count(m => !m.Groups["entry"].Value.Equals("Preface", StringComparison.OrdinalIgnoreCase));

    private static string CleanMarker(string marker) => CleanTitle(marker);

    private static string CleanTocTitle(string text)
    {
        var title = NormalizeSpace(text);
        title = Regex.Replace(title, @"(?:\s+(?:\d{1,4}|CONTENTS))+\s*$", "", RegexOptions.IgnoreCase).Trim();
        return title;
    }

    private static List<HeadingRecord> AnchorEntries(
        SlimDocument document,
        IReadOnlyList<BookTocEntry> entries,
        int tocEndIndex)
    {
        var paragraphs = document.Paragraphs
            .Where(p => p.Index > tocEndIndex &&
                        p.Role != ParagraphRole.Empty &&
                        !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Index)
            .ToList();
        var result = new List<HeadingRecord>();
        var cursor = tocEndIndex + 1;

        foreach (var entry in entries)
        {
            var match = FindAnchor(paragraphs, entry, cursor);
            if (match is null) continue;

            result.Add(new HeadingRecord
            {
                Index = match.Value.Paragraph.Index,
                StableId = match.Value.Paragraph.StableId,
                Level = entry.Level,
                Text = match.Value.UseDictionaryTitle ? entry.Title : CleanTitle(match.Value.Text),
                OriginalText = match.Value.Paragraph.Text,
                HeadingSpan = new TextOffsetSpan(match.Value.Start, match.Value.End),
                BoundarySource = "BookTocDictionary",
                StyleId = match.Value.Paragraph.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0.97,
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                ConfidenceBasis = Basis,
            });
            cursor = match.Value.Paragraph.Index;
        }

        return result;
    }

    private static MatchResult? FindAnchor(
        IReadOnlyList<SlimParagraph> paragraphs,
        BookTocEntry entry,
        int minIndex)
    {
        var split = FindSplitMarkerTitleAnchor(paragraphs, entry, minIndex);
        if (split is not null) return split;

        var needle = Tokenize(string.Join(' ', entry.AnchorParts));
        if (needle.Count == 0) return null;

        MatchResult? first = null;
        foreach (var paragraph in paragraphs.Where(p => p.Index >= minIndex))
        {
            var tokens = Tokenize(paragraph.Text);
            if (tokens.Count < needle.Count) continue;

            for (var i = 0; i <= tokens.Count - needle.Count; i++)
            {
                if (!TokenSequenceEquals(tokens, needle, i)) continue;
                if (entry.Key.StartsWith("SEC:", StringComparison.Ordinal) &&
                    !SectionMarkerCaseMatches(paragraph.Text, tokens[i]))
                    continue;
                if (entry.Key.StartsWith("BACK:", StringComparison.Ordinal) &&
                    tokens[i].Start > 5)
                    continue;

                var start = tokens[i].Start;
                var end = tokens[i + needle.Count - 1].End;
                if (LooksLikeTocOccurrence(paragraph.Text, end)) continue;

                var match = new MatchResult(paragraph, paragraph.Text[start..end], start, end, false);
                first ??= match;
                if (LooksLikeParagraphStart(paragraph.Text, start) ||
                    entry.Level > 1 && start < 80)
                    return match;
            }
        }

        return first;
    }

    private static MatchResult? FindSplitMarkerTitleAnchor(
        IReadOnlyList<SlimParagraph> paragraphs,
        BookTocEntry entry,
        int minIndex)
    {
        if (!entry.Key.StartsWith("PART:", StringComparison.Ordinal) &&
            !entry.Key.StartsWith("CH:", StringComparison.Ordinal))
            return null;

        var markerParts = entry.Key.StartsWith("PART:", StringComparison.Ordinal)
            ? entry.AnchorParts.Take(1).ToArray()
            : entry.AnchorParts.Take(2).ToArray();
        var titleParts = entry.Key.StartsWith("PART:", StringComparison.Ordinal)
            ? entry.AnchorParts.Skip(1).ToArray()
            : entry.AnchorParts.Skip(2).ToArray();
        var marker = Tokenize(string.Join(' ', markerParts));
        var title = Tokenize(string.Join(' ', titleParts));
        if (marker.Count == 0 || title.Count == 0) return null;

        for (var i = 0; i < paragraphs.Count; i++)
        {
            var markerParagraph = paragraphs[i];
            if (markerParagraph.Index < minIndex) continue;
            var markerTokens = Tokenize(markerParagraph.Text);
            if (!TokenSequenceEquals(markerTokens, marker, 0) || markerTokens.Count != marker.Count)
                continue;

            // A converter can place a page number or blank cell between the marker and title.
            for (var j = i + 1; j < paragraphs.Count && j <= i + 6; j++)
            {
                var titleParagraph = paragraphs[j];
                var titleTokens = Tokenize(titleParagraph.Text);
                for (var start = 0; start <= titleTokens.Count - title.Count; start++)
                {
                    if (!TokenSequenceEquals(titleTokens, title, start)) continue;
                    var end = titleTokens[start + title.Count - 1].End;
                    if (LooksLikeTocOccurrence(titleParagraph.Text, end)) continue;
                    return new MatchResult(
                        titleParagraph,
                        titleParagraph.Text[titleTokens[start].Start..end],
                        titleTokens[start].Start,
                        end,
                        true);
                }
            }
        }

        return null;
    }

    private static bool TokenSequenceEquals(IReadOnlyList<TokenSpan> haystack, IReadOnlyList<TokenSpan> needle, int start)
    {
        for (var i = 0; i < needle.Count; i++)
        {
            if (!string.Equals(haystack[start + i].Text, needle[i].Text, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool SectionMarkerCaseMatches(string paragraph, TokenSpan firstToken)
    {
        var raw = paragraph[firstToken.Start..firstToken.End];
        return raw.Length >= 2 && char.IsDigit(raw[0]) && char.IsLower(raw[^1]);
    }

    private static bool LooksLikeParagraphStart(string paragraph, int start)
    {
        if (start <= 3) return true;
        var prefix = paragraph[..start].Trim();
        return Regex.IsMatch(prefix, @"^(?:\d{1,4}\s+)?(?:\d{1,2}\.\s+[A-Z ]+|\d{1,2}[A-Z]\.\s+[A-Z ,'-]+)?$");
    }

    private static bool LooksLikeTocOccurrence(string paragraph, int end)
    {
        if (end >= paragraph.Length) return false;
        return Regex.IsMatch(paragraph[end..], @"^\s+\d{1,4}\s+(?:Part|Chapter|\d{1,2}[a-z]\.)\b",
            RegexOptions.IgnoreCase);
    }

    private static List<TokenSpan> Tokenize(string text)
    {
        var list = new List<TokenSpan>();
        foreach (Match m in TokenRx.Matches(text))
            list.Add(new TokenSpan(m.Value, m.Index, m.Index + m.Length));
        return list;
    }

    private static string CleanTitle(string text) => NormalizeSpace(text).Trim(' ', '.');

    private static string NormalizeSpace(string text) => WhitespaceRx.Replace(text, " ").Trim();

    private sealed record BookTocEntry(string Key, int Level, string Title, IReadOnlyList<string> AnchorParts);
    private sealed record TocCluster(int StartIndex, int EndIndex, int ParagraphCount, string Text);
    private sealed record TokenSpan(string Text, int Start, int End);
    private readonly record struct MatchResult(
        SlimParagraph Paragraph,
        string Text,
        int Start,
        int End,
        bool UseDictionaryTitle);
}
