using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocxHeaderExtractor.DocumentProcessing.Changes;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A change made only in styles.xml or numbering.xml, with the paragraph's own XML untouched, reaches its signature and the format comparison;
/// rewriting a style without changing what is read does not.
/// </summary>
public sealed class StyleDependencyTests
{
    const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    static string Numbering(string level1Style = "H2") =>
        $"<w:numbering xmlns:w=\"{W}\"><w:abstractNum w:abstractNumId=\"0\">" +
        "<w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"upperRoman\"/><w:lvlText w:val=\"%1.\"/></w:lvl>" +
        $"<w:lvl w:ilvl=\"1\"><w:start w:val=\"1\"/><w:numFmt w:val=\"upperLetter\"/><w:pStyle w:val=\"{level1Style}\"/><w:lvlText w:val=\"%2.\"/></w:lvl>" +
        "</w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>";

    static string Styles(string h2Paragraph = "<w:numPr><w:numId w:val=\"1\"/></w:numPr>", string bodyRun = "", string extra = "") =>
        $"<w:styles xmlns:w=\"{W}\"><w:style w:type=\"paragraph\" w:styleId=\"H2\"><w:name w:val=\"Mục\"/><w:pPr>{h2Paragraph}</w:pPr></w:style>" +
        $"<w:style w:type=\"paragraph\" w:styleId=\"Body\"><w:name w:val=\"Thân\"/>{extra}<w:rPr>{bodyRun}</w:rPr></w:style></w:styles>";

    static string Body => "<w:p><w:pPr><w:pStyle w:val=\"H2\"/></w:pPr><w:r><w:t>Truyền thông:</w:t></w:r></w:p>" +
                          "<w:p><w:pPr><w:pStyle w:val=\"Body\"/></w:pPr><w:r><w:t>Đã đăng 5 bài.</w:t></w:r></w:p>";

    static string Theme(string minor) =>
        "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"T\"><a:themeElements><a:fontScheme name=\"F\">" +
        $"<a:majorFont><a:latin typeface=\"Cambria\"/></a:majorFont><a:minorFont><a:latin typeface=\"{minor}\"/></a:minorFont></a:fontScheme></a:themeElements></a:theme>";

    static DocxVersion Read(string styles, string numbering, string? body = null, string? theme = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "dhx-styledep-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            using (var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                void Write(OpenXmlPart part, string xml) { using var s = part.GetStream(FileMode.Create); s.Write(Encoding.UTF8.GetBytes(xml)); }
                Write(main, $"<w:document xmlns:w=\"{W}\"><w:body>{body ?? Body}</w:body></w:document>");
                Write(main.AddNewPart<StyleDefinitionsPart>(), styles);
                Write(main.AddNewPart<NumberingDefinitionsPart>(), numbering);
                if (theme is not null) Write(main.AddNewPart<ThemePart>(), theme);
            }
            return DocxVersionReader.Read(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Numbering_through_the_paragraph_style_takes_the_level_that_names_the_style()
    {
        var heading = Read(Styles(), Numbering()).Paragraphs[0];
        Assert.Null(heading.NumId);
        Assert.Equal("style", heading.NumberingSource);
        Assert.Equal("1", heading.EffectiveNumId);
        Assert.Equal(1, heading.EffectiveIlvl);
        Assert.True(heading.NumberingResolved);
        Assert.StartsWith("ilvl=1|", heading.NumberingKey);
    }

    [Fact]
    public void A_change_only_in_the_style_or_list_definition_changes_the_signature()
    {
        var baseline = Read(Styles(), Numbering());
        // The style no longer numbers its paragraphs; the list names another style at level 1; the body style becomes bold.
        var unnumbered = Read(Styles(h2Paragraph: ""), Numbering());
        var otherLevel = Read(Styles(), Numbering(level1Style: "H3"));
        var bold = Read(Styles(bodyRun: "<w:b/>"), Numbering());
        Assert.NotEqual(OutlineStore.Signature(baseline.Paragraphs[0]), OutlineStore.Signature(unnumbered.Paragraphs[0]));
        Assert.NotEqual(OutlineStore.Signature(baseline.Paragraphs[0]), OutlineStore.Signature(otherLevel.Paragraphs[0]));
        Assert.Equal(OutlineStore.Signature(baseline.Paragraphs[0]), OutlineStore.Signature(bold.Paragraphs[0]));
        Assert.NotEqual(OutlineStore.Signature(baseline.Paragraphs[1]), OutlineStore.Signature(bold.Paragraphs[1]));

        var compared = DocxComparer.Compare(baseline, bold, new ComparerOptions(CompareDirectFormatting: true));
        var change = Assert.Single(compared.Changes, c => c.Kind == "direct_format_modified");
        Assert.Contains(change.FormatChanges!, f => f.Property == "effective.bold" && f.After == "True");
        Assert.Equal(["Body"], compared.StyleChanges);
        Assert.Null(DocxComparer.Compare(baseline, bold).StyleChanges);
    }

    [Fact]
    public void Rewriting_a_style_without_changing_what_is_read_changes_nothing()
    {
        // A name and an attribute Word adds on save are not read: the signatures and the read definitions stay the same.
        var baseline = Read(Styles(), Numbering());
        var rewritten = Read(Styles(extra: "<w:qFormat/><w:rsid w:val=\"00AB12CD\"/>"), Numbering());
        Assert.Equal(baseline.Paragraphs.Select(OutlineStore.Signature), rewritten.Paragraphs.Select(OutlineStore.Signature));
        Assert.Empty(DocxComparer.Compare(baseline, rewritten, new ComparerOptions(CompareDirectFormatting: true)).StyleChanges!);
    }

    [Fact]
    public void A_missing_paragraph_style_leaves_the_numbering_unresolved_and_is_a_gap()
    {
        var missing = Read(Styles(), Numbering(), "<w:p><w:pPr><w:pStyle w:val=\"Ghost\"/></w:pPr><w:r><w:t>Mục</w:t></w:r></w:p>").Paragraphs[0];
        Assert.False(missing.NumberingResolved);
        Assert.Contains("style_missing:Ghost", missing.FromStyle!.Gaps);
        Assert.True(OutlineStore.Paragraphs(new DocxVersion("sha", [missing], [])).Single().Unverified);
    }

    [Fact]
    public void A_theme_font_is_resolved_from_the_theme_and_its_change_reaches_the_signature()
    {
        var themed = Styles(bodyRun: "<w:rFonts w:asciiTheme=\"minorHAnsi\"/>");
        var times = Read(themed, Numbering(), theme: Theme("Times New Roman"));
        var arial = Read(themed, Numbering(), theme: Theme("Arial"));
        var body = times.Paragraphs[1];
        Assert.Equal("Times New Roman", body.Runs.Single().Effective!.FontAscii);
        Assert.True(OutlineStore.Verified(body));
        Assert.NotEqual(OutlineStore.Signature(body), OutlineStore.Signature(arial.Paragraphs[1]));
        Assert.Contains("(theme)", DocxComparer.Compare(times, arial, new ComparerOptions(CompareDirectFormatting: true)).StyleChanges!);

        // Without a theme part the font cannot be resolved: a gap, and the paragraph is never paired by signature.
        var unthemed = Read(themed, Numbering()).Paragraphs[1];
        Assert.Contains(unthemed.Runs.Single().Effective!.Gaps, g => g.StartsWith("theme_font_not_resolved", StringComparison.Ordinal));
        Assert.False(OutlineStore.Verified(unthemed));
        Assert.True(OutlineStore.Paragraphs(new DocxVersion("sha", [unthemed], [])).Single().Unverified);
    }
}
