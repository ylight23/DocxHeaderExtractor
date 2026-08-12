using DocxHeaderExtractor.Core.Llm;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Context đọc từ chính GGUF thay vì từ một allowlist tên model. Mặc định cũ là 4096 cố định,
/// trong khi model đang dùng khai <c>qwen35.context_length = 262144</c> — nhỏ hơn 64 lần khả năng
/// thật, và model không có trong allowlist thì mắc kẹt ở đó vĩnh viễn.
/// </summary>
public class AutoContextSizeTests
{
    private static Dictionary<string, string> Meta(string arch, string? ctx) =>
        ctx is null
            ? new() { ["general.architecture"] = arch }
            : new() { ["general.architecture"] = arch, [$"{arch}.context_length"] = ctx };

    /// <summary>Không hardcode tên kiến trúc: ghép từ <c>general.architecture</c>.</summary>
    [Theory]
    [InlineData("qwen35", "262144", 262144u)]
    [InlineData("qwen2", "32768", 32768u)]
    [InlineData("llama", "8192", 8192u)]
    [InlineData("kien-truc-chua-tung-gap", "16384", 16384u)]
    public void Doc_duoc_context_cua_moi_kien_truc(string arch, string ctx, uint expected)
    {
        Assert.Equal(expected, LlamaHeaderExtractor.DeclaredContextLength(Meta(arch, ctx)));
    }

    /// <summary>Metadata thiếu hoặc hỏng thì trả null để nơi gọi giữ nguyên giá trị đang có.</summary>
    [Theory]
    [InlineData("qwen35", null)]
    [InlineData("qwen35", "0")]
    [InlineData("qwen35", "khong-phai-so")]
    public void Metadata_thieu_hoac_hong_thi_khong_doi_gi(string arch, string? ctx)
    {
        Assert.Null(LlamaHeaderExtractor.DeclaredContextLength(Meta(arch, ctx)));
    }

    [Fact]
    public void Thieu_general_architecture_thi_tra_null()
    {
        Assert.Null(LlamaHeaderExtractor.DeclaredContextLength(
            new Dictionary<string, string> { ["qwen35.context_length"] = "262144" }));
    }

    /// <summary>
    /// Trần là bắt buộc: 262.144 token KV-cache của model 9B vượt xa VRAM mọi máy đang dùng, và
    /// nạp thất bại thì tệ hơn context nhỏ. 32768 là cấu hình ĐÃ ĐO của dự án (handoff §0).
    /// </summary>
    [Fact]
    public void Tran_auto_context_bang_cau_hinh_da_do()
    {
        Assert.Equal(32768u, LlamaOptions.MaxAutoContextSize);
        Assert.True(LlamaOptions.MaxAutoContextSize
                    < LlamaHeaderExtractor.DeclaredContextLength(Meta("qwen35", "262144")));
    }

    /// <summary>Mặc định BẬT; truyền <c>--ctx</c> tường minh thì CLI tắt nó đi.</summary>
    [Fact]
    public void Mac_dinh_bat()
    {
        Assert.True(new LlamaOptions().AutoContextSize);
    }
}
