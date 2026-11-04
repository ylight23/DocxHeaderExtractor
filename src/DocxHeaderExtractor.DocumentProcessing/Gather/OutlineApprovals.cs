using System.Text.Json;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// One reviewed decision about the outline of one exact file (its SHA-256 under one configuration). <see cref="Kind"/>: HEADING (the paragraph at
/// <see cref="Location"/> starts a heading with this text and level under the heading at <see cref="ParentLocation"/>, null for the top),
/// NOT_HEADING (the paragraph is not a heading), or OUTLINE_COMPLETE (the reviewer confirmed the outline has no other heading). A decision is tied
/// to the file version it was made on and says nothing about another version.
/// </summary>
public sealed record OutlineDecision(string SourceSha256, string ConfigKey, string Location, string Kind, string? Text, int? Level,
    string? ParentLocation, string ReviewedBy, DateTimeOffset ReviewedAt);

/// <summary>
/// Reviewed decisions, kept per file version next to the stored outlines. Reviewing some headings does not approve the outline:
/// <see cref="State"/> is REVIEWED only when every heading has a matching HEADING decision and the reviewer confirmed the outline is complete.
/// A later decision on the same paragraph and kind replaces the earlier one.
/// </summary>
public sealed class OutlineApprovals(string root)
{
    public const string Heading = "HEADING", NotHeading = "NOT_HEADING", OutlineComplete = "OUTLINE_COMPLETE";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    string PathOf(string sha, string key) => Path.Combine(root, "approvals", sha[..Math.Min(16, sha.Length)] + "-" + key + ".json");

    /// <summary>The decisions made on this exact file version, or none.</summary>
    public IReadOnlyList<OutlineDecision> Of(string sourceSha256, string configKey)
    {
        try
        {
            var path = PathOf(sourceSha256, configKey);
            if (!File.Exists(path)) return [];
            return (JsonSerializer.Deserialize<List<OutlineDecision>>(File.ReadAllText(path)) ?? [])
                .Where(d => d.SourceSha256 == sourceSha256 && d.ConfigKey == configKey).ToList();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return []; }
    }

    /// <summary>Records decisions. A decision on the same version, paragraph and kind as an earlier one replaces it.</summary>
    public void Add(IEnumerable<OutlineDecision> decisions)
    {
        foreach (var group in decisions.GroupBy(d => (d.SourceSha256, d.ConfigKey)))
        {
            var kept = Of(group.Key.SourceSha256, group.Key.ConfigKey)
                .Where(old => !group.Any(d => d.Location == old.Location && d.Kind == old.Kind)).ToList();
            kept.AddRange(group);
            var path = PathOf(group.Key.SourceSha256, group.Key.ConfigKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(kept, Json));
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>
    /// UNREVIEWED (no decision), REVIEWED (every heading of the outline has a HEADING decision with its text, level and parent, no NOT_HEADING
    /// decision contradicts one, and OUTLINE_COMPLETE was recorded) or PARTIAL (anything in between).
    /// </summary>
    public static string State(StoredOutline outline, IReadOnlyList<OutlineDecision> decisions)
    {
        if (decisions.Count == 0) return "UNREVIEWED";
        var parents = OutlineDiff.Parents(outline.Headings);
        var everyHeading = outline.Headings.All(h => decisions.Any(d => d.Kind == Heading && d.Location == h.StableId && d.Text == h.Text
            && d.Level == h.Level && d.ParentLocation == parents.GetValueOrDefault(h.StableId ?? "")));
        var contradicted = outline.Headings.Any(h => decisions.Any(d => d.Kind == NotHeading && d.Location == h.StableId));
        return everyHeading && !contradicted && decisions.Any(d => d.Kind == OutlineComplete) ? "REVIEWED" : "PARTIAL";
    }

    /// <summary>The paragraphs whose HEADING or NOT_HEADING decision on this version matches the outline as it is (the reviewed part).</summary>
    public static IReadOnlySet<string> ApprovedLocations(StoredOutline outline, IReadOnlyList<OutlineDecision> decisions)
    {
        var parents = OutlineDiff.Parents(outline.Headings);
        var headings = outline.Headings.Where(h => h.StableId is not null).ToDictionary(h => h.StableId!, StringComparer.Ordinal);
        var approved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in decisions)
        {
            if (d.Kind == Heading && headings.TryGetValue(d.Location, out var h) && h.Text == d.Text && h.Level == d.Level
                && parents.GetValueOrDefault(d.Location) == d.ParentLocation) approved.Add(d.Location);
            if (d.Kind == NotHeading && !headings.ContainsKey(d.Location)) approved.Add(d.Location);
        }
        return approved;
    }
}
