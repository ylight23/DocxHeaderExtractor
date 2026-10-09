using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record SourceEvidenceField(string Name, string Availability, JsonElement? Value,
    string Basis, string? MissingReason, string ValueSha256);
internal sealed record SourceEvidenceEntry(string SourceAlias, string SourceIdSha256, int Ordinal,
    int SpanStart, int SpanEnd, IReadOnlyList<SourceEvidenceField> Fields);

/// <summary>Parser-owned raw evidence. No relational predicates or model interpretation.</summary>
internal sealed class PdfSourceEvidenceStore
{
    public const string Version = "P7_RAW_SOURCE_EVIDENCE_STORE_V1";
    public string ProtocolVersion => Version;
    public string SourceSha256 { get; }
    public string SourceAliasUniverseSha256 { get; }
    public IReadOnlyList<SourceEvidenceEntry> Entries { get; }
    public string StoreSha256 => SpatialCanonical.Hash(CanonicalBytes());

    private PdfSourceEvidenceStore(string sourceHash, string universeHash, IEnumerable<SourceEvidenceEntry> entries)
    {
        SourceSha256 = sourceHash;
        SourceAliasUniverseSha256 = universeHash;
        Entries = Array.AsReadOnly(entries.Select(entry => entry with
        {
            Fields = Array.AsReadOnly(entry.Fields.Select(field => field with { Value = field.Value?.Clone() }).ToArray())
        }).ToArray());
    }

    public byte[] CanonicalBytes() => SpatialCanonical.Bytes(new
    { ProtocolVersion, SourceSha256, SourceAliasUniverseSha256, Entries });

    public static PdfSourceEvidenceStore Build(DocumentSourceSnapshot source, PdfSourceDetails details)
    {
        // Reuse D1 identity checks and native bounds/missingness, but do not expose its relation facts.
        var measurements = PdfSpatialEvidenceLedgerBuilder.Build(source, details, []);
        var atoms = source.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var blocks = details.Blocks.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var entries = measurements.Observations.Select(observation =>
        {
            var atom = atoms[observation.Alias];
            var line = blocks.TryGetValue(atom.SourceId, out var block) ? block.Lines.Single() : null;
            JsonElement? typography = null;
            if (line is not null)
            {
                var value = SpatialCanonical.Element(new
                { line.FontSize, line.FontName, line.BoldRatio, line.ItalicRatio, richTypography = line.Typography });
                // Non-finite parser summaries cannot become JSON facts or be silently coerced.
                typography = value;
            }
            return new SourceEvidenceEntry(observation.Alias, observation.SourceIdSha256, observation.Ordinal,
                observation.SpanStart, observation.SpanEnd, Array.AsReadOnly(new[]
                {
                    Field("text", SpatialCanonical.Element(atom.Text), "CANONICAL_SOURCE_OCCURRENCE_TEXT_V1"),
                    Field("page", observation.Page is null ? null : SpatialCanonical.Element(observation.Page), "PARSER_PAGE_NUMBER_V1", "PAGE_NOT_AVAILABLE"),
                    Field("bbox", observation.Bounds is null ? null : SpatialCanonical.Element(observation.Bounds), PdfSpatialEvidenceLedgerBuilder.GeometryBasis, observation.MissingReason),
                    Field("typography", typography, "PARSER_LINE_TYPOGRAPHY_SUMMARIES_V1", "PARSER_LINE_NOT_AVAILABLE"),
                    Field("readingOrder", SpatialCanonical.Element(observation.Ordinal), "SOURCE_OCCURRENCE_ORDINAL_V1_NOT_VISUAL_ROW_IDENTITY"),
                    Field("glyphs", null, "NO_GLYPH_STORE_PROJECTION_V1", "GLYPH_RECORDS_NOT_PROJECTED")
                }));

            SourceEvidenceField Field(string name, JsonElement? value, string basis, string? missing = null)
            {
                var availability = value is null ? "NOT_AVAILABLE" : "OBSERVED";
                var reason = value is null ? missing ?? "MEASUREMENT_NOT_AVAILABLE" : null;
                var hash = SpatialCanonical.Hash(SpatialCanonical.Bytes(new
                { source.SourceSha256, source.SourceAliasUniverseHash, sourceAlias = observation.Alias, name, value, availability, basis, reason }));
                return new(name, availability, value, basis, reason, hash);
            }
        });
        return new(source.SourceSha256, source.SourceAliasUniverseHash, entries);
    }
}
