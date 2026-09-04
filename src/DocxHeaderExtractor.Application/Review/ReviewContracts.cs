using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Application.Review;

/// <summary>Source span exposed to consumers without exposing parser authority types.</summary>
public sealed record TextOffsetSpan(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End);

public sealed record DocumentReviewResult(
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("validatedHeadings")] IReadOnlyList<ReviewHeadingDto> Headings,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<ReviewDiagnosticDto> Diagnostics,
    [property: JsonPropertyName("summary")] ReviewSummaryDto Summary);

public sealed record ReviewHeadingDto(
    [property: JsonPropertyName("headingId")] string HeadingId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("span")] TextOffsetSpan Span,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("evidence")] IReadOnlyList<HeadingEvidenceDto> Evidence,
    [property: JsonPropertyName("provenance")] HeadingProvenanceDto Provenance);

public sealed record HeadingEvidenceDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("origin")] string Origin);

public sealed record HeadingProvenanceDto(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceType")] string SourceType,
    [property: JsonPropertyName("paragraphIndex")] int? ParagraphIndex,
    [property: JsonPropertyName("page")] int? Page,
    [property: JsonPropertyName("basis")] string Basis);

public sealed record ReviewDiagnosticDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("sourceId")] string? SourceId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("provenance")] string Provenance);

public sealed record ReviewSummaryDto(
    [property: JsonPropertyName("totalHeadings")] int TotalHeadings,
    [property: JsonPropertyName("pendingCount")] int PendingCount,
    [property: JsonPropertyName("diagnosticCount")] int DiagnosticCount);

public enum HumanReviewAction
{
    Accept = 1,
    Reject = 2,
    Correct = 3,
}

public enum ReviewState
{
    Pending,
    Accepted,
    Rejected,
    Corrected,
}

public sealed record HumanReviewDecision(
    [property: JsonPropertyName("headingId")] string HeadingId,
    [property: JsonPropertyName("action")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] HumanReviewAction Action,
    [property: JsonPropertyName("correctedText")] string? CorrectedText,
    [property: JsonPropertyName("correctedLevel")] int? CorrectedLevel,
    [property: JsonPropertyName("comment")] string? Comment);

/// <summary>Append-only review output; it never replaces the validated pipeline result.</summary>
public sealed record HumanReviewRecord(
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("decision")] HumanReviewDecision Decision,
    [property: JsonPropertyName("state")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ReviewState State,
    [property: JsonPropertyName("recordedAtUtc")] DateTimeOffset RecordedAtUtc);

public static class DocumentReviewResultMapper
{
    public static DocumentReviewResult FromValidatedHeadings(
        string documentId,
        IEnumerable<ValidatedHeading> headings,
        IEnumerable<SourceFacts> sourceFacts,
        IEnumerable<ReviewDiagnosticDto>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(headings);
        ArgumentNullException.ThrowIfNull(sourceFacts);

        var sources = sourceFacts.ToArray();
        if (sources.Any(source => string.IsNullOrWhiteSpace(source.SourceId)) ||
            sources.Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count() != sources.Length)
            throw new InvalidOperationException("review-source-inventory-invalid");

        var sourceById = sources.ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        var mapped = headings.Select(heading => MapHeading(heading, sourceById)).ToArray();
        var mappedDiagnostics = (diagnostics ?? []).ToArray();

        return new DocumentReviewResult(
            documentId,
            mapped,
            mappedDiagnostics,
            new ReviewSummaryDto(mapped.Length, mapped.Length, mappedDiagnostics.Length));
    }

    private static ReviewHeadingDto MapHeading(
        ValidatedHeading heading,
        IReadOnlyDictionary<string, SourceFacts> sourceById)
    {
        if (!sourceById.TryGetValue(heading.SourceId, out var source))
            throw new InvalidOperationException($"review-heading-source-missing:{heading.SourceId}");
        if (!heading.HeadingSpan.IsValidFor(source.RawText))
            throw new InvalidOperationException($"review-heading-span-invalid:{heading.Id}");
        if (heading.Level is < 1 or > 9)
            throw new InvalidOperationException($"review-heading-level-invalid:{heading.Id}");

        var sourceEvidence = heading.SourceEvidence.Count > 0
            ? heading.SourceEvidence
            : source.ObservedEvidence;
        var evidence = sourceEvidence
            .Select(item => new HeadingEvidenceDto(
                item.Kind.ToString(), item.Value, item.Origin.ToString()))
            .Concat(heading.SemanticEvidence.Select(item => new HeadingEvidenceDto(
                "semantic", item.ToString(), "validated-proposal")))
            .Concat(heading.VisualEvidence.Select(item => new HeadingEvidenceDto(
                "visual", item.ToString(), "validated-proposal")))
            .ToArray();

        return new ReviewHeadingDto(
            heading.Id,
            source.RawText[heading.HeadingSpan.Start..heading.HeadingSpan.End],
            heading.Level,
            new TextOffsetSpan(heading.HeadingSpan.Start, heading.HeadingSpan.End),
            Math.Clamp(heading.Confidence ?? 0d, 0d, 1d),
            heading.Status,
            evidence,
            new HeadingProvenanceDto(
                source.SourceId,
                source.Source.SourceType,
                source.Source.ParagraphIndex,
                source.Source.Page,
                heading.Provenance));
    }
}

public static class HumanReviewDecisionRecorder
{
    public static HumanReviewRecord Record(
        string documentId,
        DocumentReviewResult review,
        HumanReviewDecision decision,
        DateTimeOffset? recordedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(decision);
        EnsureValid(decision);

        if (!review.Headings.Any(heading =>
                string.Equals(heading.HeadingId, decision.HeadingId, StringComparison.Ordinal)))
            throw new ArgumentException("review-heading-not-found", nameof(decision));

        var state = decision.Action switch
        {
            HumanReviewAction.Accept => ReviewState.Accepted,
            HumanReviewAction.Reject => ReviewState.Rejected,
            HumanReviewAction.Correct => ReviewState.Corrected,
            _ => throw new ArgumentException("review-action-invalid", nameof(decision)),
        };

        return new HumanReviewRecord(documentId, decision, state, recordedAtUtc ?? DateTimeOffset.UtcNow);
    }

    private static void EnsureValid(HumanReviewDecision decision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.HeadingId);
        if (!Enum.IsDefined(decision.Action))
            throw new ArgumentException("review-action-invalid", nameof(decision));
        if (decision.Comment?.Length > 2_000)
            throw new ArgumentException("review-comment-too-long", nameof(decision));

        if (decision.Action is HumanReviewAction.Accept or HumanReviewAction.Reject &&
            (decision.CorrectedText is not null || decision.CorrectedLevel is not null))
            throw new ArgumentException("review-correction-fields-not-allowed", nameof(decision));

        if (decision.CorrectedLevel is not null && decision.CorrectedLevel is < 1 or > 9)
            throw new ArgumentException("review-corrected-level-invalid", nameof(decision));

        if (decision.Action == HumanReviewAction.Correct &&
            string.IsNullOrWhiteSpace(decision.CorrectedText) && decision.CorrectedLevel is null)
            throw new ArgumentException("review-correction-empty", nameof(decision));
        if (decision.CorrectedText is not null && string.IsNullOrWhiteSpace(decision.CorrectedText))
            throw new ArgumentException("review-corrected-text-empty", nameof(decision));
    }
}
