using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Thuộc tính <c>mode</c> trên phần tử <c>&lt;doc&gt;</c> của XML tinh gọn. Nó là kênh đo duy nhất
/// cho chế độ tài liệu — mọi bảng phân bố ở handoff §47.2 và §48.1 đọc từ đây — nhưng trước đây
/// KHÔNG có test nào, kể cả sau khi nó làm đỏ một test và phải sửa null-safe.
/// </summary>
public class SlimXmlModeAttributeTests
{
    private static string Xml(SlimDocument doc) =>
        SlimXmlSerializer.ToFullXml(doc, new ExtractionOptions());

    private static int _next;

    private static SlimParagraph P(string text) => new()
    {
        Index = _next++,
        Text = text,
        FontSizePt = 13,
    };

    /// <summary>Chế độ đo được phải hiện ra ngoài, nếu không thì không kiểm chứng được trên tập lớn.</summary>
    [Fact]
    public void Che_do_hien_ra_thuoc_tinh_doc()
    {
        List<SlimParagraph> ps =
        [
            P("Chương I"), P("Điều 1. Phạm vi điều chỉnh"), P("Điều 2. Đối tượng áp dụng"),
            P("Điều 3. Giải thích từ ngữ"),
        ];

        var xml = Xml(new SlimDocument
        {
            FileName = "t.docx",
            SourcePath = "t.docx",
            Paragraphs = ps,
            Mode = DocumentModeClassifier.Measure(ps),
        }.Build());

        Assert.Contains($"mode=\"{DocumentMode.VietnameseLegal}\"", xml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Đây là test cho một lỗi ĐÃ XẢY RA.</b> <see cref="SlimDocument.Mode"/> là null với tài
    /// liệu dựng tay (test, dựng lại từ cache), và bản đầu tiên truy thẳng <c>doc.Mode.Mode</c> nên
    /// ném <see cref="NullReferenceException"/> — một test khác bắt được, tôi sửa null-safe nhưng
    /// KHÔNG ghim lại. Không có test này thì đúng lỗi đó tái phát âm thầm.
    /// </summary>
    [Fact]
    public void Mode_null_khong_lam_hong_serializer()
    {
        var doc = new SlimDocument
        {
            FileName = "t.docx",
            SourcePath = "t.docx",
            Paragraphs = [P("Một đoạn")],
        }.Build();

        Assert.Null(doc.Mode);

        var xml = Xml(doc);

        Assert.Contains($"mode=\"{DocumentMode.Unknown}\"", xml, StringComparison.Ordinal);
    }

    /// <summary>
    /// Serializer là đường in CHẨN ĐOÁN, không phải đường quyết định: thêm <c>mode</c> không được
    /// làm đổi phần thân. Test này giết đột biến "ghi mode vào từng phần tử p".
    /// </summary>
    [Fact]
    public void Them_mode_khong_doi_phan_than()
    {
        var doc = new SlimDocument
        {
            FileName = "t.docx",
            SourcePath = "t.docx",
            Paragraphs = [P("Điều 1. Phạm vi"), P("Điều 2. Đối tượng")],
        }.Build();

        var xml = Xml(doc);

        Assert.Equal(1, xml.Split("mode=\"").Length - 1);
        Assert.Contains("<p ", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<p mode=", xml, StringComparison.Ordinal);
    }
}
