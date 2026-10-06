using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Application.Review;

namespace DocxHeaderExtractor.Tests;

public sealed class ReviewContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

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

    private static DocumentReviewResult Review(string headingId) => new(
        "document-1",
        [new ReviewHeadingDto(
            headingId,
            "Introduction",
            1,
            new TextOffsetSpan(0, "Introduction".Length),
            "validated",
            [],
            new HeadingProvenanceDto("p-1", "docx", 0, null, "test"))],
        [],
        new ReviewSummaryDto(1, 1, 0));
}
