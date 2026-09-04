using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Application.Review;

public enum ApprovedWritebackPlanStatus
{
    Ready,
    DeferredToHuman,
}

public sealed record ReviewedHeadingDecision(
    [property: JsonPropertyName("headingId")] string HeadingId,
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("sourceText")] string SourceText,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("span")] TextOffsetSpan Span,
    [property: JsonPropertyName("action")] HumanReviewAction? Action,
    [property: JsonPropertyName("state")] ReviewState? State,
    [property: JsonPropertyName("includeInWriteback")] bool IncludeInWriteback,
    [property: JsonPropertyName("comment")] string? Comment);

public sealed record ApprovedWritebackPlan(
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("status")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ApprovedWritebackPlanStatus Status,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("headings")] IReadOnlyList<ReviewedHeadingDecision> Headings)
{
    [JsonIgnore]
    public bool IsReady => Status == ApprovedWritebackPlanStatus.Ready;
}
