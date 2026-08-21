using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfLayoutEvidenceOutlineTests
{
    [Fact]
    public void AnalystBudgetCoversEveryPageBeforeTakingSecondBlock()
    {
        var blocks = Enumerable.Range(1, 28)
            .SelectMany(page => new[]
            {
                Block($"p{page}-a", page, 700),
                Block($"p{page}-b", page, 650),
            })
            .ToArray();

        var selection = PdfLayoutEvidenceOutline.SelectAnalystCandidates(blocks, 40);

        Assert.Equal(56, selection.Available);
        Assert.Equal(28, selection.AvailablePages);
        Assert.Equal(40, selection.Selected.Count);
        Assert.Equal(28, selection.SelectedPages);
        Assert.All(Enumerable.Range(1, 28), page =>
            Assert.Contains(selection.Selected, block => block.Page == page));
    }

    private static PdfSemanticBlock Block(string id, int page, double y)
    {
        var line = new PdfLine(page, y, 14, id, 0.8, "", 0, 72, 300, "serif", "0.00,0.20,0.40");
        return new PdfSemanticBlock(id, [line], PdfStyleClusterProfile.StyleOf(line), page, y, y, 72, 300, id);
    }
}
