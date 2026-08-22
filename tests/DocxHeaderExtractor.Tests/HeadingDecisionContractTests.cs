using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class HeadingDecisionContractTests
{
    [Fact]
    public void SourceFactsBuilder_parses_typed_decimal_marker_before_any_model_call()
    {
        var facts = SourceFactsBuilder.FromParagraph(Paragraph("3.2. Ngoai du bao: body"));

        Assert.NotNull(facts.Marker);
        Assert.Equal(MarkerKind.DecimalDotted, facts.Marker!.Kind);
        Assert.Equal("3.2", facts.Marker.Normalized);
        Assert.Equal([3, 2], facts.Marker.Components);
        Assert.Contains(facts.ObservedEvidence, x => x.Kind == ObservedEvidenceKind.NumberingMarker && x.Origin == EvidenceOrigin.MarkerParser);
    }

    [Fact]
    public void Heading_proposal_without_span_is_rejected_before_tree_resolution()
    {
        var source = SourceFactsBuilder.FromParagraph(Paragraph("1. Heading"));
        var proposal = new ModelProposal { SourceId = source.SourceId, Role = ProposedRole.HeadingTopic };

        var result = ModelProposalValidator.Validate(source, proposal);

        Assert.False(result.Accepted);
        Assert.Equal("invalid-or-missing-heading-span", result.RejectionReason);
        Assert.False(result.Validation.SpanValid);
    }

    [Fact]
    public void Heading_text_is_always_derived_from_immutable_source_span()
    {
        var source = SourceFactsBuilder.FromParagraph(Paragraph("3.2. Ngoai du bao: 02/02/0"));
        var proposal = new ModelProposal
        {
            SourceId = source.SourceId,
            Role = ProposedRole.HeadingTopic,
            HeadingSpan = new SourceTextSpan(0, 17),
            SemanticEvidence = [SemanticEvidenceTag.OpensContent],
        };

        var result = ModelProposalValidator.Validate(source, proposal);

        Assert.True(result.Accepted);
        Assert.Equal("3.2. Ngoai du bao", ModelProposalValidator.HeadingText(source, proposal.HeadingSpan!));
    }

    [Fact]
    public void Default_structural_policy_excludes_local_and_list_topics_without_reclassifying_them()
    {
        var source = SourceFactsBuilder.FromParagraph(Paragraph("a. Detail"));
        var proposal = new ModelProposal
        {
            SourceId = source.SourceId,
            Role = ProposedRole.ListItemTopic,
            HeadingSpan = new SourceTextSpan(0, 2),
        };

        var result = ModelProposalValidator.Validate(source, proposal);

        Assert.True(result.Accepted);
        Assert.False(new HeadingPolicy().Includes(ProposedRole.ListItemTopic));
    }

    private static SlimParagraph Paragraph(string text) => new()
    {
        Index = 42,
        StableId = "@body[1]/p[42]",
        Text = text,
    };
}
