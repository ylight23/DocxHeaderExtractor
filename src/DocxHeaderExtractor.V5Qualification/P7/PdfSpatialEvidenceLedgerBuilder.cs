using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Geometry-only qualification builder. SAME_VISUAL_ROW is a versioned geometric row proxy
/// (positive common y-band intersection), never a parser-certified table/heading/unit assertion.
/// No Gold, model output, font-size height fallback or semantic classifier is read.
/// </summary>
internal static class PdfSpatialEvidenceLedgerBuilder
{
    public const string GeometryBasis = "PDF_LINE_GLYPH_UNION_BOUNDS_V1_PDF_POINTS_BOTTOM_LEFT";
    public const string QueryPolicy = "ALL_CONSECUTIVE_SOURCE_WINDOWS_OF_2_AND_3_V1";

    public static PdfSpatialEvidenceLedger Build(DocumentSourceSnapshot source, PdfSourceDetails details,
        IReadOnlyList<SpatialRelationQuery>? queries = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(details);
        if (source.SourceKind != "pdf" || !IsHash(source.SourceSha256) || !IsHash(source.SourceAliasUniverseHash))
            throw new InvalidOperationException("spatial-source-identity-required");
        var atoms = source.Atoms.OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).ToArray();
        if (atoms.Select(atom => atom.Alias).Distinct(StringComparer.Ordinal).Count() != atoms.Length ||
            atoms.Select(atom => atom.SourceId).Distinct(StringComparer.Ordinal).Count() != atoms.Length)
            throw new InvalidOperationException("spatial-duplicate-source-identity");
        var blocks = details.Blocks.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var observations = atoms.Select(atom =>
        {
            SpatialBounds? bounds = null;
            string? missing = null;
            if (!blocks.TryGetValue(atom.SourceId, out var block)) missing = "PARSER_LINE_NOT_AVAILABLE";
            else
            {
                if (block.Lines.Count != 1) throw new InvalidOperationException("spatial-source-not-single-parser-segment");
                var line = block.Lines[0];
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
            return new SpatialObservation(atom.Alias, SpatialCanonical.Hash(Encoding.UTF8.GetBytes(atom.SourceId)),
                atom.Ordinal, 0, atom.Text.Length, SpatialCanonical.Hash(Encoding.UTF8.GetBytes(atom.Text)),
                atom.Page > 0 ? atom.Page : null, bounds, missing);
        }).ToArray();
        var byAlias = observations.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var facts = new List<PdfSpatialEvidenceFact>();
        foreach (var observation in observations)
            facts.Add(Fact(SpatialFactTypes.Bounds, [observation],
                observation.Bounds is null || observation.Page is null ? null : SpatialCanonical.Element(new { observation.Page, observation.Bounds }),
                "EXACT_PARSER_LINE_BOUNDS_NO_FONT_SIZE_FALLBACK_V1",
                observation.MissingReason ?? (observation.Page is null ? "PAGE_NOT_AVAILABLE" : null)));
        var normalized = (queries ?? DefaultQueries(observations)).Select(query =>
        {
            if (!SpatialFactTypes.IsRelation(query.Type) || query.Subjects.Count < 2 ||
                query.Subjects.Distinct(StringComparer.Ordinal).Count() != query.Subjects.Count || query.Subjects.Any(alias => !byAlias.ContainsKey(alias)))
                throw new InvalidOperationException("spatial-query-invalid");
            return new SpatialRelationQuery(query.Type, Array.AsReadOnly(query.Subjects.OrderBy(alias => byAlias[alias].Ordinal)
                .ThenBy(alias => alias, StringComparer.Ordinal).ToArray()));
        }).ToArray();
        if (normalized.Select(query => SpatialCanonical.Hash(SpatialCanonical.Bytes(query))).Distinct().Count() != normalized.Length)
            throw new InvalidOperationException("spatial-query-duplicate");
        foreach (var query in normalized)
        {
            var subjects = query.Subjects.Select(alias => byAlias[alias]).ToArray();
            var pageMissing = subjects.Any(subject => subject.Page is null);
            var geometryMissing = subjects.Any(subject => subject.Bounds is null);
            var samePage = !pageMissing && subjects.Select(subject => subject.Page).Distinct().Count() == 1;
            bool? value;
            string? reason;
            string rule;
            if (query.Type == SpatialFactTypes.SamePage)
            { value = pageMissing ? null : samePage; reason = pageMissing ? "PAGE_NOT_AVAILABLE" : null; rule = "ALL_PAGE_NUMBERS_EQUAL_V1"; }
            else
            {
                reason = pageMissing ? "PAGE_NOT_AVAILABLE" : geometryMissing ? "SUBJECT_GEOMETRY_NOT_AVAILABLE" : null;
                rule = query.Type == SpatialFactTypes.SameRow
                    ? "SAME_PAGE_AND_STRICT_POSITIVE_COMMON_Y_BAND_INTERSECTION_V1_GEOMETRIC_ROW_PROXY"
                    : "SAME_PAGE_AND_PAIRWISE_NONOVERLAPPING_X_INTERVALS_TOUCH_ALLOWED_V1";
                value = reason is not null ? null : query.Type == SpatialFactTypes.SameRow
                    ? samePage && subjects.Max(subject => subject.Bounds!.Bottom) < subjects.Min(subject => subject.Bounds!.Top)
                    : samePage && Nonoverlapping(subjects);
            }
            facts.Add(Fact(query.Type, subjects, value is null ? null : SpatialCanonical.Element(value.Value), rule, reason));
        }
        return new(source.SourceSha256, source.SourceAliasUniverseHash, source.ModelVisibleEvidenceHash, observations,
            facts.OrderBy(fact => fact.FactId, StringComparer.Ordinal));

        PdfSpatialEvidenceFact Fact(string type, IReadOnlyList<SpatialObservation> subjects,
            System.Text.Json.JsonElement? value, string rule, string? reason)
        {
            var provenance = new SpatialFactProvenance(GeometryBasis, rule, source.SourceSha256,
                source.SourceAliasUniverseHash, SpatialCanonical.Hash(SpatialCanonical.Bytes(subjects)));
            var aliases = Array.AsReadOnly(subjects.Select(subject => subject.Alias).ToArray());
            var availability = value is null ? "NOT_AVAILABLE" : "OBSERVED";
            var id = "SPATIAL-" + SpatialCanonical.Hash(SpatialCanonical.Bytes(new
            { version = PdfSpatialEvidenceLedger.Version, type, subjects = aliases, value, availability, reason, provenance }));
            return new(id, type, aliases, value, availability,
                value is null ? "NO_VERIFIABLE_MEASUREMENT" : "RECOMPUTED_FROM_PARSER_FACTS", reason, provenance);
        }
    }

    private static bool Nonoverlapping(IReadOnlyList<SpatialObservation> subjects)
    {
        var ordered = subjects.OrderBy(subject => subject.Bounds!.Left).ThenBy(subject => subject.Alias, StringComparer.Ordinal).ToArray();
        return ordered.Skip(1).Select((subject, index) => ordered[index].Bounds!.Right <= subject.Bounds!.Left).All(value => value);
    }

    private static IReadOnlyList<SpatialRelationQuery> DefaultQueries(IReadOnlyList<SpatialObservation> observations)
    {
        var queries = new List<SpatialRelationQuery>();
        foreach (var count in new[] { 2, 3 })
        for (var start = 0; start + count <= observations.Count; start++)
        foreach (var type in new[] { SpatialFactTypes.SamePage, SpatialFactTypes.SameRow, SpatialFactTypes.DistinctHorizontalRegions })
            queries.Add(new(type, Array.AsReadOnly(observations.Skip(start).Take(count).Select(value => value.Alias).ToArray())));
        return queries;
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
}
