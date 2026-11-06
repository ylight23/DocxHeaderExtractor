using DocxHeaderExtractor.DocumentProcessing.Changes;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// How a source section is matched to a target section. <see cref="Chain"/>: by the whole heading chain (parents and own name). <see cref="Order"/>:
/// by the names alone, in document order, aligned on the target's outline; the source's own levels and parents are not read.
/// </summary>
public enum GatherMatching { Chain, Order }

public static partial class GatherByHeading
{
    /// <summary>The nearest target section before <paramref name="i"/> with a higher level (a smaller number): the section that contains it, or -1.</summary>
    static int EnclosingSection(IReadOnlyList<Section> targets, int i)
    {
        for (var j = i - 1; j >= 0; j--)
            if (targets[j].Level < targets[i].Level) return j;
        return -1;
    }

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
        var orderEdits = Myers.Diff(targetKeys, sourceKeys, (a, b) => a == b, Myers.Limit, out var orderComplete);
        foreach (var edit in orderEdits)
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
            // The order alignment stopped at its limit: an unaligned heading has no established place, so its content is listed, never guessed.
            if (!orderComplete) { placed[i] = new(-1, null, "so thứ tự heading vượt giới hạn; chưa xác định vị trí", false); continue; }

            var nextTarget = Enumerable.Range(k + 1, sourceIdx.Count - k - 1).Select(j => alignedTarget[j]).FirstOrDefault(t => t >= 0, targets.Count);
            var gap = Enumerable.Range(lastTarget + 1, nextTarget - lastTarget - 1).Where(t => !aligned[t] && !targets[t].Title).ToList();
            var scored = gap.Select(t => (Target: t, Score: Changes.DocxComparer.Similarity(s.Name, targets[t].Name, out var done), Done: done)).ToList();
            // A name comparison that stopped at the diff limit decides nothing: the heading is not placed and not added.
            if (scored.Any(x => !x.Done)) { placed[i] = new(-1, null, "so tên heading vượt giới hạn; chưa xác định vị trí", false); continue; }
            var near = scored
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
            // The place, level and parent come from the target outline. Before a deeper target heading: inside the last aligned section, at that
            // heading's level. Otherwise after the last aligned section's subtree, at its level, inside the section that contains it. With no aligned
            // predecessor: before the next aligned target heading, at its level. Two sources adding the same heading at the same place share it.
            int? boundary;
            int level, parentTarget;
            if (lastTarget < 0)
            {
                // Before the next aligned heading, at its level, inside the section that contains it.
                boundary = nextTarget < targets.Count ? nextTarget : null;
                level = boundary is { } next ? targets[next].Level : 1;
                parentTarget = boundary is { } p ? EnclosingSection(targets, p) : -1;
            }
            else if (nextTarget < targets.Count && targets[nextTarget].Level > targets[lastTarget].Level)
            { boundary = nextTarget; level = targets[nextTarget].Level; parentTarget = lastTarget; }
            else
            {
                var end = SubtreeEnd(targets, lastTarget);
                boundary = end < 0 ? null : end;
                level = targets[lastTarget].Level;
                parentTarget = EnclosingSection(targets, lastTarget);
            }
            var node = roots.FirstOrDefault(n => n.Key == key && n.Before == boundary && n.Level == level && n.ParentTarget == parentTarget);
            if (node is null)
            {
                node = new NewHeading(s.Heading, key, lastTarget, null, location) { Before = boundary, Level = level, ParentTarget = parentTarget };
                roots.Add(node);
            }
            placed[i] = new(-1, node, null, false);
        }
    }
}
