using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Cờ <c>--split-merged</c> nối <see cref="ParagraphHeadingSplitter"/> vào pipeline. Nó tạo ra con
/// số 3.712 → 6.357 mục trên corpus 95 file (handoff §45.3) nhưng trước đây KHÔNG có test nào —
/// chỉ bộ cắt thuần được test, còn phần nối thì không.
/// </summary>
public class SplitMergedParagraphsTests : IDisposable
{
    private readonly List<string> _paths = [];

    public void Dispose()
    {
        foreach (var p in _paths)
        {
            try { File.Delete(p); } catch (IOException) { }
        }
        GC.SuppressFinalize(this);
    }

    private string Docx(params string[] paragraphs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-split-{Guid.NewGuid():N}.docx");
        _paths.Add(path);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body(paragraphs.Select(t =>
            new Paragraph(new Run(new Text(t) { Space = SpaceProcessingModeValues.Preserve })))));
        main.Document.Save();
        return path;
    }

    private static async Task<DocumentOutline> RunAsync(string path, bool split)
    {
        var options = new PipelineOptions { DisableLlm = true };
        options.Extraction.SplitMergedParagraphs = split;
        using var pipeline = new AuthorityExtractionPipeline(options);
        return await pipeline.RunAsync(path);
    }

    /// <summary>
    /// MẶC ĐỊNH TẮT là một lời hứa, không phải chi tiết cài đặt: cờ này phá giả định "mỗi đoạn
    /// nhiều nhất một mục" mà mọi đáp án trong <c>keys/</c> đang dựa vào (TODO mục 10).
    /// </summary>
    [Fact]
    public void Mac_dinh_phai_tat()
    {
        Assert.False(new ExtractionOptions().SplitMergedParagraphs);
        Assert.False(new PipelineOptions().Extraction.SplitMergedParagraphs);
    }

    [Fact]
    public async Task Tai_lieu_Word_goc_khong_bi_bam_vun_khi_bat_co()
    {
        var path = Docx(
            "Điều 1. Phạm vi điều chỉnh",
            "Điều 2. Đối tượng áp dụng",
            "Nội dung thân bài trình bày chi tiết các bước thực hiện của quy trình và trách nhiệm " +
            "của từng bộ phận có liên quan trong quá trình phối hợp giữa các đơn vị.");

        var tat = await RunAsync(path, split: false);
        var bat = await RunAsync(path, split: true);

        Assert.Equal(tat.Headings.Count, bat.Headings.Count);
    }

}
