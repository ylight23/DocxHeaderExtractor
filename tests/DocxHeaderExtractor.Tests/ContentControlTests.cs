using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Hai luật đọc cùng một dữ kiện OOXML — đoạn có nằm trong <c>w:sdt</c> không — nhưng dùng nó vào
/// hai việc ngược nhau, và cả hai đều đứng sau cờ riêng.
/// <para>
/// ĐO ĐƯỢC (§36) trên khoá luận: mẫu <c>NHÃN + SỐ + HẾT</c> khớp 13 đoạn. 8 đoạn TRONG sdt là dòng
/// mục lục kèm số trang (<c>MỞ ĐẦU 1</c>) — không mục nào là đề mục. 5 đoạn NGOÀI sdt
/// (<c>PHỤ LỤC 1</c>, <c>Tiểu kết chương 2</c>…) — 5/5 là đề mục thật. Tách sạch 8/8 và 5/5.
/// </para>
/// </summary>
public class ContentControlTests
{
    private const string Body =
        "Phần thân bài trình bày phạm vi áp dụng và các bước thực hiện của quy trình này, kèm ví dụ " +
        "minh hoạ cho từng bước để người đọc đối chiếu khi triển khai thực tế.";

    /// <summary>Cờ tắt (mặc định) thì đoạn trong content control vẫn được chấm như mọi đoạn khác.</summary>
    [Fact]
    public void Mac_dinh_khong_dung_toi_doan_trong_content_control()
    {
        var p = Paragraph("PHỤ LỤC 1", inControl: true);
        HeadingHeuristics.Classify(p, new ExtractionOptions());

        Assert.NotEqual(0, p.Score);
    }

    /// <summary>Bật cờ thì hạ vai trò và điểm — nhưng KHÔNG xoá đoạn, nó vẫn là ngữ cảnh.</summary>
    [Fact]
    public void Bat_co_thi_ha_vai_tro_doan_trong_content_control()
    {
        var p = Paragraph("PHỤ LỤC 1", inControl: true);
        HeadingHeuristics.Classify(p, new ExtractionOptions { SkipContentControls = true });

        Assert.Equal(ParagraphRole.Normal, p.Role);
        Assert.Equal(0, p.Score);
        Assert.Equal("PHỤ LỤC 1", p.Text);
    }

    /// <summary>Đoạn NGOÀI content control không bị cờ đó đụng tới.</summary>
    [Fact]
    public void Co_do_khong_cham_doan_ngoai_content_control()
    {
        var p = Paragraph("PHỤ LỤC 1", inControl: false);
        HeadingHeuristics.Classify(p, new ExtractionOptions { SkipContentControls = true });

        Assert.NotEqual(ParagraphRole.Empty, p.Role);
        Assert.NotEqual(0, p.Score);
    }

    /// <summary>
    /// <c>NHÃN + SỐ + HẾT</c> đọc được thành chuỗi đánh số khi bật cờ và đoạn nằm ngoài sdt.
    /// </summary>
    [Fact]
    public void Nhan_cong_so_khong_duoi_doc_duoc_khi_bat_co()
    {
        var opts = new ExtractionOptions { AllowBareLabelledNumbers = true };
        var token = NumberingAudit.ParseParagraph(Paragraph("PHỤ LỤC 1", false), "PHỤ LỤC 1", opts);

        Assert.NotNull(token);
        Assert.Equal(NumberKind.Labelled, token!.Value.Kind);
        Assert.Equal(1, token.Value.Value);
    }

    /// <summary>Cùng hình dạng nhưng TRONG sdt là dòng mục lục kèm số trang — không được đọc.</summary>
    [Fact]
    public void Cung_hinh_dang_trong_content_control_thi_khong_doc()
    {
        var opts = new ExtractionOptions { AllowBareLabelledNumbers = true };
        var token = NumberingAudit.ParseParagraph(Paragraph("MỞ ĐẦU 1", true), "MỞ ĐẦU 1", opts);

        Assert.Null(token);
    }

    /// <summary>Cờ tắt thì hành vi cũ giữ nguyên — chốt chống ăn nhầm.</summary>
    [Fact]
    public void Co_tat_thi_khong_doc_nhan_cong_so_khong_duoi()
    {
        var token = NumberingAudit.ParseParagraph(
            Paragraph("PHỤ LỤC 1", false), "PHỤ LỤC 1", new ExtractionOptions());

        Assert.Null(token);
    }

    /// <summary>
    /// Chú thích LUÔN có phần đuôi, nên mẫu mới không được giẫm lên nó — đây chính là ca mà ràng
    /// buộc <c>HasTitleRemainder</c> sinh ra để chặn.
    /// </summary>
    [Fact]
    public void Chu_thich_co_phan_duoi_khong_bi_mau_moi_bat_nham()
    {
        var opts = new ExtractionOptions { AllowBareLabelledNumbers = true };
        var token = NumberingAudit.ParseParagraph(
            Paragraph("Bảng 1.2 Đối chiếu kết quả khảo sát", false),
            "Bảng 1.2 Đối chiếu kết quả khảo sát", opts);

        // Đọc được thì phải là do mẫu CŨ (có phần đuôi), không phải mẫu "hết ở đó".
        if (token is { } t) Assert.NotEqual("BẢNG", t.Label);
    }

    private static SlimParagraph Paragraph(string text, bool inControl) => new()
    {
        Index = 0,
        Text = text,
        Bold = true,
        FontSizePt = 14,
        InContentControl = inControl,
    };
}
