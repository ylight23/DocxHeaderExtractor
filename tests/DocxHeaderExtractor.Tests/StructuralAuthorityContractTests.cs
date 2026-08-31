using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class StructuralAuthorityContractTests
{
    [Fact]
    public void Validator_grounds_span_and_parent_before_materialization()
    {
        var source = Source("p1", "1 Introduction and body");
        var proposal = new StructuralProposal
        {
            SourceId = "p1",
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            Level = 1,
        };

        var element = StructuralProposalValidator.Materialize(
            source, proposal, new StructuralSpan(0, 14),
            new StructuralDecision("style", "AutoAcceptedEvidence", 1, "ooxml-style"), 7,
            new HashSet<string>(["p1"]));

        Assert.NotNull(element);
        Assert.Equal("1 Introduction", element.Text);
        Assert.Equal(new StructuralSpan(0, 14), element.Source.Span);
        Assert.Equal(7, element.Source.SourceOrdinal);
        Assert.True(element.Validation.Accepted);
    }

    [Fact]
    public void Validator_rejects_model_span_substitution_and_unknown_parent()
    {
        var source = Source("p1", "1 Introduction");
        var proposal = new StructuralProposal
        {
            SourceId = "p1",
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            ParentId = "p9",
            Level = 1,
        };

        var validation = StructuralProposalValidator.Validate(
            source, proposal, new StructuralSpan(0, 99), new HashSet<string>(["p1"]));

        Assert.False(validation.Accepted);
        Assert.False(validation.SpanValid);
        Assert.False(validation.ParentValid);
        Assert.Equal("invalid-source-span", validation.RejectionReason);
    }

    [Fact]
    public void Heading_projection_keeps_source_identity_span_level_text_and_relations()
    {
        var first = Materialize("p1", "1 Introduction", 0, 1, null, "style");
        var second = Materialize("p2", "1.1 Scope", 1, 2, "p1", "structure");
        var structure = ValidatedStructure.FromElements([first, second]);

        var projected = HeadingOutlineProjection.Project(structure);

        Assert.Collection(projected,
            heading =>
            {
                Assert.Equal("p1", heading.StableId);
                Assert.Equal("1 Introduction", heading.Text);
                Assert.Equal(1, heading.Level);
                Assert.Equal(new TextOffsetSpan(0, 14), heading.HeadingSpan);
                Assert.Equal(HeadingSource.Style, heading.Source);
            },
            heading =>
            {
                Assert.Equal("p2", heading.StableId);
                Assert.Equal(2, heading.Level);
                Assert.Equal(HeadingSource.Structure, heading.Source);
            });
        var relation = Assert.Single(structure.Relations);
        Assert.Equal("p1", relation.FromId);
        Assert.Equal("p2", relation.ToId);
        Assert.Equal(StructuralRelationType.ParentChild, relation.Type);
    }

    [Fact]
    public void Document_outline_projection_preserves_non_heading_metadata()
    {
        var structure = ValidatedStructure.FromElements([
            Materialize("p1", "1 Introduction", 0, 1, null, "style")]);
        var original = new DocumentOutline
        {
            File = "sample.docx",
            ParagraphCount = 3,
            CandidateCount = 1,
            Headings = [],
            ElapsedMs = 42,
            Model = "rules-only",
            DeterministicRoute = "docx-authority-v1",
        };

        var projected = HeadingOutlineProjection.Project(original, structure);

        Assert.Equal(original.File, projected.File);
        Assert.Equal(original.ParagraphCount, projected.ParagraphCount);
        Assert.Equal(original.CandidateCount, projected.CandidateCount);
        Assert.Equal(original.ElapsedMs, projected.ElapsedMs);
        Assert.Equal(original.Model, projected.Model);
        Assert.Equal(original.DeterministicRoute, projected.DeterministicRoute);
        Assert.Single(projected.Headings);
    }

    private static SourceFacts Source(string id, string text) => new()
    {
        SourceId = id,
        RawText = text,
        Source = new SourceAnchor
        {
            SourceType = "docx",
            ParagraphId = id,
            ParagraphIndex = 0,
        },
        RawSpan = new SourceTextSpan(0, text.Length),
    };

    private static ValidatedStructuralElement Materialize(
        string id, string text, int ordinal, int level, string? parentId, string origin)
    {
        var source = Source(id, text);
        var proposal = new StructuralProposal
        {
            SourceId = id,
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            ParentId = parentId,
            Level = level,
        };
        return StructuralProposalValidator.Materialize(
            source, proposal, new StructuralSpan(0, text.Length),
            new StructuralDecision(origin, "AutoAcceptedEvidence", 1, "test"), ordinal,
            new HashSet<string>([id, "p1"]))!;
    }
}
