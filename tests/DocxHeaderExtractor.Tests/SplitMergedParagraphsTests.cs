using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

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
        using var pipeline = new HeaderExtractionPipeline(options);
        return await pipeline.RunAsync(path);
    }

    /// <summary>
    /// Đoạn gộp kiểu bản chuyển PDF: cả trang trong một <c>w:p</c>, mốc bị dán vào từ trước.
    /// Không bật cờ thì không mục nào ra; bật thì ra đủ.
    /// </summary>
    [Fact]
    public async Task Bat_co_thi_cuu_duoc_tieu_de_trong_doan_gop()
    {
        var path = Docx(
            "Điều 4. Áp dụng Bộ luật dân sự1. Bộ luật này là luật chung điều chỉnh các quan hệ dân " +
            "sự.2. Luật khác có liên quan điều chỉnh quan hệ dân sự trong lĩnh vực cụ thể.Điều 5. " +
            "Áp dụng tập quán1. Tập quán là quy tắc xử sự có nội dung rõ ràng được thừa nhận.");

        var tat = await RunAsync(path, split: false);
        var bat = await RunAsync(path, split: true);

        Assert.DoesNotContain(tat.Headings, h => h.Text.StartsWith("Điều 4.", StringComparison.Ordinal));
        Assert.Contains(bat.Headings, h => h.Text == "Điều 4. Áp dụng Bộ luật dân sự");
        Assert.Contains(bat.Headings, h => h.Text == "Điều 5. Áp dụng tập quán");
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

    /// <summary>
    /// Chỉ số đoạn KHÔNG được đổi — tách đoạn thật sẽ dịch mọi chỉ số phía sau và làm hỏng toàn
    /// bộ đáp án trong <c>keys/</c>. Các lát cắt phải cùng trỏ về một <c>Index</c>.
    /// </summary>
    [Fact]
    public async Task Cac_lat_cat_dung_chung_chi_so_doan_goc()
    {
        var path = Docx(
            "Mở đầu tài liệu.",
            "Điều 4. Áp dụng Bộ luật dân sự1. Bộ luật này là luật chung điều chỉnh quan hệ dân " +
            "sự.2. Luật khác có liên quan thì áp dụng.Điều 5. Áp dụng tập quán1. Tập quán là quy " +
            "tắc xử sự có nội dung rõ ràng được thừa nhận và áp dụng lặp đi lặp lại.");

        var outline = await RunAsync(path, split: true);

        var tuDoanGop = outline.Headings
            .Where(h => h.Text.StartsWith("Điều ", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, tuDoanGop.Count);
        Assert.Single(tuDoanGop.Select(h => h.Index).Distinct());
    }

    /// <summary>
    /// Đoạn heading lành lặn KHÔNG được chẻ nhỏ khi bật cờ. Không có test này thì cờ có thể băm
    /// vụn tài liệu Word gốc mà bench 10 vẫn xanh vì bench không có đoạn gộp.
    /// </summary>
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

    /// <summary>
    /// <b>Crash tiềm ẩn.</b> Các lát cắt dùng chung một <c>Index</c> (đó là chủ đích, để đáp án
    /// trong <c>keys/</c> không hỏng), nhưng <c>StructuralHierarchyResolver.Apply</c> mở đầu bằng
    /// <c>ordered.ToDictionary(h =&gt; h.Index, …)</c> — trùng khoá thì <see cref="ArgumentException"/>.
    /// Từ §51 cờ suy cấp tất định MẶC ĐỊNH BẬT, nên hai tính năng này nay luôn gặp nhau.
    /// <para>
    /// Trên corpus 95 file điều đó chưa nổ vì mỗi đoạn gộp chỉ cho ra một lát đủ điều kiện làm
    /// tiêu đề (082: 300 mục / 300 chỉ số phân biệt). Nhưng "chưa nổ trên tập đang đo" không phải
    /// "không nổ" — test này gọi trực tiếp với hai lát cùng chỉ số.
    /// </para>
    /// </summary>
    [Fact]
    public void Hai_lat_cung_chi_so_khong_lam_sup_bo_suy_cap()
    {
        List<SlimParagraph> ps =
        [
            new() { Index = 0, Text = "Chương I QUY ĐỊNH CHUNG Điều 1. Phạm vi Điều 2. Đối tượng", FontSizePt = 13 },
            new() { Index = 1, Text = "Thân bài dài trình bày chi tiết các bước thực hiện của quy trình.", FontSizePt = 13 },
        ];
        var doc = new SlimDocument { FileName = "t.docx", SourcePath = "t.docx", Paragraphs = ps }.Build();

        List<HeadingRecord> headings =
        [
            new() { Index = 0, Level = 1, Text = "Điều 1. Phạm vi", Source = HeadingSource.Heuristic, Confidence = .5 },
            new() { Index = 0, Level = 1, Text = "Điều 2. Đối tượng", Source = HeadingSource.Heuristic, Confidence = .5 },
        ];

        var ex = Record.Exception(() => StructuralHierarchyResolver.Apply(headings, doc));

        Assert.Null(ex);
        Assert.Equal(2, headings.Count);
    }
}
