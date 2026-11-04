using DocxHeaderExtractor.DocumentProcessing.Changes;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// How a source section is matched to a target section. <see cref="Chain"/>: by the whole heading chain (parents and own name). <see cref="Order"/>:
/// by the names alone, in document order, aligned on the target's outline; the source's own levels and parents are not read.
/// </summary>
public enum GatherMatching { Chain, Order }

public static partial class GatherByHeading
{
    /// <summary>
    /// Matches the source sections to the target sections by order. The names (dates ignored, see <see cref="Key"/>) of the target's headings and of
    /// the source's, in document order, are aligned by Myers' diff; an aligned source section takes its target section. The source's own levels and
    /// parents are not read, so an outline that puts a heading at another level or under another parent still matches.
    /// A source section the alignment leaves out is held as uncertain: NEAR_NAME when a similar-named target section lies in the gap between its
    /// aligned neighbours, MOVED when a target section with the same name lies elsewhere. Otherwise it is a new heading after the target section of
    /// its nearest aligned predecessor (the level of that new heading is not read from the source).
    /// </summary>
    static void PlaceByOrder(IReadOnlyList<Section> sourceSections, IReadOnlyList<GatherUnit> units, IReadOnlyList<Section> targets,
        List<NewHeading> roots, bool addMissing, Placement[] placed)
    {
        for (var i = 0; i < sourceSections.Count; i++)
            if (sourceSections[i].Title) placed[i] = new(-1, null, "tiêu đề văn bản", false);

        var targetIdx = Enumerable.Range(0, targets.Count).Where(t => !targets[t].Title).ToList();
        var sourceIdx = Enumerable.Range(0, sourceSections.Count).Where(i => !sourceSections[i].Title).ToList();
        var targetKeys = targetIdx.Select(t => Key(targets[t].Heading)).ToList();
        var sourceKeys = sourceIdx.Select(i => Key(sourceSections[i].Heading)).ToList();
        var alignedTarget = new int[sourceIdx.Count];
        Array.Fill(alignedTarget, -1);
        foreach (var edit in Myers.Diff(targetKeys, sourceKeys, (a, b) => a == b))
            if (edit.Kind == EditKind.Equal) alignedTarget[edit.NewIndex] = targetIdx[edit.OldIndex];

        var aligned = new bool[targets.Count];
        foreach (var t in alignedTarget) if (t >= 0) aligned[t] = true;

        var lastTarget = -1;
        for (var k = 0; k < sourceIdx.Count; k++)
        {
            var i = sourceIdx[k];
            var s = sourceSections[i];
            var location = units[s.First].SourceId + " " + s.Location;
            if (s.Name.Length == 0) { placed[i] = new(-1, null, "heading không có tên", false); continue; }
            if (alignedTarget[k] >= 0) { placed[i] = new(alignedTarget[k], null, null, false); lastTarget = alignedTarget[k]; continue; }

            var nextTarget = Enumerable.Range(k + 1, sourceIdx.Count - k - 1).Select(j => alignedTarget[j]).FirstOrDefault(t => t >= 0, targets.Count);
            var gap = Enumerable.Range(lastTarget + 1, nextTarget - lastTarget - 1).Where(t => !aligned[t] && !targets[t].Title).ToList();
            var near = gap.Select(t => (Target: t, Score: Changes.DocxComparer.Similarity(s.Name, targets[t].Name)))
                .Where(x => x.Score >= NearNameThreshold).OrderByDescending(x => x.Score).Select(x => x.Target).Take(1).ToList();
            if (near.Count == 1)
            {
                placed[i] = new(-1, null, null, false, new UncertainItem("NEAR_NAME", s.Heading, location, null, near,
                    "văn bản đích không có heading \"" + s.Name + "\" đúng vị trí theo thứ tự, nhưng có heading tên gần giống \"" + targets[near[0]].Heading + "\"")
                    { Anchor = lastTarget });
                continue;
            }

            var key = Key(s.Heading);
            var elsewhere = Enumerable.Range(0, targets.Count).Where(t => !aligned[t] && !targets[t].Title && Key(targets[t].Heading) == key).ToList();
            if (elsewhere.Count > 0)
            {
                placed[i] = new(-1, null, null, false, new UncertainItem("MOVED", s.Heading, location, null, elsewhere,
                    "heading cùng tên nằm ở vị trí khác trong văn bản đích; có thể đã được chuyển") { Anchor = lastTarget });
                continue;
            }
            if (!addMissing) { placed[i] = new(-1, null, "văn bản đích không có heading \"" + s.Name + "\" theo thứ tự", false); continue; }
            // A new heading goes right before the next aligned target section (or after the last aligned one when none follows): a heading with no
            // aligned predecessor is never written at the end of the document. Two sources adding the same missing heading at the same place share it.
            int? before = nextTarget < targets.Count ? nextTarget : null;
            var node = roots.FirstOrDefault(n => n.Key == key && n.Anchor == lastTarget && n.Before == before);
            if (node is null)
            {
                node = new NewHeading(s.Heading, key, lastTarget, null, location) { Before = before };
                roots.Add(node);
            }
            placed[i] = new(-1, node, null, false);
        }
    }
}
