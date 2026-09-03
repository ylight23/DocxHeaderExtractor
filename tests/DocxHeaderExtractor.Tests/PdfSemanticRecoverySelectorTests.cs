using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfSemanticRecoverySelectorTests
{
    [Fact]
    public void SelectsRepresentedUnresolvedBlockWithoutUsingGold()
    {
        var resolved = Block("resolved", "Chapter One", 700);
        var unresolved = Block("unresolved", "Topic Label", 680, "Body prose continues with enough context.");
        var annotations = new[] { Annotation(resolved), Annotation(unresolved) };

        var selection = PdfSemanticRecoverySelector.Select([resolved, unresolved], [resolved], annotations);

        Assert.Equal(2, selection.RepresentedBlockCount);
        Assert.Equal(1, selection.DeterministicCandidateCount);
        Assert.Equal(new[] { "unresolved/line0" }, selection.EligibleBlocks.Select(block => block.Id));
        Assert.Equal("unresolved", selection.Origins["unresolved/line0"].SourceBlockId);
    }

    [Fact]
    public void DoesNotSendHardExcludedTableFactToSemanticRecovery()
    {
        var table = Block("table", "DAY 1: TUESDAY", 700);
        var annotation = new PdfLineBlockAnnotation(table.Lines[0], false, false, true, false, "table-like");

        var selection = PdfSemanticRecoverySelector.Select([table], [], [annotation]);

        Assert.Empty(selection.EligibleBlocks);
    }

    [Fact]
    public void KeepsAnotherOccurrenceWithTheSameTitleEligible()
    {
        var resolved = Block("toc-overview", "Overview", 700);
        var bodyOccurrence = Block("body-overview", "Overview Topic", 650, "Body prose continues with enough context.");
        var annotations = new[] { Annotation(resolved), Annotation(bodyOccurrence) };

        var selection = PdfSemanticRecoverySelector.Select([resolved, bodyOccurrence], [resolved], annotations);

        Assert.Equal(new[] { "body-overview/line0" }, selection.EligibleBlocks.Select(block => block.Id));
    }

    [Fact]
    public void DoesNotUseARegularSentenceAsRecoveryWork()
    {
        var body = Block("body", "This is a regular sentence", 700, "It continues with enough following prose.");

        var selection = PdfSemanticRecoverySelector.Select([body], [], [Annotation(body)]);

        Assert.Empty(selection.EligibleBlocks);
    }

    [Fact]
    public void ContextProfilesKeepEligibilityStableAndOnlyAddSourceSiblingContext()
    {
        var first = Block("first", "Regional Update", 700, "Body prose continues with enough context for the recovery selector.");
        var middle = Block("middle", "Program Review", 650, "Body prose continues with enough context for the recovery selector.");
        var last = Block("last", "Closing Discussion", 600, "Body prose continues with enough context for the recovery selector.");
        var annotations = new[] { Annotation(first), Annotation(middle), Annotation(last) };

        var current = PdfSemanticRecoverySelector.Select([first, middle, last], [], annotations,
            PdfSemanticRecoveryOptions.CurrentV6);
        var neighborhood = PdfSemanticRecoverySelector.Select([first, middle, last], [], annotations,
            PdfSemanticRecoveryOptions.NeighborhoodMicroBatch);

        Assert.Equal(current.EligibleBlocks.Select(block => block.Id), neighborhood.EligibleBlocks.Select(block => block.Id));
        Assert.Empty(current.Contexts["middle/line0"].SiblingStructuralBlocks);
        Assert.Equal(2, neighborhood.Contexts["middle/line0"].SiblingStructuralBlocks.Count);
        Assert.All(neighborhood.Contexts["middle/line0"].SiblingStructuralBlocks, sibling => Assert.Contains(": ", sibling));
    }

    private static PdfSemanticBlock Block(string id, string text, double y, string? body = null)
    {
        var line = new PdfLine(1, y, 12, text, 0, "", 0, 72, 300, "serif", "black");
        var lines = body is null
            ? new[] { line }
            : new[] { line, new PdfLine(1, y - 14, 12, body, 0, "", 0, 72, 300, "serif", "black") };
        return new PdfSemanticBlock(id, lines, PdfStyleClusterProfile.StyleOf(line), 1, y, y - (body is null ? 0 : 14), 72, 300,
            string.Join(" ", lines.Select(item => item.Text)));
    }

    private static PdfLineBlockAnnotation Annotation(PdfSemanticBlock block) =>
        new(block.Lines[0], false, false, false, false, "semantic-candidate");
}
