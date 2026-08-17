using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Helper for procurement/PDF text-layout documents with an outer PART / Section frame.
/// The production path currently uses only <see cref="LevelForHeading"/> for merged slices:
/// a standalone route needs stronger body-occurrence selection before it is safe to enable.
/// </summary>
public static class PartSectionOutline
{
    private static readonly Regex MarkerRx = new(
        @"(?<![\p{L}\d])(?<label>PART|Section)\s+(?<num>\d{1,3}|[IVXLCDM]{1,7})(?<sep>\s*(?:[\.\-\u2013:]|\u00E2\u20AC\u201C))?\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DotLeaderRx = new(@"\.{5,}\s*\d{1,4}\b", RegexOptions.Compiled);
    private static readonly Regex TocPartSectionEntryRx = new(
        @"(?<title>(?:PART\s+[IVXLCDM]+|Section\s+\d+\.\s+).*?)\.{5,}\s*(?<page>\d{1,4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PageNumberRunRx = new(@"[\s\u00A0]+\d{1,4}[\s\u00A0]+", RegexOptions.Compiled);
    private static readonly Regex PageTailRx = new(
        @"[\s\u00A0]+\d{1,4}[\s\u00A0]+(?![-\u2013:.]).*$",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex TrailingPageRx = new(@"[\s\u00A0]+\d{1,4}[\s\u00A0]*$", RegexOptions.Compiled);
    private static readonly Regex PageCueRx = new(@"[\s\u00A0]+\d{1,4}(?:[\s\u00A0]|$)", RegexOptions.Compiled);
    private static readonly Regex TrailingContentsRx = new(
        @"\s+(?:Contents|Table of Forms|Table of Clauses|\u2022)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExplanationTailRx = new(
        @"\s+This\s+(?:Section|section)\b.*$",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex WordRx = new(@"\p{L}{2,}", RegexOptions.Compiled);

    public static List<HeadingRecord> Build(SlimDocument document)
    {
        var byKey = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var firstBodyPartIndex = FirstBodyPartIndex(document);

        foreach (var p in document.Paragraphs.OrderBy(x => x.Index))
        {
            if (p.Corrupt || p.TableDepth > 0 || p.InTableOfContents || string.IsNullOrWhiteSpace(p.Text))
                continue;
            if (LooksLikeDenseContentsParagraph(p.Text)) continue;

            var slices = ParagraphHeadingSplitter.Split(p.Text)
                .Select(s => s.Text)
                .DefaultIfEmpty(p.Text);

            foreach (var slice in slices)
            {
                var unit = slice.Trim();
                var marker = MarkerRx.Match(unit);
                if (marker is not { Success: true, Index: 0 } || !IsRealMarker(unit, marker))
                    continue;

                var heading = CleanHeading(unit);
                if (DotLeaderRx.IsMatch(heading)) continue;
                if (!LooksLikeHeadingText(heading)) continue;

                var key = NormalizeKey(heading);
                var candidate = new Candidate(
                    new HeadingRecord
                    {
                        Index = p.Index,
                        StableId = p.StableId,
                        Level = LevelOf(marker.Groups["label"].Value),
                        Text = heading,
                        StyleId = p.StyleId,
                        Source = HeadingSource.Structure,
                        Confidence = 1.0,
                        ConfidenceBasis = "part_section_declared",
                        DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                    },
                    ScoreOccurrence(heading, unit, p.Index, firstBodyPartIndex));

                if (!byKey.TryGetValue(key, out var existing) ||
                    candidate.Score > existing.Score ||
                    (candidate.Score == existing.Score && candidate.Heading.Index < existing.Heading.Index))
                {
                    byKey[key] = candidate;
                }
            }
        }

        return [.. byKey.Values.Select(c => c.Heading).OrderBy(h => h.Index).ThenBy(h => h.Level)];
    }

    public static bool HasStrongSignal(SlimDocument document)
    {
        var headings = Build(document);
        return headings.Count(h => h.Level == 1) >= 1 && headings.Count(h => h.Level == 2) >= 5;
    }

    public static bool HasTextTocSignal(SlimDocument document) =>
        TextTocEntries(document).Count(e => e.Level == 1) >= 1 &&
        TextTocEntries(document).Count(e => e.Level == 2) >= 5;

    /// <summary>
    /// PDF→DOCX text-layout thường không còn OOXML TOC field/style, nhưng vẫn giữ "TABLE OF CONTENT"
    /// như text phẳng. Với World Bank Part/Section frame, TOC text là nguồn title đầy đủ còn body là
    /// nguồn anchor theo paragraph/page. Đây là route điều hướng cho file mất XML signal, không phải
    /// phục hồi span title/body hoàn chỉnh.
    /// </summary>
    public static List<HeadingRecord> BuildFromTextToc(SlimDocument document)
    {
        var entries = TextTocEntries(document);
        if (entries.Count(e => e.Level == 1) < 1 || entries.Count(e => e.Level == 2) < 5)
            return [];

        var headings = new List<HeadingRecord>();
        var cursor = entries[0].TocParagraphIndex + 1;
        foreach (var entry in entries)
        {
            var paragraph = FindBodyOccurrence(document, entry, cursor);
            if (paragraph is null) continue;

            headings.Add(new HeadingRecord
            {
                Index = paragraph.Index,
                StableId = paragraph.StableId,
                Level = entry.Level,
                Text = entry.Title,
                StyleId = paragraph.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0.95,
                ConfidenceBasis = "part_section_toc_text",
                BoundarySource = $"text_toc_page_{entry.Page}",
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
            });

            cursor = paragraph.Index;
        }

        return headings.OrderBy(h => h.Index).ThenBy(h => h.Level).ToList();
    }

    public static int? LevelForHeading(string text)
    {
        var trimmed = text.Trim();
        var marker = MarkerRx.Match(trimmed);
        return marker is { Success: true, Index: 0 } && IsRealMarker(trimmed, marker)
            ? LevelOf(marker.Groups["label"].Value)
            : null;
    }

    private static bool LooksLikeDenseContentsParagraph(string text) =>
        text.Contains("Table of Contents", StringComparison.OrdinalIgnoreCase) &&
        DotLeaderRx.Matches(text).Count >= 2;

    private static List<TocEntry> TextTocEntries(SlimDocument document)
    {
        var entries = new List<TocEntry>();
        foreach (var p in document.Paragraphs.OrderBy(x => x.Index))
        {
            if (string.IsNullOrWhiteSpace(p.Text) ||
                !p.Text.Contains("TABLE OF CONTENT", StringComparison.OrdinalIgnoreCase) ||
                DotLeaderRx.Matches(p.Text).Count < 5)
                continue;

            var matches = TocPartSectionEntryRx.Matches(p.Text);
            // Section riêng cũng có sub-TOC nội bộ (A./1./2...) mang cùng cụm "TABLE OF CONTENT" +
            // nhiều dot-leader; running header dạng "Section 2. ... 15 Section 2. ... TABLE OF
            // CONTENT A. ..." đứng trước dot-leader ĐẦU TIÊN của NÓ có thể khớp giả một entry
            // PART/Section. TOC khung toàn tài liệu luôn cho ra NHIỀU entry trong CÙNG một
            // paragraph; một entry lẻ là dấu hiệu nhiễu từ sub-TOC, không phải khung PART/Section.
            if (matches.Count < 2) continue;

            foreach (Match match in matches)
            {
                var title = CleanHeading(match.Groups["title"].Value);
                if (!LooksLikeTocEntryTitle(title)) continue;
                if (!int.TryParse(match.Groups["page"].Value, out var page)) continue;
                entries.Add(new TocEntry(title, LevelForHeading(title) ?? 1, page, p.Index));
            }
        }

        return entries
            .GroupBy(e => NormalizeKey(e.Title))
            .Select(g => g.First())
            .OrderBy(e => e.TocParagraphIndex)
            .ThenBy(e => e.Page)
            .ThenBy(e => e.Level)
            .ToList();
    }

    /// <summary>
    /// <see cref="LooksLikeHeadingText"/> đòi độ dài ≥8 và ≥2 "từ" — đúng cho ứng viên đọc từ THÂN
    /// BÀI (cần tự phân biệt heading với câu văn xuôi ngắn), nhưng loại nhầm entry TOC hợp lệ mà
    /// tiêu đề gốc chỉ là nhãn trần (<c>PART I</c>, <c>PART II</c> — không có phụ đề trong tài
    /// liệu). Ở đây <see cref="TocPartSectionEntryRx"/> đã tự xác nhận hình dạng PART/Section, nên
    /// chỉ cần chặn chuỗi rỗng/quá dài do <see cref="CleanHeading"/> cắt hỏng.
    /// </summary>
    private static bool LooksLikeTocEntryTitle(string text) => text.Length is >= 4 and <= 140;

    private static SlimParagraph? FindBodyOccurrence(SlimDocument document, TocEntry entry, int minIndex)
    {
        var marker = MarkerRx.Match(entry.Title);
        if (!marker.Success) return null;
        var markerText = MarkerText(marker);
        var titleKey = NormalizeKey(entry.Title);

        SlimParagraph? fallback = null;
        foreach (var p in document.Paragraphs.OrderBy(x => x.Index))
        {
            if (p.Index < minIndex || string.IsNullOrWhiteSpace(p.Text) || LooksLikeDenseContentsParagraph(p.Text))
                continue;
            var text = NormalizeDashes(p.Text);
            if (!ContainsPageCue(text, entry.Page)) continue;

            if (entry.Level == 2 && NormalizeKey(text).Contains(titleKey, StringComparison.Ordinal))
                return p;

            if (entry.Level == 1 &&
                text.Contains(markerText, StringComparison.OrdinalIgnoreCase))
                return p;

            fallback ??= null;
        }

        return fallback;
    }

    private static bool ContainsPageCue(string text, int page) =>
        Regex.IsMatch(text, $@"(?:^|\s){page}(?:\s|$)");

    private static string MarkerText(Match marker) =>
        Regex.Replace($"{marker.Groups["label"].Value} {marker.Groups["num"].Value}", @"\s+", " ").Trim();

    private static bool IsRealMarker(string text, Match marker)
    {
        if (!marker.Groups["sep"].Success) return false;
        var after = marker.Index + marker.Length;
        while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
        if (after >= text.Length || !char.IsLetter(text[after])) return false;

        var sep = marker.Groups["sep"].Value.Trim();
        var hadWhitespaceAfterSeparator = marker.Index + marker.Length > marker.Groups["sep"].Index + marker.Groups["sep"].Length;
        return sep != "." || hadWhitespaceAfterSeparator || char.IsUpper(text[after]);
    }

    private static string CleanHeading(string text)
    {
        var heading = text.Replace("\u00E2\u20AC\u201C", "-")
            .Replace('\u2010', '-')
            .Replace('\u2011', '-')
            .Replace('\u2012', '-')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Trim();
        foreach (Match match in PageNumberRunRx.Matches(heading))
        {
            var next = match.Index + match.Length;
            if (next < heading.Length && heading[next] is not ('-' or '\u2013' or ':' or '.'))
            {
                heading = heading[..match.Index].TrimEnd();
                break;
            }
        }
        heading = TrailingPageRx.Replace(heading, "").Trim();
        heading = PageTailRx.Replace(heading, "").Trim();
        heading = ExplanationTailRx.Replace(heading, "").Trim();
        heading = TrailingContentsRx.Replace(heading, "").Trim();
        if (heading.EndsWith(" PART", StringComparison.OrdinalIgnoreCase))
            heading = heading[..^5].TrimEnd();
        return heading;
    }

    private static bool LooksLikeHeadingText(string text)
    {
        if (text.Length is < 8 or > 140) return false;
        if (WordRx.Matches(text).Count < 2) return false;
        return MarkerRx.Match(text) is { Success: true, Index: 0 };
    }

    private static int LevelOf(string label) =>
        label.Equals("part", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    private static int ScoreOccurrence(string heading, string unit, int index, int? firstBodyPartIndex)
    {
        var letters = heading.Where(char.IsLetter).ToList();
        var lower = letters.Count(char.IsLower);
        var upper = letters.Count(char.IsUpper);
        var score = 0;
        if (lower > 0) score += 3;
        if (upper > 0 && lower == 0) score -= 2;
        if (heading.Length <= 90) score += 1;
        if (HasEmbeddedPageCue(unit)) score += 2;
        if (unit.Contains("Contents", StringComparison.OrdinalIgnoreCase) ||
            unit.Contains("Table of ", StringComparison.OrdinalIgnoreCase))
            score += 3;
        if (firstBodyPartIndex is { } first && index >= first) score += 4;
        return score;
    }

    private static bool HasEmbeddedPageCue(string text)
    {
        foreach (Match match in PageCueRx.Matches(text))
        {
            var next = match.Index + match.Length;
            if (next >= text.Length || text[next] is not ('-' or '\u2013' or ':' or '.')) return true;
        }
        return false;
    }

    private static int? FirstBodyPartIndex(SlimDocument document)
    {
        foreach (var p in document.Paragraphs.OrderBy(x => x.Index))
        {
            if (p.Corrupt || p.TableDepth > 0 || p.InTableOfContents || string.IsNullOrWhiteSpace(p.Text))
                continue;
            if (LooksLikeDenseContentsParagraph(p.Text)) continue;

            foreach (Match match in MarkerRx.Matches(p.Text))
            {
                if (!match.Groups["sep"].Success) continue;
                if (!match.Groups["label"].Value.Equals("part", StringComparison.OrdinalIgnoreCase)) continue;
                var unit = p.Text[match.Index..].Trim();
                var heading = CleanHeading(unit);
                if (!LooksLikeHeadingText(heading)) continue;
                if (heading.Any(char.IsLower)) return p.Index;
            }
        }
        return null;
    }

    private static string NormalizeKey(string text)
    {
        var normalized = NormalizeDashes(text)
            .ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\([^)]*\)", " ");
        normalized = Regex.Replace(normalized, @"[^a-z0-9]+", " ");
        return string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeDashes(string text) =>
        text.Replace("\u00E2\u20AC\u201C", "-")
            .Replace('\u2010', '-')
            .Replace('\u2011', '-')
            .Replace('\u2012', '-')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-');

    private sealed record Candidate(HeadingRecord Heading, int Score);
    private sealed record TocEntry(string Title, int Level, int Page, int TocParagraphIndex);
}
