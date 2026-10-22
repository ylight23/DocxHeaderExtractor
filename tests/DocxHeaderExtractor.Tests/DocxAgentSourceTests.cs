using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.HeadingAgent;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The DOCX agent source: the paragraphs under the aliases of the route's source snapshot, the identity hashes, the segments and the facts read
/// from the OOXML. A small document built here pins what each fact says; a real document of the corpus pins the invariants. No network.
/// </summary>
[Trait("Category", "Slow")]
public sealed class DocxAgentSourceTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-docx-agent-" + Guid.NewGuid().ToString("N"));

    public DocxAgentSourceTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private string MakeDocx(string name = "sample.docx") => DocxTestDocument.Create(Path.Combine(dir, name));

    private static DocumentFormat.OpenXml.Wordprocessing.Paragraph P(string text) => DocxTestDocument.P(text);

    private DocxAgentSource Load(string path)
    {
        var source = DocxAgentSource.Load(path, null, out var failure);
        Assert.True(source is not null, failure);
        return source!;
    }

    [Fact]
    public void Paragraphs_come_in_reading_order_under_unique_aliases_with_the_identity_hashes()
    {
        var source = Load(MakeDocx());

        Assert.Equal(new[]
        {
            "Chapter I", "GENERAL PROVISIONS", "Article 1. Scope", "1. This clause is body text.", "Header cell", "Row cell", "Table of contents entry", "Closing paragraph.",
        }, source.Lines.Select(l => l.Text));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => source.Lines[i].Ordinal).OrderBy(o => o), source.Lines.Select(l => l.Ordinal));
        Assert.Equal(source.Lines.Count, source.Lines.Select(l => l.Alias).Distinct().Count());
        Assert.All(new[] { source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256 }, h => Assert.Matches("^[0-9a-f]{64}$", h));
        Assert.Equal(source.Lines.Select(l => l.Alias).ToHashSet(), source.Facts.Keys.ToHashSet());
    }

    [Fact]
    public void Facts_report_the_style_the_numbering_and_where_the_paragraph_stands()
    {
        var source = Load(MakeDocx());
        JsonObject Fact(int line) => source.Facts[source.Lines[line].Alias];

        var chapter = Fact(0);
        Assert.Equal("heading 1", chapter["style"]!["name"]!.GetValue<string>());
        Assert.Equal("Heading1", chapter["style"]!["id"]!.GetValue<string>());
        Assert.Equal(0, chapter["style"]!["outlineLevel"]!.GetValue<int>());
        Assert.True(chapter["style"]!["bold"]!.GetValue<bool>());
        Assert.Equal(16.0, chapter["style"]!["sizePt"]!.GetValue<double>());
        Assert.False(chapter["place"]!["inTable"]!.GetValue<bool>());
        Assert.Null(chapter["numbering"]);

        var bold = Fact(2);
        Assert.Equal(1.0, bold["text"]!["boldCharRatio"]!.GetValue<double>());
        Assert.Equal(0.0, Fact(3)["text"]!["boldCharRatio"]!.GetValue<double>());

        Assert.True(Fact(4)["place"]!["inTable"]!.GetValue<bool>());
        Assert.Equal(1, Fact(4)["place"]!["tableRow"]!.GetValue<int>());
        Assert.Equal(2, Fact(5)["place"]!["tableRow"]!.GetValue<int>());
        Assert.Equal(1, Fact(4)["place"]!["tableDepth"]!.GetValue<int>());

        Assert.Equal("_Toc1", Fact(6)["internalLinks"]![0]!.GetValue<string>());
        Assert.Null(Fact(7)["internalLinks"]);
    }

    [Fact]
    public void Facts_compare_a_paragraph_with_its_neighbours_and_with_the_document()
    {
        var source = Load(MakeDocx());
        JsonObject Fact(int line) => source.Facts[source.Lines[line].Alias];

        Assert.Null(Fact(0)["neighbours"]!["sameStyleAsPrevious"]);
        Assert.True(Fact(0)["neighbours"]!["sameStyleAsNext"]!.GetValue<bool>());
        Assert.False(Fact(1)["neighbours"]!["sameStyleAsNext"]!.GetValue<bool>());
        Assert.Null(Fact(7)["neighbours"]!["sameStyleAsNext"]);

        // The two Heading1 paragraphs share the largest style of the document (rank 1, two paragraphs).
        Assert.Equal(1, Fact(0)["styleInDocument"]!["rank"]!.GetValue<int>());
        Assert.Equal(2, Fact(0)["styleInDocument"]!["linesWithThisStyleInDocument"]!.GetValue<int>());
        Assert.True(Fact(0)["sizeVsDocumentMedianPt"]!.GetValue<double>() > 0);

        var styles = source.StyleTable;
        Assert.Equal(source.Lines.Count, styles["documentLines"]!.GetValue<int>());
        var first = styles["styles"]![0]!;
        Assert.Equal(1, first["rank"]!.GetValue<int>());
        Assert.Equal("heading 1", first["styleName"]!.GetValue<string>());
        Assert.Equal(2, first["lines"]!.GetValue<int>());
        Assert.Equal(source.Lines[0].Alias, first["firstLines"]![0]!["alias"]!.GetValue<string>());
        Assert.Equal(styles["stylesTotal"]!.GetValue<int>(), styles["styles"]!.AsArray().Count + styles["stylesNotShown"]!.GetValue<int>());
    }

    [Fact]
    public void A_paragraph_inside_a_table_cell_is_placed_by_its_address()
    {
        Assert.Equal((0, (int?)null, (int?)null, false), DocxLineFacts.Place("body[1]/p[3]"));
        Assert.Equal((1, 2, 3, false), DocxLineFacts.Place("body[1]/tbl[2]/tr[2]/tc[3]/p[1]"));
        // A table inside a cell: the depth counts both, the row and the cell are the innermost ones.
        Assert.Equal((2, 1, 1, false), DocxLineFacts.Place("body[1]/tbl[1]/tr[4]/tc[2]/tbl[1]/tr[1]/tc[1]/p[1]"));
        Assert.Equal((0, (int?)null, (int?)null, true), DocxLineFacts.Place("body[1]/p[2]/txbxContent[1]/p[1]"));
        Assert.Equal((0, (int?)null, (int?)null, false), DocxLineFacts.Place("p:12"));
    }

    [Fact]
    public void The_same_file_gives_the_same_hashes_and_facts_and_a_changed_document_changes_the_store()
    {
        var path = MakeDocx("a.docx");
        var a = Load(path);
        var again = Load(path);
        Assert.Equal(a.SourceSha256, again.SourceSha256);
        Assert.Equal(a.SourceAliasUniverseSha256, again.SourceAliasUniverseSha256);
        Assert.Equal(a.EvidenceStoreSha256, again.EvidenceStoreSha256);
        Assert.Equal(a.Facts.Select(f => f.Value.ToJsonString()), again.Facts.Select(f => f.Value.ToJsonString()));

        var other = Path.Combine(dir, "other.docx");
        using (var doc = WordprocessingDocument.Create(other, WordprocessingDocumentType.Document))
        {
            doc.AddMainDocumentPart().Document = new Document(new Body(P("Only one paragraph.")));
        }
        var changed = Load(other);
        Assert.NotEqual(a.SourceSha256, changed.SourceSha256);
        Assert.NotEqual(a.EvidenceStoreSha256, changed.EvidenceStoreSha256);
    }

    [Fact]
    public void A_file_that_is_not_a_docx_or_has_no_text_is_refused_with_a_code()
    {
        var notDocx = Path.Combine(dir, "plain.docx");
        File.WriteAllText(notDocx, "this is not a zip package");
        Assert.Null(DocxAgentSource.Load(notDocx, null, out var failure));
        Assert.Equal("NOT_A_DOCX_PACKAGE", failure);

        var empty = Path.Combine(dir, "empty.docx");
        using (var doc = WordprocessingDocument.Create(empty, WordprocessingDocumentType.Document))
        {
            doc.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(), P("   ")));
        }
        Assert.Null(DocxAgentSource.Load(empty, null, out failure));
        Assert.Equal("EMPTY_UNIVERSE", failure);
    }

    [Fact]
    public void Tables_can_be_left_out_like_the_route_does_when_asked()
    {
        var path = MakeDocx();
        var source = DocxAgentSource.Load(path, new DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer.ExtractionOptions { IncludeTables = false }, out _)!;
        Assert.DoesNotContain(source.Lines, l => l.Text is "Header cell" or "Row cell");
        Assert.Equal(6, source.Lines.Count);
    }

    [Fact]
    public void A_real_document_is_cut_into_a_partition_of_windows_with_context_on_each_side()
    {
        var path = TestRepository.Path("todo10_8/generated-docx/01_phap_quy/001_Bo_luat_Dan_su_91-2015-QH13.docx");
        var source = DocxAgentSource.Load(path, null, out var failure);
        Assert.True(source is not null, failure);
        var n = source!.Lines.Count;
        Assert.True(n > 2 * DocxAgentSource.Unit);

        var next = 0;
        foreach (var (start, end) in source.Cores)
        {
            Assert.Equal(next, start);
            Assert.InRange(end - start, 1, DocxAgentSource.Unit);
            next = end;
        }
        Assert.Equal(n, next);

        var (decide, shown) = source.Scope(1);
        Assert.Equal(source.Lines.Skip(DocxAgentSource.Unit).Take(DocxAgentSource.Unit).Select(l => l.Alias), decide);
        Assert.Equal(DocxAgentSource.Unit - DocxAgentSource.Overlap, shown[0]);
        Assert.Equal(Math.Min(n, 2 * DocxAgentSource.Unit + DocxAgentSource.Overlap) - 1, shown[^1]);
        var (_, firstShown) = source.Scope(0);
        Assert.Equal(0, firstShown[0]);
        var (_, lastShown) = source.Scope(source.Cores.Count - 1);
        Assert.Equal(n - 1, lastShown[^1]);

        Assert.Equal(source.Lines.Select(l => l.Alias).Distinct().Count(), n);
        Assert.Equal(source.Lines.Select(l => l.Alias).ToHashSet(), source.Facts.Keys.ToHashSet());
        // Deterministic: a second read of the same file gives the same store.
        Assert.Equal(source.EvidenceStoreSha256, DocxAgentSource.Load(path, null, out _)!.EvidenceStoreSha256);
    }
}
