using System.Text.RegularExpressions;
using System.Text;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Builder tất định cho văn bản pháp quy: <c>Phần/Chương/Mục/Điều</c> và
/// <c>Part/Chapter/Section/Article</c>.
/// <para>
/// Không dùng chung <see cref="AdministrativeOutline"/> vì pháp quy có hệ nhãn riêng. Một văn bản
/// chỉ có <c>Điều</c>/<c>Article</c> vẫn là legal-structured; điều kiện "ít nhất hai signature"
/// của hành chính sẽ làm route trả rỗng sai.
/// </para>
/// </summary>
public static class LegalStructuredOutline
{
    private static readonly Regex LegalMarkerRx = new(
        @"(?<![\p{Lu}\d])(?<label>Phần|Chương|Mục|Điều|Part|Chapter|Section|Article)\s+" +
        @"(?<num>\d{1,4}|[IVXLCDM]{1,7})(?<sep>\s*[\.\):\-–])?\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BarePayloadRx = new(
        @"(?<![\p{Lu}\d])\d{1,3}(?:\.\d{1,3}){0,3}\s*[\.\):]\s*",
        RegexOptions.Compiled);

    private static readonly Regex TitleWordRx = new(@"\p{L}{2,}", RegexOptions.Compiled);

    public static List<HeadingRecord> Build(SlimDocument document, bool splitMergedParagraphs = true)
    {
        List<HeadingRecord> result = [];

        foreach (var p in document.Paragraphs.OrderBy(x => x.Index))
        {
            if (p.Corrupt || p.TableDepth > 0 || string.IsNullOrWhiteSpace(p.Text)) continue;
            var text = p.Text.Normalize(NormalizationForm.FormC);

            var units = splitMergedParagraphs
                ? Units(text)
                : SingleUnit(text);

            foreach (var unit in units)
            {
                var split = SplitHeadingBody(unit.Text, unit.MarkerLength);
                result.Add(new HeadingRecord
                {
                    Index = p.Index,
                    StableId = p.StableId,
                    Level = LevelOf(unit.Label),
                    Text = split.Heading,
                    StyleId = p.StyleId,
                    Source = HeadingSource.Structure,
                    Confidence = 1.0,
                    ConfidenceBasis = "legal_marker_declared",
                    DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                    OriginalText = split.Body is null ? null : unit.Text,
                    HeadingSpan = split.Body is null ? null : split.HeadingSpan,
                    InlineBody = split.Body,
                    InlineBodySpan = split.Body is null ? null : split.BodySpan,
                    BoundarySource = split.Body is null ? null : "LegalPayloadMarker",
                });
            }
        }

        return result;
    }

    private readonly record struct LegalUnit(string Text, string Label, int MarkerLength);

    private static IReadOnlyList<LegalUnit> SingleUnit(string text)
    {
        var matches = LegalMarkerRx.Matches(text)
            .Where(m => IsRealHeadingMarker(text, m))
            .ToList();
        if (matches.Count != 1) return [];

        var m = matches[0];
        return m.Success && m.Index == 0 && IsRealHeadingMarker(text, m)
            ? [new LegalUnit(text.Trim(), m.Groups["label"].Value, m.Length)]
            : [];
    }

    private static IReadOnlyList<LegalUnit> Units(string text)
    {
        var matches = LegalMarkerRx.Matches(text)
            .Where(m => IsRealHeadingMarker(text, m))
            .ToList();
        if (matches.Count == 0) return [];

        List<LegalUnit> units = [];
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var unitText = text[start..end].Trim();
            var remainder = unitText[Math.Min(matches[i].Length, unitText.Length)..];
            if (!TitleWordRx.IsMatch(remainder)) continue;
            units.Add(new LegalUnit(unitText, matches[i].Groups["label"].Value, matches[i].Length));
        }

        return units;
    }

    private static bool IsRealHeadingMarker(string text, Match marker)
    {
        // Dạng có dấu ngắt là marker mạnh: "Điều 5.", "Article 7:".
        if (marker.Groups["sep"].Success) return true;

        // Dạng không dấu ngắt phục hồi bản chuyển PDF: "Chương II QUY ĐỊNH CHUNG".
        // Chặn tham chiếu chéo giữa câu: "Điều 3 của Bộ luật này".
        var pos = marker.Index + marker.Length;
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        return pos < text.Length && char.IsUpper(text[pos]);
    }

    private static int LevelOf(string label)
    {
        var key = label.Trim().ToLowerInvariant();
        return key switch
        {
            "phần" or "part" => 1,
            "chương" or "chapter" => 2,
            "mục" or "section" => 3,
            "điều" or "article" => 4,
            _ => 4,
        };
    }

    private readonly record struct SplitResult(
        string Heading,
        string? Body,
        TextOffsetSpan? HeadingSpan,
        TextOffsetSpan? BodySpan);

    private static SplitResult SplitHeadingBody(string text, int markerLength)
    {
        var restStart = Math.Min(markerLength, text.Length);
        var rest = text[restStart..];
        var bare = BarePayloadRx.Match(rest);
        if (!bare.Success) return new SplitResult(text.Trim(), null, null, null);

        var split = restStart + bare.Index;
        var heading = text[..split].TrimEnd();
        var bodyStart = split;
        while (bodyStart < text.Length && char.IsWhiteSpace(text[bodyStart])) bodyStart++;
        var body = text[bodyStart..];
        return string.IsNullOrWhiteSpace(body)
            ? new SplitResult(heading, null, null, null)
            : new SplitResult(
                heading,
                body,
                new TextOffsetSpan(0, heading.Length),
                new TextOffsetSpan(bodyStart, text.Length));
    }
}
