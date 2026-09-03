using DocxHeaderExtractor.DocumentProcessing.Chunking;
using DocxHeaderExtractor.Cli;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Ngân sách 2200 token/khối là của bản local bị giới hạn VRAM. Nhánh OpenRouter đã nâng lên 5K từ
/// lâu, nhánh LM Studio thì không — nên mọi lượt LM Studio chạy với ngân sách của bản local và tài
/// liệu bị xé vụn: đo trên tài liệu thật là 13 ứng viên → 27 lượt RPC.
/// <para>
/// Sau khi tách <c>ChunkingOptions</c> ra khỏi <c>LocalModelOptions</c>, các test này khoá thêm một điều:
/// backend RPC KHÔNG được ghi gì vào cấu hình của backend GGUF cục bộ. Trước đây nó phải đặt
/// <c>Llama.ContextSize = 8192</c> — một giá trị giả, mô tả context của llama.cpp — chỉ để phép
/// chia khối ra đúng.
/// </para>
/// </summary>
public sealed class RemoteChunkProfileTests
{
    private const uint LocalDefaultContext = 4096;

    [Theory]
    [InlineData("--lmstudio")]
    [InlineData("--openrouter")]
    public void Backend_rpc_dung_ngan_sach_chunk_cua_rpc(string backendFlag)
    {
        var o = CommandLineOptions.Parse(["a.docx", backendFlag]);

        Assert.Equal(5000, o.Pipeline.Chunking.TokenBudget);
        // Không chạm vào context của backend cục bộ: nó không tham gia lượt chạy này.
        Assert.Equal(LocalDefaultContext, o.Pipeline.LocalModel.ContextSize);
    }

    [Fact]
    public void Backend_local_giu_nguyen_ngan_sach_local()
    {
        var o = CommandLineOptions.Parse(["a.docx"]);

        Assert.Equal(InferenceBackend.Local, o.Pipeline.Backend);
        Assert.Equal(2200, o.Pipeline.Chunking.TokenBudget);
    }

    [Theory]
    // Người dùng gõ tay thì thắng, dù cờ đứng trước hay sau cờ backend.
    [InlineData("--chunk-tokens", "3000", "--lmstudio")]
    [InlineData("--lmstudio", "--chunk-tokens", "3000")]
    public void Override_tuong_minh_thang_bat_ke_thu_tu_co(params string[] flags)
    {
        var o = CommandLineOptions.Parse(["a.docx", .. flags]);

        Assert.Equal(3000, o.Pipeline.Chunking.TokenBudget);
    }

    [Fact]
    public void Ctx_go_tay_van_thuoc_ve_backend_cuc_bo()
    {
        var o = CommandLineOptions.Parse(["a.docx", "--ctx", "16384", "--lmstudio"]);

        Assert.Equal(16384u, o.Pipeline.LocalModel.ContextSize);
        Assert.Equal(5000, o.Pipeline.Chunking.TokenBudget);
    }

    [Fact]
    public void Profile_model_cuc_bo_phai_ap_len_ngan_sach_that_cua_pipeline()
    {
        // Hồi quy đã xảy ra thật: sau khi tách ChunkingOptions, profile model được áp lên một bản
        // sao TẠM bên trong LoadAsync nên cú nâng "qwen thì 2200 → 5000" không tới được pipeline.
        // Log lộ ra ngay ở "ngân sách … token thật/khối": 5000 tụt về 2200, tức tài liệu bị xé
        // hơn gấp đôi số khối. Test này khoá con đường đó bằng cách kiểm chính đối tượng pipeline đọc.
        var o = CommandLineOptions.Parse(["a.docx", "-m", "models/Qwen2.5-7B-Instruct-Q4_K_M.gguf"]);
        Assert.Equal(2200, o.Pipeline.Chunking.TokenBudget);

        o.Pipeline.PrepareLocalModelProfile();

        Assert.Equal(5000, o.Pipeline.Chunking.TokenBudget);
        Assert.Equal(8192u, o.Pipeline.LocalModel.ContextSize);
    }

    [Fact]
    public void Profile_model_cuc_bo_khong_dung_toi_backend_rpc()
    {
        var o = CommandLineOptions.Parse(["a.docx", "--lmstudio", "-m", "models/Qwen2.5-7B-Instruct-Q4_K_M.gguf"]);

        o.Pipeline.PrepareLocalModelProfile();

        // Ngân sách RPC do nhánh backend quyết, không bị profile của file .gguf trên đĩa chen vào.
        Assert.Equal(5000, o.Pipeline.Chunking.TokenBudget);
        Assert.Equal(LocalDefaultContext, o.Pipeline.LocalModel.ContextSize);
    }

    [Theory]
    // Tái tạo đúng profile đã đo: Qwen 7B, context 8192 → ngân sách 5000.
    [InlineData(8192, 768, 5000)]
    // Context gấp đôi thì ngân sách gấp đôi theo tỉ lệ đã đo — KHÔNG dùng kịch 14016, vì khối
    // phình ra đo được là chậm hơn ~60% (attention bậc hai theo độ dài prompt).
    [InlineData(16384, 768, 10000)]
    // Context nhỏ thì ràng buộc cứng của cửa sổ thắng, ngân sách co lại cho vừa.
    [InlineData(4096, 768, 1728)]
    public void Ngan_sach_uoc_luong_theo_context_backend_khai_bao(int context, int maxOutput, int expected)
    {
        Assert.Equal(expected, ChunkingOptions.DeriveTokenBudget(context, maxOutput, 1600));
    }

    [Fact]
    public void Nguoi_dung_dat_tay_thi_khong_bi_suy_lai()
    {
        var o = CommandLineOptions.Parse(["a.docx", "--lmstudio", "--chunk-tokens", "3000"]);

        Assert.True(o.Pipeline.Chunking.TokenBudgetExplicit);
        Assert.Equal(3000, o.Pipeline.Chunking.TokenBudget);
    }

    [Fact]
    public void Khong_dat_tay_thi_de_ngo_cho_pipeline_suy()
    {
        var o = CommandLineOptions.Parse(["a.docx", "--lmstudio"]);

        Assert.False(o.Pipeline.Chunking.TokenBudgetExplicit);
    }

    [Fact]
    public void Chunking_khong_con_nam_trong_cau_hinh_backend()
    {
        // Chốt bằng phản chiếu: chừng nào LocalModelOptions còn ba trường này thì vẫn có hai nguồn sự
        // thật cho cùng một quyết định, và một trong hai sẽ lặng lẽ đi lệch.
        var llama = typeof(DocxHeaderExtractor.DocumentProcessing.Inference.LocalModelOptions);
        Assert.Null(llama.GetProperty("MaxCandidatesPerChunk"));
        Assert.Null(llama.GetProperty("ChunkOverlap"));
        Assert.Null(llama.GetMethod("UseRemoteChunkProfile"));
    }
}
