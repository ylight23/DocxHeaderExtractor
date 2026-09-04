using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class HeadingPipelineTests
{
    [Fact]
    public void Pipeline_materializes_source_text_from_parser_span_and_keeps_model_parent_non_authoritative()
    {
        var sources = new[]
        {
            Source("p1", "I. Architecture", 0, MarkerKind.RomanUpper),
            Source("p2", "1. Runtime", 1, MarkerKind.Decimal),
        };
        var result = HeadingPipeline.Evaluate(sources,
        [
            Proposal("p1", 0, 15, 1, "wrong-parent"),
            Proposal("p2", 0, 10, 2, "wrong-parent"),
        ]);

        Assert.Equal(new[] { "p1", "p2" }, result.Headings.Select(item => item.SourceId));
        Assert.Equal(new[] { 1, 2 }, result.Headings.Select(item => item.Level));
        Assert.Equal("p1", result.Headings[1].ParentId);
        Assert.Equal("source-facts-validator-marker-hierarchy", result.Headings[1].Provenance);
        Assert.Contains(result.Headings[0].SourceEvidence, item => item.Kind == ObservedEvidenceKind.NumberingMarker);
    }

    [Fact]
    public void Pipeline_rejects_model_span_that_is_inside_a_source_token()
    {
        var result = HeadingPipeline.Evaluate(
            [Source("p1", "Operating context", 0)],
            [Proposal("p1", 0, 5, 1, null)]);

        Assert.Empty(result.Headings);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.SourceId == "p1");
        Assert.Equal("rejected", diagnostic.Status);
        Assert.Equal("span-not-parser-boundary", diagnostic.Reason);
    }

    [Fact]
    public void Candidate_selection_is_deterministic_and_prefers_model_score()
    {
        var result = HeadingPipeline.Evaluate(
            [Source("p1", "Architecture", 0)],
            [
                Proposal("p1", 0, 12, 1, null) with { ModelScore = 0.40 },
                Proposal("p1", 0, 12, 1, null) with { ModelScore = 0.90 },
            ]);

        Assert.Single(result.Headings);
        Assert.Contains(result.Diagnostics, item =>
            item.Status == "discarded" && item.Reason == "duplicate-source-proposal");
        Assert.Equal(0.90, result.Headings[0].Confidence);
    }

    [Fact]
    public void Invalid_high_score_proposal_cannot_shadow_a_valid_proposal_for_same_source()
    {
        var result = HeadingPipeline.Evaluate(
            [Source("p1", "Architecture", 0)],
            [
                Proposal("p1", 0, 5, 1, null) with { ModelScore = 0.99 },
                Proposal("p1", 0, 12, 1, null) with { ModelScore = 0.50 },
            ]);

        Assert.Single(result.Headings);
        Assert.Equal(new SourceTextSpan(0, 12), result.Headings[0].HeadingSpan);
        Assert.Equal(0.50, result.Headings[0].Confidence);
        Assert.Contains(result.Diagnostics, item =>
            item.Status == "discarded" && item.Reason == "span-not-parser-boundary");
    }

    [Fact]
    public void Policy_excluded_proposals_are_diagnostic_only()
    {
        var result = HeadingPipeline.Evaluate(
            [Source("p1", "Table header", 0)],
            [new ModelProposal
            {
                SourceId = "p1",
                Role = ProposedRole.TableHeader,
                HeadingSpan = new SourceTextSpan(0, 12),
            }]);

        Assert.Empty(result.Headings);
        Assert.Contains(result.Diagnostics, item =>
            item.Status == "ignored" && item.Reason == "role-excluded-by-policy");
    }

    private static SourceFacts Source(string id, string text, int ordinal, MarkerKind? markerKind = null) => new()
    {
        SourceId = id,
        RawText = text,
        RawSpan = new SourceTextSpan(0, text.Length),
        Source = new SourceAnchor
        {
            SourceType = "docx",
            ParagraphId = id,
            ParagraphIndex = ordinal,
        },
        Marker = markerKind is { } kind
            ? new MarkerFacts { Kind = kind, Raw = text[..2], Depth = kind == MarkerKind.RomanUpper ? 1 : 1 }
            : null,
        ObservedEvidence = markerKind is null
            ? []
            : [new ObservedEvidence(ObservedEvidenceKind.NumberingMarker, text[..2], EvidenceOrigin.MarkerParser)],
    };

    private static ModelProposal Proposal(string sourceId, int start, int end, int level, string? parent) => new()
    {
        SourceId = sourceId,
        Role = ProposedRole.HeadingTopic,
        HeadingSpan = new SourceTextSpan(start, end),
        ProposedLevel = level,
        ProposedParentId = parent,
    };
}
