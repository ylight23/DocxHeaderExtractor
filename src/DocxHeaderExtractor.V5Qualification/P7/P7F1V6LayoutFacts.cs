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
    public const string VersionV2 = "P7_F1_V6_LAYOUT_FACTS_V2";
    public const double LeftEdgeTolerancePt = 1.5;

    public const string Description = "for every line in targets and context: block (the parser's layout block: its number in reading order, the number of lines in it, this line's place in it), "
        + "segments (how many separate text segments share this line's visual line, this segment's place among them, and the horizontal gaps in points before and after it), "
        + "leftEdgeSharedBy (how many other lines of the page start at the same left edge, within 1.5 points)";

    /// <summary>V2 (D4.12j) adds two relative measurements. V1 gave a raw count of lines sharing a left edge, which the model read as "at the main
    /// margin, so a heading"; V2 says where that edge is against the edge most lines of the page share, and how the font size compares with the page.
    /// Still measurements only: no label, no text read.</summary>
    public const string DescriptionV2 = Description
        + "; leftEdge (offsetFromPageCommonPt: this line's left edge minus the left edge that the most lines of its page share, in points, 0 meaning the same edge; pageCommonEdgeLines: how many lines of the page start at that common edge; pageLines: how many lines the page has)"
        + "; fontSizeVsPageMedianPt (this line's font size minus the median font size of the lines of its page, in points)";

    public static string DescriptionOf(int version) => version == 2 ? DescriptionV2 : Description;
    public static string VersionOf(int version) => version == 2 ? VersionV2 : Version;

    static double R(double x) => Math.Round(x, 1, MidpointRounding.AwayFromZero);

    /// <summary>The facts of every line, by alias. lines and blocks are parallel, in reading order.</summary>
    public static IReadOnlyDictionary<string, JsonObject> Compute(IReadOnlyList<F1QSourceLine> lines, IReadOnlyList<string> blocks, int version = 1)
    {
        if (version is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(version));
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
        // V2: per page, the left edge shared by the most lines (ties: the leftmost), and the median font size.
        var commonEdge = new Dictionary<int, (double Edge, int Lines)>();
        var pageLines = Enumerable.Range(0, lines.Count).GroupBy(i => lines[i].Page ?? 0).ToDictionary(g => g.Key, g => g.Count());
        var medianSize = new Dictionary<int, double>();
        if (version == 2)
        {
            foreach (var g in byPage)
            {
                var best = g.Select(i => (Edge: lefts[i]!.Value, Lines: g.Count(j => Math.Abs(lefts[j]!.Value - lefts[i]!.Value) <= LeftEdgeTolerancePt)))
                    .OrderByDescending(x => x.Lines).ThenBy(x => x.Edge).First();
                commonEdge[g.Key ?? 0] = best;
            }
            foreach (var g in Enumerable.Range(0, lines.Count).GroupBy(i => lines[i].Page ?? 0))
            {
                var sizes = g.Select(i => P7F1QEvidenceTools.FontSizeOf(lines[i].Typography)).Where(x => x.HasValue).Select(x => x!.Value).Order().ToArray();
                if (sizes.Length > 0) medianSize[g.Key] = sizes.Length % 2 == 1 ? sizes[sizes.Length / 2] : (sizes[sizes.Length / 2 - 1] + sizes[sizes.Length / 2]) / 2;
            }
        }

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
            if (version == 2)
            {
                var page = lines[i].Page ?? 0;
                if (lefts[i] is { } l2 && commonEdge.TryGetValue(page, out var ce))
                    node["leftEdge"] = new JsonObject { ["offsetFromPageCommonPt"] = R(l2 - ce.Edge), ["pageCommonEdgeLines"] = ce.Lines, ["pageLines"] = pageLines[page] };
                if (P7F1QEvidenceTools.FontSizeOf(lines[i].Typography) is { } fs && medianSize.TryGetValue(page, out var med))
                    node["fontSizeVsPageMedianPt"] = R(fs - med);
            }
            result[lines[i].Alias] = node;
        }
        return result;
    }
}
