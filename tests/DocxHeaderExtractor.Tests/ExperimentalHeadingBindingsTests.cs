using System.Text.Json;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.Tests;

public sealed class ExperimentalHeadingBindingsTests
{
    static HeadingRecord Heading(params HeadingSourcePart[] parts) => new() { Index = 1, StableId = "p1", Text = "heading", Level = 1,
        SourceParts = parts, ValidatedSourcePartCount = parts.Length };

    [Fact]
    public void Multiple_source_parts_require_exact_source_and_valid_utf16_spans()
    {
        var heading = Heading(new("p1", 1, new TextOffsetSpan(0, 2), "😀abc"), new("p2", 2, new TextOffsetSpan(0, 3), "xyz"));
        Assert.True(ExperimentalHeadingBindings.Check([heading], new Dictionary<string, string> { ["p1"] = "😀abc", ["p2"] = "xyz" }).Complete);
        Assert.False(ExperimentalHeadingBindings.Check([heading], new Dictionary<string, string> { ["p1"] = "😀abc" }).Complete);
        Assert.False(ExperimentalHeadingBindings.Check([heading], new Dictionary<string, string> { ["p1"] = "changed", ["p2"] = "xyz" }).Complete);
        var bad = Heading(new HeadingSourcePart("p1", 1, new TextOffsetSpan(0, 6), "😀abc"));
        Assert.False(ExperimentalHeadingBindings.Check([bad], new Dictionary<string, string> { ["p1"] = "😀abc" }).Complete);
        var split = Heading(new HeadingSourcePart("p1", 1, new TextOffsetSpan(0, 1), "😀abc"));
        Assert.False(ExperimentalHeadingBindings.Check([split], new Dictionary<string, string> { ["p1"] = "😀abc" }).Complete);
        Assert.False(ExperimentalHeadingBindings.Check([], new Dictionary<string, string>()).Complete);
    }

    [Fact]
    public void Cached_outline_json_does_not_contain_the_required_source_parts()
    {
        var before = Heading(new HeadingSourcePart("p1", 1, new TextOffsetSpan(0, 3), "abc"));
        var after = JsonSerializer.Deserialize<HeadingRecord>(JsonSerializer.Serialize(before))!;
        Assert.Null(after.SourceParts);
        Assert.Equal(0, after.ValidatedSourcePartCount);
        var result = ExperimentalHeadingBindings.Check([after], new Dictionary<string, string> { ["p1"] = "abc" });
        Assert.False(result.Complete);
        Assert.Contains(result.Problems, p => p.Contains("SOURCE_PARTS_MISSING_OR_INCOMPLETE"));
    }

    [Fact]
    public void Snapshot_roundtrips_multiple_parts_but_requires_the_original_file_and_text()
    {
        var heading = Heading(new("p1", 1, new TextOffsetSpan(0, 3), "abc"), new("p2", 2, new TextOffsetSpan(0, 2), "😀x"));
        var texts = new Dictionary<string, string> { ["p1"] = "abc", ["p2"] = "😀x" };
        var sha = new string('a', 64);
        var captured = ExperimentalHeadingBindings.Capture(sha, [heading], texts)!;
        var snapshot = JsonSerializer.Deserialize<ExperimentalHeadingBindings.Snapshot>(JsonSerializer.Serialize(captured))!;
        Assert.Equal(2, Assert.Single(snapshot.Headings).Parts.Count);
        Assert.True(ExperimentalHeadingBindings.Verify(snapshot, sha, texts).Complete);
        Assert.False(ExperimentalHeadingBindings.Verify(snapshot, new string('b', 64), texts).Complete);
        Assert.False(ExperimentalHeadingBindings.Verify(snapshot, sha, new Dictionary<string, string> { ["p1"] = "abc", ["p2"] = "changed" }).Complete);
        Assert.Null(ExperimentalHeadingBindings.Capture(sha, [heading], new Dictionary<string, string>()));
        Assert.Null(ExperimentalHeadingBindings.Capture("not-a-sha", [heading], texts));
    }
}
