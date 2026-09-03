using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Vision;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.DocumentProcessing.Repair;

public enum CorruptParagraphVisualVerdict
{
    /// <summary>Không tìm được PDF nguồn hoặc không định vị được trang — không kết luận được.</summary>
    Inconclusive,

    /// <summary>Ảnh trang PDF nguồn cho thấy đúng hiện tượng ký tự lặp/nhân đôi — lỗi ở NGUỒN, không
    /// phải parser. Giữ nguyên quyết định loại đoạn này khỏi ứng viên.</summary>
    ConfirmedSourceCorruption,

    /// <summary>Ảnh trang PDF nguồn cho thấy văn bản BÌNH THƯỜNG, không có hiện tượng lặp ký tự —
    /// nghi ngờ <c>is_doubled</c> báo động giả hoặc lỗi ở tầng đọc/parser, cần xem lại code.</summary>
    SuspectedParserBug,
}

public sealed record CorruptParagraphVisualCheck(
    int ParagraphIndex,
    string ExtractedText,
    CorruptParagraphVisualVerdict Verdict,
    string Reason,
    string? RenderedPdfPath,
    int? RenderedPage);

/// <summary>
/// Cổng chẩn đoán CÓ ĐIỀU KIỆN KÍCH HOẠT — chỉ chạy khi <see cref="OpenXmlLayer.CorruptParagraphDetector"/>
/// đã báo động bằng policy state, không chạy trên đường trích xuất chính (chậm, tốn VLM).
/// Case gốc đã xác nhận cơ chế này đúng bằng tay: <c>HHììnnhh 11.1</c> — render ra ảnh khớp với text đã
/// trích, xác nhận lỗi thật ở nguồn Word (spec §3.6, xem doc comment CorruptParagraphDetector). Lớp này
/// tự động hoá đúng bước render-rồi-nhìn đó cho các ca khác.
/// <para>
/// Giới hạn CỐ Ý: chỉ xử lý tài liệu có PDF anh em (dùng <see cref="PdfTextbookOutline.FindSiblingPdf"/>
/// sẵn có). Tài liệu DOCX thuần không có PDF nguồn thì KHÔNG render được — cần soffice/LibreOffice để
/// chuyển DOCX→PDF trước, đó là một phụ thuộc runtime mới (dự án đang tránh phụ thuộc ngoài .NET, xem
/// §153 "Docling sidecar → .NET deterministic, không gọi Python trong production") — CHƯA thêm, cần bàn
/// riêng nếu đo thấy nhóm DOCX-thuần chiếm tỷ trọng đáng kể trong các ca <c>is_doubled</c> thật.
/// </para>
/// <para>
/// Định vị trang: vì chính văn bản bị hỏng nên KHÔNG khớp canonical trực tiếp được — tìm đoạn LÀNH gần
/// nhất (trước hoặc sau) rồi khớp văn bản đoạn đó vào trang PDF, giả định đoạn hỏng nằm cùng trang hoặc
/// trang liền kề. Không định vị được vùng chính xác trong trang (không có toạ độ tin cậy cho văn bản đã
/// vỡ) nên render CẢ TRANG, không crop hẹp như các vai trò khác — đánh đổi chi phí token lấy độ tin cậy
/// định vị, chấp nhận được vì đây là cổng CÓ ĐIỀU KIỆN, không chạy hàng loạt.
/// </para>
/// </summary>
public static class CorruptParagraphVisualVerifier
{
    private const int NeighborSearchWindow = 6;

    public static Task<CorruptParagraphVisualCheck> VerifyAsync(
        string originalInputPath,
        DocxPolicyState policyState,
        DocxPolicyParagraph corruptParagraph,
        IPdfVisualQuestion vlm,
        int dpi = 110,
        CancellationToken ct = default) =>
        VerifyCore(originalInputPath, policyState.Paragraphs.Cast<IPolicyParagraph>().ToArray(),
            corruptParagraph, vlm, dpi, ct);

    private static async Task<CorruptParagraphVisualCheck> VerifyCore(
        string originalInputPath,
        IReadOnlyList<IPolicyParagraph> paragraphs,
        IPolicyParagraph corruptParagraph,
        IPdfVisualQuestion vlm,
        int dpi,
        CancellationToken ct)
    {
        if (!corruptParagraph.Corrupt)
            throw new ArgumentException(
                $"Đoạn {corruptParagraph.Index} không được đánh dấu Corrupt — cổng này chỉ dành cho đoạn đã bị is_doubled gắn cờ.",
                nameof(corruptParagraph));

        var pdfPath = PdfTextbookOutline.FindSiblingPdf(originalInputPath);
        if (pdfPath is null)
            return new CorruptParagraphVisualCheck(
                corruptParagraph.Index, corruptParagraph.Text, CorruptParagraphVisualVerdict.Inconclusive,
                "Không có PDF anh em — chưa hỗ trợ render DOCX thuần (cần soffice, chưa thêm).", null, null);

        var neighborText = FindNearestCleanNeighborText(paragraphs, corruptParagraph.Index);
        if (neighborText is null)
            return new CorruptParagraphVisualCheck(
                corruptParagraph.Index, corruptParagraph.Text, CorruptParagraphVisualVerdict.Inconclusive,
                $"Không tìm được đoạn lành trong cửa sổ ±{NeighborSearchWindow} đoạn để định vị trang.", pdfPath, null);

        int page;
        double pageWidth, pageHeight;
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            var located = LocatePage(doc, neighborText);
            if (located is null)
                return new CorruptParagraphVisualCheck(
                    corruptParagraph.Index, corruptParagraph.Text, CorruptParagraphVisualVerdict.Inconclusive,
                    "Đoạn lành gần nhất không khớp được vào trang PDF nào.", pdfPath, null);
            page = located.Value;
            var pdfPage = doc.GetPage(page);
            pageWidth = pdfPage.Width;
            pageHeight = pdfPage.Height;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new CorruptParagraphVisualCheck(
                corruptParagraph.Index, corruptParagraph.Text, CorruptParagraphVisualVerdict.Inconclusive,
                $"Không đọc được PDF: {ex.Message}", pdfPath, null);
        }

        byte[] png;
        try
        {
            png = PdfRegionRasterizer.RenderCropPng(pdfPath, page, 0, 0, pageWidth, pageHeight, dpi);
        }
        catch (Exception ex)
        {
            return new CorruptParagraphVisualCheck(
                corruptParagraph.Index, corruptParagraph.Text, CorruptParagraphVisualVerdict.Inconclusive,
                $"Không render được trang {page}: {ex.Message}", pdfPath, page);
        }

        // Prompt không được chứa CHUỖI MẪU CỤ THỂ nào của hiện tượng cần tìm: model từng "nhìn thấy"
        // đúng chuỗi ví dụ `HHììnnhh` trên một trang sách máy học tiếng Anh (064 đoạn 16, §174) — nó
        // chép ví dụ chứ không đọc ảnh. Cùng lớp lỗi với placeholder "..." ở lượt trước: bất cứ thứ gì
        // đưa vào prompt đều có thể quay lại trong câu trả lời dưới dạng "bằng chứng".
        var question =
            "Đây là một trang tài liệu PDF gốc. Tầng đọc tự động đã trích ra đoạn văn bản sau, nghi ngờ " +
            "mỗi chữ cái bị lặp lại hai lần liên tiếp:\n\n" +
            $"\"{Truncate(corruptParagraph.Text, 200)}\"\n\n" +
            "Nhìn vào TRANG ẢNH này: chữ trên trang có thực sự bị lặp từng ký tự như vậy không, hay " +
            "trang hiển thị văn bản bình thường (đọc được rõ ràng, mỗi chữ cái xuất hiện một lần)? " +
            "Trả lời bằng TIẾNG VIỆT, ĐÚNG MỘT dòng JSON, không thêm lời dẫn.\n" +
            "verdict CHỈ được là một trong hai giá trị: \"doubled_in_source\" (chữ trên trang bị lặp) " +
            "hoặc \"normal_in_source\" (chữ trên trang bình thường).\n" +
            "evidence phải trích đúng vài chữ BẠN ĐỌC ĐƯỢC trên ảnh để chứng minh, ví dụ dạng: " +
            "{\"verdict\": \"<một trong hai giá trị trên>\", \"evidence\": [\"dòng đầu trang đọc được là " +
            "<chữ bạn thấy>, mỗi ký tự xuất hiện một lần\"]}";

        // 300 token quá ít cho câu trả lời tiếng Việt — đã cắt cụt JSON giữa chừng và làm validator bác
        // nhầm evidence thật (064 đoạn 12, §174).
        var answer = await vlm.AskAsync(png, question, maxTokens: 600, ct: ct);
        var verdict = ParseVerdict(answer);
        if (verdict != CorruptParagraphVisualVerdict.Inconclusive && !HasUsableEvidence(answer))
            return new CorruptParagraphVisualCheck(
                corruptParagraph.Index, corruptParagraph.Text, CorruptParagraphVisualVerdict.Inconclusive,
                $"BÁC verdict vì evidence rỗng/chép lại ví dụ — không kiểm chứng được. Nguyên văn: {answer}",
                pdfPath, page);

        return new CorruptParagraphVisualCheck(
            corruptParagraph.Index, corruptParagraph.Text, verdict, answer, pdfPath, page);
    }

    internal static string? FindNearestCleanNeighborText(
        IReadOnlyList<IPolicyParagraph> paragraphs, int index)
    {
        var byIndex = paragraphs.ToDictionary(p => p.Index);
        for (var offset = 1; offset <= NeighborSearchWindow; offset++)
        {
            foreach (var candidateIndex in new[] { index - offset, index + offset })
            {
                if (byIndex.TryGetValue(candidateIndex, out var p) &&
                    !p.Corrupt && !string.IsNullOrWhiteSpace(p.Text) && p.Text.Length >= 12)
                    return p.Text;
            }
        }
        return null;
    }

    /// <summary>
    /// Độ dài tiền tố canonical dùng làm needle khớp trang. Đo trên ca thật (053, đoạn 175/177/179/181):
    /// needle CẢ ĐOẠN (1000+ ký tự) không khớp được trang nào — hai tầng đọc (OpenXML cho DOCX, PdfPig
    /// cho PDF) đủ khác nhau ở khoảng trắng/ngắt dòng để một chuỗi liên tục dài lệch giữa chừng. Tiền tố
    /// quá ngắn (~40 ký tự) thì khớp nhầm trang khác có cùng tiêu đề lặp lại (báo cáo tài chính nhiều
    /// trang cùng header). 80 ký tự là điểm cân bằng đã đo — khớp đúng cả 4 ca thật, xem handoff §174.
    /// </summary>
    private const int PageMatchPrefixLength = 80;

    internal static int? LocatePage(PdfDocument doc, string neighborText)
    {
        var canon = Canon(neighborText);
        if (canon.Length < 8) return null;
        var needle = canon[..Math.Min(PageMatchPrefixLength, canon.Length)];

        foreach (var page in doc.GetPages())
        {
            if (Canon(page.Text).Contains(needle, StringComparison.Ordinal))
                return page.Number;
        }
        return null;
    }

    /// <summary>
    /// Grounding tối thiểu: verdict chỉ được nhận khi kèm evidence NÓI ĐƯỢC ĐIỀU GÌ. Bác khi evidence
    /// trống, chỉ chứa dấu chấm lửng, hoặc quá ngắn để mô tả được thứ nhìn thấy. Bắt đúng ca đã gặp ở
    /// §174: model echo nguyên placeholder <c>"..."</c> của prompt — khi đó verdict đi kèm cũng chỉ là
    /// khớp mẫu output, không phải phán đoán từ ảnh.
    /// <para>
    /// CHẤP NHẬN JSON bị cắt cụt giữa chừng (thiếu <c>"]}</c> đóng): câu trả lời tiếng Việt tốn token
    /// hơn tiếng Anh nhiều nên dễ chạm trần <c>maxTokens</c>. Bản đầu đòi dấu <c>]</c> đóng nên BÁC NHẦM
    /// một evidence thật đã bị cắt cụt (064 đoạn 12, §174) — nội dung mới là thứ đáng xét, không phải
    /// JSON có đóng ngoặc hay không.
    /// </para>
    /// </summary>
    internal static bool HasUsableEvidence(string modelAnswer)
    {
        var match = EvidenceRx.Match(modelAnswer);
        if (!match.Success) return false;

        var body = match.Groups["body"].Value;
        var items = EvidenceItemRx.Matches(body)
            .Select(m => m.Groups["text"].Value.Trim())
            .ToList();

        // JSON cắt cụt: chuỗi cuối chưa được đóng nháy nên EvidenceItemRx không bắt được — lấy phần
        // đuôi sau dấu nháy mở cuối cùng làm một mục.
        var lastQuote = body.LastIndexOf('"');
        if (lastQuote >= 0 && items.Count * 2 <= body.Count(ch => ch == '"') - 1)
            items.Add(body[(lastQuote + 1)..].Trim());

        return items.Any(text => text.Trim('.', '…', ' ', '"').Length >= 15);
    }

    /// <summary>
    /// Đọc verdict theo GIÁ TRỊ TRƯỜNG, không phải tìm chuỗi con trong cả câu trả lời.
    /// <para>
    /// Bản đầu dùng <c>Contains("normal_in_source")</c> — và model trả về <c>"abnormal_in_source"</c>
    /// (giá trị NGOÀI hợp đồng) thì chuỗi đó CHỨA <c>normal_in_source</c>, nên bị đọc ngược thành
    /// "bình thường" trong khi model muốn nói "bất thường". Lỗi im lặng, không exception — bắt được
    /// nhờ đọc nguyên văn log chứ không nhìn mỗi verdict (064 đoạn 16, §174).
    /// </para>
    /// <para>
    /// Giá trị lạ ⇒ <c>Inconclusive</c>, KHÔNG đoán ý model. Nếu nó không trả đúng một trong hai giá trị
    /// đã cho thì bản thân việc đó đã là dấu hiệu câu trả lời không đáng tin.
    /// </para>
    /// </summary>
    internal static CorruptParagraphVisualVerdict ParseVerdict(string modelAnswer)
    {
        var match = VerdictRx.Match(modelAnswer);
        if (!match.Success) return CorruptParagraphVisualVerdict.Inconclusive;

        return match.Groups["value"].Value.ToLowerInvariant() switch
        {
            "doubled_in_source" => CorruptParagraphVisualVerdict.ConfirmedSourceCorruption,
            "normal_in_source" => CorruptParagraphVisualVerdict.SuspectedParserBug,
            _ => CorruptParagraphVisualVerdict.Inconclusive,
        };
    }

    private static readonly Regex VerdictRx = new(
        @"""verdict""\s*:\s*""(?<value>[^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // `\]|$` — chấp nhận cả JSON bị cắt cụt (thiếu ngoặc đóng) vì trả lời tiếng Việt dễ chạm maxTokens.
    private static readonly Regex EvidenceRx = new(
        @"""evidence""\s*:\s*\[(?<body>.*?)(?:\]|$)", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex EvidenceItemRx = new(
        @"""(?<text>(?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    private static string Canon(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9À-ỹ]+", "");

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
