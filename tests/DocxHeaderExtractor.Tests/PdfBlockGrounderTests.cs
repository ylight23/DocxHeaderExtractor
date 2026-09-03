using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfBlockGrounderTests
{
    [Fact]
    public void GrounderKeepsDirectHeadingRoleWhenClusterSaysTableButMarksConflict()
    {
        var heading = Block("b1", "AVAILABILITY OF INFORMATION", 16);
        var table = Block("b2", "Net Commitments Disbursements", 12);
        var profile = new PdfStyleClusterProfile(
            new PdfStyleKey(10, "body", "0.00,0.00,0.00"),
            [],
            new HashSet<PdfStyleKey> { heading.PrimaryStyle, table.PrimaryStyle },
            new HashSet<PdfStyleKey> { heading.PrimaryStyle },
            new HashSet<PdfStyleKey>());
        var samples = new[]
        {
            new PdfSemanticClusterSample("c1", heading.PrimaryStyle, 1, 1, heading.Text.Length, [heading.Text]),
            new PdfSemanticClusterSample("c2", table.PrimaryStyle, 1, 1, table.Text.Length, [table.Text]),
        };
        var clusterDecisions = new[]
        {
            new PdfSemanticClusterDecision("c1", PdfSemanticClusterRole.HeadingTopic, 0.9, "heading style"),
            new PdfSemanticClusterDecision("c2", PdfSemanticClusterRole.TableOrChartLabel, 0.9, "table label style"),
        };
        var blockDecisions = new[]
        {
            new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.9, "topic"),
            new PdfBlockDecision("b2", PdfBlockRole.HeadingTopic, 0.9, "model over-called heading"),
        };

        var result = PdfBlockGrounder.Ground([heading, table], blockDecisions, profile, samples, clusterDecisions);

        Assert.Contains(result.Headings, h => h.Id == "b1" && h.Evidence == "block-role+cluster-heading");
        Assert.Contains(result.Headings, h => h.Id == "b2" && h.Evidence == "block-role+cluster-table-conflict");
        Assert.DoesNotContain(result.Rejected, r => r.Id == "b2");
    }

    [Fact]
    public void PdfFirstGroundingDoesNotReapplySparseStyleRetrievalGate()
    {
        var heading = Block("b1", "Chapter 1 Vectors", 12);
        var profile = new PdfStyleClusterProfile(
            new PdfStyleKey(10, "body", "0.00,0.00,0.00"), [],
            new HashSet<PdfStyleKey>(), new HashSet<PdfStyleKey>(), new HashSet<PdfStyleKey>());

        var result = PdfBlockGrounder.Ground(
            [heading], [new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.9, "topic")],
            profile, [], [], requireLearnedCandidateStyle: false);

        Assert.Contains(result.Headings, item => item.Id == "b1");
        Assert.DoesNotContain(result.Rejected, item => item.Reason == "not-visual-candidate-style");
    }

    private static PdfSemanticBlock Block(string id, string text, double fontSize)
    {
        var line = new PdfLine(
            Page: 1,
            Y: 700 - fontSize,
            FontSize: fontSize,
            Text: text,
            BoldRatio: 0.8,
            LeadingBoldPrefix: "",
            ItalicRatio: 0,
            Left: 72,
            Right: 420,
            FontName: fontSize > 14 ? "serif" : "sans",
            FillColorKey: fontSize > 14 ? "0.00,0.20,0.40" : "0.10,0.45,0.70");
        return new PdfSemanticBlock(
            id,
            [line],
            PdfStyleClusterProfile.StyleOf(line),
            Page: 1,
            TopY: line.Y,
            BottomY: line.Y,
            Left: 72,
            Right: 420,
            Text: text);
    }
}
