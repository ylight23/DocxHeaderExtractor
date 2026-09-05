using System.Text;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.HarnessLift;

public enum HarnessOccurrenceJoinStatus
{
    [JsonStringEnumMemberName("EXACT_SOURCE_ID")] ExactSourceId,
    [JsonStringEnumMemberName("EXACT_SPAN")] ExactSpan,
    [JsonStringEnumMemberName("EXACT_ORDINAL_TEXT")] ExactOrdinalText,
    [JsonStringEnumMemberName("UNIQUE_EXACT_TEXT")] UniqueExactText,
    [JsonStringEnumMemberName("AMBIGUOUS")] Ambiguous,
    [JsonStringEnumMemberName("NOT_FOUND")] NotFound,
    [JsonStringEnumMemberName("NOT_SUPPORTED")] NotSupported,
}

public sealed record HarnessSourceOccurrence(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("stableId")] string? StableId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("span")] HarnessSpan Span,
    [property: JsonPropertyName("rawText")] string RawText);

public sealed record HarnessReferenceOccurrenceInput
{
    [JsonPropertyName("referenceId")] public string ReferenceId { get; init; } = "reference";
    [JsonPropertyName("documentId")] public string DocumentId { get; init; } = "DOC-UNKNOWN";
    [JsonPropertyName("documentGroupId")] public string? DocumentGroupId { get; init; }
    [JsonPropertyName("sourceSha256")] public string SourceSha256 { get; init; } = "";
    [JsonPropertyName("referenceAuthority")] public HarnessReferenceAuthority ReferenceAuthority { get; init; } = HarnessReferenceAuthority.HumanKey;
    [JsonPropertyName("supportedFields")] public IReadOnlyList<string> SupportedFields { get; init; } = [];
    [JsonPropertyName("referenceSourceId")] public string? ReferenceSourceId { get; init; }
    [JsonPropertyName("referenceStableId")] public string? ReferenceStableId { get; init; }
    [JsonPropertyName("referenceOrdinal")] public int? ReferenceOrdinal { get; init; }
    [JsonPropertyName("referenceSpan")] public HarnessSpan? ReferenceSpan { get; init; }
    [JsonPropertyName("referenceText")] public string? ReferenceText { get; init; }
    [JsonPropertyName("expectedIsHeading")] public bool? ExpectedIsHeading { get; init; }
    [JsonPropertyName("expectedRole")] public string? ExpectedRole { get; init; }
    [JsonPropertyName("expectedLevel")] public int? ExpectedLevel { get; init; }
    [JsonPropertyName("expectedParentOccurrenceId")] public string? ExpectedParentOccurrenceId { get; init; }
    [JsonPropertyName("expectedSpan")] public HarnessSpan? ExpectedSpan { get; init; }
    [JsonPropertyName("sourceContract")] public string? SourceContract { get; init; }
    [JsonPropertyName("notes")] public string? Notes { get; init; }
}

public sealed record HarnessOccurrenceJoinResult
{
    [JsonPropertyName("referenceId")] public required string ReferenceId { get; init; }
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("documentGroupId")] public string? DocumentGroupId { get; init; }
    [JsonPropertyName("sourceSha256")] public required string SourceSha256 { get; init; }
    [JsonPropertyName("referenceAuthority")] public required HarnessReferenceAuthority ReferenceAuthority { get; init; }
    [JsonPropertyName("supportedFields")] public IReadOnlyList<string> SupportedFields { get; init; } = [];
    [JsonPropertyName("referenceOccurrenceIdentity")] public HarnessOccurrenceIdentity ReferenceOccurrenceIdentity { get; init; } = new();
    [JsonPropertyName("resolvedSourceId")] public string? ResolvedSourceId { get; init; }
    [JsonPropertyName("resolvedStableId")] public string? ResolvedStableId { get; init; }
    [JsonPropertyName("resolvedOrdinal")] public int? ResolvedOrdinal { get; init; }
    [JsonPropertyName("resolvedSpan")] public HarnessSpan? ResolvedSpan { get; init; }
    [JsonPropertyName("resolvedSourceText")] public string? ResolvedSourceText { get; init; }
    [JsonPropertyName("joinMethod")] public string JoinMethod { get; init; } = "NONE";
    [JsonPropertyName("joinStatus")] public HarnessOccurrenceJoinStatus JoinStatus { get; init; }
    [JsonPropertyName("officialMetricEligible")] public bool OfficialMetricEligible { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
    [JsonPropertyName("expectedIsHeading")] public bool? ExpectedIsHeading { get; init; }
    [JsonPropertyName("expectedRole")] public string? ExpectedRole { get; init; }
    [JsonPropertyName("expectedLevel")] public int? ExpectedLevel { get; init; }
    [JsonPropertyName("expectedParentOccurrenceId")] public string? ExpectedParentOccurrenceId { get; init; }
    [JsonPropertyName("expectedSpan")] public HarnessSpan? ExpectedSpan { get; init; }
    [JsonPropertyName("sourceContract")] public string? SourceContract { get; init; }
}

public sealed record HarnessModelOccurrenceTrace
{
    [JsonPropertyName("runId")] public required string RunId { get; init; }
    [JsonPropertyName("repeat")] public int Repeat { get; init; }
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("documentGroupId")] public string? DocumentGroupId { get; init; }
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("sourceOrdinal")] public int? SourceOrdinal { get; init; }
    [JsonPropertyName("sourceSpan")] public HarnessSpan? SourceSpan { get; init; }
    [JsonPropertyName("candidateConstructed")] public bool? CandidateConstructed { get; init; }
    [JsonPropertyName("candidateSelected")] public bool? CandidateSelected { get; init; }
    [JsonPropertyName("modelCalled")] public bool? ModelCalled { get; init; }
    [JsonPropertyName("modelExposed")] public bool ModelExposed { get; init; }
    [JsonPropertyName("modelRole")] public string? ModelRole { get; init; }
    [JsonPropertyName("modelLevel")] public int? ModelLevel { get; init; }
    [JsonPropertyName("modelParent")] public string? ModelParent { get; init; }
    [JsonPropertyName("modelSpan")] public HarnessSpan? ModelSpan { get; init; }
    [JsonPropertyName("validationStatus")] public string? ValidationStatus { get; init; }
    [JsonPropertyName("validationReason")] public string? ValidationReason { get; init; }
    [JsonPropertyName("afterMarkerRole")] public string? AfterMarkerRole { get; init; }
    [JsonPropertyName("afterMarkerLevel")] public int? AfterMarkerLevel { get; init; }
    [JsonPropertyName("afterMarkerParent")] public string? AfterMarkerParent { get; init; }
    [JsonPropertyName("afterStructuralRole")] public string? AfterStructuralRole { get; init; }
    [JsonPropertyName("afterStructuralLevel")] public int? AfterStructuralLevel { get; init; }
    [JsonPropertyName("afterStructuralParent")] public string? AfterStructuralParent { get; init; }
    [JsonPropertyName("finalIncluded")] public bool FinalIncluded { get; init; }
    [JsonPropertyName("finalRole")] public string? FinalRole { get; init; }
    [JsonPropertyName("finalLevel")] public int? FinalLevel { get; init; }
    [JsonPropertyName("finalParent")] public string? FinalParent { get; init; }
    [JsonPropertyName("finalSpan")] public HarnessSpan? FinalSpan { get; init; }
}

public sealed record HarnessHumanReviewDecision
{
    [JsonPropertyName("documentId")] public string DocumentId { get; init; } = "DOC-UNKNOWN";
    [JsonPropertyName("sourceId")] public string SourceId { get; init; } = "";
    [JsonPropertyName("sourceOrdinal")] public int? SourceOrdinal { get; init; }
    [JsonPropertyName("isHeading")] public string IsHeading { get; init; } = "UNSURE";
    [JsonPropertyName("role")] public string Role { get; init; } = "UNSURE";
    [JsonPropertyName("headingSpan")] public HarnessSpan? HeadingSpan { get; init; }
    [JsonPropertyName("level")] public string Level { get; init; } = "UNKNOWN";
    [JsonPropertyName("parentOccurrenceId")] public string ParentOccurrenceId { get; init; } = "UNKNOWN";
    [JsonPropertyName("notes")] public string? Notes { get; init; }
    [JsonPropertyName("reviewerId")] public string ReviewerId { get; init; } = "";
    [JsonPropertyName("reviewedAt")] public string ReviewedAt { get; init; } = "";
    [JsonPropertyName("sourceSha256")] public string SourceSha256 { get; init; } = "";
    [JsonPropertyName("packetSha256")] public string PacketSha256 { get; init; } = "";
    [JsonPropertyName("decisionVersion")] public string DecisionVersion { get; init; } = "";
}

public sealed record HarnessReviewValidationResult(bool Accepted, string? Reason);

public static class HarnessOccurrenceIdentityJoiner
{
    public static HarnessOccurrenceJoinResult Join(
        HarnessReferenceOccurrenceInput reference,
        IReadOnlyList<HarnessSourceOccurrence> sourceOccurrences,
        string sourceSha256)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(sourceOccurrences);

        var identity = new HarnessOccurrenceIdentity
        {
            SourceId = reference.ReferenceSourceId,
            StableId = reference.ReferenceStableId,
            Index = reference.ReferenceOrdinal,
            Span = reference.ReferenceSpan,
        };
        var official = HarnessLiftAccounting.IsOfficial(reference.ReferenceAuthority);
        var baseResult = new HarnessOccurrenceJoinResult
        {
            ReferenceId = reference.ReferenceId,
            DocumentId = reference.DocumentId,
            DocumentGroupId = reference.DocumentGroupId,
            SourceSha256 = sourceSha256,
            ReferenceAuthority = reference.ReferenceAuthority,
            SupportedFields = reference.SupportedFields,
            ReferenceOccurrenceIdentity = identity,
            OfficialMetricEligible = official,
            Reason = "not evaluated",
            ExpectedIsHeading = reference.ExpectedIsHeading,
            ExpectedRole = reference.ExpectedRole,
            ExpectedLevel = reference.ExpectedLevel,
            ExpectedParentOccurrenceId = reference.ExpectedParentOccurrenceId,
            ExpectedSpan = reference.ExpectedSpan,
            SourceContract = reference.SourceContract,
        };

        if (string.IsNullOrWhiteSpace(reference.SourceSha256) ||
            !string.Equals(reference.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase))
            return baseResult with
            {
                JoinStatus = HarnessOccurrenceJoinStatus.NotSupported,
                JoinMethod = "SOURCE_SHA_MISMATCH",
                Reason = "reference source SHA does not match the corpus source SHA",
            };

        var sourceId = NormalizeIdentity(reference.ReferenceSourceId ?? reference.ReferenceStableId);
        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            var byId = sourceOccurrences.Where(item =>
                    string.Equals(item.SourceId, sourceId, StringComparison.Ordinal) ||
                    string.Equals(item.StableId, sourceId, StringComparison.Ordinal))
                .ToArray();
            if (byId.Length == 1) return Resolved(baseResult, byId[0], HarnessOccurrenceJoinStatus.ExactSourceId, "EXACT_SOURCE_ID", "exact parser source identity");
            if (byId.Length > 1) return Ambiguous(baseResult, "exact source identity resolved to multiple occurrences");
            return baseResult with
            {
                JoinStatus = HarnessOccurrenceJoinStatus.NotFound,
                JoinMethod = "EXACT_SOURCE_ID",
                Reason = "explicit source identity was not present in the current parser source",
            };
        }

        if (reference.ReferenceSpan is { } span)
        {
            var bySpan = sourceOccurrences.Where(item => item.Span == span).ToArray();
            if (bySpan.Length == 1) return Resolved(baseResult, bySpan[0], HarnessOccurrenceJoinStatus.ExactSpan, "EXACT_SPAN", "exact parser span");
            if (bySpan.Length > 1) return Ambiguous(baseResult, "exact span resolved to multiple occurrences");
        }

        if (reference.ReferenceOrdinal is { } ordinal && !string.IsNullOrWhiteSpace(reference.ReferenceText))
        {
            var byOrdinalText = sourceOccurrences.Where(item => item.SourceOrdinal == ordinal &&
                SameText(item.RawText, reference.ReferenceText)).ToArray();
            if (byOrdinalText.Length == 1) return Resolved(baseResult, byOrdinalText[0], HarnessOccurrenceJoinStatus.ExactOrdinalText, "EXACT_ORDINAL_TEXT", "exact ordinal and normalized source text");
            if (byOrdinalText.Length > 1) return Ambiguous(baseResult, "exact ordinal and text resolved to multiple occurrences");
        }

        if (!string.IsNullOrWhiteSpace(reference.ReferenceText))
        {
            var byText = sourceOccurrences.Where(item => SameText(item.RawText, reference.ReferenceText)).ToArray();
            if (byText.Length == 1) return Resolved(baseResult, byText[0], HarnessOccurrenceJoinStatus.UniqueExactText, "UNIQUE_EXACT_TEXT", "unique exact normalized source text");
            if (byText.Length > 1) return Ambiguous(baseResult, "exact normalized source text is duplicated");
        }

        return baseResult with
        {
            JoinStatus = HarnessOccurrenceJoinStatus.NotFound,
            JoinMethod = "NONE",
            Reason = "no permitted exact identity strategy resolved an occurrence",
        };
    }

    public static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormC).Trim();
        return string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool SameText(string left, string? right) =>
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(NormalizeText(left), NormalizeText(right), StringComparison.Ordinal);

    private static string? NormalizeIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimStart('@');

    private static HarnessOccurrenceJoinResult Resolved(
        HarnessOccurrenceJoinResult result,
        HarnessSourceOccurrence occurrence,
        HarnessOccurrenceJoinStatus status,
        string method,
        string reason) => result with
        {
            JoinStatus = status,
            JoinMethod = method,
            Reason = reason,
            ResolvedSourceId = occurrence.SourceId,
            ResolvedStableId = occurrence.StableId,
            ResolvedOrdinal = occurrence.SourceOrdinal,
            ResolvedSpan = occurrence.Span,
            ResolvedSourceText = occurrence.RawText,
        };

    private static HarnessOccurrenceJoinResult Ambiguous(HarnessOccurrenceJoinResult result, string reason) => result with
    {
        JoinStatus = HarnessOccurrenceJoinStatus.Ambiguous,
        JoinMethod = "EXACT_IDENTITY_UNIQUE_REQUIRED",
        Reason = reason,
    };
}

public static class HarnessHumanReviewValidator
{
    public static HarnessReviewValidationResult Validate(
        HarnessHumanReviewDecision decision,
        HarnessSourceOccurrence source,
        string expectedSourceSha256,
        string expectedPacketSha256)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(decision.DocumentId))
            return new(false, "document-id-missing");
        if (string.IsNullOrWhiteSpace(decision.SourceId)) return new(false, "source-id-missing");
        if (!string.Equals(decision.SourceId.Trim().TrimStart('@'), source.SourceId.Trim().TrimStart('@'), StringComparison.Ordinal))
            return new(false, "source-id-mismatch");
        if (decision.SourceOrdinal is { } ordinal && ordinal != source.SourceOrdinal)
            return new(false, "source-ordinal-mismatch");
        if (string.IsNullOrWhiteSpace(decision.ReviewerId)) return new(false, "reviewer-id-missing");
        if (string.IsNullOrWhiteSpace(decision.ReviewedAt)) return new(false, "reviewed-at-missing");
        if (string.IsNullOrWhiteSpace(decision.DecisionVersion)) return new(false, "decision-version-missing");
        if (!string.Equals(decision.SourceSha256, expectedSourceSha256, StringComparison.OrdinalIgnoreCase))
            return new(false, "source-sha-mismatch");
        if (!string.Equals(decision.PacketSha256, expectedPacketSha256, StringComparison.OrdinalIgnoreCase))
            return new(false, "packet-sha-mismatch");
        if (decision.IsHeading is not ("YES" or "NO" or "UNSURE")) return new(false, "is-heading-invalid");
        if (decision.Role is not ("heading" or "title" or "caption" or "label" or "body" or "other" or "UNSURE"))
            return new(false, "role-invalid");
        if (decision.HeadingSpan is { } span && !new StructuralSpan(span.Start, span.End).IsValidFor(source.RawText))
            return new(false, "heading-span-invalid");
        if (int.TryParse(decision.Level, out var level) && (level is < 1 or > 9))
            return new(false, "level-invalid");
        if (string.Equals(decision.IsHeading, "YES", StringComparison.Ordinal) && decision.Role is ("body" or "other"))
            return new(false, "heading-role-incompatible");
        return new(true, null);
    }
}
