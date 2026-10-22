using System.Text.Json;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The shape of a line is a measurement of its form: its first two words, with every number or roman numeral written as #. The grouping payload
/// carries it with the number of lines of the document that share it. No word is judged here.
/// </summary>
public sealed class DocxLineShapeTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-shape-" + Guid.NewGuid().ToString("N"));

    public DocxLineShapeTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("Chapter I GENERAL PROVISIONS", "Chapter #")]
    [InlineData("Article 1. Scope", "Article #")]
    [InlineData("1. This clause is body text.", "# This")]
    [InlineData("Điều 10. Hệ thống", "Điều #")]
    [InlineData("", "")]
    public void The_shape_keeps_the_words_and_writes_every_number_as_hash(string text, string expected)
    {
        Assert.Equal(expected, DocxFormat.Shape(text));
    }

    [Fact]
    public void A_grouping_line_carries_its_shape_and_how_many_lines_of_the_document_share_it()
    {
        var source = DocxAgentSource.Load(DocxTestDocument.Create(Path.Combine(dir, "shape.docx")), null, out _)!;
        var chapter = Enumerable.Range(0, source.Lines.Count).First(i => source.Lines[i].Text == "Chapter I");

        var item = JsonSerializer.Serialize(DocxFormat.Instance.GroupLineItem(source, chapter));

        Assert.Contains("\"shape\":\"Chapter #\"", item, StringComparison.Ordinal);
        Assert.Contains("\"shapeCount\":1", item, StringComparison.Ordinal);
    }
}
