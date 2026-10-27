using System.Text.Json;

namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>Experimental boundary proposals, subordinate to validated selection membership.</summary>
internal static class InlineGrouping
{
    public const string Version = "PDF_SELECT_EXTENT_GROUPS_V1";
    public const string Prompt = """
        Also return selectionGroups: {"groups":[["alias", "alias"]],"unresolvedAliases":[]}.
        Partition the SELECT targets into complete headings, including singleton headings. Each group contains
        one to eight consecutive source lines in source order. A heading is the section label itself; do not
        join a document title with its venue, date or following headings merely because they are adjacent.
        Use the selected headingExtent text when supplied. Include only this unit's SELECT targets, never
        context aliases or NOT_SELECT targets. Every SELECT target occurs exactly once across groups and
        unresolvedAliases. Use unresolvedAliases for a boundary you cannot settle locally, including a
        heading that continues outside the target scope. Grouping does not change any selection decision.
        """;

    public static (IReadOnlyList<IReadOnlyList<string>>? Groups, string? Failure) Parse(JsonElement root,
        IReadOnlyDictionary<string, string> decisions, IReadOnlyDictionary<string, int> positions)
    {
        if (!root.TryGetProperty("selectionGroups", out var value) || value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Count() != 2 || !value.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array ||
            !value.TryGetProperty("unresolvedAliases", out var unresolved) || unresolved.ValueKind != JsonValueKind.Array)
            return (null, "INLINE_GROUP_SHAPE");
        var selected = decisions.Where(d => d.Value == "SELECT").Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<IReadOnlyList<string>>();
        var lastEnd = -1;
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Array || group.GetArrayLength() is < 1 or > 8) return (null, "INLINE_GROUP_SIZE");
            var aliases = new List<string>();
            var previous = -1;
            foreach (var item in group.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !selected.Contains(item.GetString()!) || !seen.Add(item.GetString()!))
                    return (null, "INLINE_GROUP_MEMBERSHIP");
                var alias = item.GetString()!;
                var at = positions[alias];
                if (previous < 0 ? at <= lastEnd : at != previous + 1) return (null, "INLINE_GROUP_ORDER");
                aliases.Add(alias); previous = at;
            }
            lastEnd = previous;
            result.Add(aliases);
        }
        foreach (var item in unresolved.EnumerateArray())
            if (item.ValueKind != JsonValueKind.String || !selected.Contains(item.GetString()!) || !seen.Add(item.GetString()!))
                return (null, "INLINE_GROUP_MEMBERSHIP");
        return seen.SetEquals(selected) ? (result, null) : (null, "INLINE_GROUP_COVERAGE");
    }
}
