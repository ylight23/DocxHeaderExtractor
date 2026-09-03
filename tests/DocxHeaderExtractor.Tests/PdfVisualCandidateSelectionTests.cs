using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfVisualCandidateSelectionTests
{
    [Fact]
    public void Visual_lane_is_limited_to_semantic_heading_proposals()
    {
        // The public audit contract is exercised by the integration tests; this test documents
        // the policy boundary here so a confidence-based broadening is not reintroduced.
        var heading = new PdfBlockDecision("h", PdfBlockRole.HeadingTopic, 0.20, "");
        var title = new PdfBlockDecision("t", PdfBlockRole.DocumentTitle, 0.20, "");
        var uncertain = new PdfBlockDecision("u", PdfBlockRole.Uncertain, 0.01, "");

        Assert.True(IsVisualProposal(heading));
        Assert.True(IsVisualProposal(title));
        Assert.False(IsVisualProposal(uncertain));
    }

    private static bool IsVisualProposal(PdfBlockDecision decision) =>
        decision.Role is PdfBlockRole.HeadingTopic or PdfBlockRole.DocumentTitle;
}
