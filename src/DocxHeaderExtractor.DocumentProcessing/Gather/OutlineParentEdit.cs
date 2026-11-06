using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>Changes the parent of one existing source-bound heading and its subtree without changing source order or text.</summary>
public static class OutlineParentEdit
{
    public static (IReadOnlyList<HeadingRecord>? Headings, string? Error) Apply(StoredOutline outline, string location, string? parentLocation)
    {
        var ordered = outline.Headings.OrderBy(h => h.Index).ToArray();
        var seenTreeHeading = false;
        foreach (var h in ordered)
        {
            if (h.StableId is null) return (null, "OUTLINE_UNRESOLVED");
            if (h.Level is null && h.HierarchyResolution == "model-out-of-hierarchy" && !seenTreeHeading) continue;
            if (h.Level is not (>= 1 and <= 9)) return (null, "OUTLINE_UNRESOLVED");
            seenTreeHeading = true;
        }
        if (ordered.Select(h => h.StableId).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            return (null, "OUTLINE_UNRESOLVED");
        var at = Array.FindIndex(ordered, h => h.StableId == location);
        if (at < 0) return (null, "HEADING_NOT_FOUND");
        if (ordered[at].Level is null) return (null, "HEADING_OUTSIDE_TREE");
        var oldLevel = ordered[at].Level!.Value;
        var end = at + 1;
        while (end < ordered.Length && ordered[end].Level > oldLevel) end++;
        var parent = parentLocation is null ? -1 : Array.FindIndex(ordered, h => h.StableId == parentLocation);
        if (parentLocation is not null && (parent < 0 || parent >= at || ordered[parent].Level is null)) return (null, "PARENT_NOT_EARLIER");
        var newLevel = parent < 0 ? 1 : ordered[parent].Level!.Value + 1;
        var shift = newLevel - oldLevel;
        if (shift == 0) return (null, "NO_CHANGE");
        if (ordered.Skip(at).Take(end - at).Any(h => h.Level + shift is < 1 or > 9)) return (null, "LEVEL_OUT_OF_RANGE");
        var changed = ordered.Select((h, i) => Copy(h, i >= at && i < end ? h.Level!.Value + shift : h.Level,
            i >= at && i < end)).ToArray();
        var parents = OutlineDiff.Parents(changed);
        if (!parents.TryGetValue(location, out var actual) || actual != parentLocation || parents.Count != changed.Count(h => h.Level is not null))
            return (null, "PARENT_NOT_REPRESENTABLE_IN_SOURCE_ORDER");
        // Only the chosen heading may change its parent: a heading after the moved subtree that would be adopted by it (or by any other heading)
        // is a change the reviewer did not choose.
        var before = OutlineDiff.Parents(ordered);
        var moved = ordered.Skip(at + 1).Take(end - at - 1).Select(h => h.StableId!).ToHashSet(StringComparer.Ordinal);
        if (before.Any(kv => kv.Key != location && !moved.Contains(kv.Key) && parents.GetValueOrDefault(kv.Key) != kv.Value))
            return (null, "OTHER_PARENTS_WOULD_CHANGE");
        return (changed, null);
    }

    static HeadingRecord Copy(HeadingRecord h, int? level, bool changed) => new()
    {
        StructuralElementId = h.StructuralElementId, SourceParts = h.SourceParts, ValidatedSourcePartCount = h.ValidatedSourcePartCount,
        ScanSources = h.ScanSources, Index = h.Index, StableId = h.StableId, SourceId = h.SourceId, Level = level, Text = h.Text,
        OriginalText = h.OriginalText, HeadingSpan = h.HeadingSpan, InlineBody = h.InlineBody, InlineBodySpan = h.InlineBodySpan,
        BoundarySource = h.BoundarySource, HierarchyResolution = changed ? "human-parent-relation" : h.HierarchyResolution,
        StyleId = h.StyleId, Source = changed ? HeadingSource.HumanCorrection : h.Source,
        DecisionStatus = HeadingDecisionStatus.RequiresReview, ConfidenceBasis = h.ConfidenceBasis, Disputed = h.Disputed,
    };
}
