using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.Contracts.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// One reviewed decision about the outline of one exact file (its SHA-256 under one configuration). <see cref="Kind"/>: HEADING (the paragraph at
/// <see cref="Location"/> starts a heading with this text and level under the heading at <see cref="ParentLocation"/>, null for the top),
/// NOT_HEADING (the paragraph is not a heading), or OUTLINE_COMPLETE (the reviewer confirmed the outline has no other heading). A decision is tied
/// to the file version and exact outline hash it was made on and says nothing about another version or extraction. Legacy records without an
/// outline hash remain readable but cannot be treated as approvals.
/// </summary>
public sealed record OutlineDecision(string SourceSha256, string ConfigKey, string Location, string Kind, string? Text, int? Level,
    string? ParentLocation, string ReviewedBy, DateTimeOffset ReviewedAt, string? OutlineSha256 = null,
    TextOffsetSpan? HeadingSpan = null, TextOffsetSpan? InlineBodySpan = null);

/// <summary>
/// Reviewed decisions, kept per file version next to the stored outlines. Reviewing some headings does not approve the outline:
/// <see cref="State"/> is REVIEWED only when every heading has complete source binding, a matching HEADING decision and the reviewer confirmed this exact outline.
/// A later decision on the same paragraph and kind replaces the earlier one.
/// </summary>
public sealed class OutlineApprovals(string root)
{
    public const string Heading = "HEADING", NotHeading = "NOT_HEADING", OutlineComplete = "OUTLINE_COMPLETE";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Identity of this exact outline and source/configuration. Old decisions without this identity are not transferable.</summary>
    public static string OutlineHash(StoredOutline outline)
    {
        var canonical = JsonSerializer.Serialize(new { outline.SourceSha256, outline.ConfigKey,
            Headings = outline.Headings.OrderBy(h => h.Index).Select(h => new { Heading = h, h.SourceParts, h.ValidatedSourcePartCount }).ToArray() });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    static bool SplitsSurrogate(string text, int offset) => offset > 0 && offset < text.Length && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]);
    static bool ValidSpan(string text, TextOffsetSpan? span) => span is not null && span.Start >= 0 && span.End > span.Start &&
        span.End <= text.Length && !SplitsSurrogate(text, span.Start) && !SplitsSurrogate(text, span.End);

    static bool Bound(DocxHeaderExtractor.DocumentProcessing.Output.HeadingRecord h) => h.ValidatedSourcePartCount > 0 &&
        h.SourceParts is { } parts && parts.Count == h.ValidatedSourcePartCount && parts.All(p =>
            p is not null && !string.IsNullOrWhiteSpace(p.SourceId) && p.Span is not null && p.OriginalText is not null &&
            p.Span.Start >= 0 && p.Span.End > p.Span.Start && p.Span.End <= p.OriginalText.Length &&
            !SplitsSurrogate(p.OriginalText, p.Span.Start) && !SplitsSurrogate(p.OriginalText, p.Span.End)) &&
        ValidSpan(parts[0].OriginalText, h.HeadingSpan) && (h.InlineBody is null || h.InlineBodySpan is not null) &&
        (h.InlineBodySpan is null || ValidSpan(parts[0].OriginalText, h.InlineBodySpan));

    static IReadOnlyList<OutlineDecision> Applicable(StoredOutline outline, IReadOnlyList<OutlineDecision> decisions)
    {
        var hash = OutlineHash(outline);
        return decisions.Where(d => d.SourceSha256 == outline.SourceSha256 && d.ConfigKey == outline.ConfigKey &&
            d.OutlineSha256 == hash && !string.IsNullOrWhiteSpace(d.ReviewedBy) && d.ReviewedAt != default).ToArray();
    }

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
                .Where(old => !group.Any(d => d.Location == old.Location && d.Kind == old.Kind && d.OutlineSha256 == old.OutlineSha256)).ToList();
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
    /// <summary>
    /// The decision a reviewer's verdict on one paragraph makes about this exact outline, built from the outline itself (text, level, parent and
    /// spans are never taken from the client). HEADING only for a paragraph the outline has as a bound heading with a known parent; NOT_HEADING
    /// for any paragraph of the file; OUTLINE_COMPLETE with no location. Null with the reason when the verdict cannot be recorded.
    /// </summary>
    public static (OutlineDecision? Decision, string? Refusal) Decide(StoredOutline outline, string kind, string? location, string reviewer, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(reviewer)) return (null, "REVIEWER_REQUIRED");
        var hash = OutlineHash(outline);
        if (kind == OutlineComplete) return (new(outline.SourceSha256, outline.ConfigKey, "", OutlineComplete, null, null, null, reviewer, at, hash), null);
        if (location is null || !outline.Paragraphs.Any(p => p.Location == location)) return (null, "LOCATION_NOT_IN_FILE");
        if (kind == NotHeading) return (new(outline.SourceSha256, outline.ConfigKey, location, NotHeading, null, null, null, reviewer, at, hash), null);
        if (kind != Heading) return (null, "UNKNOWN_KIND");
        var matches = outline.Headings.Where(h => h.StableId == location).ToArray();
        if (matches.Length != 1) return (null, matches.Length == 0 ? "NOT_A_HEADING_IN_THIS_OUTLINE" : "LOCATION_REPEATED");
        var h = matches[0];
        if (!Bound(h)) return (null, "HEADING_NOT_BOUND_TO_SOURCE");
        if (!OutlineDiff.Parents(outline.Headings).TryGetValue(location, out var parent)) return (null, "PARENT_UNRESOLVED");
        return (new(outline.SourceSha256, outline.ConfigKey, location, Heading, h.Text, h.Level, parent, reviewer, at, hash, h.HeadingSpan, h.InlineBodySpan), null);
    }

    public static string State(StoredOutline outline, IReadOnlyList<OutlineDecision> decisions)
    {
        decisions = Applicable(outline, decisions);
        if (decisions.Count == 0) return "UNREVIEWED";
        var parents = OutlineDiff.Parents(outline.Headings);
        var locations = outline.Paragraphs.Select(p => p.Location).ToHashSet(StringComparer.Ordinal);
        var unique = outline.Headings.All(h => h.StableId is not null && locations.Contains(h.StableId)) &&
            outline.Headings.Select(h => h.StableId).Distinct(StringComparer.Ordinal).Count() == outline.Headings.Count;
        var everyHeading = unique && outline.Headings.All(h => h.StableId is not null && Bound(h) && h.HeadingSpan is { Start: >= 0 } span && span.End > span.Start &&
            parents.TryGetValue(h.StableId, out var parent) && decisions.Any(d =>
            d.Kind == Heading && d.Location == h.StableId && d.Text == h.Text && d.Level == h.Level && d.ParentLocation == parent &&
            d.HeadingSpan == h.HeadingSpan && d.InlineBodySpan == h.InlineBodySpan));
        var contradicted = outline.Headings.Any(h => decisions.Any(d => d.Kind == NotHeading && d.Location == h.StableId));
        return everyHeading && !contradicted && decisions.Any(d => d.Kind == OutlineComplete) ? "REVIEWED" : "PARTIAL";
    }

    /// <summary>The paragraphs whose HEADING or NOT_HEADING decision on this version matches the outline as it is (the reviewed part).</summary>
    public static IReadOnlySet<string> ApprovedLocations(StoredOutline outline, IReadOnlyList<OutlineDecision> decisions)
    {
        decisions = Applicable(outline, decisions);
        var parents = OutlineDiff.Parents(outline.Headings);
        var headings = outline.Headings.Where(h => h.StableId is not null).GroupBy(h => h.StableId!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var locations = outline.Paragraphs.Select(p => p.Location).ToHashSet(StringComparer.Ordinal);
        var approved = new HashSet<string>(StringComparer.Ordinal);
        if (outline.Headings.Any(h => h.StableId is null || !locations.Contains(h.StableId)) || headings.Count != outline.Headings.Count) return approved;
        foreach (var d in decisions)
        {
            if (!locations.Contains(d.Location)) continue;
            if (d.Kind == Heading && headings.TryGetValue(d.Location, out var h) && Bound(h) && h.HeadingSpan is { Start: >= 0 } span && span.End > span.Start && h.Text == d.Text && h.Level == d.Level
                && parents.TryGetValue(d.Location, out var parent) && parent == d.ParentLocation && d.HeadingSpan == h.HeadingSpan &&
                d.InlineBodySpan == h.InlineBodySpan && !decisions.Any(x => x.Kind == NotHeading && x.Location == d.Location)) approved.Add(d.Location);
            if (d.Kind == NotHeading && !outline.Headings.Any(h => h.StableId == d.Location)) approved.Add(d.Location);
        }
        return approved;
    }
}
