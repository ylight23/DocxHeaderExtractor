using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal enum PageContextScope { LocalVerticalWindow, SubjectPages, SubjectAndAdjacentPages }
internal sealed record PageContextPolicy(PageContextScope Scope, double RadiusPoints = 72,
    int AdjacentPageRadius = 0, int MaxContextUtf8Bytes = 262_144);
internal sealed record PageContextObservation(string SourceAlias, int Ordinal, int SpanStart, int SpanEnd,
    bool Selectable, IReadOnlyList<SourceEvidenceField> Fields);
internal sealed record PageContextPage(int Page, int AvailableSourceOccurrences, string Coverage,
    IReadOnlyList<PageContextObservation> Observations);

/// <summary>Immutable source-only context. Source order is preserved; no inferred rows, columns,
/// table/heading labels, or new selectable occurrences. Glyph detail remains in parser diagnostics.</summary>
internal sealed class PdfPageEvidenceContext
{
    public const string Version = "P7_PAGE_SOURCE_CONTEXT_V1";
    public string ProtocolVersion => Version;
    public string SourceSha256 { get; }
    public string EvidenceStoreSha256 { get; }
    public string SourceAliasUniverseSha256 { get; }
    public string CoordinateSystem => PdfSpatialEvidenceLedgerBuilder.GeometryBasis;
    public string OrderBasis => "CANONICAL_SOURCE_ORDINAL_NOT_TABLE_ROW_OR_COLUMN_IDENTITY";
    public PageContextPolicy Policy { get; }
    public IReadOnlyList<string> SubjectAliases { get; }
    public IReadOnlyList<string> MissingGeometryAliases { get; }
    public IReadOnlyList<PageContextPage> Pages { get; }
    public string ContextSha256 => SpatialCanonical.Hash(CanonicalBytes());
    public byte[] CanonicalBytes() => SpatialCanonical.Bytes(new { ProtocolVersion, SourceSha256,
        EvidenceStoreSha256, SourceAliasUniverseSha256, CoordinateSystem, OrderBasis,
        Policy, SubjectAliases, MissingGeometryAliases, Pages });
    internal PdfPageEvidenceContext(PdfSourceEvidenceStore store, PageContextPolicy policy,
        string[] subjects, string[] missing, PageContextPage[] pages)
    {
        SourceSha256 = store.SourceSha256; EvidenceStoreSha256 = store.StoreSha256;
        SourceAliasUniverseSha256 = store.SourceAliasUniverseSha256; Policy = policy;
        SubjectAliases = Array.AsReadOnly(subjects); MissingGeometryAliases = Array.AsReadOnly(missing);
        Pages = Array.AsReadOnly(pages);
    }
}

internal static class PdfPageEvidenceContextBuilder
{
    public static PdfPageEvidenceContext Build(PdfSourceEvidenceStore store,
        IReadOnlyList<string> subjects, PageContextPolicy policy)
    {
        if (!Enum.IsDefined(policy.Scope) || !double.IsFinite(policy.RadiusPoints) || policy.RadiusPoints < 0 ||
            policy.AdjacentPageRadius < 0 || policy.AdjacentPageRadius > 2 || policy.MaxContextUtf8Bytes <= 0 ||
            policy.Scope != PageContextScope.SubjectAndAdjacentPages && policy.AdjacentPageRadius != 0)
            throw new InvalidOperationException("page-context-policy-invalid");
        var entries = store.Entries.OrderBy(x => x.Ordinal).ThenBy(x => x.SourceAlias, StringComparer.Ordinal).ToArray();
        var byAlias = entries.ToDictionary(x => x.SourceAlias, StringComparer.Ordinal);
        if (subjects.Count == 0 || subjects.Distinct(StringComparer.Ordinal).Count() != subjects.Count || subjects.Any(x => !byAlias.ContainsKey(x)))
            throw new InvalidOperationException("page-context-subject-invalid");
        var anchors = entries.Where(x => subjects.Contains(x.SourceAlias)).ToArray();
        if (anchors.Any(x => Page(x) is null)) throw new InvalidOperationException("page-context-subject-page-unavailable");
        var subjectPages = anchors.Select(x => Page(x)!.Value).ToHashSet();
        var pages = entries.Where(x => Page(x) is not null).GroupBy(x => Page(x)!.Value).OrderBy(x => x.Key)
            .Where(group => policy.Scope == PageContextScope.SubjectAndAdjacentPages ?
                subjectPages.Any(p => Math.Abs((long)p - group.Key) <= policy.AdjacentPageRadius) : subjectPages.Contains(group.Key))
            .Select(group =>
            {
                var selected = group.Where(entry => policy.Scope != PageContextScope.LocalVerticalWindow ||
                    subjects.Contains(entry.SourceAlias) || anchors.Any(anchor => Page(anchor) == group.Key &&
                        Bounds(anchor) is { } a && Bounds(entry) is { } b &&
                        b.Top >= a.Bottom - policy.RadiusPoints && b.Bottom <= a.Top + policy.RadiusPoints)).ToArray();
                return new PageContextPage(group.Key, group.Count(), selected.Length == group.Count() ?
                    "FULL_CANONICAL_OCCURRENCE_PAGE_NOT_FULL_VISUAL_PAGE" : "PARTIAL_VERTICAL_WINDOW",
                    Array.AsReadOnly(selected.Select(entry => new PageContextObservation(entry.SourceAlias,
                        entry.Ordinal, entry.SpanStart, entry.SpanEnd, false,
                        Array.AsReadOnly(entry.Fields.Where(field => field.Name != "glyphs").Select(field => field with
                        { Value = field.Value?.Clone() }).ToArray()))).ToArray()));
            }).ToArray();
        var missing = entries.Where(entry => subjectPages.Contains(Page(entry) ?? -1) && Bounds(entry) is null)
            .Select(x => x.SourceAlias).ToArray();
        var result = new PdfPageEvidenceContext(store, policy, anchors.Select(x => x.SourceAlias).ToArray(), missing, pages);
        // Never silently truncate page observations or change policy to fit a budget.
        if (result.CanonicalBytes().Length > policy.MaxContextUtf8Bytes) throw new InvalidOperationException("page-context-byte-cap-exceeded");
        return result;
    }

    private static JsonElement? Value(SourceEvidenceEntry entry, string name)
    {
        var field = entry.Fields.Single(x => x.Name == name);
        return field.Availability == "OBSERVED" ? field.Value : null;
    }
    private static int? Page(SourceEvidenceEntry entry) => Value(entry, "page")?.GetInt32();
    private static SpatialBounds? Bounds(SourceEvidenceEntry entry) => Value(entry, "bbox") is { } value ?
        JsonSerializer.Deserialize<SpatialBounds>(value.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
}
