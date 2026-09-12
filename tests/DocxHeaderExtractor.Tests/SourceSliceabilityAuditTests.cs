using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class SourceSliceabilityAuditTests
{
    [Fact]
    public void ParserOwnedSlicesReconstructSourceExactlyAndUseStableAliasIds()
    {
        var paragraph = new SourceParagraph
        {
            SourceId = "body[1]/p[4]",
            SourceOrdinal = 3,
            Text = "Alpha Beta Gamma",
            TextSpans = [new(0, 5, true, false, false, 12), new(6, 10, false, false, false, 11), new(11, 16, false, false, false, 11)],
            LineBreakOffsets = [10],
            SourceSegments = [new(0, 5, 0, 0), new(6, 10, 1, 0), new(11, 16, 2, 0)],
            Style = new(),
            Numbering = new(),
            Layout = new(),
        };

        var result = A99V6SourceSliceabilityAuditRunner.BuildSlices(paragraph, "S0003");

        Assert.True(result.ReconstructedExactly);
        Assert.Equal("S0003", result.SourceAlias);
        Assert.Equal("body[1]/p[4]", result.SourceId);
        Assert.Equal("Alpha Beta Gamma", string.Concat(result.Slices.Select(slice => slice.Text)));
        Assert.Equal("S0003.0001", result.Slices[0].SliceId);
        Assert.All(result.Slices, slice => Assert.DoesNotContain("Gold", string.Join(',', slice.BoundaryKindsAtStart), StringComparison.OrdinalIgnoreCase));
    }
}
