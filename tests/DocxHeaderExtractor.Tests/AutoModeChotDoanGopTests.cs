using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Auto-mode MẶC ĐỊNH BẬT, nhưng chỉ áp cho tài liệu có ĐOẠN GỘP. Chốt đó là thứ làm nó an toàn.
/// <para>
/// Ba bộ có đáp án nói ngược nhau nếu không có chốt, và mâu thuẫn có hệ thống — tiêu đề sống ở chỗ
/// khác nhau. Đo được (§101):
/// </para>
/// <list type="bullet">
/// <item>bench, 7 tài liệu Word gốc: không chốt <b>2/7</b> · có chốt <b>6/7</b>, F1 96% → 98,6%</item>
/// <item>5 đáp án người kiểm, PDF→DOCX: tắt hẳn đúng cấp <b>6,5%</b> · bật <b>100%</b></item>
/// <item>14 đáp án gồm toc-derived WB: tắt hẳn <b>0/14</b> · bật <b>8/14</b></item>
/// </list>
/// <para>
/// Nên cả "tắt mặc định" (§100) lẫn "bật không chốt" đều sai. Ai lật lại phải đo CẢ BA bộ.
/// </para>
/// </summary>
public class AutoModeChotDoanGopTests
{
    [Fact]
    public void Auto_mode_mac_dinh_BAT()
    {
        Assert.True(new PipelineOptions().AutoDetectDocumentMode);
    }

    /// <summary>Ba bộ dựng THỦ CÔNG vẫn mặc định tắt — chúng là lựa chọn tường minh của người dùng.</summary>
    [Fact]
    public void Ba_bo_dung_thu_cong_van_mac_dinh_tat()
    {
        var o = new PipelineOptions();

        Assert.False(o.StyleDeclaredOutline);
        Assert.False(o.NumberingDeclaredOutline);
        Assert.False(o.AdministrativeDeclaredOutline);
    }

    /// <summary>
    /// Tài liệu Word gốc — mỗi tiêu đề một paragraph riêng — KHÔNG có đoạn gộp, nên không định
    /// tuyến. Đây là ca <c>bench/02-dinh-dang-thu-cong</c>: nó vô tình có "PHẦN I"/"PHẦN II" nên bị
    /// gán <c>VietnameseLegal</c> và route pháp quy chỉ dựng 2/7 mục.
    /// </summary>
    [Fact]
    public void Tai_lieu_khong_co_doan_gop_thi_khong_dinh_tuyen()
    {
        var doc = Doc(
            "PHẦN I. CƠ SỞ LÝ LUẬN",
            "1. Khái niệm cơ bản",
            "1.1. Định nghĩa",
            "PHẦN II. THỰC TRẠNG");

        Assert.All(doc.Paragraphs, p =>
            Assert.Single(ParagraphHeadingSplitter.Segments(p.Text!)));
    }

    /// <summary>
    /// Bản chuyển PDF — cả trang trong một <c>w:p</c> — CÓ đoạn gộp, nên được định tuyến. Ở đây
    /// tầng ứng viên gần như không thấy gì vì mốc nằm giữa đoạn (§47.1: 1.596 ở đầu, 24.220 bên trong).
    /// </summary>
    [Fact]
    public void Tai_lieu_co_doan_gop_thi_duoc_dinh_tuyen()
    {
        var doc = Doc("Điều 4. Áp dụng Bộ luật dân sự1. Bộ luật này là luật chung.Điều 5. Áp dụng tập quán");

        Assert.True(ParagraphHeadingSplitter.Segments(doc.Paragraphs[0].Text!).Count > 1);
    }

    private static SlimDocument Doc(params string[] texts)
    {
        List<SlimParagraph> ps = [];
        for (var i = 0; i < texts.Length; i++)
            ps.Add(new SlimParagraph { Index = i, Text = texts[i], FontSizePt = 13 });
        return new SlimDocument { FileName = "t.docx", SourcePath = "t.docx", Paragraphs = ps }.Build();
    }
}
