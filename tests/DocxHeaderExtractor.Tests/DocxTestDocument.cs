using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxHeaderExtractor.Tests;

/// <summary>Small DOCX files built in code for the tests of the DOCX source, tools, selector and route.</summary>
internal static class DocxTestDocument
{
    public static Paragraph P(string text, string? style = null, bool bold = false, string? anchor = null)
    {
        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        if (bold) run.PrependChild(new RunProperties(new Bold()));
        var p = new Paragraph();
        if (style is not null) p.AppendChild(new ParagraphProperties(new ParagraphStyleId { Val = style }));
        if (anchor is null) p.AppendChild(run); else p.AppendChild(new Hyperlink(run) { Anchor = anchor });
        return p;
    }

    private static StyleDefinitionsPart AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new Styles(
            new Style(new StyleName { Val = "Normal" }, new StyleRunProperties(new FontSize { Val = "22" })) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            new Style(new StyleName { Val = "heading 1" }, new BasedOn { Val = "Normal" },
                new StyleParagraphProperties(new OutlineLevel { Val = 0 }),
                new StyleRunProperties(new Bold(), new FontSize { Val = "32" })) { Type = StyleValues.Paragraph, StyleId = "Heading1" });
        return part;
    }

    /// <summary>0 Chapter I and 1 GENERAL PROVISIONS (Heading1), 2 a bold Normal paragraph, 3 a Normal clause, 4-5 a table with two rows, 6 a
    /// paragraph that links to a bookmark, 7 a Normal paragraph.</summary>
    public static string Create(string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        AddStyles(main);
        var table = new Table(
            new TableRow(new TableCell(P("Header cell"))),
            new TableRow(new TableCell(P("Row cell"))));
        main.Document = new Document(new Body(
            P("Chapter I", "Heading1"), P("GENERAL PROVISIONS", "Heading1"),
            P("Article 1. Scope", bold: true), P("1. This clause is body text."),
            table,
            P("Table of contents entry", anchor: "_Toc1"),
            P("Closing paragraph.")));
        return path;
    }

    /// <summary>A document of <paramref name="paragraphs"/> paragraphs: a Heading1 paragraph at the start of every 20, body text between.</summary>
    public static string CreateLong(string path, int paragraphs)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        AddStyles(main);
        var body = new Body();
        for (var i = 0; i < paragraphs; i++)
            body.AppendChild(i % 20 == 0 ? P("Part " + (i / 20 + 1), "Heading1") : P("Body paragraph " + i + " with some words in it."));
        main.Document = new Document(body);
        return path;
    }
}
