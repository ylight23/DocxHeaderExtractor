using DocxHeaderExtractor.DocumentProcessing.Changes;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// The stored outline carried onto a new version of the file. <see cref="Status"/>:
/// EXACT: every paragraph is the same (text and formatting), the headings are reused as they are.
/// MAPPED: every heading paragraph is unchanged and in the same order; other paragraphs were added or removed. The headings are moved to their
/// new places, and <see cref="AddedParagraphs"/> lists the new paragraphs: nobody has decided whether any of them is a heading.
/// RUN: the outline cannot be carried over (<see cref="Reason"/>) and the file must be extracted again.
/// </summary>
public sealed record OutlineMapResult(string Status, string? Reason, IReadOnlyList<HeadingRecord> Headings,
    IReadOnlyList<string> AddedParagraphs, IReadOnlyList<string> RemovedParagraphs);

/// <summary>
/// Maps a stored outline onto a new version of the same file without a model. The paragraphs of both versions are aligned by Myers on their
/// signatures (<see cref="OutlineStore.Signature"/>): two paragraphs are the same only when text and formatting both are. A heading is carried
/// over only when its own paragraph is aligned and its signature is unique in both versions, so a repeated line cannot be matched to the wrong
/// copy. A heading paragraph edited, removed or moved means the file is extracted again: the code never decides that a changed region holds no
/// heading. So does a tracked change in either version (the paragraphs read are only the accepted view), or a different set of regions the reader
/// does not read in full (a text box or table kind that appeared or went away). Regions present in both versions (headers, footers, merged cells)
/// are outside the body paragraphs compared here and outside the outline.
/// </summary>
public static class OutlineMap
{
    static readonly string[] Unmappable = ["tracked_changes", "tracked_formatting"];

    public static OutlineMapResult Map(StoredOutline stored, IReadOnlyList<StoredParagraph> current, IReadOnlyList<string> currentUnsupportedRegions)
    {
        static OutlineMapResult Run(string reason) => new("RUN", reason, [], [], []);
        if (stored.UnsupportedRegions.Concat(currentUnsupportedRegions).FirstOrDefault(Unmappable.Contains) is { } tracked) return Run("UNSUPPORTED_REGION:" + tracked);
        if (!stored.UnsupportedRegions.Order(StringComparer.Ordinal).SequenceEqual(currentUnsupportedRegions.Order(StringComparer.Ordinal)))
            return Run("UNSUPPORTED_REGIONS_CHANGED");

        var edits = Myers.Diff(stored.Paragraphs, current, (a, b) => a.Signature == b.Signature);
        var oldToNew = edits.Where(e => e.Kind == EditKind.Equal).ToDictionary(e => e.OldIndex, e => e.NewIndex);
        var added = edits.Where(e => e.Kind == EditKind.Insert).Select(e => current[e.NewIndex].Location).ToArray();
        var removed = edits.Where(e => e.Kind == EditKind.Delete).Select(e => stored.Paragraphs[e.OldIndex].Location).ToArray();

        var oldIndexByLocation = stored.Paragraphs.Select((p, i) => (p.Location, i)).ToDictionary(x => x.Location, x => x.i, StringComparer.Ordinal);
        var oldCounts = stored.Paragraphs.GroupBy(p => p.Signature).ToDictionary(g => g.Key, g => g.Count());
        var newCounts = current.GroupBy(p => p.Signature).ToDictionary(g => g.Key, g => g.Count());
        var headings = new List<HeadingRecord>();
        foreach (var h in stored.Headings)
        {
            if (h.StableId is null || !oldIndexByLocation.TryGetValue(h.StableId, out var oldIndex)) return Run("HEADING_WITHOUT_LOCATION:" + h.Index);
            var signature = stored.Paragraphs[oldIndex].Signature;
            if (oldCounts[signature] > 1 || newCounts.GetValueOrDefault(signature) > 1) return Run("HEADING_TEXT_REPEATED:" + h.StableId);
            if (!oldToNew.TryGetValue(oldIndex, out var newIndex)) return Run("HEADING_CHANGED_OR_MOVED:" + h.StableId);
            var now = current[newIndex];
            headings.Add(Moved(h, h.Index + now.Ordinal - stored.Paragraphs[oldIndex].Ordinal, now.Location));
        }
        return added.Length == 0 && removed.Length == 0
            ? new("EXACT", null, headings, [], [])
            : new("MAPPED", null, headings, added, removed);
    }

    static HeadingRecord Moved(HeadingRecord h, int index, string location) => new()
    {
        Index = index, StableId = location, Level = h.Level, Text = h.Text, OriginalText = h.OriginalText, HeadingSpan = h.HeadingSpan,
        InlineBody = h.InlineBody, InlineBodySpan = h.InlineBodySpan, BoundarySource = h.BoundarySource, HierarchyResolution = h.HierarchyResolution,
        StyleId = h.StyleId, Source = h.Source, DecisionStatus = h.DecisionStatus, ConfidenceBasis = h.ConfidenceBasis, Disputed = h.Disputed,
    };
}
