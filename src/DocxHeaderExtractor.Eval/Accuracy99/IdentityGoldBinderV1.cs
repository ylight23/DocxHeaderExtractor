using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.Accuracy99;

/// <summary>
/// Binding status for a source-backed identity-Gold endpoint.  Semantic labels are
/// deliberately absent from this API: binding resolves source identity only.
/// </summary>
public enum IdentityGoldBindingStatus
{
    [JsonStringEnumMemberName("EXACT_BOUND")] ExactBound,
    [JsonStringEnumMemberName("AMBIGUOUS")] Ambiguous,
    [JsonStringEnumMemberName("SOURCE_NOT_FOUND")] SourceNotFound,
    [JsonStringEnumMemberName("SOURCE_HASH_MISMATCH")] SourceHashMismatch,
    [JsonStringEnumMemberName("TEXT_NOT_FOUND")] TextNotFound,
    [JsonStringEnumMemberName("STRUCTURAL_CONTEXT_INSUFFICIENT")] StructuralContextInsufficient,
}

/// <summary>
/// Parser-owned source occurrence.  Optional coordinates remain nullable when the
/// source representation does not provide them.
/// </summary>
public sealed record IdentityGoldSourceOccurrence(
    string DocumentId,
    string SourceSha256,
    string? SourceOccurrenceId,
    string? SourceAlias,
    string VerbatimText,
    int? Page,
    int? SourceOrdinal,
    string? NativePath,
    int? Utf16Start,
    int? Utf16Length,
    string? BoundingBoxJson = null);

/// <summary>
/// A source-only endpoint query.  It contains no semantic relation, confidence, or
/// model/candidate field.  Explicit native identity is verified against exact text.
/// </summary>
public sealed record IdentityGoldEndpointQuery(
    string DocumentId,
    string ExpectedSourceSha256,
    string ExactText,
    string? SourceOccurrenceId = null,
    string? SourceAlias = null,
    int? SourceOrdinal = null,
    string? NativePath = null);

public sealed record IdentityGoldBoundOccurrence(
    string DocumentId,
    string SourceSha256,
    string SourceOccurrenceId,
    string? SourceAlias,
    string VerbatimText,
    int? Page,
    int? SourceOrdinal,
    string? NativePath,
    int? Utf16Start,
    int? Utf16Length,
    string? BoundingBoxJson,
    string TextSha256,
    string BindingMethod,
    string BindingConfidence,
    bool SourceLineageVerified);

public sealed record IdentityGoldEndpointBinding(
    IdentityGoldBindingStatus Status,
    IdentityGoldBoundOccurrence? Occurrence,
    IReadOnlyList<string> BindingEvidence,
    IReadOnlyList<string> Ambiguities);

public sealed record IdentityGoldPairBinding(
    IdentityGoldBindingStatus Status,
    IdentityGoldEndpointBinding Left,
    IdentityGoldEndpointBinding Right,
    bool MachineEvaluable);

/// <summary>
/// Deterministic source binder for semantic identity Gold.  The binder never receives
/// or branches on the Gold relation; relation labels belong in a separate artifact.
/// </summary>
public static class IdentityGoldBinderV1
{
    public const string Version = "identity-gold-binder-v1";

    public static IdentityGoldEndpointBinding Bind(
        IdentityGoldEndpointQuery query,
        string actualDocumentId,
        string actualSourceSha256,
        IReadOnlyList<IdentityGoldSourceOccurrence> sourceOccurrences)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(sourceOccurrences);

        if (!string.Equals(query.DocumentId, actualDocumentId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(actualDocumentId))
            return Failure(IdentityGoldBindingStatus.SourceNotFound, "document identity does not match the bound source");

        if (!string.Equals(query.ExpectedSourceSha256, actualSourceSha256, StringComparison.OrdinalIgnoreCase))
            return Failure(IdentityGoldBindingStatus.SourceHashMismatch, "expected source SHA256 does not match the actual source");

        var source = sourceOccurrences
            .Where(item => string.Equals(item.DocumentId, actualDocumentId, StringComparison.Ordinal))
            .Where(item => string.Equals(item.SourceSha256, actualSourceSha256, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (source.Length == 0)
            return Failure(IdentityGoldBindingStatus.SourceNotFound, "no source occurrences belong to the verified document and SHA");

        IReadOnlyList<IdentityGoldSourceOccurrence> matches;
        var method = "UNIQUE_EXACT_TEXT";

        if (!string.IsNullOrWhiteSpace(query.SourceOccurrenceId))
        {
            matches = source.Where(item => string.Equals(StableSourceId(item), query.SourceOccurrenceId, StringComparison.Ordinal)).ToArray();
            method = "NATIVE_SOURCE_ID_AND_EXACT_TEXT";
        }
        else if (!string.IsNullOrWhiteSpace(query.SourceAlias))
        {
            matches = source.Where(item => string.Equals(item.SourceAlias, query.SourceAlias, StringComparison.Ordinal)).ToArray();
            method = "SOURCE_ALIAS_AND_EXACT_TEXT";
        }
        else if (!string.IsNullOrWhiteSpace(query.NativePath))
        {
            matches = source.Where(item => string.Equals(item.NativePath, query.NativePath, StringComparison.Ordinal)).ToArray();
            method = "NATIVE_PATH_AND_EXACT_TEXT";
        }
        else if (query.SourceOrdinal is { } ordinal)
        {
            matches = source.Where(item => item.SourceOrdinal == ordinal).ToArray();
            method = "SOURCE_ORDINAL_AND_EXACT_TEXT";
        }
        else
        {
            matches = source.ToArray();
        }

        matches = matches.Where(item => string.Equals(item.VerbatimText, query.ExactText, StringComparison.Ordinal)).ToArray();

        if (matches.Count == 0)
            return Failure(IdentityGoldBindingStatus.TextNotFound, "no exact verbatim source text matched the source identity");
        if (matches.Count > 1)
            return new(
                IdentityGoldBindingStatus.Ambiguous,
                null,
                ["exact source selector and text resolved to multiple source occurrences"],
                ["UNIQUE_EXACT_SOURCE_OCCURRENCE_REQUIRED"]);

        var occurrence = matches[0];
        var stableId = StableSourceId(occurrence);
        return new(
            IdentityGoldBindingStatus.ExactBound,
            new IdentityGoldBoundOccurrence(
                occurrence.DocumentId,
                actualSourceSha256,
                stableId,
                occurrence.SourceAlias,
                occurrence.VerbatimText,
                occurrence.Page,
                occurrence.SourceOrdinal,
                occurrence.NativePath,
                occurrence.Utf16Start,
                occurrence.Utf16Length,
                occurrence.BoundingBoxJson,
                Sha256(occurrence.VerbatimText),
                method,
                "EXACT",
                true),
            ["source SHA256 verified", "exact verbatim text verified", $"stable source identity verified: {stableId}"],
            []);
    }

    public static IdentityGoldPairBinding BindPair(
        IdentityGoldEndpointQuery left,
        IdentityGoldEndpointQuery right,
        string actualDocumentId,
        string actualSourceSha256,
        IReadOnlyList<IdentityGoldSourceOccurrence> sourceOccurrences)
    {
        var leftResult = Bind(left, actualDocumentId, actualSourceSha256, sourceOccurrences);
        var rightResult = Bind(right, actualDocumentId, actualSourceSha256, sourceOccurrences);
        var status = leftResult.Status == IdentityGoldBindingStatus.ExactBound &&
                     rightResult.Status == IdentityGoldBindingStatus.ExactBound
            ? IdentityGoldBindingStatus.ExactBound
            : FirstFailure(leftResult.Status, rightResult.Status);
        return new(status, leftResult, rightResult, status == IdentityGoldBindingStatus.ExactBound);
    }

    /// <summary>Returns a stable source-only ID, deriving one only when the source has no native ID.</summary>
    public static string StableSourceId(IdentityGoldSourceOccurrence occurrence)
    {
        if (!string.IsNullOrWhiteSpace(occurrence.SourceOccurrenceId)) return occurrence.SourceOccurrenceId;
        if (!string.IsNullOrWhiteSpace(occurrence.SourceAlias)) return occurrence.SourceAlias;
        var key = string.Join("|", [
            occurrence.SourceSha256,
            occurrence.Page?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<null>",
            occurrence.SourceOrdinal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<null>",
            occurrence.NativePath ?? "<null>",
            Sha256(occurrence.VerbatimText),
        ]);
        return "derived:" + Sha256(key);
    }

    private static IdentityGoldEndpointBinding Failure(IdentityGoldBindingStatus status, string reason) =>
        new(status, null, [reason], []);

    private static IdentityGoldBindingStatus FirstFailure(IdentityGoldBindingStatus left, IdentityGoldBindingStatus right)
    {
        foreach (var status in new[] {
                     IdentityGoldBindingStatus.SourceHashMismatch,
                     IdentityGoldBindingStatus.SourceNotFound,
                     IdentityGoldBindingStatus.TextNotFound,
                     IdentityGoldBindingStatus.StructuralContextInsufficient,
                     IdentityGoldBindingStatus.Ambiguous,
                 })
        {
            if (left == status || right == status) return status;
        }
        return left != IdentityGoldBindingStatus.ExactBound ? left : right;
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
