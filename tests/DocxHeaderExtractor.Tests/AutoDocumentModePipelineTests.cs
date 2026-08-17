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

    [Fact]
    public async Task Auto_typed_numbering_giu_cap_theo_do_sau_marker_khi_no_llm()
    {
        var paragraphs = new List<string>();
        for (var chapter = 1; chapter <= 5; chapter++)
        {
            paragraphs.Add($"{chapter} Chapter {chapter}");
            paragraphs.Add($"{chapter}.1 \u2022 First typed section {chapter} Body text that belongs to the converted page.");
            paragraphs.Add($"{chapter}.2 \u2022 Second typed section {chapter} Body text that belongs to the converted page.");
        }
        paragraphs.Add(Body);

        var path = Docx([.. paragraphs]);

        var options = new PipelineOptions { DisableLlm = true };
        options.Extraction.SplitMergedParagraphs = true;
        using var pipeline = new HeaderExtractionPipeline(options);
        var outline = await pipeline.RunAsync(path);

        Assert.Equal("auto:typed-numbering", outline.DeterministicRoute);
        Assert.Contains(outline.Headings, h => h.Text.StartsWith("1.1", StringComparison.Ordinal) && h.Level == 2);
        Assert.Contains(outline.Headings, h => h.Text.StartsWith("5.2", StringComparison.Ordinal) && h.Level == 2);
    }

    [Fact]
    public async Task Toc_text_day_thang_route_typed_numbering_khi_mat_xml_signal()
    {
        // Cùng khung số gõ tay như test TypedNumbering ở trên (đẩy DocumentMode về TypedNumbering),
        // nhưng thêm một khối "TABLE OF CONTENT" PART/Section dày ở đầu — mô phỏng PDF→DOCX mất hết
        // outlineLvl/pStyle/numPr (§ PartSectionOutline.BuildFromTextToc). Route phải đổi từ
        // auto:typed-numbering sang auto:part-section-text-toc, và title lấy từ TOC (không bị
        // InlineHeadingSplitter cắt cụt theo đoạn body chỉ còn "PART I" trơ trọi).
        var paragraphs = new List<string>
        {
            "TABLE OF CONTENT PART I – GENERAL PROVISIONS .......... 1 " +
            "Section 1. Alpha .......... 1 Section 2. Beta .......... 2 " +
            "Section 3. Gamma .......... 3 Section 4. Delta .......... 4 " +
            "Section 5. Epsilon .......... 5",
            "Running header 1 PART I – GENERAL PROVISIONS",
            "Section 1. Alpha 1",
            "Section 2. Beta 2",
            "Section 3. Gamma 3",
            "Section 4. Delta 4",
            "Section 5. Epsilon 5",
        };
        for (var chapter = 1; chapter <= 5; chapter++)
        {
            paragraphs.Add($"{chapter} Chapter {chapter}");
            paragraphs.Add($"{chapter}.1 • First typed section {chapter} Body text that belongs to the converted page.");
            paragraphs.Add($"{chapter}.2 • Second typed section {chapter} Body text that belongs to the converted page.");
        }
        paragraphs.Add(Body);

        var path = Docx([.. paragraphs]);

        var options = new PipelineOptions { DisableLlm = true };
        options.Extraction.SplitMergedParagraphs = true;
        using var pipeline = new HeaderExtractionPipeline(options);
        var outline = await pipeline.RunAsync(path);

        Assert.Equal(DocumentMode.TypedNumbering, outline.DocumentMode?.Mode);
        Assert.Equal("auto:part-section-text-toc", outline.DeterministicRoute);
        Assert.Contains(outline.Headings, h =>
            h.Level == 1 && h.Text == "PART I - GENERAL PROVISIONS");
        Assert.Contains(outline.Headings, h =>
            h.Level == 2 && h.Text == "Section 1. Alpha");
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
