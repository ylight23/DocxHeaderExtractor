using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.Tests;

public sealed class DocxVersionReaderTests
{
    const string W14 = "http://schemas.microsoft.com/office/word/2010/wordml";

    static string Make(string path, Action<Body> fill)
    {
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var body = new Body();
            fill(body);
            main.Document = new Document(body);
            main.Document.Save();
        }
        return path;
    }

    static Paragraph Text(string text, string? paraId = null, string? numId = null, int? ilvl = null, string? style = null, bool? bold = null)
    {
        var run = new Run(new DocumentFormat.OpenXml.Wordprocessing.Text(text));
        if (bold == true) run.RunProperties = new RunProperties(new Bold());
        var paragraph = new Paragraph(run);
        if (paraId is not null) paragraph.SetAttribute(new OpenXmlAttribute("w14", "paraId", W14, paraId));
        if (numId is not null || ilvl is not null || style is not null)
        {
            var properties = new ParagraphProperties();
            if (style is not null) properties.ParagraphStyleId = new ParagraphStyleId { Val = style };
            if (numId is not null || ilvl is not null)
                properties.NumberingProperties = new NumberingProperties(
                    new NumberingLevelReference { Val = ilvl ?? 0 },
                    new NumberingId { Val = int.Parse(numId ?? "0") });
            paragraph.ParagraphProperties = properties;
        }
        return paragraph;
    }

    static string TempPath() => Path.Combine(Path.GetTempPath(), "dhx-version-" + Guid.NewGuid().ToString("N") + ".docx");

    static void Cleanup(params string[] paths)
    {
        foreach (var path in paths) if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void Keeps_raw_text_location_declared_numbering_style_paragraph_id_and_container()
    {
        var path = TempPath();
        try
        {
            Make(path, body => body.Append(Text("Chương I", paraId: "1A2B3C4D", numId: "3", ilvl: 1, style: "Heading1")));
            var version = DocxVersionReader.Read(path);

            var only = Assert.Single(version.Paragraphs);
            Assert.Equal("Chương I", only.RawText);
            Assert.Equal("body[1]/p[1]", only.Location);
            Assert.Equal("1A2B3C4D", only.ParaId);
            Assert.Equal("3", only.NumId);
            Assert.Equal(1, only.Ilvl);
            Assert.Equal("Heading1", only.StyleId);
            Assert.Equal("body", only.Container);
            Assert.Equal(64, version.FileSha256.Length);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Tabs_line_breaks_and_non_breaking_hyphens_stay_in_the_raw_text()
    {
        var path = TempPath();
        try
        {
            Make(path, body => body.Append(new Paragraph(
                new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("A"), new TabChar(), new DocumentFormat.OpenXml.Wordprocessing.Text("B"),
                    new Break(), new DocumentFormat.OpenXml.Wordprocessing.Text("C"), new NoBreakHyphen(), new DocumentFormat.OpenXml.Wordprocessing.Text("D")))));

            var paragraph = Assert.Single(DocxVersionReader.Read(path).Paragraphs);
            Assert.Equal("A\tB\nC‑D", paragraph.RawText);
            Assert.Equal(new[] { 3 }, paragraph.LineBreakOffsets);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Run_formatting_offsets_are_taken_on_the_raw_text_across_runs_and_tabs()
    {
        var path = TempPath();
        try
        {
            Make(path, body => body.Append(new Paragraph(
                new Run(new RunProperties(new Bold()), new DocumentFormat.OpenXml.Wordprocessing.Text("Điều")),
                new Run(new DocumentFormat.OpenXml.Wordprocessing.Text(" "), new TabChar(), new DocumentFormat.OpenXml.Wordprocessing.Text("1")))));

            var paragraph = Assert.Single(DocxVersionReader.Read(path).Paragraphs);
            Assert.Equal("Điều \t1", paragraph.RawText);
            Assert.Equal(2, paragraph.Runs.Count);
            Assert.Equal((0, 4, true), (paragraph.Runs[0].Start, paragraph.Runs[0].End, paragraph.Runs[0].Bold ?? false));
            Assert.Equal((4, 7), (paragraph.Runs[1].Start, paragraph.Runs[1].End));
        }
        finally { Cleanup(path); }
    }

    /// <summary>Fixed fixture: 026 has two text boxes whose text is exactly "JANUARY, 2017" (checked in the file's XML).</summary>
    static string CorpusFixture()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var path = Path.Combine(root, "todo10_8", "heading_corpus_100", "02_hop_dong_mua_sam", "026_WB_RFB_Goods_One_Envelope_2017.docx");
        Assert.True(File.Exists(path), "Fixture missing: " + path);
        return path;
    }

    [Fact]
    public void Text_box_paragraphs_keep_their_own_text_and_the_anchor_paragraph_does_not_take_it()
    {
        var version = DocxVersionReader.Read(CorpusFixture());

        var boxes = version.Paragraphs.Where(p => p.Container == "text_box" && p.RawText.Length > 0).ToArray();
        Assert.Equal(2, boxes.Length);
        Assert.All(boxes, box => Assert.Equal("JANUARY, 2017", box.RawText));
        Assert.All(boxes, box => Assert.Contains("txbxContent", box.Location));
        Assert.Contains("text_box", version.UnsupportedRegions);
        Assert.DoesNotContain(version.Paragraphs.Where(p => p.Container == "body"), p => p.RawText.Contains("JANUARY, 2017"));
    }

    [Fact]
    public void Column_and_page_breaks_are_different_characters_and_line_breaks_are_recorded()
    {
        var path = TempPath();
        try
        {
            Make(path, body => body.Append(new Paragraph(
                new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("A"), new Break { Type = BreakValues.Page },
                    new DocumentFormat.OpenXml.Wordprocessing.Text("B"), new Break { Type = BreakValues.Column },
                    new DocumentFormat.OpenXml.Wordprocessing.Text("C"), new CarriageReturn(),
                    new DocumentFormat.OpenXml.Wordprocessing.Text("D")))));

            var paragraph = Assert.Single(DocxVersionReader.Read(path).Paragraphs);
            Assert.Equal("A\fB\vC\nD", paragraph.RawText);
            Assert.Equal(new[] { 5 }, paragraph.LineBreakOffsets);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Offsets_count_utf16_units_so_an_emoji_takes_two()
    {
        var path = TempPath();
        try
        {
            Make(path, body => body.Append(new Paragraph(
                new Run(new RunProperties(new Bold()), new DocumentFormat.OpenXml.Wordprocessing.Text("😀")),
                new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("x")))));

            var paragraph = Assert.Single(DocxVersionReader.Read(path).Paragraphs);
            Assert.Equal("😀x", paragraph.RawText);
            Assert.Equal((0, 2), (paragraph.Runs[0].Start, paragraph.Runs[0].End));
            Assert.Equal((2, 3), (paragraph.Runs[1].Start, paragraph.Runs[1].End));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Each_font_slot_is_kept_separately()
    {
        var path = TempPath();
        try
        {
            Make(path, body => body.Append(new Paragraph(new Run(
                new RunProperties(new RunFonts { Ascii = "Arial", EastAsia = "MS Mincho" }),
                new DocumentFormat.OpenXml.Wordprocessing.Text("t")))));

            var run = Assert.Single(Assert.Single(DocxVersionReader.Read(path).Paragraphs).Runs);
            Assert.Equal("Arial", run.FontAscii);
            Assert.Null(run.FontHighAnsi);
            Assert.Equal("MS Mincho", run.FontEastAsia);
            Assert.Null(run.FontComplexScript);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Raw_text_keeps_extra_spaces_while_the_matching_key_collapses_them()
    {
        var path = TempPath();
        try
        {
            Make(path, body => body.Append(Text("Chương   I")));
            var paragraph = Assert.Single(DocxVersionReader.Read(path).Paragraphs);

            Assert.Equal("Chương   I", paragraph.RawText);
            Assert.Equal("Chương I", paragraph.NormalizedKey);
            Assert.Equal(DocxVersionReader.Hash("Chương I"), paragraph.KeyHash);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Changing_the_list_number_does_not_change_the_matching_key()
    {
        var before = TempPath();
        var after = TempPath();
        try
        {
            Make(before, body => body.Append(Text("Bên B chịu phí vận chuyển.", numId: "3", ilvl: 0)));
            Make(after, body => body.Append(Text("Bên B chịu phí vận chuyển.", numId: "7", ilvl: 2)));

            var a = Assert.Single(DocxVersionReader.Read(before).Paragraphs);
            var b = Assert.Single(DocxVersionReader.Read(after).Paragraphs);
            Assert.Equal(a.KeyHash, b.KeyHash);
            Assert.NotEqual(a.NumId, b.NumId);
        }
        finally { Cleanup(before, after); }
    }

    [Fact]
    public void Composed_and_decomposed_accents_share_the_key_but_keep_their_raw_text()
    {
        var composed = TempPath();
        var decomposed = TempPath();
        try
        {
            Make(composed, body => body.Append(Text("Chương é I")));
            Make(decomposed, body => body.Append(Text("Chương é I")));

            var a = Assert.Single(DocxVersionReader.Read(composed).Paragraphs);
            var b = Assert.Single(DocxVersionReader.Read(decomposed).Paragraphs);
            Assert.Equal(a.KeyHash, b.KeyHash);
            Assert.NotEqual(a.RawText, b.RawText);
        }
        finally { Cleanup(composed, decomposed); }
    }

    [Fact]
    public void Direct_bold_and_alignment_are_recorded()
    {
        var path = TempPath();
        try
        {
            Make(path, body =>
            {
                var paragraph = Text("Điều 1", bold: true);
                paragraph.ParagraphProperties = new ParagraphProperties(new Justification { Val = JustificationValues.Center });
                body.Append(paragraph);
            });
            var paragraph = Assert.Single(DocxVersionReader.Read(path).Paragraphs);

            Assert.Equal("center", paragraph.Alignment);
            var run = Assert.Single(paragraph.Runs);
            Assert.Equal((0, 6), (run.Start, run.End));
            Assert.True(run.Bold);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Deleted_text_is_left_out_inserted_text_is_kept_and_tracked_changes_are_reported()
    {
        var path = TempPath();
        try
        {
            Make(path, body => body.Append(new Paragraph(
                new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Giữ ")),
                new DeletedRun(new Run(new DeletedText("bỏ "))),
                new InsertedRun(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("thêm"))))));

            var version = DocxVersionReader.Read(path);
            Assert.Equal("Giữ thêm", Assert.Single(version.Paragraphs).RawText);
            Assert.Contains("tracked_changes", version.UnsupportedRegions);
        }
        finally { Cleanup(path); }
    }

}
