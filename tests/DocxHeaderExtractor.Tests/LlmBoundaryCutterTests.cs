using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class LlmBoundaryCutterTests
{
    [Theory]
    [InlineData(DocumentMode.VietnameseLegal)]
    [InlineData(DocumentMode.TypedNumbering)]
    [InlineData(DocumentMode.FormatDriven)]
    public void IsSupported_dung_cho_ba_domain_da_do(DocumentMode mode) =>
        Assert.True(LlmBoundaryCutter.IsSupported(mode));

    [Theory]
    [InlineData(DocumentMode.VietnameseAdministrative)]
    [InlineData(DocumentMode.NumberingDriven)]
    [InlineData(DocumentMode.CustomStyle)]
    [InlineData(DocumentMode.SemanticOnly)]
    [InlineData(DocumentMode.OutlineLevelDriven)]
    [InlineData(DocumentMode.TocAnchored)]
    public void IsSupported_tra_ve_false_cho_domain_chua_do(DocumentMode mode) =>
        Assert.False(LlmBoundaryCutter.IsSupported(mode));

    [Fact]
    public async Task Cat_dung_khi_model_tra_ve_dung_prefix_cua_input()
    {
        var text = "Điều 44. Hiệu lực thi hành Luật này có hiệu lực thi hành từ ngày 01 tháng 01 năm 2019.";
        using var classifier = new ScriptedClassifier("Điều 44. Hiệu lực thi hành");

        var end = await LlmBoundaryCutter.TryCutAsync(classifier, DocumentMode.VietnameseLegal, text);

        Assert.NotNull(end);
        Assert.Equal("Điều 44. Hiệu lực thi hành", text[..end!.Value]);
    }

    [Fact]
    public async Task Bo_qua_tien_to_nhan_neu_model_lap_lai_ca_tu_khoa()
    {
        var text = "Global progress with ICP 2021 cycle Ms. Nada Hamadeh presented the report.";
        // Model đôi khi lặp lại cả từ khoá "Label:" trong câu trả lời dù prompt cấm — phải tách được.
        using var classifier = new ScriptedClassifier("Label: Global progress with ICP 2021 cycle");

        var end = await LlmBoundaryCutter.TryCutAsync(classifier, DocumentMode.FormatDriven, text);

        Assert.NotNull(end);
        Assert.Equal("Global progress with ICP 2021 cycle", text[..end!.Value]);
    }

    [Fact]
    public async Task Bo_qua_dau_ngoac_kep_bao_quanh_cau_tra_loi()
    {
        var text = "3.2. Updating Stored Header Fields Caches are required to update stored fields.";
        using var classifier = new ScriptedClassifier("\"3.2. Updating Stored Header Fields\"");

        var end = await LlmBoundaryCutter.TryCutAsync(classifier, DocumentMode.TypedNumbering, text);

        Assert.NotNull(end);
        Assert.Equal("3.2. Updating Stored Header Fields", text[..end!.Value]);
    }

    [Fact]
    public async Task Tu_choi_khi_cau_tra_loi_khong_phai_prefix_cua_input()
    {
        var text = "Điều 44. Hiệu lực thi hành Luật này có hiệu lực thi hành từ ngày 01 tháng 01 năm 2019.";
        // Model "sửa lại" chính tả hoặc bịa thêm chữ — không phải nguyên văn đầu input.
        using var classifier = new ScriptedClassifier("Điều 44: Hiệu lực thi hành");

        var end = await LlmBoundaryCutter.TryCutAsync(classifier, DocumentMode.VietnameseLegal, text);

        Assert.Null(end);
    }

    [Fact]
    public async Task Tu_choi_khi_domain_chua_co_bang_do()
    {
        using var classifier = new ScriptedClassifier("bất kỳ");

        var end = await LlmBoundaryCutter.TryCutAsync(
            classifier, DocumentMode.VietnameseAdministrative, "1. Mục nào đó nội dung...");

        Assert.Null(end);
        Assert.False(classifier.WasCalled);
    }

    [Fact]
    public async Task Tu_choi_khi_backend_nem_loi()
    {
        using var classifier = new ThrowingClassifier();

        var end = await LlmBoundaryCutter.TryCutAsync(
            classifier, DocumentMode.VietnameseLegal, "Điều 1. Something dài dòng.");

        Assert.Null(end);
    }

    private sealed class ScriptedClassifier(string response) : IHeaderClassifier
    {
        public bool WasCalled { get; private set; }

        public string ModelName => "test/boundary";
        public int ContextSize => 4096;
        public string RuntimeDescription => "scripted";
        public int SharedPrefixTokens => 0;

        public Task<ChunkResult> ClassifyAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChunkResult> CritiqueAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChunkResult> ClassifyHierarchyAsync(
            IReadOnlyList<HierarchyItem> context,
            IReadOnlyList<HierarchyItem> headings,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> BoundaryCutAsync(
            string systemPrompt, string userMessage, CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(response);
        }

        public void Dispose() { }
    }

    private sealed class ThrowingClassifier : IHeaderClassifier
    {
        public string ModelName => "test/throwing";
        public int ContextSize => 4096;
        public string RuntimeDescription => "throwing";
        public int SharedPrefixTokens => 0;

        public Task<ChunkResult> ClassifyAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChunkResult> CritiqueAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChunkResult> ClassifyHierarchyAsync(
            IReadOnlyList<HierarchyItem> context,
            IReadOnlyList<HierarchyItem> headings,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> BoundaryCutAsync(
            string systemPrompt, string userMessage, CancellationToken ct = default) =>
            throw new InvalidOperationException("backend lỗi (mô phỏng cho test)");

        public void Dispose() { }
    }
}
