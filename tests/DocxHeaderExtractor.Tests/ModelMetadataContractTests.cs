using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Hợp đồng của thứ MÌNH GỬI cho mô hình, và của thứ mình KHÔNG được sửa của người gọi.
/// Hai bất biến này trước đây chỉ được xác nhận gián tiếp qua bench — tức chúng có thể vỡ mà
/// bench vẫn xanh nếu bộ bench không có tài liệu chạm tới chúng.
/// </summary>
public sealed class ModelMetadataContractTests
{
    /// <summary>
    /// ĐO ĐƯỢC trên một báo cáo thật (chuyển từ PDF): styles.xml khai
    /// <c>Heading1 → w:outlineLvl = 1</c>, lệch quy ước 0-based, và cả 73/73 đoạn mang style Heading
    /// đều lệch. Khi đó metadata chở <c>outlineLevel:1</c> cạnh <c>guessedLevel:1</c> còn system
    /// prompt dạy "outlineLevel: 0 = cấp 1" — hai trường mâu thuẫn cộng một luật sai. Mô hình yếu
    /// chọn outlineLevel và đẩy MỌI mục cấp 1 xuống cấp 2 (6/10 lỗi cấp của Haiku là ca này).
    /// </summary>
    [Fact]
    public void Doan_co_style_Heading_built_in_khong_gui_kem_outlineLevel_tho()
    {
        var view = View(new SlimParagraph
        {
            Index = 0, StableId = "p[0]", Text = "LỜI CAM ĐOAN",
            StyleId = "Heading1", StyleName = "Heading 1",
            OutlineLevel = 1,          // tài liệu khai sai quy ước
            GuessedLevel = 1,          // pipeline suy đúng từ tên style
            HasBuiltInHeadingStyle = true,
            Role = ParagraphRole.StyledHeading,
        });

        Assert.DoesNotContain("\"outlineLevel\"", view);
        Assert.Contains("\"guessedLevel\":1", view);
    }

    /// <summary>
    /// Đoạn KHÔNG có style built-in thì outlineLvl là nguồn DUY NHẤT nói về cấp — vẫn phải gửi.
    /// Bỏ luôn cả hai nhánh là đánh đổi một lỗi lấy một lỗi khác.
    /// </summary>
    [Fact]
    public void Doan_khong_co_style_built_in_van_gui_outlineLevel()
    {
        var view = View(new SlimParagraph
        {
            Index = 0, StableId = "p[0]", Text = "MỘT ĐỀ MỤC ĐỊNH DẠNG TAY",
            StyleId = "Normal", StyleName = "Normal",
            OutlineLevel = 2,
            Role = ParagraphRole.HeadingCandidate,
        });

        Assert.Contains("\"outlineLevel\":2", view);
    }

    /// <summary>
    /// <c>LoadAsync</c> áp model profile lên BẢN SAO. Áp lên chính đối tượng người gọi thì một hàm
    /// tên "Load" âm thầm sửa context/ngân sách khối mà caller đang giữ và dùng lại cho lượt sau.
    /// Test khoá <see cref="LlamaOptions.Clone"/> — vế mà LoadAsync dựa vào.
    /// </summary>
    [Fact]
    public void Clone_khong_cho_ApplyRecommendedModelProfile_ghi_nguoc_len_ban_goc()
    {
        var original = new LlamaOptions { ModelPath = "models/Qwen2.5-7B-Instruct-Q4_K_M.gguf" };
        var beforeContext = original.ContextSize;
        var beforeBudget = original.ChunkTokenBudget;

        var copy = original.Clone();
        copy.ApplyRecommendedModelProfile(new Core.Chunking.ChunkingOptions());

        Assert.Equal(beforeContext, original.ContextSize);
        Assert.Equal(beforeBudget, original.ChunkTokenBudget);
        Assert.NotEqual(beforeContext, copy.ContextSize);   // bản sao PHẢI đổi, nếu không test vô nghĩa
    }

    private static string View(SlimParagraph paragraph)
    {
        var doc = new SlimDocument
        {
            FileName = "x.docx", SourcePath = "x.docx", Paragraphs = [paragraph],
        }.Build();
        var lines = NeutralDocumentViewSerializer.BuildLines(doc, new ExtractionOptions(), reviewIndexes: null);
        return NeutralDocumentViewSerializer.WrapChunk(lines, 1, 1);
    }
}
