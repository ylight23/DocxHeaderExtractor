using DocxHeaderExtractor.DocumentProcessing.Changes;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>A span of paragraphs of the new version, [Start, End).</summary>
public sealed record RegionSpan(int Start, int End);

/// <summary>
/// What must be decided again for a new version of a file, and what is carried over unchanged. <see cref="Status"/>: UNCHANGED (nothing to do),
/// REGION (only <see cref="Spans"/> are decided again; every other heading is carried over), or FULL (extract the whole file; <see cref="Reason"/>).
/// <see cref="HeadingsToDecide"/> are the stored headings whose own paragraph changed or was removed: they are decided again inside a span.
/// </summary>
public sealed record RegionPlan(string Status, string? Reason, IReadOnlyList<RegionSpan> Spans, IReadOnlyList<HeadingRecord> CarriedHeadings,
    IReadOnlyList<HeadingRecord> HeadingsToDecide, int ChangedParagraphs, int TotalParagraphs);

/// <summary>
/// Finds the part of a new version that must be decided again, from the stored outline of the old version (no model). The paragraphs are aligned
/// on their signatures (Myers, as in <see cref="OutlineMap"/>). Every changed or removed place gets its neighbours (<see cref="Context"/> paragraphs
/// each side) and nearby spans are merged. The headings outside the spans are carried over. A tracked change, or a change of more than
/// <see cref="MaxChangedShare"/> of the paragraphs, means the whole file is extracted again.
/// </summary>
public static class OutlineRegion
{
    /// <summary>Paragraphs shown on each side of a changed place, so its decision sees the text around it.</summary>
    public const int Context = 3;
    /// <summary>A version that changed more than this share of its paragraphs is extracted again whole.</summary>
    public const double MaxChangedShare = 0.3;

    static readonly string[] Unreadable = ["tracked_changes", "tracked_formatting"];

    public static RegionPlan Plan(StoredOutline stored, IReadOnlyList<StoredParagraph> current, IReadOnlyList<string> currentUnsupported)
    {
        if (stored.UnsupportedRegions.Concat(currentUnsupported).FirstOrDefault(Unreadable.Contains) is { } tracked)
            return Full("UNSUPPORTED_REGION:" + tracked, stored, current.Count);
        if (current.Count == 0) return Full("EMPTY_VERSION", stored, 0);

        if (stored.SignatureVersion != OutlineStore.SignatureVersion) return Full("SIGNATURE_VERSION_CHANGED", stored, current.Count);
        var edits = Myers.Diff(stored.Paragraphs, current, StoredParagraph.Same, Myers.Limit, out var complete);
        if (!complete) return Full("DIFF_LIMIT_EXCEEDED", stored, current.Count);
        var newAligned = new bool[current.Count];
        var oldToNew = new Dictionary<int, int>();
        var places = new SortedSet<int>();   // new positions where a change is: a changed paragraph, or where one was removed
        var pos = 0;
        foreach (var e in edits)
        {
            switch (e.Kind)
            {
                case EditKind.Equal: newAligned[e.NewIndex] = true; oldToNew[e.OldIndex] = e.NewIndex; pos = e.NewIndex + 1; break;
                case EditKind.Insert: places.Add(e.NewIndex); pos = e.NewIndex + 1; break;
                case EditKind.Delete: places.Add(Math.Min(pos, current.Count - 1)); break;
            }
        }
        var changedNew = current.Count - newAligned.Count(a => a);
        var removedOld = stored.Paragraphs.Count - oldToNew.Count;
        // A replaced paragraph is one change (removed and added at once): the larger of the two counts, not their sum.
        var changed = Math.Max(changedNew, removedOld);
        var total = Math.Max(current.Count, stored.Paragraphs.Count);
        if (changed > 0 && (double)changed / total > MaxChangedShare)
            return Full($"CHANGED_SHARE:{(double)changed / total:0.00}", stored, current.Count);

        var oldIndexByLocation = stored.Paragraphs.Select((p, i) => (p.Location, i)).ToDictionary(x => x.Location, x => x.i, StringComparer.Ordinal);
        var carried = new List<HeadingRecord>();
        var toDecide = new List<HeadingRecord>();
        foreach (var h in stored.Headings.OrderBy(h => h.Index))
        {
            if (h.StableId is null || !oldIndexByLocation.TryGetValue(h.StableId, out var oldIndex))
                return Full("HEADING_WITHOUT_LOCATION:" + h.Index, stored, current.Count);
            if (oldToNew.TryGetValue(oldIndex, out var newIndex))
                carried.Add(OutlineMap.Moved(h, h.Index + current[newIndex].Ordinal - stored.Paragraphs[oldIndex].Ordinal, current[newIndex].Location));
            else
                toDecide.Add(h);
        }
        if (changed == 0)
            return new RegionPlan("UNCHANGED", null, [], carried, [], 0, total);

        foreach (var i in Enumerable.Range(0, current.Count).Where(i => !newAligned[i])) places.Add(i);
        var spans = new List<RegionSpan>();
        foreach (var p in places)
        {
            var span = new RegionSpan(Math.Max(0, p - Context), Math.Min(current.Count, p + Context + 1));
            if (spans.Count > 0 && span.Start <= spans[^1].End) spans[^1] = new RegionSpan(spans[^1].Start, Math.Max(spans[^1].End, span.End));
            else spans.Add(span);
        }
        return new RegionPlan("REGION", null, spans, carried, toDecide, changed, total);
    }

    /// <summary>The whole file is extracted again: nothing is carried over.</summary>
    static RegionPlan Full(string reason, StoredOutline stored, int currentCount) =>
        new("FULL", reason, [], [], stored.Headings, 0, Math.Max(currentCount, stored.Paragraphs.Count));
}
