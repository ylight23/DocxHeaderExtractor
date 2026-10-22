using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Formats;

/// <summary>
/// Layout facts V3 of every line (ported from the qualified P7F1V6LayoutFacts, version 3): the parser layout block and the line's place in it,
/// the text segments on its visual line and the gaps around it, how many lines of the page share its left edge, where that edge lies against
/// the page's common edge, its font size against the page median, its layout, and its style against the whole document. Measurements of the
/// page only; no fact names a kind of line and none reads the text. Also the document style table.
/// </summary>
internal static class AgentLayoutFacts
{
    public const string VersionV3 = "P7_F1_V6_LAYOUT_FACTS_V3";
    public const string StylesVersion = "P7_F1_V6_DOCUMENT_STYLES_V1";
    public const int MaxStylesShown = 15, StyleExamples = 3, StyleExampleChars = 80;
    public const double LeftEdgeTolerancePt = 1.5;

    const string Description = "for every line in targets and context: block (the parser's layout block: its number in reading order, the number of lines in it, this line's place in it), "
        + "segments (how many separate text segments share this line's visual line, this segment's place among them, and the horizontal gaps in points before and after it), "
        + "leftEdgeSharedBy (how many other lines of the page start at the same left edge, within 1.5 points)";
    const string DescriptionV2 = Description
        + "; leftEdge (offsetFromPageCommonPt: this line's left edge minus the left edge that the most lines of its page share, in points, 0 meaning the same edge; pageCommonEdgeLines: how many lines of the page start at that common edge; pageLines: how many lines the page has)"
        + "; fontSizeVsPageMedianPt (this line's font size minus the median font size of the lines of its page, in points)";
    public const string DescriptionV3 = DescriptionV2
        + "; layout (bbox in PDF points, bottom-left origin, and font: size, name, bold and italic ratio)"
        + "; style (sizePt and bold of this line, rankInDocument: the place of that size and weight among the distinct styles of the whole document, 1 being the largest size, bold before regular at the same size; linesWithThisStyleInDocument)";

    /// <summary>A style is a font size (rounded to half a point) and a weight (bold when at least half the line is bold).</summary>
    public readonly record struct Style(double SizePt, bool Bold);

    static Style? StyleOf(AgentSourceLine l) =>
        Evidence.FontSizeOf(l.Typography) is { } s ? new Style(Math.Round(s * 2, MidpointRounding.AwayFromZero) / 2, (Evidence.Bold(l.Typography) ?? 0) >= 0.5) : null;

    /// <summary>The distinct styles of the document, largest size first, bold before regular at the same size; rank 1 is the first.</summary>
    public static IReadOnlyList<(Style Style, int Rank, int[] Lines)> Styles(IReadOnlyList<AgentSourceLine> lines) =>
        Enumerable.Range(0, lines.Count).Select(i => (i, s: StyleOf(lines[i]))).Where(x => x.s.HasValue).GroupBy(x => x.s!.Value)
            .OrderByDescending(g => g.Key.SizePt).ThenByDescending(g => g.Key.Bold)
            .Select((g, k) => (g.Key, k + 1, g.Select(x => x.i).ToArray())).ToList();

    /// <summary>The document style table: every distinct style of the whole document, largest first, with how many lines and pages use it and the
    /// first lines set in it.</summary>
    public static JsonObject DocumentStyles(IReadOnlyList<AgentSourceLine> lines)
    {
        var styles = Styles(lines);
        static string Clip(string t) => t.Length <= StyleExampleChars ? t : t[..StyleExampleChars] + "…";
        return new JsonObject
        {
            ["readBy"] = "the program, from the parser's typography of every line of the whole document: each distinct style (font size rounded to half a point, bold when at least half the line is bold), largest size first, bold before regular at the same size, with how many lines and pages use it and the first lines in reading order set in it",
            ["version"] = StylesVersion,
            ["documentLines"] = lines.Count,
            ["stylesTotal"] = styles.Count,
            ["styles"] = new JsonArray(styles.Take(MaxStylesShown).Select(s => (JsonNode)new JsonObject
            {
                ["rank"] = s.Rank, ["sizePt"] = s.Style.SizePt, ["bold"] = s.Style.Bold, ["lines"] = s.Lines.Length,
                ["pages"] = s.Lines.Select(i => lines[i].Page).Distinct().Count(),
                ["firstLines"] = new JsonArray(s.Lines.Take(StyleExamples).Select(i => (JsonNode)new JsonObject { ["alias"] = lines[i].Alias, ["page"] = lines[i].Page, ["text"] = Clip(lines[i].Text) }).ToArray()),
            }).ToArray()),
            ["stylesNotShown"] = Math.Max(0, styles.Count - MaxStylesShown),
        };
    }

    static double R(double x) => Math.Round(x, 1, MidpointRounding.AwayFromZero);

    /// <summary>The V3 facts of every line, by alias. lines and blocks are parallel, in reading order.</summary>
    public static IReadOnlyDictionary<string, JsonObject> Compute(IReadOnlyList<AgentSourceLine> lines, IReadOnlyList<string> blocks)
    {
        if (lines.Count != blocks.Count) throw new ArgumentException("LAYOUT_FACTS_LENGTH", nameof(blocks));
        var ordinal = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in blocks) ordinal.TryAdd(b, ordinal.Count + 1);
        var blockSize = blocks.GroupBy(b => b, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var placed = new Dictionary<string, int>(StringComparer.Ordinal);

        static string VisualLine(string alias) { var k = alias.IndexOf(':'); return k < 0 ? alias : alias[..k]; }
        var lefts = lines.Select(l => Evidence.LeftEdgeOf(l.Bbox)).ToArray();
        var rights = lines.Select(l => Evidence.RightEdgeOf(l.Bbox)).ToArray();
        var segments = Enumerable.Range(0, lines.Count).GroupBy(i => VisualLine(lines[i].Alias), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => lefts[i] ?? double.MaxValue).ThenBy(i => i).ToArray(), StringComparer.Ordinal);
        var byPage = Enumerable.Range(0, lines.Count).Where(i => lefts[i].HasValue).ToLookup(i => lines[i].Page);
        // Per page, the left edge shared by the most lines (ties: the leftmost), and the median font size.
        var commonEdge = new Dictionary<int, (double Edge, int Lines)>();
        var pageLines = Enumerable.Range(0, lines.Count).GroupBy(i => lines[i].Page ?? 0).ToDictionary(g => g.Key, g => g.Count());
        var medianSize = new Dictionary<int, double>();
        var styleRank = new Dictionary<int, (Style Style, int Rank, int Lines)>();
        foreach (var s in Styles(lines))
            foreach (var i in s.Lines) styleRank[i] = (s.Style, s.Rank, s.Lines.Length);
        foreach (var g in byPage)
        {
            var best = g.Select(i => (Edge: lefts[i]!.Value, Lines: g.Count(j => Math.Abs(lefts[j]!.Value - lefts[i]!.Value) <= LeftEdgeTolerancePt)))
                .OrderByDescending(x => x.Lines).ThenBy(x => x.Edge).First();
            commonEdge[g.Key ?? 0] = best;
        }
        foreach (var g in Enumerable.Range(0, lines.Count).GroupBy(i => lines[i].Page ?? 0))
        {
            var sizes = g.Select(i => Evidence.FontSizeOf(lines[i].Typography)).Where(x => x.HasValue).Select(x => x!.Value).Order().ToArray();
            if (sizes.Length > 0) medianSize[g.Key] = sizes.Length % 2 == 1 ? sizes[sizes.Length / 2] : (sizes[sizes.Length / 2 - 1] + sizes[sizes.Length / 2]) / 2;
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
            if (Evidence.CompactLayout(lines[i].Bbox, lines[i].Typography) is { } lay) node["layout"] = System.Text.Json.JsonSerializer.SerializeToNode(lay);
            if (styleRank.TryGetValue(i, out var sr))
                node["style"] = new JsonObject { ["sizePt"] = sr.Style.SizePt, ["bold"] = sr.Style.Bold, ["rankInDocument"] = sr.Rank, ["linesWithThisStyleInDocument"] = sr.Lines };
            var page = lines[i].Page ?? 0;
            if (lefts[i] is { } l2 && commonEdge.TryGetValue(page, out var ce))
                node["leftEdge"] = new JsonObject { ["offsetFromPageCommonPt"] = R(l2 - ce.Edge), ["pageCommonEdgeLines"] = ce.Lines, ["pageLines"] = pageLines[page] };
            if (Evidence.FontSizeOf(lines[i].Typography) is { } fs && medianSize.TryGetValue(page, out var med))
                node["fontSizeVsPageMedianPt"] = R(fs - med);
            result[lines[i].Alias] = node;
        }
        return result;
    }
}
