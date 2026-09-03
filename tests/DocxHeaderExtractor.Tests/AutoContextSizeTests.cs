using DocxHeaderExtractor.DocumentProcessing.Inference;

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
        Assert.Equal(32768u, LocalModelOptions.MaxAutoContextSize);
        Assert.True(LocalModelOptions.MaxAutoContextSize
                    < LlamaHeaderExtractor.DeclaredContextLength(Meta("qwen35", "262144")));
    }

    /// <summary>Mặc định BẬT; truyền <c>--ctx</c> tường minh thì CLI tắt nó đi.</summary>
    [Fact]
    public void Mac_dinh_bat()
    {
        Assert.True(new LocalModelOptions().AutoContextSize);
    }

    /// <summary>
    /// <b>Chữ ký cấu hình phải nói đúng con số đã dùng.</b> <c>LoadAsync</c> clone options rồi
    /// chỉnh trên bản clone, còn <c>PrecisionCalibrationProfile.ConfigurationFor</c> đọc bản GỐC —
    /// nên nếu không ghi lại, chữ ký ghi <c>ctx=4096</c> cho lượt chạy thật sự dùng 32768.
    /// <para>
    /// Test này ghim rằng <see cref="LocalModelOptions.Clone"/> KHÔNG chia sẻ trạng thái (nếu nó chia sẻ
    /// thì việc ghi lại là vô nghĩa và test dưới cũng vô nghĩa), và rằng chữ ký cấu hình đọc chính
    /// trường <c>ContextSize</c> — tức ghi lại vào bản gốc là cách duy nhất làm nó trung thực.
    /// </para>
    /// </summary>
    [Fact]
    public void Clone_khong_chia_se_trang_thai_nen_phai_ghi_lai()
    {
        var goc = new LocalModelOptions { ContextSize = 4096 };
        var ban = goc.Clone();

        ban.ContextSize = 32768;

        Assert.Equal(4096u, goc.ContextSize);      // clone độc lập -> ghi lại là bắt buộc
        Assert.Equal(32768u, ban.ContextSize);
    }

    /// <summary>Chữ ký cấu hình lấy ctx từ chính trường đó, nên ghi lại là đủ để nó trung thực.</summary>
    [Fact]
    public void Chu_ky_cau_hinh_doc_ctx_tu_LocalModelOptions()
    {
        var o = new DocxHeaderExtractor.DocumentProcessing.Pipeline.PipelineOptions();
        var provider = new DocxHeaderExtractor.Infrastructure.AI.InferenceProviderSelection();
        provider.LocalModel.ContextSize = 32768;

        Assert.Contains("ctx=32768",
            DocxHeaderExtractor.Eval.PrecisionCalibrationProfile.ConfigurationFor(o, provider),
            StringComparison.Ordinal);
    }
}
