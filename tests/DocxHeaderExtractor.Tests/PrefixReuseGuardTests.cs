using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Tái dùng prefill phải TỰ TẮT với mô hình có lớp trạng thái hồi quy.
/// <para>
/// ĐO ĐƯỢC (§35): cấu hình mặc định của Web (ctx 8192, 5000 token/khối, 30 khối) với Qwen3.5-9B
/// chết ở khối ĐẦU TIÊN — <c>llama_decode failed: 'NoKvSlot'</c>, 0/30 khối. Cùng mọi tham số, chỉ
/// tắt tái dùng: 30/30 khối chạy hết. Đường CLI không bao giờ chạm phải vì mọi phép đo đều truyền
/// <c>--no-reuse-prefix</c>.
/// </para>
/// <para>
/// Kiểm trên hàm thuần nhận metadata, nên không phải nạp 5,3 GB trọng số — nếu không thì đúng cái
/// nhánh này sẽ không bao giờ có test, y như nhánh "đã yêu cầu GPU nhưng rơi về CPU" ở §7.
/// </para>
/// </summary>
public class PrefixReuseGuardTests
{
    /// <summary>Qwen3.5: có <c>qwen35.ssm.*</c> ⇒ phải từ chối, và nói rõ vì sao.</summary>
    [Fact]
    public void Mo_hinh_khai_lop_ssm_thi_tu_choi_tai_dung_prefill()
    {
        var reason = LlamaHeaderExtractor.RecurrentStateReason(new Dictionary<string, string>
        {
            ["general.architecture"] = "qwen35",
            ["qwen35.block_count"] = "48",
            ["qwen35.ssm.state_size"] = "128",
        });

        Assert.NotNull(reason);
        Assert.Contains("qwen35", reason);
        Assert.Contains("ssm", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Qwen2.5: attention thuần ⇒ giữ nguyên tái dùng, vì nó thật sự chạy được.</summary>
    [Fact]
    public void Mo_hinh_attention_thuan_thi_giu_nguyen_tai_dung()
    {
        var reason = LlamaHeaderExtractor.RecurrentStateReason(new Dictionary<string, string>
        {
            ["general.architecture"] = "qwen2",
            ["qwen2.block_count"] = "28",
            ["qwen2.attention.head_count"] = "28",
        });

        Assert.Null(reason);
    }

    /// <summary>
    /// Luật bám vào KIẾN TRÚC đọc từ GGUF, không vào tên họ mô hình — nếu không thì Mamba/Jamba/
    /// RWKV và mọi kiến trúc lai sau này lại vấp đúng lỗi đó một lần nữa.
    /// </summary>
    [Fact]
    public void Luat_khong_gan_voi_rieng_qwen()
    {
        var reason = LlamaHeaderExtractor.RecurrentStateReason(new Dictionary<string, string>
        {
            ["general.architecture"] = "jamba",
            ["jamba.ssm.conv_kernel"] = "4",
        });

        Assert.NotNull(reason);
        Assert.Contains("jamba", reason);
    }

    /// <summary>Không có metadata thì không được đoán bừa là hỏng.</summary>
    [Fact]
    public void Khong_co_metadata_thi_khong_tu_choi()
    {
        Assert.Null(LlamaHeaderExtractor.RecurrentStateReason(new Dictionary<string, string>()));
    }
}
