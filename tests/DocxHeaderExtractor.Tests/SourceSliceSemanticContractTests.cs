using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class SourceSliceSemanticContractTests
{
    [Fact]
    public void BinderReconstructsOnlyFromContiguousSliceIds()
    {
        var paragraph = new SourceParagraph
        {
            SourceId = "body[1]/p[4]", SourceOrdinal = 3, Text = "CHAPTER ONE",
            TextSpans = [new(0, 7, true, false, false, 12), new(8, 11, true, false, false, 12)],
            SourceSegments = [new(0, 7, 0, 0), new(8, 11, 1, 0)],
            Style = new(), Numbering = new(), Layout = new(),
        };
        var slices = A99V6SourceSliceabilityAuditRunner.BuildSlices(paragraph, "S0003").Slices;
        var heading = new SourceSliceSemanticHeading("S0003", "SOURCE_SLICE", slices.Select(slice => slice.SliceId).ToArray(), true, "CHAPTER");

        var proposals = SourceSliceSemanticBinder.Bind([heading], new Dictionary<string, IReadOnlyList<SourceSlice>> { ["S0003"] = slices }, out var observations);

        Assert.Single(proposals);
        Assert.Equal("CHAPTER ONE", proposals[0].VerbatimText);
        Assert.Equal(SourceSliceBindingStatus.Bound, observations[0].Status);
    }

    [Fact]
    public void BinderRejectsUnknownAndNonContiguousSliceIds()
    {
        var first = new SourceSlice("S0003.0001", "S0003", "body[1]/p[4]", 1, 0, 3, "ABC", [], []);
        var second = new SourceSlice("S0003.0002", "S0003", "body[1]/p[4]", 2, 3, 6, "DEF", [], []);
        var available = new Dictionary<string, IReadOnlyList<SourceSlice>> { ["S0003"] = [first, second] };
        var headings = new[]
        {
            new SourceSliceSemanticHeading("S0003", "SOURCE_SLICE", ["missing"], true, "ARTICLE"),
            new SourceSliceSemanticHeading("S0003", "SOURCE_SLICE", [second.SliceId, first.SliceId], true, "ARTICLE"),
        };

        var proposals = SourceSliceSemanticBinder.Bind(headings, available, out var observations);

        Assert.Empty(proposals);
        Assert.Equal(SourceSliceBindingStatus.InvalidSliceId, observations[0].Status);
        Assert.Equal(SourceSliceBindingStatus.InvalidOrder, observations[1].Status);
    }

    [Fact]
    public void ParserRejectsModelEchoTextAndNumericCoordinates()
    {
        var raw = "{\"headings\":[{\"source\":\"S0003\",\"selectionMode\":\"SOURCE_SLICE\",\"sliceIds\":[\"S0003.0001\"],\"isHeading\":true,\"semanticRole\":\"ARTICLE\",\"text\":\"forbidden\"}]}";

        Assert.Throws<FormatException>(() => SourceSliceSemanticContract.Parse(raw));
    }
}
