using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal static class SpatialFactTypes
{
    public const string Bounds = "SOURCE_BOUNDS";
    public const string SamePage = "SAME_PAGE";
    public const string SameRow = "SAME_VISUAL_ROW";
    public const string DistinctHorizontalRegions = "DISTINCT_HORIZONTAL_REGIONS";
    public static bool IsRelation(string type) => type is SamePage or SameRow or DistinctHorizontalRegions;
}

internal sealed record SpatialBounds(double Left, double Right, double Bottom, double Top);
internal sealed record SpatialObservation(string Alias, string SourceIdSha256, int Ordinal,
    int SpanStart, int SpanEnd, string TextSha256, int? Page, SpatialBounds? Bounds, string? MissingReason);
internal sealed record SpatialRelationQuery(string Type, IReadOnlyList<string> Subjects);
internal sealed record SpatialFactProvenance(string Basis, string Rule, string SourceSha256,
    string SourceAliasUniverseSha256, string MeasurementsSha256);
internal sealed record PdfSpatialEvidenceFact(string FactId, string Type, IReadOnlyList<string> Subjects,
    JsonElement? Value, string Availability, string VerificationStatus, string? MissingReason,
    SpatialFactProvenance Provenance);
internal sealed record SpatialEvidenceReference(string FactId, IReadOnlyList<string> Subjects,
    JsonElement? AssertedValue = null);
internal sealed record SpatialEvidenceValidationIssue(string Code, string? FactId = null);

/// <summary>Qualification-only immutable evidence; no text, heading policy or model authority.</summary>
internal sealed class PdfSpatialEvidenceLedger
{
    public const string Version = "P7_SPATIAL_EVIDENCE_LEDGER_V1";
    public string ProtocolVersion => Version;
    public string SourceSha256 { get; }
    public string SourceAliasUniverseSha256 { get; }
    public string ModelVisibleEvidenceSha256 { get; }
    public IReadOnlyList<SpatialObservation> Observations { get; }
    public IReadOnlyList<PdfSpatialEvidenceFact> Facts { get; }
    public string LedgerSha256 => SpatialCanonical.Hash(CanonicalBytes());

    internal PdfSpatialEvidenceLedger(string sourceHash, string universeHash, string evidenceHash,
        IEnumerable<SpatialObservation> observations, IEnumerable<PdfSpatialEvidenceFact> facts)
    {
        SourceSha256 = sourceHash;
        SourceAliasUniverseSha256 = universeHash;
        ModelVisibleEvidenceSha256 = evidenceHash;
        Observations = Array.AsReadOnly(observations.ToArray());
        Facts = Array.AsReadOnly(facts.Select(fact => fact with
        {
            Subjects = Array.AsReadOnly(fact.Subjects.ToArray()),
            Value = fact.Value?.Clone(),
        }).ToArray());
    }

    public byte[] CanonicalBytes() => SpatialCanonical.Bytes(new
    { ProtocolVersion, SourceSha256, SourceAliasUniverseSha256, ModelVisibleEvidenceSha256, Observations, Facts });
}

internal static class SpatialCanonical
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
