using DocxHeaderExtractor.Core.Models;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Builder tất định cho tài liệu dùng số gõ tay kiểu <c>1.</c>, <c>1.1.</c>,
/// <c>1.1.1.</c>. Khác văn bản hành chính, cấp của nhóm này nằm ngay trong marker:
/// đếm độ sâu số, không suy theo thứ tự chữ ký xuất hiện.
/// </summary>
public static class TypedNumberingOutline
{
    private static readonly Regex RfcPageFooterRx = new(
        @"\b[A-Z][A-Za-z]+(?:,\s+et al\.)?\s+Standards Track Page\s+\d+\b",
        RegexOptions.Compiled);

    private static readonly Regex TextLayoutPageHeaderRx = new(
        @"^\s*\d{1,4}\s+\d{1,3}(?:\.\d{1,3}){0,3}\s+•\s+",
        RegexOptions.Compiled);

    private static readonly Regex TocEntryLikeSegmentRx = new(
        @"\p{L}.*\s+\d{1,4}$",
        RegexOptions.Compiled);

    private static readonly Regex InlineTocEntryRx = new(
        @"(?:^|\s)\d{1,3}(?:\.\d{1,3}){0,3}\s+\p{Lu}[\p{L}\p{N}\s,&.'’/\(\)\-–:]{0,100}?\s+\d{1,4}(?=\s|$)",
        RegexOptions.Compiled);

    private static readonly Regex NumericUnitRemainderRx = new(
        @"^\s*\d{1,2}(?:\.\d{1,2}){0,4}\s+(?:GHz|MHz|kHz|Hz|GB|MB|KB|TB|bps|kbps|Mbps|Gbps|ms|sec|secs|min|mins|hr|hrs|km|cm|mm|kg|mg|lb|lbs|oz|USD|EUR|VND)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuantitativeAmountRemainderRx = new(
        @"^\s*\d{1,3}(?:\.\d{1,3}){1,4}\s+(?:\$|USD|US\$|EUR|VND|million|billion|trillion|thousand|percent|percentage|per\s+cent|basis\s+points?|bps)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NumericTokenRx = new(
        @"(?<![\p{L}\d])[A-Z]?\d[\d.,]*(?![\p{L}\d])",
        RegexOptions.Compiled);

    private static readonly Regex ArabicPathRx = new(
        @"^\s*(\d{1,2}(?:\.\d{1,2}){0,4})(?!\d)",
        RegexOptions.Compiled);

    private static readonly Regex TextLayoutSectionPageRx = new(
        @"^\s*(?<marker>\d{1,3}(?:\.\d{1,3}){1,4})\s*\u2022\s*(?<title>[^\d\u2022]{2,120}?)\s+\d{1,4}\s+(?<body>.{12,})$",
        RegexOptions.Compiled);

    public static List<HeadingRecord> Build(SlimDocument document, bool splitMergedParagraphs = true)
    {
        List<HeadingRecord> result = [];
        var seen = new HashSet<(int Index, string Text)>();
        var usePartSectionLevels = PartSectionOutline.HasStrongSignal(document);

        // Ngưỡng "nhan đề đã dính thân bài" do CHÍNH tài liệu khai ra — trung vị độ dài các đơn vị
        // sau khi cắt, nhân một tỉ lệ. Không có hằng số ký tự nào ở đây.
        var nguong = AdministrativeOutline.NguongNhanDe(
            document.Paragraphs
                .Where(x => !x.Corrupt && x.TableDepth == 0 && !x.InTableOfContents)
                .SelectMany(x => splitMergedParagraphs
                    ? ParagraphHeadingSplitter.Segments(StripPageArtifacts(x.Text ?? string.Empty))
                    : [StripPageArtifacts(x.Text ?? string.Empty)]));

        foreach (var p in document.Paragraphs.OrderBy(x => x.Index))
        {
            if (p.Corrupt || p.TableDepth > 0 || p.InTableOfContents || string.IsNullOrWhiteSpace(p.Text)) continue;
            var text = StripPageArtifacts(p.Text);
            var segments = splitMergedParagraphs
                ? ParagraphHeadingSplitter.Segments(text)
                : [text];
            if (LooksLikeDenseTypedTableOfContents(text, segments)) continue;

            foreach (var seg in segments)
            {
                if (LooksLikeTextLayoutPageHeader(seg)) continue;
                if (NumberingAudit.Parse(seg) is not { } token) continue;
                if (LooksLikeCaptionLabel(token)) continue;
                if (HasZeroArabicPathComponent(token, seg)) continue;
                if (LooksLikeNumericMeasurement(token, seg)) continue;
                if (LooksLikeQuantitativeAmount(token, seg)) continue;
                if (LooksLikeQuantitativeTableRow(token, seg)) continue;

                var split = SplitTypedHeadingBody(token, seg, nguong);
                if (!seen.Add((p.Index, split.Heading))) continue;
                result.Add(new HeadingRecord
                {
                    Index = p.Index,
                    StableId = p.StableId,
                    Level = usePartSectionLevels
                        ? PartSectionOutline.LevelForHeading(split.Heading) ?? Math.Clamp(token.Depth, 1, 9)
                        : Math.Clamp(token.Depth, 1, 9),
                    Text = split.Heading,
                    StyleId = p.StyleId,
                    Source = HeadingSource.Structure,
                    Confidence = 1.0,
                    ConfidenceBasis = "typed_number_depth",
                    DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                    InlineBody = split.Body,
                    OriginalText = split.Body is null ? null : seg,
                    HeadingSpan = split.Body is null ? null : split.HeadingSpan,
                    InlineBodySpan = split.Body is null ? null : split.BodySpan,
                });
            }
        }

        return result;
    }

    internal readonly record struct TypedHeadingBodySplit(
        string Heading,
        string? Body,
        TextOffsetSpan? HeadingSpan,
        TextOffsetSpan? BodySpan);

    internal static TypedHeadingBodySplit SplitTypedHeadingBody(NumberToken token, string text, int nguongNhanDe = 0)
    {
        if (token is { Kind: NumberKind.Arabic, Depth: >= 2 } &&
            TextLayoutSectionPageRx.Match(text) is { Success: true } match)
        {
            var marker = match.Groups["marker"].Value.TrimEnd('.');
            var title = match.Groups["title"].Value.Trim();
            var body = match.Groups["body"].Value;
            return new TypedHeadingBodySplit(
                $"{marker} {title}",
                body,
                new TextOffsetSpan(0, match.Groups["body"].Index),
                new TextOffsetSpan(match.Groups["body"].Index, text.Length));
        }

        var (heading, splitBody) = AdministrativeOutline.SplitHeadingBody(text, nguongNhanDe);
        var bodyStart = splitBody is null ? -1 : text.Length - splitBody.Length;
        return new TypedHeadingBodySplit(
            heading,
            splitBody,
            splitBody is null ? null : new TextOffsetSpan(0, heading.Length),
            splitBody is null ? null : new TextOffsetSpan(bodyStart, text.Length));
    }

    internal static string StripPageArtifacts(string text) =>
        RfcPageFooterRx.Replace(text, "").Trim();

    internal static bool LooksLikeTextLayoutPageHeader(string text) =>
        TextLayoutPageHeaderRx.IsMatch(text);

    internal static bool LooksLikeCaptionLabel(NumberToken token) =>
        token.Kind == NumberKind.Labelled &&
        token.Label is "table" or "figure" or "box" or "note";

    internal static bool LooksLikeNumericMeasurement(NumberToken token, string text) =>
        token.Kind == NumberKind.Arabic &&
        token.Depth >= 2 &&
        NumericUnitRemainderRx.IsMatch(text);

    internal static bool LooksLikeQuantitativeAmount(NumberToken token, string text) =>
        token.Kind == NumberKind.Arabic &&
        token.Depth >= 2 &&
        QuantitativeAmountRemainderRx.IsMatch(text);

    internal static bool LooksLikeQuantitativeTableRow(NumberToken token, string text)
    {
        if (token.Kind != NumberKind.Arabic || token.Depth < 2) return false;
        var first = text[..Math.Min(text.Length, 180)];
        var numericTokens = NumericTokenRx.Matches(first).Count;
        if (numericTokens >= 4) return true;

        var alnum = first.Count(char.IsLetterOrDigit);
        if (alnum == 0) return false;
        var digits = first.Count(char.IsDigit);
        return numericTokens >= 3 && digits / (double)alnum >= 0.18;
    }

    internal static bool HasZeroArabicPathComponent(NumberToken token, string text)
    {
        if (token.Kind != NumberKind.Arabic) return false;
        if (ArabicPathRx.Match(text) is not { Success: true } match) return false;
        return match.Groups[1].Value
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => int.TryParse(part, out var value) && value == 0);
    }

    internal static bool LooksLikeDenseTypedTableOfContents(string text, IReadOnlyList<string> segments)
    {
        if (InlineTocEntryRx.Matches(text).Count >= 6) return true;
        if (segments.Count < 6) return false;

        var tocLike = segments.Count(s =>
        {
            var text = StripPageArtifacts(s);
            return text.Length <= 140 && TocEntryLikeSegmentRx.IsMatch(text);
        });
        return tocLike >= 4 && tocLike >= segments.Count * 0.6;
    }

    internal static bool LooksLikeQuantitativeTypedLayout(SlimDocument document)
    {
        var typed = 0;
        var quantitative = 0;
        foreach (var p in document.Paragraphs)
        {
            if (p.Corrupt || p.TableDepth > 0 || p.InTableOfContents || string.IsNullOrWhiteSpace(p.Text)) continue;
            foreach (var seg in ParagraphHeadingSplitter.Segments(StripPageArtifacts(p.Text)))
            {
                if (NumberingAudit.Parse(seg) is not { Kind: NumberKind.Arabic, Depth: >= 2 } token) continue;
                typed++;
                if (LooksLikeNumericMeasurement(token, seg) ||
                    LooksLikeQuantitativeAmount(token, seg) ||
                    LooksLikeQuantitativeTableRow(token, seg) ||
                    HasZeroArabicPathComponent(token, seg))
                    quantitative++;
            }
        }

        return typed >= 30 && quantitative >= typed * 0.45;
    }
}
