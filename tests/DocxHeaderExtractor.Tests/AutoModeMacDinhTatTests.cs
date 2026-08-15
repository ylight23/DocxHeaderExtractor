using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Ghim lựa chọn mặc định của <see cref="PipelineOptions.AutoDetectDocumentMode"/>.
/// <para>
/// Đo trên <c>bench</c> — bộ DUY NHẤT có đáp án người kiểm — bật auto-mode kém hơn ở MỌI chỉ số:
/// P 92,3% → 89,3% · R <b>100% → 69,4%</b> · F1 96% → 78,1% · đúng cấp 100% → 92% ·
/// tuyệt đối <b>6/7 → 2/7</b>.
/// </para>
/// <para>
/// Nó CÓ THỂ giúp nhóm PDF trong corpus 95 file, nhưng corpus đó không có đáp án nên lợi ích ở đó
/// không đo được. Một tính năng kém hơn ở nơi đo được và không đo được ở nơi còn lại thì không
/// được bật mặc định — §10.4.
/// </para>
/// Ai muốn lật lại phải kèm phép đo mới trên bench, không phải chỉ đổi giá trị.
/// </summary>
public class AutoModeMacDinhTatTests
{
    [Fact]
    public void Auto_mode_mac_dinh_TAT()
    {
        Assert.False(new PipelineOptions().AutoDetectDocumentMode);
    }

    /// <summary>
    /// Ba bộ dựng THỦ CÔNG vẫn mặc định tắt và độc lập với auto-mode — chúng là lựa chọn tường
    /// minh của người dùng, không phải suy đoán của bộ phân loại.
    /// </summary>
    [Fact]
    public void Ba_bo_dung_thu_cong_van_mac_dinh_tat()
    {
        var o = new PipelineOptions();

        Assert.False(o.StyleDeclaredOutline);
        Assert.False(o.NumberingDeclaredOutline);
        Assert.False(o.AdministrativeDeclaredOutline);
    }
}
