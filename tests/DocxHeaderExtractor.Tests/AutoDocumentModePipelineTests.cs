using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class AutoDocumentModePipelineTests : IDisposable
{
    private readonly List<string> _paths = [];

    public void Dispose()
    {
        foreach (var path in _paths)
            try { File.Delete(path); } catch (IOException) { }
    }

    [Fact]
    public async Task Auto_mode_chon_route_phap_luat_khi_khong_goi_model()
    {
        var path = Docx(
            "Chương I QUY ĐỊNH CHUNG",
            "Điều 1. Phạm vi điều chỉnh1. Luật này quy định về quản lý.Điều 2. Đối tượng áp dụng",
            Body);

        var options = new PipelineOptions { DisableLlm = true };
        options.Extraction.SplitMergedParagraphs = true;
        using var pipeline = new HeaderExtractionPipeline(options);
        var outline = await pipeline.RunAsync(path);

        Assert.Equal(DocumentMode.VietnameseLegal, outline.DocumentMode?.Mode);
        Assert.Equal("auto:vietnamese-legal", outline.DeterministicRoute);
        Assert.Contains(outline.Headings, h => h.Text.StartsWith("Điều 1.", StringComparison.Ordinal));
        Assert.Contains(outline.Headings, h => h.Text.StartsWith("Chương I", StringComparison.Ordinal) && h.Level == 2);
        Assert.Contains(outline.Headings, h => h.Text.StartsWith("Điều 1.", StringComparison.Ordinal) && h.Level == 4);
        Assert.Contains(outline.Headings, h => h.Text.StartsWith("Điều 2.", StringComparison.Ordinal) && h.Level == 4);
        Assert.All(outline.Headings, h => Assert.Equal(HeadingSource.Structure, h.Source));
    }

    [Fact]
    public async Task Tat_auto_mode_thi_quay_ve_heuristic_no_llm()
    {
        var path = Docx(
            "1.1 Muc con thu nhat",
            "1.2 Muc con thu hai",
            Body);

        using var pipeline = new HeaderExtractionPipeline(new PipelineOptions
        {
            DisableLlm = true,
            AutoDetectDocumentMode = false,
        });
        var outline = await pipeline.RunAsync(path);

        Assert.Null(outline.DeterministicRoute);
        Assert.NotNull(outline.DocumentMode);
    }

    private string Docx(params string[] paragraphs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-auto-mode-{Guid.NewGuid():N}.docx");
        _paths.Add(path);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body(paragraphs.Select(t =>
            new Paragraph(new Run(new Text(t) { Space = SpaceProcessingModeValues.Preserve })))));
        main.Document.Save();
        return path;
    }

    private const string Body =
        "Phần thân bài trình bày phạm vi áp dụng và các bước thực hiện của quy trình này, kèm ví dụ " +
        "minh hoạ cho từng bước để người đọc đối chiếu khi triển khai thực tế, và nêu rõ trách nhiệm " +
        "của từng bộ phận trong quá trình phối hợp giữa các đơn vị có liên quan tới nhiệm vụ này.";
}
