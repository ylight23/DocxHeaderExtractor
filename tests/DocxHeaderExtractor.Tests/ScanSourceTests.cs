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
        var written = File.GetLastWriteTimeUtc(Path.Combine(entry, "evidence.msgpack"));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(entry, "pages")).Length);

        var second = preflight.Run(path, options);

        Assert.Equal(first.ParametersSha256, second.ParametersSha256);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(entry, "evidence.msgpack")));
        Assert.Equal(original.Pages[0].Lines.Select(l => l.Text), second.Views[0].Pages[0].Lines.Select(l => l.Text));
    }

    // ---------------- the raster: one decode per page, crops in memory ----------------

    [Fact]
    public void A_png_is_read_as_grey_levels_whatever_its_colour_type_and_depth()
    {
        var grey = PngRaster.TryDecode(TestPng.WithBlack(20, 10, 200, (5, 2, 4, 3)))!;
        Assert.Equal((20, 10), (grey.Width, grey.Height));
        Assert.Equal((200, 0), (grey[0, 0], grey[6, 3]));
        Assert.Null(PngRaster.TryDecode([1, 2, 3]));

        var magick = TesseractOcr.FindImageMagick();
        if (magick is null) return;
        // The same image written by the image tool as 1-bit grey, RGB and palette PNG reads the same.
        var source = TestPng.WithBlack(40, 12, 255, (3, 3, 10, 5), (20, 1, 6, 9));
        foreach (var form in new[] { new[] { "-type", "bilevel" }, ["-type", "TrueColor", "-define", "png:color-type=2"], ["-type", "Palette"] })
        {
            var other = PngRaster.TryDecode(TesseractOcr.MagickBytes(magick, source, form)!)!;
            Assert.Equal(PngRaster.TryDecode(source)!.Grey, other.Grey);
        }
    }

    [Fact]
    public void A_crop_cut_in_memory_has_every_pixel_of_the_one_the_image_tool_makes()
    {
        var magick = TesseractOcr.FindImageMagick();
        var docx = TestRepository.Path("todo10_8/generated-docx/01_phap_quy/023_ND_23-2015_Chung_thuc.docx");
        if (magick is null || !File.Exists(docx)) return;
        using var zip = System.IO.Compression.ZipFile.OpenRead(docx);
        var page = Path.Combine(dir, "page.png");
        using (var e = zip.Entries.First(x => x.FullName.StartsWith("word/media/", StringComparison.Ordinal) && x.Name.EndsWith(".png", StringComparison.Ordinal)).Open())
        using (var f = File.Create(page)) e.CopyTo(f);
        var raster = PngRaster.Load(page, magick)!;
        // A line inside the page with a neighbour reaching into its margin, one at the top-left corner, one at the bottom-right edge.
        var cases = new (PixelBox Box, PixelBox[] Mask)[]
        {
            (new PixelBox(300, 400, 900, 45), [new PixelBox(320, 360, 400, 44), new PixelBox(250, 440, 300, 40)]),
            (new PixelBox(2, 3, 300, 40), []),
            (new PixelBox(raster.Width - 200, raster.Height - 30, 198, 28), [new PixelBox(raster.Width - 150, raster.Height - 70, 100, 45)]),
        };
        foreach (var (box, mask) in cases)
        {
            var file = Path.Combine(dir, "tool.png");
            Assert.True(TesseractOcr.CropScaled(magick, page, file, box, 8, 2.0, bilevel: true, mask: mask));
            var tool = PngRaster.TryDecode(File.ReadAllBytes(file))!;
            var mine = PngRaster.TryDecode(raster.CropBilevel(box, 8, 2, mask, TesseractOcr.BilevelThresholdShare))!;
            Assert.Equal((tool.Width, tool.Height), (mine.Width, mine.Height));
            Assert.Equal(tool.Grey, mine.Grey);
        }
    }

    [Fact]
    public void Bold_type_is_measured_as_strokes_wider_than_the_pages_median()
    {
        // 30 lines of "text": vertical strokes 2 px wide every 8 px; line 5 has strokes 4 px wide (bold); one speck line.
        const int width = 600, height = 30 * 40 + 40;
        var grey = Enumerable.Repeat((byte)255, width * height).ToArray();
        var lines = new List<OcrLine>();
        for (var n = 0; n < 30; n++)
        {
            var top = 20 + n * 40;
            var stroke = n == 5 ? 4 : 2;
            for (var y = top; y < top + 30; y++)
                for (var x = 50; x < 550; x += 8)
                    for (var k = 0; k < stroke; k++) grey[y * width + x + k] = 0;
            lines.Add(new OcrLine($"P001-ocr-original-L{n + 1:000}", "x", new PixelBox(50, top, 500, 30), 95, 95, false, []));
        }
        lines.Add(new OcrLine("P001-ocr-original-L031", ".", new PixelBox(10, 5, 4, 4), 20, 20, false, []));
        grey[6 * width + 11] = 0;
        var page = new OcrPage(1, new string('a', 64), width, height, new string('b', 64), lines);

        var measured = ScanTypography.Measure(new PngRaster(width, height, grey), page).ToDictionary(t => t.LineId);

        // Each line is measured on its own box: the stroke width is the line's own, and a thicker line reads as thicker.
        Assert.Equal(2.0, measured["P001-ocr-original-L001"].StrokeWidthPx);
        Assert.Equal(4.0, measured["P001-ocr-original-L006"].StrokeWidthPx);
        Assert.True(measured["P001-ocr-original-L006"].InkShare > measured["P001-ocr-original-L001"].InkShare);
        // The speck is measured as well, with its own count of runs.
        Assert.Equal(1, measured["P001-ocr-original-L031"].Runs);
    }
}
