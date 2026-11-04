using DocxHeaderExtractor.DocumentProcessing.Changes;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// One difference between an old and a new outline, to be reviewed. <see cref="Kind"/>:
/// ADDED (a heading only the new outline has, on unchanged text), REMOVED (a heading only the old one has, on unchanged text),
/// TEXT (same start paragraph, other heading text), SPLIT (the old heading's text runs into a new heading), MERGED (an old heading is now part of
/// the text of another one), LEVEL, PARENT, ON_CHANGED_TEXT (a new heading on a paragraph that changed) or SOURCE_CHANGED (an old heading whose
/// paragraph changed or was removed). <see cref="OldApproved"/>: the old decision was reviewed. <see cref="ContextChanged"/>: a changed paragraph
/// lies within <see cref="OutlineDiff.Context"/> paragraphs. <see cref="Uncertain"/>: the paragraphs could not be paired for sure (repeated text).
/// Nothing here decides which side is right.
/// </summary>
public sealed record OutlineDifference(string Kind, string? OldLocation, string? NewLocation, string? OldText, string? NewText,
    int? OldLevel, int? NewLevel, string? OldParent, string? NewParent, bool OldApproved, bool ContextChanged, bool Uncertain);

/// <summary>The differences, how many headings are the same on both sides, and how many paragraphs changed between the two files.</summary>
public sealed record OutlineComparison(IReadOnlyList<OutlineDifference> Differences, int SameHeadings, int ChangedParagraphs);

/// <summary>
/// Compares two outlines without a model: of the same file (two runs) or of two versions of a file. The paragraphs of both are paired by Myers on
/// their signatures; a heading is identified by the paragraph it starts on, through that pairing. Every difference is listed for review with the
/// evidence that matters (approved or not, text changed nearby, pairing certain or not); none is resolved here.
/// </summary>
public static class OutlineDiff
{
    /// <summary>Paragraphs on each side of a difference within which a changed paragraph marks its context as changed.</summary>
    public const int Context = 20;

    /// <summary>The parent of each heading (by start location): the nearest earlier heading of a higher level; a heading without a level keeps the level before it.</summary>
    public static IReadOnlyDictionary<string, string?> Parents(IReadOnlyList<HeadingRecord> headings)
    {
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        var open = new List<(string Location, int Level)>();
        var last = 1;
        foreach (var h in headings.Where(h => h.StableId is not null).OrderBy(h => h.Index))
        {
            var level = h.Level ?? last;
            last = level;
            while (open.Count > 0 && open[^1].Level >= level) open.RemoveAt(open.Count - 1);
            parents[h.StableId!] = open.Count > 0 ? open[^1].Location : null;
            open.Add((h.StableId!, level));
        }
        return parents;
    }

    static string Compact(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public static OutlineComparison Compare(StoredOutline old, StoredOutline next, IReadOnlySet<string>? approvedOld = null)
    {
        approvedOld ??= new HashSet<string>();
        var edits = Myers.Diff(old.Paragraphs, next.Paragraphs, (a, b) => a.Signature == b.Signature);
        var oldToNew = new Dictionary<int, int>();
        var newToOld = new Dictionary<int, int>();
        var marks = new List<int>();   // new positions of changed or removed paragraphs
        var pos = 0;
        foreach (var e in edits)
        {
            if (e.Kind == EditKind.Equal) { oldToNew[e.OldIndex] = e.NewIndex; newToOld[e.NewIndex] = e.OldIndex; pos = e.NewIndex + 1; }
            else if (e.Kind == EditKind.Insert) { marks.Add(e.NewIndex); pos = e.NewIndex + 1; }
            else marks.Add(pos);
        }
        bool ChangedNear(int newIndex) => marks.Any(m => Math.Abs(m - newIndex) <= Context);

        var oldIndex = old.Paragraphs.Select((p, i) => (p.Location, i)).ToDictionary(x => x.Location, x => x.i, StringComparer.Ordinal);
        var newIndex = next.Paragraphs.Select((p, i) => (p.Location, i)).ToDictionary(x => x.Location, x => x.i, StringComparer.Ordinal);
        var oldRepeated = old.Paragraphs.GroupBy(p => p.Signature).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var newRepeated = next.Paragraphs.GroupBy(p => p.Signature).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        bool Repeated(int? o, int? n) => (o is { } a && oldRepeated.Contains(old.Paragraphs[a].Signature))
            || (n is { } b && newRepeated.Contains(next.Paragraphs[b].Signature));

        var oldHeadings = old.Headings.Where(h => h.StableId is not null && oldIndex.ContainsKey(h.StableId)).ToDictionary(h => h.StableId!, StringComparer.Ordinal);
        var newHeadings = next.Headings.Where(h => h.StableId is not null && newIndex.ContainsKey(h.StableId)).ToDictionary(h => h.StableId!, StringComparer.Ordinal);
        var oldParents = Parents(old.Headings);
        var newParents = Parents(next.Headings);
        string? NewLocationOf(string? oldLocation) =>
            oldLocation is not null && oldIndex.TryGetValue(oldLocation, out var o) && oldToNew.TryGetValue(o, out var n) ? next.Paragraphs[n].Location : null;

        var differences = new List<OutlineDifference>();
        var same = 0;
        var handledNew = new HashSet<string>(StringComparer.Ordinal);
        OutlineDifference Item(string kind, HeadingRecord? o, HeadingRecord? n, int? oi, int? ni) => new(kind,
            o?.StableId, n?.StableId ?? (ni is { } x ? next.Paragraphs[x].Location : null), o?.Text, n?.Text, o?.Level, n?.Level,
            o?.StableId is { } ol ? oldParents.GetValueOrDefault(ol) : null, n?.StableId is { } nl ? newParents.GetValueOrDefault(nl) : null,
            o?.StableId is { } al && approvedOld.Contains(al),
            ni is { } c ? ChangedNear(c) : oi is { } d && oldToNew.TryGetValue(d, out var m) ? ChangedNear(m) : true,
            Repeated(oi, ni));

        foreach (var (location, o) in oldHeadings)
        {
            var oi = oldIndex[location];
            if (!oldToNew.TryGetValue(oi, out var ni)) { differences.Add(Item("SOURCE_CHANGED", o, null, oi, null)); continue; }
            var newLocation = next.Paragraphs[ni].Location;
            if (newHeadings.TryGetValue(newLocation, out var n))
            {
                handledNew.Add(newLocation);
                var a = Compact(o.Text); var b = Compact(n.Text);
                var kinds = new List<string>();
                if (a != b) kinds.Add(a.StartsWith(b, StringComparison.Ordinal) ? "SPLIT" : b.StartsWith(a, StringComparison.Ordinal) ? "MERGED" : "TEXT");
                if (o.Level != n.Level) kinds.Add("LEVEL");
                if (NewLocationOf(oldParents.GetValueOrDefault(location)) != newParents.GetValueOrDefault(newLocation)) kinds.Add("PARENT");
                if (kinds.Count == 0) same++;
                foreach (var k in kinds) differences.Add(Item(k, o, n, oi, ni));
                continue;
            }
            // Not a heading start in the new outline: part of a heading that starts earlier (merged into it), or no longer a heading.
            var into = newHeadings.Values.FirstOrDefault(h => newIndex[h.StableId!] < ni && newIndex[h.StableId!] >= ni - 2
                && Compact(h.Text).Contains(Compact(o.Text), StringComparison.Ordinal));
            differences.Add(into is not null ? Item("MERGED", o, into, oi, newIndex[into.StableId!]) : Item("REMOVED", o, null, oi, ni));
            if (into is not null) handledNew.Add(into.StableId!);
        }
        foreach (var (location, n) in newHeadings)
        {
            if (handledNew.Contains(location)) continue;
            var ni = newIndex[location];
            if (!newToOld.TryGetValue(ni, out var oi)) { differences.Add(Item("ON_CHANGED_TEXT", null, n, null, ni)); continue; }
            // A new start inside the text of an old heading that starts just before: the old heading was split.
            var from = oldHeadings.Values.FirstOrDefault(h => oldIndex[h.StableId!] < oi && oldIndex[h.StableId!] >= oi - 2
                && Compact(h.Text).Contains(Compact(n.Text), StringComparison.Ordinal));
            differences.Add(from is not null ? Item("SPLIT", from, n, oldIndex[from.StableId!], ni) : Item("ADDED", null, n, oi, ni));
        }
        var changed = Math.Max(next.Paragraphs.Count - newToOld.Count, old.Paragraphs.Count - oldToNew.Count);
        return new OutlineComparison(differences.OrderBy(d => d.NewLocation is { } l && newIndex.TryGetValue(l, out var i) ? i : int.MaxValue).ToList(), same, changed);
    }
}
