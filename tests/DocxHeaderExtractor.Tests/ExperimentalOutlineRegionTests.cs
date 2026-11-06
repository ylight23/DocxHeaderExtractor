using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.Tests;

public sealed class ExperimentalOutlineRegionTests
{
    static IReadOnlyList<StoredParagraph> Paragraphs(IEnumerable<string> text) => text.Select((s, i) => new StoredParagraph($"p{i}", i, s)).ToArray();
    static StoredOutline Stored(IReadOnlyList<StoredParagraph> p, params HeadingRecord[] h) => new(OutlineStore.Version, "sha", "cfg", "doc", DateTimeOffset.UtcNow, p, [], h, SignatureVersion: OutlineStore.SignatureVersion);
    static HeadingRecord Heading(int i) => new() { Index = i, StableId = $"p{i}", Text = $"heading {i}", Level = 1 };

    [Fact]
    public void Missing_or_changed_dependencies_and_unbound_headings_require_full_extraction()
    {
        var p = Paragraphs(Enumerable.Range(0, 50).Select(i => $"p{i}")); var old = Stored(p, Heading(25));
        Assert.Equal("DEPENDENCIES_NOT_VERIFIED", ExperimentalOutlineRegion.Plan(old, p, [], null, "a", true).Reason);
        Assert.Equal("DEPENDENCIES_CHANGED", ExperimentalOutlineRegion.Plan(old, p, [], "a", "b", true).Reason);
        Assert.Equal("HEADING_BINDINGS_INCOMPLETE", ExperimentalOutlineRegion.Plan(old, p, [], "a", "a", false).Reason);
        Assert.Equal("UNSUPPORTED_REGIONS_CHANGED", ExperimentalOutlineRegion.Plan(old, p, ["merged_cells"], "a", "a", true).Reason);
    }

    [Fact]
    public void Changed_document_with_duplicated_paragraphs_cannot_use_ambiguous_alignment()
    {
        var rows = Enumerable.Range(0, 50).Select(i => $"p{i}").ToArray(); rows[4] = rows[5] = "duplicate";
        var p = Paragraphs(rows); rows[25] = "changed";
        var plan = ExperimentalOutlineRegion.Plan(Stored(p, Heading(30)), Paragraphs(rows), [], "a", "a", true);
        Assert.Equal("FULL", plan.Status);
        Assert.Equal("REPEATED_PARAGRAPH_ALIGNMENT_UNCERTAIN", plan.Reason);
    }

    [Fact]
    public void Unchanged_duplicate_far_from_the_edit_does_not_force_full_extraction()
    {
        var rows = Enumerable.Range(0, 100).Select(i => $"p{i}").ToArray(); rows[0] = rows[1] = "duplicate";
        var p = Paragraphs(rows); rows[70] = "changed";
        var plan = ExperimentalOutlineRegion.Plan(Stored(p, Heading(80)), Paragraphs(rows), [], "a", "a", true);
        Assert.Equal("REGION", plan.Status);
    }

    [Fact]
    public void A_changed_duplicate_count_requires_full_extraction_even_far_from_the_edit()
    {
        var rows = Enumerable.Range(0, 100).Select(i => $"p{i}").ToArray(); rows[0] = rows[1] = "duplicate";
        var p = Paragraphs(rows); rows[70] = "duplicate";
        var plan = ExperimentalOutlineRegion.Plan(Stored(p, Heading(80)), Paragraphs(rows), [], "a", "a", true);
        Assert.Equal("FULL", plan.Status);
        Assert.Equal("REPEATED_PARAGRAPH_ALIGNMENT_UNCERTAIN", plan.Reason);
    }

    [Fact]
    public void A_repeated_heading_requires_full_extraction_even_when_far_from_the_edit()
    {
        var rows = Enumerable.Range(0, 100).Select(i => $"p{i}").ToArray(); rows[0] = rows[1] = "duplicate";
        var p = Paragraphs(rows); rows[70] = "changed";
        var plan = ExperimentalOutlineRegion.Plan(Stored(p, Heading(0)), Paragraphs(rows), [], "a", "a", true);
        Assert.Equal("FULL", plan.Status);
        Assert.Equal("REPEATED_PARAGRAPH_ALIGNMENT_UNCERTAIN", plan.Reason);
    }

    [Fact]
    public void Candidate_region_has_twenty_paragraphs_of_context_and_redecides_headings_within_it()
    {
        var rows = Enumerable.Range(0, 100).Select(i => $"p{i}").ToArray(); var p = Paragraphs(rows);
        rows[50] = "changed";
        var plan = ExperimentalOutlineRegion.Plan(Stored(p, Heading(35), Heading(80)), Paragraphs(rows), [], "a", "a", true);
        Assert.Equal("REGION", plan.Status);
        var span = Assert.Single(plan.Spans);
        Assert.Equal((30, 71), (span.Start, span.End));
        Assert.Contains(plan.HeadingsToDecide, h => h.Text == "heading 35");
        Assert.DoesNotContain(plan.CarriedHeadings, h => h.Text == "heading 35");
        Assert.Contains(plan.CarriedHeadings, h => h.Text == "heading 80");
    }

    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("move")]
    public void Insert_delete_and_move_create_candidate_regions_and_require_later_tree_review(string operation)
    {
        var rows = Enumerable.Range(0, 100).Select(i => $"p{i}").ToList();
        var original = Paragraphs(rows);
        if (operation == "add") rows.Insert(50, "new heading or body");
        else if (operation == "delete") rows.RemoveAt(50);
        else { rows.RemoveAt(50); rows.Insert(65, "p50"); }
        var plan = ExperimentalOutlineRegion.Plan(Stored(original, Heading(35), Heading(80)), Paragraphs(rows), [], "a", "a", true);
        Assert.Equal("REGION", plan.Status);
        Assert.Contains(plan.Spans, s => s.Start <= 50 && s.End > 50);
        Assert.Contains(plan.HeadingsToDecide, h => h.Text == "heading 35");
    }
}
