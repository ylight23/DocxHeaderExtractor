using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Ba cổng dùng <c>NumberingAudit.Parse</c> làm bằng chứng "có đánh số", và cả ba nằm NGOÀI đường
/// <c>--no-llm</c> nên <c>bench</c> mù hoàn toàn với chúng (§55.8). Nới <c>LabelledRx</c> ở §55.2
/// đổi hành vi của cả ba cùng lúc; các test dưới ghim hành vi ĐÚNG cho từng cổng.
/// </summary>
public class NhanKhongDauNgatQuaCacCongTests
{
    private const string ChuongInHoa = "Chương II QUY ĐỊNH CHUNG";
    private const string ChuThich = "Bảng 2 Thống kê sau điều chỉnh";

    private static SlimParagraph Yeu(string text) => new()
    {
        Index = 0,
        Text = text,
        FontSizePt = 13,
        Score = 0.4,                 // dưới WeakEvidenceThreshold
        HasBuiltInHeadingStyle = false,
        OutlineLevel = null,
        NumberingId = null,
    };

    private static HeadingRecord Model(string text) => new()
    {
        Index = 0,
        Level = 1,
        Text = text,
        Source = HeadingSource.Model,
        Confidence = 0.6,
    };

    /// <summary>
    /// Đề mục chương dạng Nghị định 30 bị dán liền vẫn là đề mục có ĐÁNH SỐ, nên không cần critic —
    /// nó có bằng chứng cấu trúc, không phải phỏng đoán ngữ nghĩa của mô hình.
    /// </summary>
    [Fact]
    public void Critic_bo_qua_chuong_khong_dau_ngat()
    {
        Assert.False(ModelHeadingCriticGate.NeedsCritique(Model(ChuongInHoa), Yeu(ChuongInHoa)));
    }

    /// <summary>
    /// Mặt còn lại: chú thích bảng KHÔNG có bằng chứng đánh số nên vẫn phải qua critic. Nếu chốt
    /// in-hoa (§55.7) bị gỡ, test này đỏ — và nó đỏ vì một lý do THẬT: critic là lưới cuối cho
    /// nhóm mục yếu, bỏ nó đi với chú thích là mở đường cho dương tính giả.
    /// </summary>
    [Fact]
    public void Critic_van_xet_chu_thich_bang()
    {
        Assert.True(ModelHeadingCriticGate.NeedsCritique(Model(ChuThich), Yeu(ChuThich)));
    }

    /// <summary>
    /// Chữ ký evidence quyết định bucket calibration. Đề mục chương dạng dán liền phải vào bucket
    /// <c>numbered</c>; chú thích vào <c>unnumbered</c>. Đây chính là "đổi phân phối dự đoán" buộc
    /// phải bump <see cref="PrecisionCalibrationProfile.CurrentPipelineSignature"/>.
    /// </summary>
    [Fact]
    public void Chu_ky_evidence_xep_dung_bucket()
    {
        Assert.Contains("numbered", HeadingAcceptanceSignature.For(Model(ChuongInHoa)), StringComparison.Ordinal);
        Assert.Contains("unnumbered", HeadingAcceptanceSignature.For(Model(ChuThich)), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Ghim hợp đồng bump chữ ký.</b> Chú thích của
    /// <see cref="PrecisionCalibrationProfile.CurrentPipelineSignature"/> viết rõ: đổi phân phối dự
    /// đoán thì profile holdout cũ KHÔNG được âm thầm hiệu chỉnh pipeline này. §55.2 đã đổi phân
    /// phối. Lần này các ngưỡng gate/confidence chuyển thành cấu hình có thể hiệu chuẩn, nên profile
    /// cũ không còn mô tả cùng runtime policy.
    /// <para>
    /// Test này không kiểm được "có bump khi cần" một cách tổng quát — không máy nào biết điều đó.
    /// Nó ghim rằng lần bump NÀY đã xảy ra, để ai hạ chữ ký về v2 phải giải trình.
    /// </para>
    /// </summary>
    [Fact]
    public void Chu_ky_pipeline_da_bump_sau_khi_doi_phan_phoi()
    {
        Assert.Equal("dhx-semantic-precision/2026-08-21-v5-configured-gates",
            PrecisionCalibrationProfile.CurrentPipelineSignature);
    }

    /// <summary>
    /// Profile của chữ ký CŨ phải bị từ chối, không phải bị bỏ qua im lặng — đó là toàn bộ mục đích
    /// của việc bump.
    /// </summary>
    [Fact]
    public void Profile_chu_ky_cu_bi_tu_choi()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-cal-{Guid.NewGuid():N}.json");
        try
        {
            new PrecisionCalibrationProfile
            {
                PipelineSignature = "dhx-semantic-precision/2026-08-04-v2",
                Documents = 1,
            }.Save(path);

            var ex = Assert.Throws<FormatException>(() => PrecisionCalibrationProfile.Load(path));
            Assert.Contains("2026-08-04-v2", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
