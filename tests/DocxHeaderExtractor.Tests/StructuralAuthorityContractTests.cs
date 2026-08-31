using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class StructuralAuthorityContractTests
{
    [Fact]
    public void One_source_proposed_subspan_materializes_validated_subspan()
    {
        var source = Source("p1", "1 Introduction and body", 0, 23);
        var candidate = Candidate("c1", source);
        var proposal = new StructuralProposal
        {
            CandidateId = "c1",
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            ProposedSources = [new ProposedSourceReference("p1", new StructuralSpan(0, 14))],
            ProposedLevel = 1,
        };

        var element = StructuralProposalValidator.Materialize(
            candidate, proposal, "se:1",
            new StructuralDecision("style", "AutoAcceptedEvidence", 1, "ooxml-style"));

        Assert.NotNull(element);
        Assert.Equal("se:1", element.Id);
        Assert.Equal("p1", Assert.Single(element.Sources).SourceId);
        Assert.Equal(new StructuralSpan(0, 14), element.Sources[0].Span);
        Assert.Equal("1 Introduction", element.Text);
        Assert.True(element.Validation.SourceSelectionValid);
    }

    [Fact]
    public void Multi_source_proposal_materializes_per_source_spans()
    {
        var first = Source("p20", "CHƯƠNG II", 20, 9);
        var second = Source("p21", "QUYỀN VÀ NGHĨA VỤ", 21, 17);
        var candidate = new StructuralCandidate
        {
            CandidateId = "c-chapter",
            ObservedSourceFacts = [first, second],
        };
        var proposal = new StructuralProposal
        {
            CandidateId = "c-chapter",
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            ProposedSources =
            [
                new ProposedSourceReference("p20", new StructuralSpan(0, first.RawText.Length)),
                new ProposedSourceReference("p21", new StructuralSpan(0, second.RawText.Length)),
            ],
            ProposedLevel = 1,
        };

        var element = StructuralProposalValidator.Materialize(
            candidate, proposal, "se:chapter-2", Decision("structure"));

        Assert.NotNull(element);
        Assert.Equal(["p20", "p21"], element.Sources.Select(source => source.SourceId));
        Assert.Equal(
            [new StructuralSpan(0, first.RawText.Length), new StructuralSpan(0, second.RawText.Length)],
            element.Sources.Select(source => source.Span));
        Assert.Equal("CHƯƠNG II QUYỀN VÀ NGHĨA VỤ", element.Text);
    }

    [Fact]
    public void Proposed_source_for_wrong_source_is_rejected()
    {
        var candidate = Candidate("c1", Source("p1", "Heading", 0, 7));
        var proposal = new StructuralProposal
        {
            CandidateId = "c1",
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            ProposedSources = [new ProposedSourceReference("p9", new StructuralSpan(0, 7))],
            ProposedLevel = 1,
        };

        var validation = StructuralProposalValidator.Validate(candidate, proposal);
        var element = StructuralProposalValidator.Materialize(
            candidate, proposal, "se:wrong", Decision("model"));

        Assert.False(validation.Accepted);
        Assert.Equal("invalid-proposed-sources", validation.RejectionReason);
        Assert.Null(element);
    }

    [Fact]
    public void One_source_can_produce_multiple_structural_elements()
    {
        var source = Source("p1", "1 Heading body", 0, 14);
        var first = Element("se:heading", source, new StructuralSpan(0, 9), "1 Heading", 1);
        var second = Element("se:body", source, new StructuralSpan(10, 14), "body", null,
            StructuralElementType.Subtitle, ProposedRole.BodyText);

        var structure = ValidatedStructure.FromElements([first, second]);

        Assert.Equal(2, structure.Elements.Count);
        Assert.All(structure.Elements, item => Assert.Equal("p1", Assert.Single(item.Sources).SourceId));
        Assert.NotEqual(structure.Elements[0].Id, structure.Elements[1].Id);
    }

    [Fact]
    public void Multiple_sources_can_form_one_structural_element()
    {
        var first = SourceReference("p20", 20, 0, 10);
        var second = SourceReference("p21", 21, 0, 18);
        var element = new ValidatedStructuralElement
        {
            Id = "se:chapter-2",
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            Sources = [first, second],
            Text = "CHƯƠNG II QUYỀN VÀ NGHĨA VỤ",
            Level = 1,
            Validation = AcceptedValidation(),
            Decision = Decision("structure"),
        };

        var structure = ValidatedStructure.FromElements([element]);

        Assert.Single(structure.Elements);
        Assert.Equal(["p20", "p21"], element.Sources.Select(source => source.SourceId));
    }

    [Fact]
    public void Parent_is_validated_against_structural_ids_not_source_ids()
    {
        var candidate = Candidate("c1", Source("p1", "Heading", 0, 7));
        var proposal = new StructuralProposal
        {
            CandidateId = "c1",
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            ProposedParentId = "p1",
            ProposedLevel = 2,
        };

        var validation = StructuralProposalValidator.Validate(
            candidate, proposal, new HashSet<string>(["se:parent"]));

        Assert.False(validation.Accepted);
        Assert.False(validation.ParentValid);
        Assert.Equal("structural-parent-not-grounded", validation.RejectionReason);
    }

    [Fact]
    public void Heading_projection_round_trips_every_heading_record_field()
    {
        var original = new HeadingRecord
        {
            Index = 7,
            StableId = "p7",
            SourceId = "p7",
            Level = 2,
            Text = "1 Introduction",
            OriginalText = "1 Introduction body",
            HeadingSpan = new TextOffsetSpan(0, 14),
            InlineBody = "body",
            InlineBodySpan = new TextOffsetSpan(15, 19),
            BoundarySource = "source-pointer",
            StyleId = "Heading2",
            Source = HeadingSource.Structure,
            Confidence = 0.75,
            ModelConfirmed = true,
            CriticConfirmed = true,
            DecisionStatus = HeadingDecisionStatus.AutoAcceptedCalibrated,
            ConfidenceBasis = "validated-structure",
            AcceptanceSignature = "sig",
            CalibrationSamples = 3,
            Evidence = new HeadingEvidence(true, true, false, true, true, "validated"),
            Disputed = true,
        };
        var source = SourceReference("p7", 7, 0, 14);
        var element = new ValidatedStructuralElement
        {
            Id = "se:7",
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            Sources = [source],
            Text = original.Text,
            Level = original.Level,
            Validation = AcceptedValidation(),
            Decision = new StructuralDecision(
                "structure", "AutoAcceptedCalibrated", original.Confidence,
                original.ConfidenceBasis, original.Disputed),
            ProjectionMetadata = new StructuralProjectionMetadata
            {
                OriginalText = original.OriginalText,
                InlineBody = original.InlineBody,
                InlineBodySpan = new StructuralSpan(15, 19),
                BoundarySource = original.BoundarySource,
                StyleId = original.StyleId,
                ModelConfirmed = original.ModelConfirmed,
                CriticConfirmed = original.CriticConfirmed,
                AcceptanceSignature = original.AcceptanceSignature,
                CalibrationSamples = original.CalibrationSamples,
                Evidence = original.Evidence,
            },
        };

        var projected = Assert.Single(HeadingOutlineProjection.Project(
            ValidatedStructure.FromElements([element])));

        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(projected));
    }

    private static StructuralCandidate Candidate(string id, SourceFacts source) => new()
    {
        CandidateId = id,
        ObservedSourceFacts = [source],
    };

    private static SourceFacts Source(string id, string text, int ordinal, int end) => new()
    {
        SourceId = id,
        RawText = text,
        Source = new SourceAnchor
        {
            SourceType = "docx",
            ParagraphId = id,
            ParagraphIndex = ordinal,
        },
        RawSpan = new SourceTextSpan(0, end),
    };

    private static SourceReference SourceReference(string id, int ordinal, int start, int end) =>
        new(id, ordinal, new StructuralSpan(start, end));

    private static ValidatedStructuralElement Element(
        string id,
        SourceFacts source,
        StructuralSpan span,
        string text,
        int? level,
        StructuralElementType type = StructuralElementType.Heading,
        ProposedRole role = ProposedRole.HeadingTopic) => new()
    {
        Id = id,
        Type = type,
        Role = role,
        Sources = [new SourceReference(source.SourceId, source.Source.ParagraphIndex ?? 0, span)],
        Text = text,
        Level = level,
        Validation = AcceptedValidation(),
        Decision = Decision("test"),
    };

    private static StructuralValidation AcceptedValidation() =>
        new(true, true, true, true, 1, true, true, true, null);

    private static StructuralDecision Decision(string origin) =>
        new(origin, "AutoAcceptedEvidence", 1, "test");
}
