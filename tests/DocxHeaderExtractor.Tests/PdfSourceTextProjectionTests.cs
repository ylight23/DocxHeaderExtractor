using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// What "verbatim" means for a PDF.
/// <para>
/// A PDF has no text, only ordered glyphs with positions, so every string is a reconstruction. The
/// canonical source is therefore a declared projection of those glyphs with word gaps decided by
/// geometry - and nothing else. It must repair spacing without inventing words, and it must be able
/// to point back at the glyphs, so that a bad reconstruction stays an auditable parser defect
/// instead of becoming a semantic error blamed on the model.
/// </para>
/// </summary>
public sealed class PdfSourceTextProjectionTests
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    [Fact]
    public void The_projection_repairs_spacing_on_a_real_document()
    {
        var blocks = Blocks();

        var title = blocks.First(block => block.VerbatimText.Contains("COMPARISON", StringComparison.Ordinal));

        Assert.Equal("MINUTES OF THE INTERNATIONAL COMPARISON PROGRAM", title.VerbatimText);
        // The raw parser string is kept, so the repair is visible rather than assumed.
        Assert.Contains("M I N UTES", title.Projection.RawParserText, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_canonical_span_points_back_at_the_glyphs_it_came_from()
    {
        var blocks = Blocks();
        var title = blocks.First(block => block.VerbatimText.Contains("COMPARISON", StringComparison.Ordinal));
        var start = title.VerbatimText.IndexOf("INTERNATIONAL", StringComparison.Ordinal);

        var glyphs = title.Projection.Resolve(start, "INTERNATIONAL".Length);

        Assert.NotEmpty(glyphs);
        Assert.Equal(13, glyphs.Count);
        Assert.All(glyphs, glyph => Assert.True(glyph.Right > glyph.Left));
        // The run is contiguous in reading order and on one page, which is what makes it a location.
        Assert.Equal(glyphs.Select(glyph => glyph.GlyphOrdinal).Order(), glyphs.Select(glyph => glyph.GlyphOrdinal));
        Assert.Single(glyphs.Select(glyph => glyph.Page).Distinct());
    }

    [Fact]
    public void The_projection_only_removes_spaces_never_changes_characters()
    {
        // The line that separates a spacing reconstruction from a spelling correction. If the
        // non-space characters ever differ, the projection has started guessing at words, and a
        // parser defect would become indistinguishable from a model error.
        var blocks = Blocks();

        Assert.All(blocks.Take(300), block =>
        {
            var raw = Strip(block.Projection.RawParserText);
            var verbatim = Strip(block.VerbatimText);
            Assert.Equal(raw, verbatim);
        });
    }

    [Fact]
    public void Every_glyph_of_a_block_is_represented_in_its_span_map()
    {
        var blocks = Blocks();

        Assert.All(blocks.Where(block => block.Projection.HasGlyphProvenance).Take(200), block =>
        {
            var mapped = block.Projection.SpanMap.Sum(entry => entry.VerbatimLength);
            var nonSpace = Strip(block.VerbatimText).Length;
            Assert.Equal(nonSpace, mapped);
        });
    }

    [Fact]
    public void The_span_map_indexes_the_canonical_text_not_the_raw_one()
    {
        var blocks = Blocks();
        var title = blocks.First(block => block.VerbatimText.Contains("COMPARISON", StringComparison.Ordinal));

        Assert.All(title.Projection.SpanMap, entry =>
        {
            Assert.InRange(entry.VerbatimStart, 0, title.VerbatimText.Length - 1);
            Assert.Equal(
                title.VerbatimText.Substring(entry.VerbatimStart, entry.VerbatimLength),
                title.Projection.RawParserText.Substring(entry.RawStart, entry.RawLength));
        });
    }

    [Fact]
    public void A_line_built_from_a_string_is_its_own_projection_rather_than_empty()
    {
        // Anything not produced by glyph extraction has nothing to reconstruct. Blanking it would
        // silently erase the text; standing in for it, with no span map, says so honestly.
        var line = new PdfLine(1, 700, 11, "Session I: Welcome", 0, "", 0, 72, 400, "Arial", "k");

        Assert.Equal("Session I: Welcome", line.Projection.VerbatimText);
        Assert.False(line.Projection.HasGlyphProvenance);
        Assert.Empty(line.Projection.Resolve(0, 7));
    }

    [Fact]
    public void Joining_occurrences_rebases_the_map_instead_of_re_deriving_it()
    {
        var first = PdfSourceTextProjection.Identity("Session I") with
        {
            SpanMap = [new PdfVerbatimSpanMapEntry(0, 7, 0, 7, 0, 1, 10, 50, 100, 110)],
        };
        var second = PdfSourceTextProjection.Identity("Welcome") with
        {
            SpanMap = [new PdfVerbatimSpanMapEntry(0, 7, 0, 7, 1, 1, 60, 100, 100, 110)],
        };

        var joined = PdfSourceTextProjection.Join([first, second]);

        Assert.Equal("Session I Welcome", joined.VerbatimText);
        Assert.Equal([0, 10], joined.SpanMap.Select(entry => entry.VerbatimStart));
        // Geometry travels unchanged: composition moves offsets, never measurements.
        Assert.Equal([10.0, 60.0], joined.SpanMap.Select(entry => entry.Left));
    }

    [Fact]
    public void The_projection_declares_its_version_so_a_stored_span_is_interpretable()
    {
        var blocks = Blocks();

        Assert.All(blocks.Take(50), block =>
            Assert.Equal(PdfSourceTextProjection.CurrentVersion, block.Projection.ProjectionVersion));
    }

    private static string Strip(string value) =>
        new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private static IReadOnlyList<PdfSemanticBlock> Blocks()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, Pdf.Replace('/', Path.DirectorySeparatorChar));
        IReadOnlyList<PdfLine> lines;
        using (var document = UglyToad.PdfPig.PdfDocument.Open(path))
        {
            lines = PdfLineExtraction.ExtractLines(document);
        }

        return PdfSemanticBlockGrouper.Build(PdfLineBlockFilter.Analyze(lines), includeRiskLines: true);
    }
}
