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

    public static List<HeadingRecord> Build(SlimDocument document, bool splitMergedParagraphs = true)
    {
        List<HeadingRecord> result = [];

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

                var (heading, body) = AdministrativeOutline.SplitHeadingBody(seg);
                result.Add(new HeadingRecord
                {
                    Index = p.Index,
                    StableId = p.StableId,
                    Level = Math.Clamp(token.Depth, 1, 9),
                    Text = heading,
                    StyleId = p.StyleId,
                    Source = HeadingSource.Structure,
                    Confidence = 1.0,
                    ConfidenceBasis = "typed_number_depth",
                    DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                    InlineBody = body,
                    OriginalText = body is null ? null : seg,
                });
            }
        }

        return result;
    }

    internal static string StripPageArtifacts(string text) =>
        RfcPageFooterRx.Replace(text, "").Trim();

    internal static bool LooksLikeTextLayoutPageHeader(string text) =>
        TextLayoutPageHeaderRx.IsMatch(text);

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
}
