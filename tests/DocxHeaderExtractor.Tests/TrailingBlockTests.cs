using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Một đề mục phải mở ra văn xuôi CỦA CHÍNH NÓ. Nhãn khối chữ ký đứng cuối một phần thì không —
/// sau nó chỉ có đề mục của phần kế tiếp.
/// <para>
/// Đây là chế độ hỏng mà TODO xếp đầu vì §10.3 và §11.2 đo được rằng KHÔNG tầng nào bác được:
/// mô hình được hỏi vẫn không cắt, và StyleTrust nhận đúng "style tài liệu này không đáng tin"
/// nhưng kết quả không đổi một chữ số vì hạ quyền style là chuyển quyền cho một chỗ trống.
/// </para>
/// <para>
/// Nội dung mẫu là văn bản trung tính; test khoá QUAN HỆ VỊ TRÍ, không khoá chữ nghĩa (§7.6).
/// </para>
/// </summary>
public sealed class TrailingBlockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dhx-trailing-{Guid.NewGuid():N}");

    private const string Body =
        "Phần thân bài trình bày phạm vi áp dụng, đối tượng điều chỉnh và các bước thực hiện " +
        "của quy trình, kèm ví dụ minh hoạ cho từng bước để người đọc đối chiếu khi triển khai.";

    /// <summary>
    /// Style nói dối: hai nhãn khối chữ ký cũng mang style Heading. Khi StyleTrust hạ quyền chọn,
    /// luật hình dạng phải TIẾP QUẢN được — đó là "bộ chấp hành" mà §11.2 ghi là còn thiếu.
    /// ĐO ĐƯỢC trên <c>09-style-ap-sai</c>: precision 57,1% → 100%, và bench 9 tài liệu 92,5% → 95,6%.
    /// </summary>
    [Fact]
    public void Nhan_khoi_chu_ky_mang_style_Heading_van_bi_ha_khi_style_khong_dang_tin()
    {
        // Cần ≥8 đoạn mang style, nếu không StyleTrust coi mẫu quá nhỏ để phán xét
        // (StyledCount < MinimumStyledSample ⇒ SelectionTrusted = true) và luật không bao giờ chạy.
        // Mật độ style cao cũng là thứ khiến tài liệu này bị hạ quyền, giống 09-style-ap-sai.
        var doc = new BenchDoc("trailing-signature", "Nhãn khối chữ ký mang style Heading",
        [
            new("Chương 1. Tổng quan", 1, Style: "Heading1"),
            new(Body),
            new("1.1. Phạm vi áp dụng", 2, Style: "Heading2"),
            new(Body),
            new("1.2. Đối tượng điều chỉnh", 2, Style: "Heading2"),
            new(Body),
            new("Chương 2. Trình tự thực hiện", 1, Style: "Heading1"),
            new(Body),
            new("2.1. Chuẩn bị hồ sơ", 2, Style: "Heading2"),
            new(Body),
            new("Người lập biểu", Style: "Heading3"),      // ← không phải đề mục
            new("Nguyễn Văn A", Style: "Heading3"),        // ← không phải đề mục
            new("Chương 3. Kết luận", 1, Style: "Heading1"),
            new(Body),
        ]);
        var path = BenchDocumentFactory.Write(doc, _dir);

        var slim = new DocxSlimExtractor(new ExtractionOptions { UseStyleTrust = true }).Extract(path);
        SlimParagraph Find(string starts) => slim.Paragraphs.First(p => p.Text.StartsWith(starts));

        // Hai nhãn đuôi bị hạ…
        Assert.False(Find("Người lập biểu").IsCandidate);
        Assert.False(Find("Nguyễn Văn A").IsCandidate);

        // …còn đề mục thật thì không, kể cả khi style đã mất quyền phủ quyết.
        Assert.True(Find("Chương 1.").IsCandidate);
        Assert.True(Find("1.1.").IsCandidate);
        Assert.True(Find("Chương 2.").IsCandidate);
        Assert.True(Find("Chương 3.").IsCandidate);
    }

    /// <summary>
    /// Chốt ngược: chuỗi đề mục lồng nhau cũng là một dãy ứng viên liên tiếp không xen văn xuôi.
    /// Thiếu vế miễn trừ theo tuyên bố cấu trúc thì luật sẽ giết đúng phần cha của mọi cây heading.
    /// </summary>
    [Fact]
    public void Chuoi_de_muc_long_nhau_khong_bi_dai_cuon_theo()
    {
        // ≥5 đoạn mang dấu hiệu cấu trúc, nếu không chốt mức tài liệu chặn luật lại và test thành
        // vô nghĩa — bản đầu của test này đúng như vậy: kiểm đột biến "bỏ vế miễn trừ" vẫn xanh.
        var doc = new BenchDoc("nested-chain", "Ba cấp lồng nhau rồi mới tới văn xuôi",
        [
            new("Chương 1. Tổng quan", 1, Style: "Heading1"),
            new("1.1. Phạm vi áp dụng", 2, Style: "Heading2"),
            new("1.1.1. Đối tượng điều chỉnh", 3, Style: "Heading3"),
            new(Body),
            new("1.2. Trình tự thực hiện", 2, Style: "Heading2"),
            new(Body),
            new("Chương 2. Kết luận", 1, Style: "Heading1"),
            new(Body),
        ]);
        var path = BenchDocumentFactory.Write(doc, _dir);

        var slim = new DocxSlimExtractor(new ExtractionOptions()).Extract(path);

        Assert.All(
            slim.Paragraphs.Where(p => p.Text.StartsWith("Chương") || p.Text.StartsWith("1.1")),
            p => Assert.True(p.IsCandidate, $"\"{p.Text}\" là đề mục thật, không được bị dãy cuốn theo"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
