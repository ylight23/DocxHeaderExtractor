using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Sources.Snapshot;
using DocxHeaderExtractor.Sources.Pdf;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Formats;

/// <summary>
/// The parser-owned evidence store (P7_RAW_SOURCE_EVIDENCE_STORE_V1): one entry per atom with its text, page, bbox, typography and reading
/// order, and the store's SHA-256 over its canonical bytes. Only the observations of the qualified spatial ledger are computed; its relation
/// facts never entered the store.
/// </summary>
internal static class EvidenceStore
{
    public const string Version = "P7_RAW_SOURCE_EVIDENCE_STORE_V1";
    public const string GeometryBasis = "PDF_LINE_GLYPH_UNION_BOUNDS_V1_PDF_POINTS_BOTTOM_LEFT";

    static readonly JsonSerializerOptions Canonical = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Canonical);
    static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Canonical);
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    sealed record Bounds(double Left, double Right, double Bottom, double Top);
    sealed record Observation(string Alias, int Ordinal, int? Page, Bounds? Bounds, string? MissingReason, string SourceIdSha256);
    sealed record Field(string Name, string Availability, JsonElement? Value, string Basis, string? MissingReason, string ValueSha256);
    sealed record Entry(string SourceAlias, string SourceIdSha256, int Ordinal, int SpanStart, int SpanEnd, IReadOnlyList<Field> Fields);

    public static (string StoreSha256, IReadOnlyList<AgentSourceLine> Lines) Build(DocumentSourceSnapshot source, PdfSourceDetails details)
    {
        if (source.SourceKind != "pdf" || !IsHash(source.SourceSha256) || !IsHash(source.SourceAliasUniverseHash))
            throw new InvalidOperationException("spatial-source-identity-required");
        var atoms = source.Atoms.OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).ToArray();
        if (atoms.Select(atom => atom.Alias).Distinct(StringComparer.Ordinal).Count() != atoms.Length ||
            atoms.Select(atom => atom.SourceId).Distinct(StringComparer.Ordinal).Count() != atoms.Length)
            throw new InvalidOperationException("spatial-duplicate-source-identity");
        var blocks = details.Blocks.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var entries = new List<Entry>();
        var lines = new List<AgentSourceLine>();
        foreach (var atom in atoms)
        {
            Bounds? bounds = null;
            string? missing = null;
            PdfLine? parserLine = null;
            if (!blocks.TryGetValue(atom.SourceId, out var block)) missing = "PARSER_LINE_NOT_AVAILABLE";
            else
            {
                if (block.Lines.Count != 1) throw new InvalidOperationException("spatial-source-not-single-parser-segment");
                var line = block.Lines[0];
                parserLine = line;
                if (PdfLineIdentity.Of(line) != atom.SourceId || line.Page != atom.Page || line.Projection.VerbatimText != atom.Text)
                    throw new InvalidOperationException("spatial-parser-source-mismatch");
                if (line.Top is null || line.Bottom is null) missing = "GLYPH_VERTICAL_BOUNDS_NOT_AVAILABLE";
                else if (!double.IsFinite(line.Left) || !double.IsFinite(line.Right) ||
                    !double.IsFinite(line.Top.Value) || !double.IsFinite(line.Bottom.Value) ||
                    line.Right <= line.Left || line.Top.Value <= line.Bottom.Value)
                    missing = "INVALID_OR_DEGENERATE_GLYPH_BOUNDS";
                else bounds = new(line.Left == 0 ? 0 : line.Left, line.Right == 0 ? 0 : line.Right,
                    line.Bottom.Value == 0 ? 0 : line.Bottom.Value, line.Top.Value == 0 ? 0 : line.Top.Value);
            }
            var observation = new Observation(atom.Alias, atom.Ordinal, atom.Page > 0 ? atom.Page : null, bounds, missing, Hash(Encoding.UTF8.GetBytes(atom.SourceId)));
            JsonElement? typography = parserLine is null ? null
                : Element(new { parserLine.FontSize, parserLine.FontName, parserLine.BoldRatio, parserLine.ItalicRatio, richTypography = parserLine.Typography });

            Field Make(string name, JsonElement? value, string basis, string? missingReason = null)
            {
                var availability = value is null ? "NOT_AVAILABLE" : "OBSERVED";
                var reason = value is null ? missingReason ?? "MEASUREMENT_NOT_AVAILABLE" : null;
                var hash = Hash(Bytes(new
                { source.SourceSha256, source.SourceAliasUniverseHash, sourceAlias = observation.Alias, name, value, availability, basis, reason }));
                return new(name, availability, value, basis, reason, hash);
            }
            var textValue = Element(atom.Text);
            JsonElement? pageValue = observation.Page is null ? null : Element(observation.Page);
            JsonElement? bboxValue = observation.Bounds is null ? null : Element(observation.Bounds);
            entries.Add(new Entry(observation.Alias, observation.SourceIdSha256, observation.Ordinal, 0, atom.Text.Length, new[]
            {
                Make("text", textValue, "CANONICAL_SOURCE_OCCURRENCE_TEXT_V1"),
                Make("page", pageValue, "PARSER_PAGE_NUMBER_V1", "PAGE_NOT_AVAILABLE"),
                Make("bbox", bboxValue, GeometryBasis, observation.MissingReason),
                Make("typography", typography, "PARSER_LINE_TYPOGRAPHY_SUMMARIES_V1", "PARSER_LINE_NOT_AVAILABLE"),
                Make("readingOrder", Element(observation.Ordinal), "SOURCE_OCCURRENCE_ORDINAL_V1_NOT_VISUAL_ROW_IDENTITY"),
                Make("glyphs", null, "NO_GLYPH_STORE_PROJECTION_V1", "GLYPH_RECORDS_NOT_PROJECTED"),
            }));
            lines.Add(new AgentSourceLine(atom.Alias, atom.Ordinal, observation.Page, atom.Text, bboxValue, typography));
        }
        var storeSha = Hash(Bytes(new { ProtocolVersion = Version, SourceSha256 = source.SourceSha256, SourceAliasUniverseSha256 = source.SourceAliasUniverseHash, Entries = entries }));
        return (storeSha, lines);
    }

    static bool IsHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
}
