using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record CoverageSource(string Occurrence, string Alias, int? Page, string Text, bool BoundsAvailable);
internal sealed record PageCoverage(int Page, int SourceOccurrences, int VisibleTextOccurrences,
    int VisibleBoundsOccurrences, string CanonicalOccurrenceCoverage, IReadOnlyList<string> MissingAliases);
internal sealed record RequestEvidenceCoverage(int VisibleTextOccurrences, int VisibleBoundsOccurrences,
    int AnonymousTextContexts, int SamePagePairsWithBounds, int CrossPagePairsWithBounds,
    IReadOnlyList<PageCoverage> Pages, bool FullVisualPageEvidence = false);

/// <summary>Counts actual JSON visibility. Physical pair availability is not a semantic relation.
/// Full canonical occurrence coverage never implies full visual-page coverage.</summary>
internal static class PageEvidenceCoverageAudit
{
    public static RequestEvidenceCoverage Measure(JsonElement user, IReadOnlyList<CoverageSource> source, bool interpretation)
    {
        if (source.Select(x => x.Occurrence).Distinct(StringComparer.Ordinal).Count() != source.Count ||
            source.Select(x => x.Alias).Distinct(StringComparer.Ordinal).Count() != source.Count)
            throw new InvalidOperationException("coverage-source-identity-duplicate");
        var trusted = source.ToDictionary(x => x.Occurrence, StringComparer.Ordinal);
        var text = new HashSet<string>(StringComparer.Ordinal);
        var bounds = new HashSet<string>(StringComparer.Ordinal);
        var anonymous = 0;
        Visit(interpretation ? user.GetProperty("stageInput") : user);
        if (interpretation)
        {
            foreach (var row in user.GetProperty("sourceEvidence").EnumerateArray())
            {
                var id = row.GetProperty("occurrence").GetString()!;
                if (!trusted.TryGetValue(id, out var item)) throw new InvalidOperationException("coverage-unissued-evidence");
                var entry = row.GetProperty("source");
                if (entry.GetProperty("sourceAlias").GetString() != item.Alias)
                    throw new InvalidOperationException("coverage-alias-mismatch");
                var fields = entry.GetProperty("fields").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
                if (Observed("text") && fields["text"].GetProperty("value").GetString() == item.Text) text.Add(id);
                else throw new InvalidOperationException("coverage-text-mismatch");
                if (Observed("bbox"))
                {
                    if (!item.BoundsAvailable) throw new InvalidOperationException("coverage-invented-bounds");
                    bounds.Add(id);
                }
                bool Observed(string field) => fields.TryGetValue(field, out var value) &&
                    value.GetProperty("availability").GetString() == "OBSERVED" && value.GetProperty("value").ValueKind != JsonValueKind.Null;
            }
        }
        var pages = source.Where(x => x.Page.HasValue).GroupBy(x => x.Page!.Value).OrderBy(x => x.Key)
            .Select(group => new PageCoverage(group.Key, group.Count(), group.Count(x => text.Contains(x.Occurrence)),
                group.Count(x => bounds.Contains(x.Occurrence)), group.All(x => text.Contains(x.Occurrence)) ?
                "FULL_CANONICAL_OCCURRENCE_PAGE" : group.Any(x => text.Contains(x.Occurrence)) ? "PARTIAL" : "NONE",
                group.Where(x => !text.Contains(x.Occurrence)).Select(x => x.Alias).Order(StringComparer.Ordinal).ToArray())).ToArray();
        var physical = source.Where(x => bounds.Contains(x.Occurrence) && x.Page.HasValue).ToArray();
        var same = physical.GroupBy(x => x.Page).Sum(group => group.Count() * (group.Count() - 1) / 2);
        var all = physical.Length * (physical.Length - 1) / 2;
        return new(text.Count, bounds.Count, anonymous, same, all - same, pages);

        void Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array) { foreach (var child in node.EnumerateArray()) Visit(child); return; }
            if (node.ValueKind != JsonValueKind.Object) return;
            if (node.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
            {
                var id = new[] { "id", "occurrence", "primary" }.Select(key => node.TryGetProperty(key, out var p) &&
                    p.ValueKind == JsonValueKind.String ? p.GetString() : null).FirstOrDefault(x => x is not null);
                if (id is null) anonymous++;
                else
                {
                    if (!trusted.TryGetValue(id, out var item) || value.GetString() != item.Text ||
                        !node.TryGetProperty("page", out var page) || (page.ValueKind == JsonValueKind.Null ? null : (int?)page.GetInt32()) != item.Page)
                        throw new InvalidOperationException("coverage-stage-source-mismatch");
                    text.Add(id);
                }
            }
            foreach (var property in node.EnumerateObject()) Visit(property.Value);
        }
    }
}
