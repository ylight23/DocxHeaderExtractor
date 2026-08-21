using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Fallback hẹp cho biên bản họp dùng mã phiên kiểu "D1.00 - Title", "D2.03 - Title" (World Bank
/// ICP IACG minutes) — khác hẳn <see cref="PdfBoldLabelOutline"/>: KHÔNG cần PDF/bold, vì mã phiên
/// còn nguyên là TEXT trong DOCX dù mọi định dạng ký tự đã mất (đúng đặc điểm <c>FormatDriven</c>).
/// Ranh giới title/body không có dấu câu (khác nhóm bold-run-in): dùng hình dạng "PresenterName,
/// Organization, [động từ thường]" — cụm mở đầu chuẩn của thể loại biên bản này ("Marko Rissanen,
/// World Bank, presented...") — làm điểm cắt, không phải dấu ':'/'.'.
/// </summary>
public static class SessionCodeOutline
{
    private static readonly Regex MarkerRx = new(
        @"(?<![\p{L}\d])(?<code>D\d{1,2}\.\d{2})\s*[-–]\s*",
        RegexOptions.Compiled);

    // "Marko Rissanen, World Bank, presented" — ĐÚNG 2 từ Hoa (Tên Họ, cho phép hạt nối kiểu "de"/
    // "van"/"von" xen giữa cho họ nhiều phần), có thể kèm đồng trình bày "and Tên Họ", phẩy, cụm tổ
    // chức bắt đầu bằng chữ Hoa, phẩy, rồi một chữ thường (động từ). Giới hạn ĐÚNG 2 từ (không phải
    // 1-4) để không khớp nhầm cụm nhiều-từ-Hoa NẰM TRONG chính title (ví dụ "PPP Mapping Giovanni
    // Tonutti," từng bị đọc thành "tên" vì cho phép tới 4 từ trước dấu phẩy).
    private static readonly Regex AttributionRx = new(
        @"[A-Z][\p{L}'-]+\s+(?:(?:de|van|von|der|bin|al)\s+)?[A-Z][\p{L}'-]+" +
        @"(?:\s+and\s+[A-Z][\p{L}'-]+\s+[A-Z][\p{L}'-]+)?,\s+[A-Z][\p{L}0-9&(),./ -]{1,60}?,\s+(?=[a-z])",
        RegexOptions.Compiled);

    // Chỉ tìm ranh giới trong một cửa sổ hẹp sau marker — khớp muộn (xa marker) gần như luôn là một
    // cụm phẩy tình cờ ở xa, không phải điểm bắt đầu thân bài thật.
    private const int BoundarySearchWindow = 220;

    private const int MinMarkerCount = 3;
    private const int MaxHeadingChars = 160;

    public static List<HeadingRecord> Build(SlimDocument document, DocumentModeReport mode)
    {
        if (DocumentStructureEvidence.HasNativeSemanticStructure(document)) return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<HeadingRecord>();

        foreach (var p in document.Paragraphs.OrderBy(x => x.Index))
        {
            if (p.Corrupt || p.TableDepth > 0 || string.IsNullOrWhiteSpace(p.Text)) continue;

            foreach (Match marker in MarkerRx.Matches(p.Text))
            {
                var code = marker.Groups["code"].Value;
                if (!seen.Add(code)) continue; // giữ occurrence ĐẦU TIÊN (thân bài) — agenda lặp lại ở cuối tài liệu, không lấy

                var titleStart = marker.Index;
                var afterMarker = marker.Index + marker.Length;
                var cut = FindBoundary(p.Text, afterMarker);
                var end = Math.Min(cut, titleStart + MaxHeadingChars);
                if (end <= afterMarker) continue;

                var heading = p.Text[titleStart..end].TrimEnd();
                if (heading.Length < marker.Length + 2) continue; // chỉ có mã, không có title thật

                result.Add(new HeadingRecord
                {
                    Index = p.Index,
                    StableId = p.StableId,
                    Level = 1,
                    Text = heading,
                    OriginalText = p.Text,
                    HeadingSpan = new TextOffsetSpan(titleStart, end),
                    BoundarySource = "session-code-attribution",
                    StyleId = p.StyleId,
                    Source = HeadingSource.Structure,
                    Confidence = 0.9,
                    DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                    ConfidenceBasis = "session_code_marker",
                });
            }
        }

        if (result.Count < MinMarkerCount) return [];
        return result;
    }

    private static int FindBoundary(string text, int from)
    {
        var windowEnd = Math.Min(text.Length, from + BoundarySearchWindow);
        var m = AttributionRx.Match(text, from, windowEnd - from);
        return m.Success ? m.Index : Math.Min(text.Length, from + MaxHeadingChars);
    }

    private static bool HasStrongDocxStructure(SlimDocument document) =>
        document.Paragraphs.Any(p =>
            p.OutlineLevel is not null ||
            p.HasBuiltInHeadingStyle ||
            p.NumberingStyleLevel is not null);
}
