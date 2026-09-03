using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfProposalConflictResolverTests
{
    [Fact]
    public void VisualTableConflictLowersTextHeadingToUnresolved()
    {
        var resolved = PdfProposalConflictResolver.Resolve(
            [new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.9, "text")],
            [new PdfVisualBlockDecision("b1", PdfBlockRole.TableOrChartLabel, 0.8, "visible table grid", "{}")] ,
            Contexts("table", ["table_like"]));

        Assert.Equal(PdfBlockRole.Uncertain, Assert.Single(resolved.Decisions).Role);
        Assert.Equal("conflict-lowered-to-unresolved", Assert.Single(resolved.Audit).Resolution);
    }

    [Fact]
    public void VisualHeadingEscalatesTextBodyProposalToUnresolved()
    {
        var resolved = PdfProposalConflictResolver.Resolve(
            [new PdfBlockDecision("b1", PdfBlockRole.BodySentence, 0.9, "text")],
            [new PdfVisualBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.9, "visible label", "{}")] ,
            Contexts("document_body", []));

        Assert.Equal(PdfBlockRole.Uncertain, Assert.Single(resolved.Decisions).Role);
        Assert.Equal("visual-heading-escalated-unresolved", Assert.Single(resolved.Audit).Resolution);
    }

    [Fact]
    public void VisualHeadingCorroboratesButDoesNotChangeTextHeading()
    {
        var model = new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.9, "text");
        var resolved = PdfProposalConflictResolver.Resolve(
            [model], [new PdfVisualBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.9, "visible label", "{}")] ,
            Contexts("document_body", []));

        Assert.Equal(model, Assert.Single(resolved.Decisions));
        Assert.Equal("visual-corroborated", Assert.Single(resolved.Audit).Resolution);
    }

    [Fact]
    public void MarkerAndSemanticHeadingOutweighVisualBodyWithoutTableEvidence()
    {
        var model = new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.9, "text");
        var resolved = PdfProposalConflictResolver.Resolve(
            [model], [new PdfVisualBlockDecision("b1", PdfBlockRole.BodySentence, 0.8, "prose below", "{}")] ,
            Contexts("document_body", ["marker:loose_labelled"]));

        Assert.Equal(PdfBlockRole.HeadingTopic, Assert.Single(resolved.Decisions).Role);
        Assert.Equal("marker-semantic-retained-over-visual-conflict", Assert.Single(resolved.Audit).Resolution);
    }

    [Fact]
    public void MarkerOnlyProposalRequiresVisualHeadingEvidence()
    {
        var model = new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.9, "text");
        var withoutVisual = PdfProposalConflictResolver.Resolve([model], [], Contexts("document_body", ["marker:loose_labelled", "marker_only_source"]));
        var corroborated = PdfProposalConflictResolver.Resolve(
            [model], [new PdfVisualBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.9, "heading above body", "{}")] ,
            Contexts("document_body", ["marker:loose_labelled", "marker_only_source"]));

        Assert.Equal(PdfBlockRole.Uncertain, Assert.Single(withoutVisual.Decisions).Role);
        Assert.Equal("marker-only-needs-visual", Assert.Single(withoutVisual.Audit).Resolution);
        Assert.Equal(PdfBlockRole.HeadingTopic, Assert.Single(corroborated.Decisions).Role);
        Assert.Equal("visual-corroborated", Assert.Single(corroborated.Audit).Resolution);
    }

    private static IReadOnlyDictionary<string, PdfCandidateContext> Contexts(string scope, IReadOnlyList<string> evidence) =>
        new Dictionary<string, PdfCandidateContext>(StringComparer.Ordinal)
        {
            ["b1"] = new PdfCandidateContext(
                new PdfSourceFacts("b1", "text", 1, 1, 0, 100, 100, 90, scope, evidence),
                [], [], [], "document_body", []),
        };
}
