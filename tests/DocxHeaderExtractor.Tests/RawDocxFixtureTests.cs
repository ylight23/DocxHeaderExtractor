using System.IO.Compression;
using System.Text;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Fixtures written as raw WordprocessingML, so the structures Word really writes (moveFrom/moveTo, w:delText, pPrChange, style chains)
/// are tested without the Open XML object model constraining them.
/// </summary>
public sealed class RawDocxFixtureTests
{
    const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    const string Rev = "w:id=\"1\" w:author=\"tester\" w:date=\"2026-01-01T00:00:00Z\"";

    internal static string Build(string documentBody, string? stylesXml = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "dhx-raw-" + Guid.NewGuid().ToString("N") + ".docx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Entry(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            (stylesXml is null ? "" : "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>") +
            "</Types>");
        Entry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>");
        Entry(zip, "word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<w:document xmlns:w=\"{W}\"><w:body>{documentBody}</w:body></w:document>");
        if (stylesXml is not null)
        {
            Entry(zip, "word/_rels/document.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "</Relationships>");
            Entry(zip, "word/styles.xml", stylesXml);
        }
        return path;
    }

    static void Entry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void Moved_away_text_is_left_out_moved_to_text_is_kept_and_reported()
    {
        var path = Build(
            $"<w:p><w:moveFrom {Rev}><w:r><w:t>cũ</w:t></w:r></w:moveFrom>" +
            $"<w:moveTo {Rev}><w:r><w:t>mới</w:t></w:r></w:moveTo></w:p>");
        try
        {
            var version = DocxVersionReader.Read(path);
            Assert.Equal("mới", Assert.Single(version.Paragraphs).RawText);
            Assert.Contains("tracked_changes", version.UnsupportedRegions);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Deleted_text_uses_delText_and_is_left_out_while_inserted_text_is_kept()
    {
        var path = Build(
            "<w:p><w:r><w:t xml:space=\"preserve\">Giữ </w:t></w:r>" +
            $"<w:del {Rev}><w:r><w:delText>bỏ</w:delText></w:r></w:del>" +
            $"<w:ins {Rev}><w:r><w:t>thêm</w:t></w:r></w:ins></w:p>");
        try
        {
            var version = DocxVersionReader.Read(path);
            Assert.Equal("Giữ thêm", Assert.Single(version.Paragraphs).RawText);
            Assert.Contains("tracked_changes", version.UnsupportedRegions);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void A_tracked_paragraph_property_change_is_reported_as_unsupported_formatting()
    {
        var path = Build(
            $"<w:p><w:pPr><w:jc w:val=\"center\"/><w:pPrChange {Rev}><w:pPr/></w:pPrChange></w:pPr>" +
            "<w:r><w:t>x</w:t></w:r></w:p>");
        try
        {
            var version = DocxVersionReader.Read(path);
            Assert.Contains("tracked_formatting", version.UnsupportedRegions);
            Assert.Equal("center", Assert.Single(version.Paragraphs).Alignment);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Effective_formatting_follows_paragraph_style_then_defaults_and_direct_formatting_wins()
    {
        const string styles =
            $"<w:styles xmlns:w=\"{W}\">" +
            "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Arial\"/><w:sz w:val=\"22\"/></w:rPr></w:rPrDefault></w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/>" +
            "<w:rPr><w:b/><w:color w:val=\"1F3864\"/></w:rPr></w:style>" +
            "</w:styles>";
        var path = Build(
            "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr>" +
            "<w:r><w:t>Điều 1</w:t></w:r>" +
            "<w:r><w:rPr><w:b w:val=\"0\"/></w:rPr><w:t xml:space=\"preserve\"> x</w:t></w:r></w:p>",
            styles);
        try
        {
            var paragraph = Assert.Single(DocxVersionReader.Read(path).Paragraphs);
            Assert.Equal("Điều 1 x", paragraph.RawText);

            var inherited = paragraph.Runs[0].Effective!;
            Assert.True(inherited.Bold);
            Assert.Equal("Arial", inherited.FontAscii);
            Assert.Equal("22", inherited.FontSize);
            Assert.Equal("1F3864", inherited.Color);

            var direct = paragraph.Runs[1].Effective!;
            Assert.False(direct.Bold);
            Assert.Equal("Arial", direct.FontAscii);
            Assert.True(inherited.Complete);
            Assert.Empty(inherited.Gaps);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void A_missing_basedOn_style_marks_the_result_incomplete_instead_of_falling_back_silently()
    {
        const string styles =
            $"<w:styles xmlns:w=\"{W}\">" +
            "<w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val=\"22\"/></w:rPr></w:rPrDefault></w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Child\"><w:name w:val=\"child\"/><w:basedOn w:val=\"Gone\"/></w:style>" +
            "</w:styles>";
        var path = Build("<w:p><w:pPr><w:pStyle w:val=\"Child\"/></w:pPr><w:r><w:t>x</w:t></w:r></w:p>", styles);
        try
        {
            var effective = Assert.Single(Assert.Single(DocxVersionReader.Read(path).Paragraphs).Runs).Effective!;
            Assert.False(effective.Complete);
            Assert.Contains("style_missing:Gone", effective.Gaps);
            Assert.Equal("22", effective.FontSize);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Text_in_a_styled_table_is_marked_incomplete_because_table_styles_are_not_resolved()
    {
        const string styles = $"<w:styles xmlns:w=\"{W}\"><w:style w:type=\"table\" w:styleId=\"Grid\"><w:name w:val=\"grid\"/></w:style></w:styles>";
        var path = Build(
            "<w:tbl><w:tblPr><w:tblStyle w:val=\"Grid\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid>" +
            "<w:tr><w:tc><w:p><w:r><w:t>ô</w:t></w:r></w:p></w:tc></w:tr></w:tbl>",
            styles);
        try
        {
            var paragraph = Assert.Single(DocxVersionReader.Read(path).Paragraphs);
            Assert.Equal("table", paragraph.Container);
            var effective = Assert.Single(paragraph.Runs).Effective!;
            Assert.False(effective.Complete);
            Assert.Contains("table_style_not_resolved:Grid", effective.Gaps);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Without_a_styles_part_the_inherited_values_are_unresolved_not_off()
    {
        var path = Build("<w:p><w:r><w:t>plain</w:t></w:r></w:p>");
        try
        {
            var effective = Assert.Single(Assert.Single(DocxVersionReader.Read(path).Paragraphs).Runs).Effective!;
            Assert.Null(effective.Bold);
            Assert.Null(effective.FontAscii);
            Assert.Null(effective.FontSize);
            Assert.False(effective.Complete);
            Assert.Contains("styles_part_missing", effective.Gaps);
        }
        finally { Cleanup(path); }
    }
}
