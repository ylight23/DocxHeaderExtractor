using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.DocumentProcessing.Source.Docx;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;
using DocxHeaderExtractor.HeadingAgent;
using A = DocumentFormat.OpenXml.Drawing;
using Dw = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using Pic = DocumentFormat.OpenXml.Drawing.Pictures;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Source integrity and the OCR preflight. A DOCX whose pages are images is reported as SCANNED_IMAGE_ONLY instead of being read as its one
/// signature paragraph; the preflight writes immutable OCR evidence with two views and their provenance, and the comparer only says where the
/// views read a line differently.
/// </summary>
public sealed class ScanSourceTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-scan-" + Guid.NewGuid().ToString("N"));

    public ScanSourceTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A 1x1 PNG.</summary>
    static readonly byte[] Png = TestPng.Grey(1, 1);

    /// <summary>A document of <paramref name="pages"/> paragraphs that each hold one image of the page's size, and the given text paragraphs.</summary>
    string Build(string name, int pages, params string[] texts)
    {
        var path = Path.Combine(dir, name + ".docx");
        using var doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        foreach (var t in texts) body.Append(new Paragraph(new Run(new Text(t))));
        for (var k = 0; k < pages; k++)
        {
            var part = main.AddImagePart(ImagePartType.Png);
            using (var s = new MemoryStream(Png)) part.FeedData(s);
            var rid = main.GetIdOfPart(part);
            body.Append(new Paragraph(new Run(new Drawing(new Dw.Inline(
                new Dw.Extent { Cx = 7_000_000L, Cy = 9_500_000L },
                new Dw.DocProperties { Id = (uint)(k + 1), Name = "page" + k },
                new A.Graphic(new A.GraphicData(new Pic.Picture(
                    new Pic.NonVisualPictureProperties(new Pic.NonVisualDrawingProperties { Id = (uint)k, Name = "p" }, new Pic.NonVisualPictureDrawingProperties()),
                    new Pic.BlipFill(new A.Blip { Embed = rid }, new A.Stretch(new A.FillRectangle())),
                    new Pic.ShapeProperties())) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))))));
        }
        body.Append(new SectionProperties(new PageSize { Width = 11906U, Height = 16838U }));
        main.Document = new Document(body);
        return path;
    }

    // ---------------- integrity ----------------

    [Fact]
    public void Pages_that_are_images_with_only_a_signature_block_are_scanned_image_only()
    {
        var integrity = DocxSourceIntegrity.Read(Build("scan", 5, "Ký bởi: Cổng Thông tin điện tử Chính phủ"));

        Assert.Equal(DocxSourceKind.ScannedImageOnly, integrity.Kind);
        Assert.Equal(5, integrity.PageImages);
        Assert.Equal(5, integrity.BodyImages.Count(i => i.CoversPage));
        Assert.Equal(Enumerable.Range(0, 5), integrity.BodyImages.Select(i => i.Ordinal));
    }

    [Fact]
    public void Text_with_a_page_sized_cover_is_mixed_and_text_alone_is_native()
    {
        var text = string.Concat(Enumerable.Repeat("A paragraph of real body text that the OOXML holds as text. ", 20));
        Assert.Equal(DocxSourceKind.Mixed, DocxSourceIntegrity.Read(Build("cover", 1, text)).Kind);
        Assert.Equal(DocxSourceKind.NativeText, DocxSourceIntegrity.Read(Build("plain", 0, text)).Kind);
    }

    [Fact]
    public void The_agent_source_refuses_a_scanned_document_with_a_code_that_says_so()
    {
        var source = DocxAgentSource.Load(Build("scan", 3, "Ký bởi: Cổng Thông tin điện tử Chính phủ"), null, out var failure);

        Assert.Null(source);
        Assert.Equal("SCANNED_IMAGE_ONLY", failure);
    }

    [Theory]
    [InlineData("todo10_8/generated-docx/01_phap_quy/023_ND_23-2015_Chung_thuc.docx", DocxSourceKind.ScannedImageOnly)]
    [InlineData("todo10_8/generated-docx/01_phap_quy/011_Luat_An_toan_thong_tin_mang_86-2015-QH13.docx", DocxSourceKind.ScannedImageOnly)]
    [InlineData("todo10_8/generated-docx/03_tai_chinh_ke_toan/046_IBRD_Financial_Statements_September_2024.docx", DocxSourceKind.Mixed)]
    [InlineData("samples/mau.docx", DocxSourceKind.NativeText)]
    public void Corpus_documents_are_classified_by_their_measured_content(string relative, DocxSourceKind expected)
    {
        var path = TestRepository.Path(relative);
        if (!File.Exists(path)) return;

        Assert.Equal(expected, DocxSourceIntegrity.Read(path).Kind);
    }

    // ---------------- OCR parsing and measurements (no engine needed) ----------------

    const string Tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"
        + "5\t1\t1\t1\t1\t1\t150\t300\t90\t60\t96.5\tĐiều\n"
        + "5\t1\t1\t1\t1\t2\t270\t300\t30\t60\t95.0\t5.\n"
        + "5\t1\t1\t1\t2\t1\t150\t420\t60\t45\t40.0\tTham\n"
        + "5\t1\t1\t1\t2\t2\t225\t420\t15\t45\t-1\t \n";

    [Fact]
    public void A_tsv_becomes_lines_of_words_in_the_original_images_pixels()
    {
        var lines = TesseractOcr.ParseTsv(Tsv, scale: 1.5);

        Assert.Equal(2, lines.Count);
        Assert.Equal(["Điều", "5."], lines[0].Words.Select(w => w.Text));
        Assert.Equal(new PixelBox(100, 200, 60, 40), lines[0].Words[0].Box);
        Assert.Single(lines[1].Words);   // the empty word is left out
    }

    [Fact]
    public void A_line_that_another_line_overlaps_is_measured_as_such()
    {
        var words = (int top, int h, string t) => (IReadOnlyList<OcrWord>)[new OcrWord(t, new PixelBox(380, top, 600, h), 90)];
        var lines = ScanOcrPreflight.Lines(3, "ocr-original", [("1.1.1", words(152, 32, "h À ` là")), ("1.1.2", words(162, 42, "Điều 4.")), ("1.1.3", words(230, 59, "1. Cơ quan")), ("1.1.4", words(280, 48, "cấp bản sao"))]);

        Assert.Equal([true, true, false, false], lines.Select(l => l.OverlapsOtherLine));
        Assert.Equal("P003-ocr-original-L002", lines[1].Id);
    }

    [Fact]
    public void Two_readings_of_a_line_are_reported_where_they_differ_and_never_resolved()
    {
        var diffs = OcrViewComparer.Differences("Điều 4. Tham quyền và trách nhiệm cap bản sao".Split(' '), "Điều 4. Tham quyên và trách nhiệm cap ban sao".Split(' '));

        Assert.Equal(2, diffs.Count);
        Assert.Equal(("quyền", "quyên"), (diffs[0].Original, diffs[0].Alternative));
        Assert.Equal(("bản", "ban"), (diffs[1].Original, diffs[1].Alternative));
        Assert.All(diffs, d => Assert.Equal(VerificationStatus.Unverified, d.Status));
        Assert.Empty(OcrViewComparer.Differences(["a", "b"], ["a", "b"]));
    }

    [Fact]
    public void The_size_of_a_png_is_read_from_its_header()
    {
        Assert.Equal((1, 1), TesseractOcr.ImageSize(Png));
        Assert.Equal((0, 0), TesseractOcr.ImageSize([1, 2, 3]));
    }

    // ---------------- the preflight with the engine, when the machine has it ----------------

    [Fact]
    public void The_preflight_writes_immutable_evidence_with_its_provenance_and_reads_it_back()
    {
        var path = TestRepository.Path("todo10_8/generated-docx/01_phap_quy/023_ND_23-2015_Chung_thuc.docx");
        if (TesseractOcr.FindTesseract() is null || !File.Exists(path) || !TesseractOcr.Languages(TesseractOcr.FindTesseract()!).Contains("vie")) return;
        // A folder name with Vietnamese letters and spaces: the engine gets the image on its input, never this path.
        var store = Path.Combine(dir, "Thư mục của Tuấn", "store");
        var preflight = new ScanOcrPreflight(store);
        var options = new ScanOcrPreflight.Options(MaxPages: 2, Parallelism: 2);

        var first = preflight.Run(path, options);

        Assert.Equal(ScanOcrEvidence.CurrentVersion, first.Version);
        Assert.Equal(nameof(DocxSourceKind.ScannedImageOnly), first.SourceKind);
        Assert.Equal(2, first.PageCount);
        var original = first.Views.Single(v => v.Spec.ViewId == ScanOcrPreflight.OriginalView);
        Assert.Equal(EvidenceOrigin.OcrDerived, original.Origin);
        Assert.StartsWith("tesseract", original.Spec.EngineVersion, StringComparison.Ordinal);
        Assert.All(original.Pages, p => Assert.Equal(64, p.ImageSha256.Length));
        Assert.NotEmpty(original.Pages[0].Lines);
        // Either both views were made, or the one that could not be made is recorded with its reason.
        Assert.Equal(2, first.Views.Count + first.Gaps.Count);
        var entry = Directory.GetDirectories(Directory.GetDirectories(store).Single(d => !Path.GetFileName(d).StartsWith('.'))).Single();
        var written = File.GetLastWriteTimeUtc(Path.Combine(entry, "evidence.json"));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(entry, "pages")).Length);

        var second = preflight.Run(path, options);

        Assert.Equal(first.ParametersSha256, second.ParametersSha256);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(entry, "evidence.json")));
        Assert.Equal(original.Pages[0].Lines.Select(l => l.Text), second.Views[0].Pages[0].Lines.Select(l => l.Text));
    }
}
