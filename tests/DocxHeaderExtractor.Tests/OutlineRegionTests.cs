using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The part of a new version that must be decided again, from the stored outline alone (no model). Paragraphs are given by their signature (the
/// text stands for its signature here); headings are the paragraphs at the given indices.
/// </summary>
public sealed class OutlineRegionTests
{
    static readonly string[] Base = ["T1", "a", "b", "H2", "c", "d", "e", "H3", "f", "g"];

    static IReadOnlyList<StoredParagraph> Paras(IEnumerable<string> signatures) =>
        signatures.Select((s, i) => new StoredParagraph($"body[1]/p[{i + 1}]", i, s)).ToArray();

    static HeadingRecord H(int index, string text) => new() { Index = index, Level = 1, Text = text, StableId = $"body[1]/p[{index + 1}]" };

    static StoredOutline Stored(IEnumerable<string> signatures, params HeadingRecord[] headings) =>
        new(OutlineStore.Version, "sha", "k", "f.docx", DateTimeOffset.UtcNow, Paras(signatures), [], headings, SignatureVersion: OutlineStore.SignatureVersion);

    static readonly HeadingRecord[] Headings = [H(3, "H2"), H(7, "H3")];

    [Fact]
    public void An_unchanged_version_needs_no_decision()
    {
        var plan = OutlineRegion.Plan(Stored(Base, Headings), Paras(Base), []);
        Assert.Equal("UNCHANGED", plan.Status);
        Assert.Empty(plan.Spans);
        Assert.Equal(2, plan.CarriedHeadings.Count);
        Assert.Empty(plan.HeadingsToDecide);
    }

    [Fact]
    public void Paragraphs_added_under_a_heading_form_one_span_and_the_headings_are_carried()
    {
        var current = new[] { "T1", "a", "b", "x1", "x2", "H2", "c", "d", "e", "H3", "f", "g" };
        var plan = OutlineRegion.Plan(Stored(Base, Headings), Paras(current), []);
        Assert.Equal("REGION", plan.Status);
        var span = Assert.Single(plan.Spans);
        Assert.True(span.Start <= 3 && span.End >= 5);
        Assert.Equal(2, plan.CarriedHeadings.Count);
        Assert.Equal([5, 9], plan.CarriedHeadings.Select(h => h.Index));
        Assert.Empty(plan.HeadingsToDecide);
    }

    [Fact]
    public void An_edited_heading_is_decided_again_and_the_others_are_carried()
    {
        var current = new[] { "T1", "a", "b", "H2 (sửa)", "c", "d", "e", "H3", "f", "g" };
        var plan = OutlineRegion.Plan(Stored(Base, Headings), Paras(current), []);
        Assert.Equal("REGION", plan.Status);
        Assert.Equal(["H2"], plan.HeadingsToDecide.Select(h => h.Text));
        Assert.Equal(["H3"], plan.CarriedHeadings.Select(h => h.Text));
        Assert.Contains(plan.Spans, s => s.Start <= 3 && 3 < s.End);
    }

    [Fact]
    public void Two_distant_edits_form_two_spans()
    {
        var current = new[] { "T1 (sửa)", "a", "b", "H2", "c", "d", "e", "H3", "f", "g (sửa)" };
        var plan = OutlineRegion.Plan(Stored(Base, Headings), Paras(current), []);
        Assert.Equal("REGION", plan.Status);
        Assert.Equal(2, plan.Spans.Count);
        Assert.Equal(2, plan.CarriedHeadings.Count);
    }

    [Fact]
    public void A_removed_heading_is_decided_again_at_the_place_it_was()
    {
        var current = new[] { "T1", "a", "b", "H2", "c", "d", "e", "f", "g" };
        var plan = OutlineRegion.Plan(Stored(Base, Headings), Paras(current), []);
        Assert.Equal("REGION", plan.Status);
        Assert.Equal(["H3"], plan.HeadingsToDecide.Select(h => h.Text));
        Assert.Single(plan.CarriedHeadings);
        Assert.NotEmpty(plan.Spans);
    }

    [Fact]
    public void A_tracked_change_in_either_version_extracts_the_whole_file()
    {
        var plan = OutlineRegion.Plan(Stored(Base, Headings), Paras(Base), ["tracked_changes"]);
        Assert.Equal("FULL", plan.Status);
        Assert.StartsWith("UNSUPPORTED_REGION:tracked_changes", plan.Reason);
        Assert.Equal(2, plan.HeadingsToDecide.Count);
    }

    [Fact]
    public void A_change_of_more_than_the_limit_extracts_the_whole_file()
    {
        var current = new[] { "T1", "a", "X", "Y", "Z", "W", "e", "H3", "f", "g" };
        var plan = OutlineRegion.Plan(Stored(Base, Headings), Paras(current), []);
        Assert.Equal("FULL", plan.Status);
        Assert.StartsWith("CHANGED_SHARE", plan.Reason);
    }
}
