using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class HeadingOutlineProjectionTests
{
    [Fact]
    public void Outline_coordinates_do_not_replace_generic_pdf_source_authority()
    {
        var element = new ValidatedStructuralElement
        {
            Id = "structural:pdf:heading-1",
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            Sources =
            [
                new SourceReference(
                    "b17",
                    16,
                    new StructuralSpan(4, 20))
            ],
            Text = "generic source text",
            Level = 2,
            Validation = new StructuralValidation(true, true, true, true, 1, true, true, true, null),
            Decision = new StructuralDecision(StructuralDecisionOrigin.Model, nameof(HeadingDecisionStatus.HumanVerified), "test"),
            ProjectionMetadata = new StructuralProjectionMetadata
            {
                OutlineSourceId = "para-451",
                OutlineSourceOrdinal = 451,
                OutlineStableId = "para-451",
                OutlineHeadingSpan = new StructuralSpan(10, 26),
                OutlineText = "outline heading text",
            },
        };

        var source = element.Sources.Single();
        var projected = HeadingOutlineProjection.Project(
            new ValidatedStructure([element])).Single();

        Assert.Equal("b17", source.SourceId);
        Assert.Equal(16, source.SourceOrdinal);
        Assert.Equal(new StructuralSpan(4, 20), source.Span);

        Assert.Equal(451, projected.Index);
        Assert.Equal("para-451", projected.StableId);
        Assert.Equal("para-451", projected.SourceId);
        Assert.Equal("outline heading text", projected.Text);
        Assert.Equal(new TextOffsetSpan(10, 26), projected.HeadingSpan);
    }

    [Fact]
    public void Model_origin_projects_as_model_source_awaiting_review()
    {
        var projected = HeadingOutlineProjection.Project(new ValidatedStructure(
            [Element(StructuralDecisionOrigin.Model, nameof(HeadingDecisionStatus.RequiresReview))])).Single();

        Assert.Equal(HeadingSource.Model, projected.Source);
        Assert.Equal(HeadingDecisionStatus.RequiresReview, projected.DecisionStatus);
    }

    [Theory]
    [InlineData("structure", "RequiresReview")]
    [InlineData("model", "AutoAcceptedEvidence")]
    public void Unknown_origin_or_status_fails_closed_instead_of_being_relabelled(string origin, string status)
    {
        Assert.Throws<InvalidOperationException>(() =>
            HeadingOutlineProjection.Project(new ValidatedStructure([Element(origin, status)])));
    }

    private static ValidatedStructuralElement Element(string origin, string status) => new()
    {
        Id = "structural:test:S0001",
        Type = StructuralElementType.Heading,
        Role = ProposedRole.HeadingTopic,
        Sources = [new SourceReference("S0001", 0, new StructuralSpan(0, 5))],
        Text = "Alpha",
        Level = 1,
        Validation = new StructuralValidation(true, true, true, true, 1, true, true, true, null),
        Decision = new StructuralDecision(origin, status, "source-grounded-pointer-span"),
    };
}
