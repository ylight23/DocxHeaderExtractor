using DocxHeaderExtractor.DocumentProcessing.Changes;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.Tests;

public sealed class ReorderDetectorTests
{
    static string P(string text) => $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>";
    static string Pid(string id, string text) => $"<w:p xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\" w14:paraId=\"{id}\"><w:r><w:t>{text}</w:t></w:r></w:p>";
    static (ComparisonResult Plain, ComparisonResult Candidate) Compare(string a, string b)
    {
        var first = RawDocxFixtureTests.Build(a); var second = RawDocxFixtureTests.Build(b);
        try
        {
            var old = DocxVersionReader.Read(first); var current = DocxVersionReader.Read(second);
            return (DocxComparer.Compare(old, current), DocxComparer.Compare(old, current, new ComparerOptions(DetectReorders: true)));
        }
        finally { File.Delete(first); File.Delete(second); }
    }

    [Fact]
    public void Swap_reports_both_crossing_blocks_without_selecting_the_moved_side()
    {
        var (plain, result) = Compare(P("A") + P("B") + P("C") + P("D"), P("C") + P("D") + P("A") + P("B"));
        Assert.Null(plain.Reorders);
        Assert.Equal(plain.Changes, result.Changes);
        var blocks = result.Reorders!.Blocks;
        Assert.Equal(2, blocks.Count);
        Assert.Equal(new[] { "A", "B" }, blocks[0].Before.Select(p => p.Text));
        Assert.Equal(new[] { "C", "D" }, blocks[1].Before.Select(p => p.Text));
        Assert.All(blocks, b => Assert.True(b.Uncertain));
    }

    [Fact]
    public void Insertion_and_deletion_shift_positions_without_creating_reorder()
    {
        var (_, result) = Compare(P("A") + P("B") + P("C"), P("New") + P("A") + P("C"));
        Assert.Empty(result.Reorders!.Blocks);
    }

    [Fact]
    public void Repeated_text_without_unique_ids_is_not_used_as_an_anchor()
    {
        var (_, result) = Compare(P("A") + P("same") + P("same") + P("B"), P("same") + P("A") + P("B") + P("same"));
        Assert.Empty(result.Reorders!.Blocks);
        Assert.Equal(2, result.Reorders.UnmatchedOrAmbiguousBodyParagraphs);
    }

    [Fact]
    public void An_id_reused_for_changed_text_does_not_assert_identity()
    {
        var (_, result) = Compare(Pid("11111111", "A") + Pid("22222222", "B"),
            Pid("22222222", "B") + Pid("11111111", "C"));
        Assert.Empty(result.Reorders!.Blocks);
        Assert.Equal(1, result.Reorders.UnmatchedOrAmbiguousBodyParagraphs);
    }

    [Fact]
    public void Table_cells_are_not_claimed_as_body_reorder_blocks()
    {
        static string Table(string text) => "<w:tbl><w:tr><w:tc>" + P(text) + "</w:tc></w:tr></w:tbl>";
        var (_, result) = Compare(P("A") + Table("Cell") + P("B"), P("A") + P("B") + Table("Cell"));
        Assert.Empty(result.Reorders!.Blocks);
        Assert.True(result.Reorders.NonBodyParagraphsSkipped >= 2);
    }
}
