using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Application.Review;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class ReviewContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Mapper_preserves_source_text_span_evidence_and_provenance()
    {
        var source = Source("p-1", "1. Introduction", 4, "docx");
        var heading = Heading("h-1", "p-1", 3, source.RawText.Length, .91);

        var result = DocumentReviewResultMapper.FromValidatedHeadings(
            "document-1",
            [heading],
            [source],
            [new ReviewDiagnosticDto("pipeline.info", "info", "p-2", "observed", "test")]);

        Assert.Equal("document-1", result.DocumentId);
        Assert.Equal("Introduction", result.Headings[0].Text);
        Assert.Equal(new TextOffsetSpan(3, source.RawText.Length), result.Headings[0].Span);
        Assert.Equal(.91, result.Headings[0].Confidence);
        Assert.Contains(result.Headings[0].Evidence, item => item.Kind == "FontWeight");
        Assert.Equal("source-facts-validator-marker-hierarchy", result.Headings[0].Provenance.Basis);
        Assert.Equal(1, result.Summary.TotalHeadings);
        Assert.Equal(1, result.Summary.PendingCount);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void Contract_serializes_and_round_trips_with_string_review_action()
    {
        var decision = new HumanReviewDecision("h-1", HumanReviewAction.Correct, "Introduction", 1, "ok");

        var json = JsonSerializer.Serialize(decision, JsonOptions);
        var restored = JsonSerializer.Deserialize<HumanReviewDecision>(json, JsonOptions);

        Assert.Contains("\"action\":\"Correct\"", json, StringComparison.Ordinal);
        Assert.Equal(decision, restored);
    }

    [Fact]
    public void Invalid_review_requests_fail_closed()
    {
        var review = Review("h-1");

        Assert.Throws<ArgumentException>(() => HumanReviewDecisionRecorder.Record(
            "document-1", review,
            new HumanReviewDecision("h-1", HumanReviewAction.Accept, "tamper", null, null)));
        Assert.Throws<ArgumentException>(() => HumanReviewDecisionRecorder.Record(
            "document-1", review,
            new HumanReviewDecision("h-1", HumanReviewAction.Correct, null, 10, null)));
        Assert.Throws<ArgumentException>(() => HumanReviewDecisionRecorder.Record(
            "document-1", review,
            new HumanReviewDecision("missing", HumanReviewAction.Reject, null, null, null)));

        var missingAction = JsonSerializer.Deserialize<HumanReviewDecision>(
            "{\"headingId\":\"h-1\"}", JsonOptions)!;
        Assert.Throws<ArgumentException>(() => HumanReviewDecisionRecorder.Record(
            "document-1", review, missingAction));
    }

    [Fact]
    public void Recording_review_decision_does_not_mutate_pipeline_result()
    {
        var review = Review("h-1");
        var before = JsonSerializer.Serialize(review, JsonOptions);

        var record = HumanReviewDecisionRecorder.Record(
            "document-1",
            review,
            new HumanReviewDecision("h-1", HumanReviewAction.Correct, "Corrected", 2, "human"),
            DateTimeOffset.Parse("2026-09-04T00:00:00Z"));

        var after = JsonSerializer.Serialize(review, JsonOptions);
        Assert.Equal(before, after);
        Assert.Equal(ReviewState.Corrected, record.State);
        Assert.Equal("Corrected", record.Decision.CorrectedText);
        Assert.Equal("Introduction", review.Headings[0].Text);
    }

    [Fact]
    public void Pipeline_projection_exposes_diagnostics_separately()
    {
        var source = Source("p-1", "Introduction", 2, "pdf");
        var pipeline = new HeadingPipelineResult(
            [Heading("h-1", "p-1", 0, source.RawText.Length, .8)],
            [new HeadingPipelineDiagnostic("p-2", "rejected", "span-invalid", "validator")]);

        var result = HeadingPipelineReviewProjection.ToReviewResult("document-1", pipeline, [source]);

        Assert.Single(result.Headings);
        Assert.Single(result.Diagnostics);
        Assert.Equal("pipeline.rejected.span-invalid", result.Diagnostics[0].Code);
        Assert.Equal("error", result.Diagnostics[0].Severity);
        Assert.Equal("p-2", result.Diagnostics[0].SourceId);
    }

    private static DocumentReviewResult Review(string headingId) =>
        DocumentReviewResultMapper.FromValidatedHeadings(
            "document-1",
            [Heading(headingId, "p-1", 0, "Introduction".Length, .8)],
            [Source("p-1", "Introduction", 0, "docx")]);

    private static SourceFacts Source(string id, string text, int paragraphIndex, string sourceType) => new()
    {
        SourceId = id,
        RawText = text,
        Source = new SourceAnchor
        {
            SourceType = sourceType,
            ParagraphId = id,
            ParagraphIndex = paragraphIndex,
            Page = sourceType == "pdf" ? 1 : null,
        },
        RawSpan = new SourceTextSpan(0, text.Length),
        ObservedEvidence = [new ObservedEvidence(
            ObservedEvidenceKind.FontWeight, "bold", EvidenceOrigin.DocxParser)],
    };

    private static ValidatedHeading Heading(string id, string sourceId, int start, int end, double confidence) => new()
    {
        Id = id,
        SourceId = sourceId,
        Role = ProposedRole.HeadingTopic,
        HeadingSpan = new SourceTextSpan(start, end),
        Level = 1,
        Validation = new HeadingValidation(true, true, true, true, true, true, true),
        Confidence = confidence,
        Status = "validated",
        Provenance = "source-facts-validator-marker-hierarchy",
    };
}
