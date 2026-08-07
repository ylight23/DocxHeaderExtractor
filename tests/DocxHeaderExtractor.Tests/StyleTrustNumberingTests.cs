using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// TODO 3b: style mất quyền gán cấp khi nó KHÔNG bám độ sâu của chuỗi đánh số người soạn gõ.
/// <para>
/// Hai vế cũ của <see cref="StyleTrust"/> chỉ soi chính style — bao nhiêu cấp riêng biệt, có bỏ cấp
/// giữa chừng không — nên chúng mù với kiểu hỏng mà §16 đo được trên khoá luận thật: cùng một
/// <c>Heading3</c> mang cấp 2 ở 9 mục và cấp 3 ở 8 mục, trong khi tài liệu dùng đủ 5 cấp liên tục và
/// "khoẻ mạnh" theo cả hai vế đó. Vế thứ ba đối chiếu style với một nguồn ĐỘC LẬP.
/// </para>
/// <para>ĐO ĐƯỢC: khoá luận thật đúng cấp 26,5% → 37,2%; bench 10 tài liệu giữ nguyên 10/10.</para>
/// </summary>
public sealed class StyleTrustNumberingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dhx-stn-{Guid.NewGuid():N}");

    private const string Body =
        "Phần thân bài trình bày phạm vi áp dụng và các bước thực hiện của quy trình này, " +
        "kèm ví dụ minh hoạ cho từng bước để người đọc đối chiếu khi triển khai thực tế.";

    private StyleTrust Measure(params BenchPara[] paragraphs)
    {
        var path = BenchDocumentFactory.Write(
            new BenchDoc($"stn-{Guid.NewGuid():N}", "fixture", paragraphs), _dir);
        return new DocxSlimExtractor(new ExtractionOptions()).Extract(path).StyleTrust!;
    }

    /// <summary>
    /// Style dùng đủ cấp, liên tục — hai vế cũ đều "khoẻ" — nhưng độ sâu đánh số nói khác:
    /// <c>1.1.</c> (sâu 2) lại mang Heading3. Vế thứ ba phải bắt được.
    /// </summary>
    [Fact]
    public void Style_khong_bam_do_sau_danh_so_thi_mat_quyen_gan_cap()
    {
        // Dùng LIÊN TỤC cả ba cấp style để hai vế cũ đều "khoẻ" — nếu bỏ cấp thì vế SkipsLevels
        // kích hoạt và test không còn cô lập vế mới. Bất nhất nằm ở chỗ: cùng độ sâu đánh số 2 mà
        // ba mục mang Heading2 còn sáu mục mang Heading3, đúng hình dạng của khoá luận thật (§16.2).
        var trust = Measure(
            new("Chương 1. Tổng quan", 1, Style: "Heading1"), new(Body),
            new("1.1. Phạm vi áp dụng", 2, Style: "Heading2"), new(Body),
            new("1.2. Đối tượng điều chỉnh", 2, Style: "Heading2"), new(Body),
            new("1.3. Giải thích từ ngữ", 2, Style: "Heading2"), new(Body),
            new("2.1. Trình tự thực hiện", 2, Style: "Heading3"), new(Body),
            new("2.2. Hồ sơ kèm theo", 2, Style: "Heading3"), new(Body),
            new("2.3. Thời hạn giải quyết", 2, Style: "Heading3"), new(Body),
            new("3.1. Trách nhiệm thi hành", 2, Style: "Heading3"), new(Body),
            new("3.2. Điều khoản chuyển tiếp", 2, Style: "Heading3"), new(Body),
            new("3.3. Hiệu lực thi hành", 2, Style: "Heading3"), new(Body));

        Assert.True(trust.DistinctLevels > 1, "hai vế cũ phải 'khoẻ' thì test mới kiểm đúng vế mới");
        Assert.False(trust.SkipsLevels);
        Assert.False(trust.LevelTrusted, trust.Describe());
    }

    /// <summary>Style bám đúng độ sâu đánh số thì giữ nguyên quyền — chốt chống ăn nhầm.</summary>
    [Fact]
    public void Style_bam_dung_do_sau_thi_giu_quyen()
    {
        var trust = Measure(
            new("Chương 1. Tổng quan", 1, Style: "Heading1"), new(Body),
            new("1.1. Phạm vi áp dụng", 2, Style: "Heading2"), new(Body),
            new("1.2. Đối tượng điều chỉnh", 2, Style: "Heading2"), new(Body),
            new("1.3. Giải thích từ ngữ", 2, Style: "Heading2"), new(Body),
            new("2.1. Trình tự thực hiện", 2, Style: "Heading2"), new(Body),
            new("2.2. Hồ sơ kèm theo", 2, Style: "Heading2"), new(Body),
            new("2.3. Thời hạn giải quyết", 2, Style: "Heading2"), new(Body),
            new("3.1. Trách nhiệm thi hành", 2, Style: "Heading2"), new(Body),
            new("3.2. Điều khoản chuyển tiếp", 2, Style: "Heading2"), new(Body));

        Assert.True(trust.LevelTrusted, trust.Describe());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
