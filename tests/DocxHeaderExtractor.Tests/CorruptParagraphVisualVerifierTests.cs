using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Repair;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

public sealed class CorruptParagraphVisualVerifierTests
{
    [Fact]
    public void FindNearestCleanNeighborText_prefers_closest_clean_paragraph_either_side()
    {
        var doc = Doc(
            P(0, "Đoạn lành xa phía trước, đủ dài để hợp lệ làm mỏ neo."),
            P(2, "ngắn"), // quá ngắn (< 12 ký tự), không đủ điều kiện
            P(4, corrupt: true, text: "HHììnnhh 11.1"),
            P(6, "Đoạn lành liền sau, đủ dài để hợp lệ làm mỏ neo."));

        var neighbor = CorruptParagraphVisualVerifier.FindNearestCleanNeighborText(doc, index: 4);

        Assert.Equal("Đoạn lành liền sau, đủ dài để hợp lệ làm mỏ neo.", neighbor);
    }

    [Fact]
    public void FindNearestCleanNeighborText_skips_other_corrupt_paragraphs()
    {
        var doc = Doc(
            P(0, corrupt: true, text: "cũng hỏng, phải bỏ qua luôn đoạn này"),
            P(2, corrupt: true, text: "hỏng ở giữa, đoạn cần định vị"),
            P(4, corrupt: true, text: "cũng hỏng nốt, bỏ qua luôn"),
            P(6, "Đoạn lành duy nhất trong cửa sổ tìm kiếm, đủ dài."));

        var neighbor = CorruptParagraphVisualVerifier.FindNearestCleanNeighborText(doc, index: 2);

        Assert.Equal("Đoạn lành duy nhất trong cửa sổ tìm kiếm, đủ dài.", neighbor);
    }

    [Fact]
    public void FindNearestCleanNeighborText_returns_null_when_nothing_clean_nearby()
    {
        var doc = Doc(P(0, corrupt: true, text: "chỉ có một đoạn hỏng, không có gì khác"));

        Assert.Null(CorruptParagraphVisualVerifier.FindNearestCleanNeighborText(doc, index: 0));
    }

    [Theory]
    // Ca thật đã gặp ở §174: model echo nguyên placeholder "..." của prompt thay vì suy luận. Khi
    // evidence là bản sao prompt thì verdict đi kèm cũng chỉ là khớp mẫu output, không đáng tin.
    [InlineData("{\"verdict\": \"doubled_in_source\", \"evidence\": [\"...\"]}", false)]
    [InlineData("{\"verdict\": \"normal_in_source\", \"evidence\": []}", false)]
    [InlineData("{\"verdict\": \"normal_in_source\", \"evidence\": [\"\"]}", false)]
    [InlineData("{\"verdict\": \"normal_in_source\", \"evidence\": [\"ngắn\"]}", false)]
    [InlineData("{\"verdict\": \"normal_in_source\"}", false)]
    [InlineData("{\"verdict\": \"normal_in_source\", \"evidence\": [\"tiêu đề bảng hiển thị một lần, không thấy ký tự nhân đôi\"]}", true)]
    [InlineData("{\"verdict\": \"doubled_in_source\", \"evidence\": [\"chữ Hình trên trang hiện thành HHììnnhh rõ ràng\"]}", true)]
    // JSON cắt cụt vì chạm maxTokens — nội dung mới là thứ đáng xét, không phải ngoặc đóng. Bản đầu
    // đòi dấu `]` nên bác nhầm đúng ca này (064 đoạn 12, §174).
    [InlineData("{\"verdict\": \"normal_in_source\", \"evidence\": [\"trang đầu của một cuốn sách, có dòng Acknowledgements, dòng tiếp theo", true)]
    [InlineData("{\"verdict\": \"normal_in_source\", \"evidence\": [\"ngắn quá", false)]
    public void HasUsableEvidence_rejects_empty_or_echoed_placeholder_evidence(string answer, bool expected)
    {
        Assert.Equal(expected, CorruptParagraphVisualVerifier.HasUsableEvidence(answer));
    }

    /// <summary>
    /// Ca thật §174: model trả <c>"abnormal_in_source"</c> — giá trị NGOÀI hợp đồng. Bản đầu dùng
    /// Contains nên chuỗi đó khớp <c>normal_in_source</c> và bị đọc NGƯỢC thành "bình thường". Giá trị
    /// lạ phải là Inconclusive, không được đoán ý model.
    /// </summary>
    [Theory]
    [InlineData("{\"verdict\": \"abnormal_in_source\", \"evidence\": [\"có ký tự nhân đôi ở dòng 8.7\"]}")]
    [InlineData("{\"verdict\": \"unclear\", \"evidence\": [\"không nhìn rõ chữ trên ảnh này\"]}")]
    [InlineData("{\"verdict\": \"\", \"evidence\": [\"không nhìn rõ chữ trên ảnh này\"]}")]
    public void ParseVerdict_treats_out_of_contract_values_as_inconclusive(string answer)
    {
        Assert.Equal(CorruptParagraphVisualVerdict.Inconclusive, CorruptParagraphVisualVerifier.ParseVerdict(answer));
    }

    [Theory]
    [InlineData("{\"verdict\": \"doubled_in_source\", \"evidence\": [\"...\"]}", CorruptParagraphVisualVerdict.ConfirmedSourceCorruption)]
    [InlineData("{\"verdict\": \"normal_in_source\", \"evidence\": [\"...\"]}", CorruptParagraphVisualVerdict.SuspectedParserBug)]
    [InlineData("model trả lời lung tung không theo hợp đồng JSON", CorruptParagraphVisualVerdict.Inconclusive)]
    public void ParseVerdict_reads_the_two_known_answers_and_falls_back_to_inconclusive(
        string modelAnswer, CorruptParagraphVisualVerdict expected)
    {
        Assert.Equal(expected, CorruptParagraphVisualVerifier.ParseVerdict(modelAnswer));
    }

    [Fact]
    public void LocatePage_finds_the_real_page_containing_a_known_clean_paragraph()
    {
        var pdf = Path.Combine(
            "todo10_8", "heading_corpus_100", "03_tai_chinh_ke_toan",
            "052_WBG_Trust_Fund_FIS_December_2025.pdf");
        if (!File.Exists(pdf)) return;

        using var doc = PdfDocument.Open(pdf);

        // "Cost Recovery" cấp 2 (tiêu đề mục thật) nằm ở trang 27, đã xác nhận trực tiếp ở handoff §172.
        var page = CorruptParagraphVisualVerifier.LocatePage(doc, "Cost Recovery");

        Assert.NotNull(page);
        Assert.True(page is 27 or 28); // cùng cụm "Cost Recovery" xuất hiện ở cả hai trang liền kề
    }

    [Fact]
    public void LocatePage_returns_null_for_text_that_does_not_exist_in_the_document()
    {
        var pdf = Path.Combine(
            "todo10_8", "heading_corpus_100", "03_tai_chinh_ke_toan",
            "052_WBG_Trust_Fund_FIS_December_2025.pdf");
        if (!File.Exists(pdf)) return;

        using var doc = PdfDocument.Open(pdf);

        var page = CorruptParagraphVisualVerifier.LocatePage(doc, "Chuỗi ký tự chắc chắn không tồn tại trong tài liệu này XYZQWERTY");

        Assert.Null(page);
    }

    [Fact]
    public void LocatePage_matches_a_full_page_sized_paragraph_by_prefix_not_exact_full_text()
    {
        // Ca thật đã đo ở handoff §174: dùng CẢ đoạn (1000+ ký tự) làm needle không khớp trang nào —
        // hai tầng đọc (OpenXML cho DOCX, PdfPig cho PDF) đủ khác nhau ở khoảng trắng/ngắt dòng để một
        // chuỗi liên tục dài lệch giữa chừng. Khớp theo tiền tố mới đúng.
        var docx = Path.Combine(
            "todo10_8", "heading_corpus_95_word", "03_tai_chinh_ke_toan",
            "053_IDA_Information_Statement_FY25.docx");
        var pdf = Path.Combine(
            "todo10_8", "heading_corpus_100", "03_tai_chinh_ke_toan",
            "053_IDA_Information_Statement_FY25.pdf");
        if (!File.Exists(docx) || !File.Exists(pdf)) return;

        var slim = new DocxSlimExtractor().Extract(docx);
        var fullParagraphText = slim.Paragraphs.First(p => p.Index == 175).Text;
        Assert.True(fullParagraphText.Length > 1000); // xác nhận đúng dạng "cả trang một đoạn"

        using var doc = PdfDocument.Open(pdf);
        var page = CorruptParagraphVisualVerifier.LocatePage(doc, fullParagraphText);

        Assert.NotNull(page);
    }

    private static SlimDocument Doc(params SlimParagraph[] paragraphs) => new SlimDocument
    {
        FileName = "test.docx",
        SourcePath = "test.docx",
        Paragraphs = paragraphs,
    }.Build();

    private static SlimParagraph P(int index, string text, bool corrupt = false) =>
        new() { Index = index, Text = text, Corrupt = corrupt };
}
