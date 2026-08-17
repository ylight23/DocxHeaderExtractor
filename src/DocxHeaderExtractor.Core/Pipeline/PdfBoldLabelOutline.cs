using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Fallback hẹp cho nhóm PDF→DOCX ngắn (biên bản họp, minutes) mà bản chuyển DOCX làm rớt TOÀN BỘ
/// định dạng ký tự (không "b"/"br" nào còn lại, kể cả trên đoạn thân thật) trong khi PDF gốc vẫn in
/// đậm rõ nhãn mở đầu mỗi mục ("<b>Opening:</b> Cuộc họp...", "<b>Next Steps.</b> Ban thư ký..."),
/// đúng hình dạng mà <see cref="InlineHeadingSplitter.TryFindBoundary"/> đã biết xử lý khi DOCX còn
/// giữ run bold — chỉ là DOCX ở nhóm này không còn tín hiệu đó để splitter dùng.
/// <para>
/// Khác <see cref="PdfTextbookOutline"/> (tín hiệu font-size, tài liệu dài, có TOC/chapter outline
/// cần né), nhóm này tín hiệu là BOLD-RUN-ĐẦU-DÒNG, tài liệu ngắn (1-10 trang), không có mục lục
/// riêng nên không cần luật né TOC/chapter-outline.
/// </para>
/// </summary>
public static class PdfBoldLabelOutline
{
    private const double FullBoldThreshold = 0.90;
    private const int MinLeadingBoldChars = 3;
    private const int MaxHeadingChars = 180;
    private static readonly Regex LetterRunRx = new(@"\p{L}{2,}", RegexOptions.Compiled);

    public static PdfTextbookOutlineResult TryBuild(
        string originalInputPath,
        SlimDocument slim,
        DocumentModeReport mode)
    {
        if (mode.Mode != DocumentMode.FormatDriven)
            return PdfTextbookOutlineResult.NotApplicable($"mode={mode.Mode}");

        if (HasStrongDocxStructure(slim))
            return PdfTextbookOutlineResult.NotApplicable("docx-structure-present");

        var pdf = PdfTextbookOutline.FindSiblingPdf(originalInputPath);
        if (pdf is null)
            return PdfTextbookOutlineResult.NotApplicable("no-pdf");

        IReadOnlyList<PdfLine> lines;
        try
        {
            using var doc = PdfDocument.Open(pdf);
            lines = PdfLineExtraction.ExtractLines(doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return PdfTextbookOutlineResult.NotApplicable("pdf-read-failed");
        }

        if (!IsBoldStrong(lines))
            return PdfTextbookOutlineResult.NotApplicable("pdf-not-bold-strong");

        var candidates = DetectBoldLabelHeadings(lines);
        if (candidates.Count < 2)
            return PdfTextbookOutlineResult.NotApplicable($"too-few-bold-labels:{candidates.Count}");

        var aligned = AlignToDocx(candidates, slim);
        if (aligned.Count < Math.Max(2, (int)Math.Ceiling(candidates.Count * 0.60)))
            return PdfTextbookOutlineResult.NotApplicable($"low-docx-alignment:{aligned.Count}/{candidates.Count}");

        return new PdfTextbookOutlineResult(
            aligned, $"pdf={Path.GetFileName(pdf)}, aligned={aligned.Count}/{candidates.Count}");
    }

    private static bool HasStrongDocxStructure(SlimDocument slim) =>
        slim.Paragraphs.Any(p =>
            p.OutlineLevel is not null ||
            p.HasBuiltInHeadingStyle ||
            p.NumberingStyleLevel is not null);

    /// <summary>
    /// Cùng ý nghĩa với <c>PdfTextbookOutline.IsFontStrong</c>: cần đủ dòng bold RÕ RÀNG PHÂN BIỆT
    /// với phần còn lại — không dùng ngưỡng tuyệt đối trên tỉ lệ toàn văn bản vì tài liệu ngắn
    /// (1-2 trang) có thể có rất ít dòng tổng.
    /// </summary>
    private static bool IsBoldStrong(IReadOnlyList<PdfLine> lines)
    {
        if (lines.Count < 3) return false;
        var boldish = lines.Count(l => l.BoldRatio >= FullBoldThreshold || l.LeadingBoldPrefix.Length >= MinLeadingBoldChars);
        // Cần có bold để phân biệt, nhưng KHÔNG được gần như mọi dòng đều bold — lúc đó bold không
        // còn là tín hiệu chọn lọc (ví dụ trang bìa toàn chữ đậm).
        var ratio = boldish / (double)lines.Count;
        return boldish >= 2 && ratio < 0.90;
    }

    private static List<PdfHeadingCandidate> DetectBoldLabelHeadings(IReadOnlyList<PdfLine> lines)
    {
        var headings = new List<PdfHeadingCandidate>();
        string? accumulating = null;
        var accPage = 0;
        var accY = 0.0;
        double? previousY = null;
        var previousPage = 0;
        var previousFontSize = 0.0;
        var suppressNextLine = false;

        foreach (var line in lines)
        {
            // Khoảng cách dòng lớn bất thường (so với cỡ chữ) là ranh giới khối/đoạn thật trong PDF —
            // heading trần (không dấu ngắt câu) kết thúc TẠI ĐÂY cũng hợp lệ, như khi bold chuyển
            // sang không-bold; chốt bằng LooksLikeLabel giống mọi điểm kết thúc accumulation khác.
            // Khối tiêu đề tài liệu (3 dòng bold liên tiếp) tự bị loại vì vượt MaxHeadingChars khi
            // cộng dồn cả 3 dòng, không cần luật riêng để né nó.
            if (previousY is { } prevY && line.Page == previousPage &&
                prevY - line.Y > Math.Max(previousFontSize, line.FontSize) * 1.8 &&
                accumulating is not null)
            {
                if (LooksLikeLabel(accumulating))
                    headings.Add(new PdfHeadingCandidate(accumulating, accPage, accY));
                accumulating = null;
            }
            previousY = line.Y;
            previousPage = line.Page;
            previousFontSize = line.FontSize;

            var suppressThisLine = suppressNextLine;
            suppressNextLine = false;

            // Callout/trích dẫn quyết định trong biên bản thường in đậm+nghiêng cả khối, cùng hình
            // dạng "câu hoàn chỉnh in đậm" như heading thật nhưng KHÔNG phải heading (ví dụ "The
            // Governing Board adopted the proposed meeting agenda."). Nghiêng là tín hiệu tách được
            // hai loại này mà không cần đọc nghĩa — heading thật trong nhóm tài liệu này không nghiêng.
            var isItalicBlock = line.ItalicRatio >= FullBoldThreshold;

            if (accumulating is null)
            {
                if (suppressThisLine || isItalicBlock) continue;

                if (line.LeadingBoldPrefix.Length >= MinLeadingBoldChars)
                {
                    var text = CapAtSentenceEnd(line.LeadingBoldPrefix, line.Text);
                    if (LooksLikeLabel(text))
                        headings.Add(new PdfHeadingCandidate(text, line.Page, line.Y));
                    // Phần bold còn lại sau điểm cắt (nếu có) là phần tràn của CÙNG câu, không phải
                    // heading mới — dòng kế tiếp có thể chỉ là phần đuôi bold đó lộ ra.
                    if (text.Length < line.LeadingBoldPrefix.Length) suppressNextLine = true;
                    continue;
                }

                if (line.BoldRatio >= FullBoldThreshold)
                {
                    var cut = FindSentenceEnd(line.Text);
                    if (cut >= 0)
                    {
                        var text = line.Text[..(cut + 1)];
                        if (LooksLikeLabel(text))
                            headings.Add(new PdfHeadingCandidate(text, line.Page, line.Y));
                        if (cut + 1 < line.Text.Length) suppressNextLine = true;
                    }
                    else
                    {
                        // Không có dấu ngắt câu trong dòng — nhóm tài liệu này cũng dùng heading TRẦN
                        // kiểu style Heading (không kết bằng ':'/'.' , ví dụ "Global progress with ICP
                        // 2021 cycle"). Tích luỹ tiếp; nếu dòng SAU không còn bold, bold-run coi như
                        // kết thúc TẠI ĐÂY và chính phần đã tích luỹ là heading — không đòi dấu câu.
                        accumulating = line.Text;
                        accPage = line.Page;
                        accY = line.Y;
                    }
                }
            }
            else if (line.BoldRatio >= FullBoldThreshold && !isItalicBlock)
            {
                var cut = FindSentenceEnd(line.Text);
                if (cut >= 0)
                {
                    var combined = $"{accumulating} {line.Text[..(cut + 1)]}";
                    if (LooksLikeLabel(combined))
                        headings.Add(new PdfHeadingCandidate(combined, accPage, accY));
                    accumulating = null;
                    // Cùng lý do trên: nếu dòng vừa cắt còn bold sau điểm cắt, dòng kế tiếp có thể là
                    // phần tràn, không phải mục mới.
                    if (cut + 1 < line.Text.Length) suppressNextLine = true;
                }
                else
                {
                    accumulating = $"{accumulating} {line.Text}";
                }
            }
            else
            {
                // Bold-run kết thúc (dòng này không đủ bold, hoặc chuyển sang nghiêng) mà chưa từng
                // gặp dấu ngắt câu — CHÍNH việc bold dừng lại là ranh giới heading/thân bài của kiểu
                // heading trần. Chốt bằng đúng phần đã tích luỹ, không đoán thêm.
                if (LooksLikeLabel(accumulating))
                    headings.Add(new PdfHeadingCandidate(accumulating, accPage, accY));
                accumulating = null;
            }
        }

        if (accumulating is not null && LooksLikeLabel(accumulating))
            headings.Add(new PdfHeadingCandidate(accumulating, accPage, accY));

        return headings;
    }

    private static string CapAtSentenceEnd(string prefix, string fullLineText)
    {
        var idx = fullLineText.IndexOf(prefix, StringComparison.Ordinal);
        var end = idx >= 0 ? idx + prefix.Length : prefix.Length;
        if (end < fullLineText.Length && IsSentenceEnd(fullLineText, end)) end++;
        return fullLineText[..Math.Min(end, fullLineText.Length)];
    }

    private static bool EndsWithSentenceTerminator(string text) =>
        text.Length > 0 && IsSentenceEnd(text, text.Length - 1);

    private static int FindSentenceEnd(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (IsSentenceEnd(text, i)) return i;
        return -1;
    }

    /// <summary>
    /// Danh sách viết tắt danh xưng/chức danh CỐ Ý ngắn: đây không phải danh sách từ khoá nội dung
    /// (thứ spec cấm §9), mà là danh sách hình thái học tiếng Anh đóng, giống cách trình soạn thảo
    /// văn bản (Word, LanguageTool) tự xử lý "Mr./Mrs./Ms./Dr." khi tách câu — không đọc nghĩa tài
    /// liệu, chỉ nhận diện MỘT hình dạng chữ viết cố định.
    private static readonly HashSet<string> TitleAbbreviations = new(StringComparer.OrdinalIgnoreCase)
        { "Mr", "Mrs", "Ms", "Dr", "Prof", "Sr", "Jr", "St", "Rev", "Hon", "Gov", "Sen", "Rep" };

    /// <summary>
    /// Dấu chấm câu tại vị trí <paramref name="i"/> có phải ranh giới câu THẬT không — phải loại trừ
    /// viết tắt kiểu "F.O.R.T.I.S." / "U.S." (mỗi dấu chấm chỉ đứng sau ĐÚNG MỘT chữ cái) và danh
    /// xưng "Ms./Dr./Prof." (<see cref="TitleAbbreviations"/>). Ranh giới câu thật đứng sau một TỪ
    /// và không bị theo ngay bởi một chữ cái khác (không nằm giữa hai chữ của cùng một token).
    /// </summary>
    private static bool IsSentenceEnd(string text, int i)
    {
        if (text[i] is ':' or ';') return true;
        if (text[i] != '.') return false;
        var singleLetterAbbrev = i >= 1 && char.IsLetter(text[i - 1]) &&
            (i < 2 || !char.IsLetter(text[i - 2]));
        if (singleLetterAbbrev) return false;

        var wordStart = i;
        while (wordStart > 0 && char.IsLetter(text[wordStart - 1])) wordStart--;
        if (TitleAbbreviations.Contains(text[wordStart..i])) return false;

        return i + 1 >= text.Length || !char.IsLetter(text[i + 1]);
    }

    private static bool LooksLikeLabel(string text) =>
        text.Length is >= MinLeadingBoldChars and <= MaxHeadingChars &&
        char.IsLetter(text[0]) &&
        LetterRunRx.Matches(text).Count >= 1 &&
        // Nhãn ngắn KHÔNG dấu ngắt câu và KHÔNG khoảng trắng gần như chắc chắn là mảnh từ bị cắt
        // cụt giữa chừng — kiểm chéo trên báo cáo tài chính 051 (cùng gate FormatDriven nhưng
        // layout dashboard/bảng nhiều cột) lộ đúng dạng này ("Tota", "Cas", "TSoo": tiêu đề cột bảng
        // bị cắt) lẫn mảnh cột chồng lấn bị bucket-theo-Y gộp nhầm ("CoJJnuunnteer3300i,,b220022").
        // "Opening:"/"Present:" là nhãn MỘT TỪ hợp lệ vì có dấu ':' đứng ngay sau — ranh giới ngữ
        // pháp thật, không phải điểm cắt PDF ngẫu nhiên.
        (EndsWithSentenceTerminator(text) || text.Contains(' '));

    private static List<HeadingRecord> AlignToDocx(IReadOnlyList<PdfHeadingCandidate> candidates, SlimDocument slim)
    {
        // Khớp trên chuỗi CANON (chỉ chữ/số, bỏ mọi khoảng trắng) thay vì khớp token-theo-token:
        // PDF và DOCX ở nhóm tài liệu này đều có thể lỡ khoảng trắng giữa hai từ ("of the" ->
        // "ofthe") vì cùng một khâu trích chữ theo khoảng cách letter — canon bỏ qua đúng chỗ lệch
        // đó mà không cần đoán quy tắc chèn khoảng trắng đúng hơn.
        var docx = slim.Paragraphs
            .Where(p => p.Role != ParagraphRole.Empty && !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => new DocxParagraphCanon(p, BuildCanon(p.Text)))
            .ToList();

        var result = new List<HeadingRecord>();
        var seen = new HashSet<(int Index, string Text)>();
        var cursor = 0;

        foreach (var candidate in candidates)
        {
            var (needleCanon, _) = BuildCanon(candidate.Text);
            if (needleCanon.Length == 0) continue;

            var match = FindCanonSubstring(docx, needleCanon, cursor);
            if (match is null) continue;

            // PHẢI dùng nguyên văn (không NormalizeSpace) — OutlineGroundingValidator của harness đòi
            // heading.Text khớp CHÍNH XÁC OriginalText[Start..End]; chuẩn hoá khoảng trắng ở đây làm
            // lệch hai bên khi nguồn có khoảng trắng bất thường, khiến validator cách ly heading đó
            // ở lượt sau — mất âm thầm, log không báo lỗi rõ ràng.
            var text = match.Value.Text;
            if (!seen.Add((match.Value.Paragraph.Index, text))) continue;

            result.Add(new HeadingRecord
            {
                Index = match.Value.Paragraph.Index,
                StableId = match.Value.Paragraph.StableId,
                Level = 1,
                Text = text,
                OriginalText = match.Value.Paragraph.Text,
                HeadingSpan = new TextOffsetSpan(match.Value.Start, match.Value.End),
                BoundarySource = "pdf-bold-label",
                StyleId = match.Value.Paragraph.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0.9,
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                ConfidenceBasis = "pdf_bold_label",
            });
            // Không lùi lại paragraph trước — nhưng VẪN cho phép nhiều heading cùng paragraph này
            // (>= chứ không phải >), đúng thực tế nhóm tài liệu này (một trang PDF thường gộp thành
            // một paragraph DOCX chứa nhiều heading).
            cursor = match.Value.Paragraph.Index;
        }

        return result;
    }

    private static MatchResult? FindCanonSubstring(
        IReadOnlyList<DocxParagraphCanon> paragraphs, string needleCanon, int minIndex)
    {
        foreach (var p in paragraphs.Where(p => p.Paragraph.Index >= minIndex))
        {
            var (canon, originalIndex) = p.Canon;
            var at = canon.IndexOf(needleCanon, StringComparison.Ordinal);
            if (at < 0) continue;
            var start = originalIndex[at];
            var end = originalIndex[at + needleCanon.Length - 1] + 1;
            // Canon bỏ dấu chấm câu vì không phải chữ/số; heading thật thường kết thúc đúng tại đó
            // (nhãn bold luôn có ":"/".", xem CapAtSentenceEnd phía PDF) nên trả lại nếu liền kề.
            if (end < p.Paragraph.Text.Length && p.Paragraph.Text[end] is '.' or ':' or ';') end++;
            return new MatchResult(p.Paragraph, p.Paragraph.Text[start..end], start, end);
        }
        return null;
    }

    /// <summary>Chuỗi chỉ gồm chữ/số viết thường, kèm bản đồ chỉ số ngược về vị trí gốc trong text.</summary>
    private static (string Canon, int[] OriginalIndex) BuildCanon(string text)
    {
        var canon = new System.Text.StringBuilder(text.Length);
        var indices = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c)) continue;
            canon.Append(char.ToLowerInvariant(c));
            indices.Add(i);
        }
        return (canon.ToString(), [.. indices]);
    }

    private sealed record PdfHeadingCandidate(string Text, int Page, double Y);
    private sealed record DocxParagraphCanon(SlimParagraph Paragraph, (string Canon, int[] OriginalIndex) Canon);
    private readonly record struct MatchResult(SlimParagraph Paragraph, string Text, int Start, int End);
}
