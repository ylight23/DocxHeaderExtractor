using DocxHeaderExtractor.Cli;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Ngân sách 2200 token/khối là của bản local bị giới hạn VRAM. Nhánh OpenRouter đã nâng lên
/// 8K/5K từ lâu, nhánh LM Studio thì không — nên mọi lượt LM Studio chạy với ngân sách của bản
/// local và tài liệu bị xé vụn: đo trên tài liệu thật là 13 ứng viên → 27 lượt RPC.
/// </summary>
public sealed class RemoteChunkProfileTests
{
    [Theory]
    [InlineData("--lmstudio")]
    [InlineData("--openrouter")]
    public void Backend_rpc_dung_ngan_sach_chunk_cua_rpc(string backendFlag)
    {
        var o = CommandLineOptions.Parse(["a.docx", backendFlag]);

        Assert.Equal(5000, o.Pipeline.Llama.ChunkTokenBudget);
        Assert.Equal(8192u, o.Pipeline.Llama.ContextSize);
    }

    [Fact]
    public void Backend_local_giu_nguyen_ngan_sach_local()
    {
        var o = CommandLineOptions.Parse(["a.docx"]);

        Assert.Equal(InferenceBackend.Local, o.Pipeline.Backend);
        Assert.Equal(2200, o.Pipeline.Llama.ChunkTokenBudget);
    }

    [Theory]
    // Người dùng gõ tay thì thắng, dù cờ đứng trước hay sau cờ backend.
    [InlineData("--chunk-tokens", "3000", "--lmstudio")]
    [InlineData("--lmstudio", "--chunk-tokens", "3000")]
    public void Override_tuong_minh_thang_bat_ke_thu_tu_co(params string[] flags)
    {
        var o = CommandLineOptions.Parse(["a.docx", .. flags]);

        Assert.Equal(3000, o.Pipeline.Llama.ChunkTokenBudget);
    }

    [Fact]
    public void Ctx_go_tay_khong_bi_profile_rpc_de_len()
    {
        var o = CommandLineOptions.Parse(["a.docx", "--ctx", "16384", "--lmstudio"]);

        Assert.Equal(16384u, o.Pipeline.Llama.ContextSize);
        Assert.Equal(5000, o.Pipeline.Llama.ChunkTokenBudget);
    }
}
