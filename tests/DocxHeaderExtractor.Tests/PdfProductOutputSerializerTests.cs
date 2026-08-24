using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M9.3 locks. The serializer only reshapes what M9.1/M9.2 already decided: it may not re-litigate
/// emission, fill an unresolved relation, or reach for anything beyond <see cref="PdfFinalStructure"/>
/// and <see cref="PdfOutputDecision"/>.
/// </summary>
public sealed class PdfProductOutputSerializerTests
{
    [Fact]
    public void OnlyEmitTrueDecisionsAreSerialized()
    {
        var structure = Project(
            (Structure("b1"), "1 Introduction"),
            (Structure("b2") with { StructuralScope = "appendix_table" }, "4 3 Validation"));

        var output = PdfProductOutputSerializer.Serialize(structure, PdfOutputDecisionPolicy.Decide(structure));

        var heading = Assert.Single(output.Headings);
        Assert.Equal("1 Introduction", heading.Text);
    }

    /// <summary>
    /// The emission invariant belongs to the decision policy, but a record without a canonical
    /// occurrence can never be written back, so the serializer re-checks it rather than trusting an
    /// emit flag blindly.
    /// </summary>
    [Fact]
    public void AHeadingWithoutCanonicalGroundingIsNeverEmittedEvenIfMarkedEmit()
    {
        var structure = PdfFinalStructureProjection.Project("sha", [Structure("b1")],
            [Fact("b1", 0, "4 3 Validation")], []);
        var forcedEmit = new[] { new PdfOutputDecision(structure.Headings[0].Id, true, false, []) };

        var output = PdfProductOutputSerializer.Serialize(structure, forcedEmit);

        Assert.Empty(output.Headings);
    }

    [Fact]
    public void TextComesFromTheGroundedHeadingTextNotTheObservedPdfText()
    {
        var fact = Fact("b1", 0, "4 3 Ca che-Control");
        var grounding = new PdfCanonicalGrounding("b1", 90, "@body[1]/p[90]", new DocxTextSpan(0, 17),
            "4.3 Cache-Control and the rest of the paragraph");
        var structure = PdfFinalStructureProjection.Project("sha", [Structure("b1")], [fact], [grounding]);

        var output = PdfProductOutputSerializer.Serialize(structure, PdfOutputDecisionPolicy.Decide(structure));

        var heading = Assert.Single(output.Headings);
        Assert.Equal("4.3 Cache-Control", heading.Text);
        Assert.Equal(90, heading.ParagraphIndex);
        Assert.Equal("@body[1]/p[90]", heading.StableId);
    }

    [Fact]
    public void OutputOrderMatchesFinalStructureSourceOrder()
    {
        var structure = Project(("b2", 1, "2 Overview"), ("b1", 0, "1 Introduction"));

        var output = PdfProductOutputSerializer.Serialize(structure, PdfOutputDecisionPolicy.Decide(structure));

        Assert.Equal(structure.Headings.Select(h => h.Id), output.Headings.Select(h => h.Id));
    }

    [Fact]
    public void UnresolvedHierarchyIsCarriedVerbatimNeverFilled()
    {
        var structure = Project(("b1", 0, "Topic without a marker"));

        var output = PdfProductOutputSerializer.Serialize(structure, PdfOutputDecisionPolicy.Decide(structure));

        var heading = Assert.Single(output.Headings);
        Assert.Null(heading.Level);
        Assert.Null(heading.ParentId);
    }

    [Fact]
    public void RequiresReviewAndReasonsComeFromTheDecisionNotRecomputed()
    {
        var structure = Project(("b1", 0, "Topic without a marker"));

        var decisions = PdfOutputDecisionPolicy.Decide(structure);
        var output = PdfProductOutputSerializer.Serialize(structure, decisions);

        var decision = Assert.Single(decisions);
        var heading = Assert.Single(output.Headings);
        Assert.Equal(decision.RequiresReview, heading.RequiresReview);
        Assert.Equal(decision.Reasons, heading.Reasons);
    }

    [Fact]
    public void SerializationIsDeterministicOnTheSameFrozenInput()
    {
        var structure = Project(("b1", 0, "1 Introduction"), ("b2", 1, "2 Overview"));
        var decisions = PdfOutputDecisionPolicy.Decide(structure);

        var first = PdfProductOutputSerializer.Serialize(structure, decisions);
        var second = PdfProductOutputSerializer.Serialize(structure, decisions);

        Assert.Equal(
            first.Headings.Select(h => (h.Id, h.Text, h.Level, h.ParentId, h.RequiresReview, string.Join(",", h.Reasons))),
            second.Headings.Select(h => (h.Id, h.Text, h.Level, h.ParentId, h.RequiresReview, string.Join(",", h.Reasons))));
    }

    private static PdfFinalStructure Project(params (string Id, int Order, string Text)[] cases) =>
        PdfFinalStructureProjection.Project(
            "sha",
            cases.Select(item => Structure(item.Id)).ToArray(),
            cases.Select(item => Fact(item.Id, item.Order, item.Text)).ToArray(),
            cases.Select(item => new PdfCanonicalGrounding(item.Id, item.Order,
                $"@body[1]/p[{item.Order}]", new DocxTextSpan(0, item.Text.Length), item.Text)).ToArray());

    private static PdfFinalStructure Project(params (PdfValidatedStructure Structure, string Text)[] cases) =>
        PdfFinalStructureProjection.Project(
            "sha",
            cases.Select(item => item.Structure).ToArray(),
            cases.Select((item, index) => Fact(item.Structure.SourceId, index, item.Text)).ToArray(),
            cases.Select((item, index) => new PdfCanonicalGrounding(item.Structure.SourceId, index,
                $"@body[1]/p[{index}]", new DocxTextSpan(0, item.Text.Length), item.Text)).ToArray());

    private static PdfValidatedStructure Structure(string id) =>
        new(id, 1, null, "unresolved", "requires_review") { StructuralScope = "document_body" };

    private static PdfHierarchyFactAudit Fact(string id, int order, string text) =>
        new(id, order, 1, "document_body", "document_body", null, null, false, null, null, null,
            null, "relationship_unresolved", [])
        {
            FactId = $"p1:{id}:s0-{text.Length}",
            HeadingSpan = new TextOffsetSpan(0, text.Length),
            SourceBlockText = text,
            HeadingText = text,
        };
}
