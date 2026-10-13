using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Geometry facts the program computes for every line of a document (D4.12): which parser layout block a line is in and where in it, how many
/// separate text segments share its visual line and the horizontal gaps around it, and how many other lines of the page start at the same left
/// edge. They are measurements of the page. No fact names a kind of line (a table, a heading, a running header) and none reads the text.
/// </summary>
internal static class P7F1V6LayoutFacts
{
    public const string Version = "P7_F1_V6_LAYOUT_FACTS_V1";
    public const double LeftEdgeTolerancePt = 1.5;

    public const string Description = "for every line in targets and context: block (the parser's layout block: its number in reading order, the number of lines in it, this line's place in it), "
        + "segments (how many separate text segments share this line's visual line, this segment's place among them, and the horizontal gaps in points before and after it), "
        + "leftEdgeSharedBy (how many other lines of the page start at the same left edge, within 1.5 points)";

    static double R(double x) => Math.Round(x, 1, MidpointRounding.AwayFromZero);

    /// <summary>The facts of every line, by alias. lines and blocks are parallel, in reading order.</summary>
    public static IReadOnlyDictionary<string, JsonObject> Compute(IReadOnlyList<F1QSourceLine> lines, IReadOnlyList<string> blocks)
    {
        if (lines.Count != blocks.Count) throw new ArgumentException("LAYOUT_FACTS_LENGTH", nameof(blocks));
        var ordinal = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in blocks) ordinal.TryAdd(b, ordinal.Count + 1);
        var blockSize = blocks.GroupBy(b => b, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var placed = new Dictionary<string, int>(StringComparer.Ordinal);

        static string VisualLine(string alias) { var k = alias.IndexOf(':'); return k < 0 ? alias : alias[..k]; }
        var lefts = lines.Select(l => P7F1QEvidenceTools.LeftEdgeOf(l.Bbox)).ToArray();
        var rights = lines.Select(l => P7F1QEvidenceTools.RightEdgeOf(l.Bbox)).ToArray();
        var segments = Enumerable.Range(0, lines.Count).GroupBy(i => VisualLine(lines[i].Alias), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => lefts[i] ?? double.MaxValue).ThenBy(i => i).ToArray(), StringComparer.Ordinal);
        var byPage = Enumerable.Range(0, lines.Count).Where(i => lefts[i].HasValue).ToLookup(i => lines[i].Page);

        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Count; i++)
        {
            var b = blocks[i];
            placed[b] = placed.GetValueOrDefault(b) + 1;
            var seg = segments[VisualLine(lines[i].Alias)];
            var at = Array.IndexOf(seg, i);
            var node = new JsonObject
            {
                ["alias"] = lines[i].Alias,
                ["block"] = new JsonObject { ["number"] = ordinal[b], ["lines"] = blockSize[b], ["place"] = placed[b] },
            };
            var s = new JsonObject { ["count"] = seg.Length, ["index"] = at + 1 };
            if (seg.Length > 1)
            {
                if (at > 0 && lefts[i] is { } l && rights[seg[at - 1]] is { } pr) s["gapBeforePt"] = R(l - pr);
                if (at + 1 < seg.Length && rights[i] is { } r && lefts[seg[at + 1]] is { } nl) s["gapAfterPt"] = R(nl - r);
            }
            node["segments"] = s;
            if (lefts[i] is { } left)
                node["leftEdgeSharedBy"] = byPage[lines[i].Page].Count(j => j != i && Math.Abs(lefts[j]!.Value - left) <= LeftEdgeTolerancePt);
            result[lines[i].Alias] = node;
        }
        return result;
    }
}
