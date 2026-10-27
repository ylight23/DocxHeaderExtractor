using DocxHeaderExtractor.HeadingAgent.Formats;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.HeadingAgent.Runtime;

/// <summary>
/// Parser-derived selection units. A unit is a contiguous geometry region (paragraph flow or a
/// repeated-column table region); aliases remain the only source identities that may be selected,
/// grouped, placed, or bound downstream.  The kind is evidence about how the parser drew the
/// page, never a heading label.
/// </summary>
internal sealed record PdfSelectionUnit(string UnitId, string Kind, IReadOnlyList<int> Members, JsonObject Geometry)
{
    public string FirstAlias(AgentSource source) => source.Lines[Members[0]].Alias;
    public string LastAlias(AgentSource source) => source.Lines[Members[^1]].Alias;
}

internal static class PdfSelectionUnits
{
    public const string Version = "PDF_STRUCTURAL_GEOMETRY_SELECTION_UNITS_V2";

    public static IReadOnlyList<PdfSelectionUnit> Build(AgentSource source)
    {
        var rows = Rows(source);
        var units = new List<PdfSelectionUnit>();
        for (var first = 0; first < rows.Count;)
        {
            var last = TableRegionEnd(rows, first);
            var kind = last > first + 1 ? "table-region" : "paragraph-region";
            if (kind == "paragraph-region") last = ParagraphEnd(rows, first);
            var members = rows.Skip(first).Take(last - first).SelectMany(r => r.Members).ToArray();
            units.Add(new PdfSelectionUnit("U" + (units.Count + 1).ToString("D5"), kind, members, Geometry(rows, first, last)));
            first = last;
        }
        return units;
    }

    sealed record Row(int Page, IReadOnlyList<int> Members, string? LayoutBlock, double Left, double Right, double Bottom, double Top, IReadOnlyList<double> ColumnLefts);

    static IReadOnlyList<Row> Rows(AgentSource source)
    {
        var result = new List<Row>();
        for (var first = 0; first < source.Lines.Count;)
        {
            var rowId = source.Lines[first].Alias.Split(':')[0];
            var last = first + 1;
            while (last < source.Lines.Count && source.Lines[last].Alias.Split(':')[0] == rowId) last++;
            var members = Enumerable.Range(first, last - first).ToArray();
            var layoutBlocks = members.Select(i => source.Blocks[i]).Distinct(StringComparer.Ordinal).ToArray();
            // A visual row that crosses parser blocks is not safe to merge as paragraph flow.
            // Repeated-column table detection below can still group such rows geometrically.
            var layoutBlock = layoutBlocks.Length == 1 ? layoutBlocks[0] : null;
            var boxes = members.Select(i => Box(source.Lines[i])).Where(b => b is not null).Select(b => b!.Value).ToArray();
            // A missing box is deliberately isolated: geometry may not invent a merge for it.
            if (boxes.Length != members.Length)
                result.Add(new Row(source.Lines[first].Page ?? 0, members, layoutBlock, double.NaN, double.NaN, double.NaN, double.NaN, []));
            else
                result.Add(new Row(source.Lines[first].Page ?? 0, members, layoutBlock, boxes.Min(b => b.Left), boxes.Max(b => b.Right),
                    boxes.Min(b => b.Bottom), boxes.Max(b => b.Top), boxes.Select(b => b.Left).Order().ToArray()));
            first = last;
        }
        return result;
    }

    static int ParagraphEnd(IReadOnlyList<Row> rows, int first)
    {
        var end = first + 1;
        var block = rows[first].LayoutBlock;
        // A paragraph-region is one parser paragraph/block. Geometry is a consistency check for
        // wrapped lines inside that block; it must never bridge separate blocks into a prose run.
        while (block is not null && end < rows.Count && SamePage(rows[end - 1], rows[end])
            && string.Equals(rows[end].LayoutBlock, block, StringComparison.Ordinal)
            && FlowAdjacent(rows[end - 1], rows[end])) end++;
        return end;
    }

    // A table region starts only on a repeated visual row grid and ends as soon as that grid or
    // its measured vertical cadence stops. A distant pair of matching rows must not absorb the
    // intervening prose or the rest of the page.
    static int TableRegionEnd(IReadOnlyList<Row> rows, int first)
    {
        if (rows[first].ColumnLefts.Count < 2 || first + 1 >= rows.Count) return first + 1;
        if (!SamePage(rows[first], rows[first + 1]) || !SameColumns(rows[first], rows[first + 1])
            || !TableRowsAdjacent(rows[first], rows[first + 1])) return first + 1;

        var end = first + 2;
        while (end < rows.Count && SamePage(rows[end - 1], rows[end])
            && SameColumns(rows[first], rows[end]) && TableRowsAdjacent(rows[end - 1], rows[end])) end++;
        return end;
    }

    static bool SamePage(Row a, Row b) => a.Page != 0 && a.Page == b.Page;
    static bool FlowAdjacent(Row a, Row b) => !double.IsNaN(a.Left) && !double.IsNaN(b.Left)
        && VerticalGap(a, b) <= 1.9 * Math.Max(a.Top - a.Bottom, b.Top - b.Bottom)
        && Math.Abs(a.Left - b.Left) <= 36;
    static bool TableRowsAdjacent(Row a, Row b) => !double.IsNaN(a.Top) && !double.IsNaN(b.Top)
        && VerticalGap(a, b) <= 1.9 * Math.Max(a.Top - a.Bottom, b.Top - b.Bottom);
    static double VerticalGap(Row above, Row below) => Math.Max(0, above.Bottom - below.Top);
    static bool SameColumns(Row a, Row b) => a.ColumnLefts.Count == b.ColumnLefts.Count
        && a.ColumnLefts.Zip(b.ColumnLefts).All(pair => Math.Abs(pair.First - pair.Second) <= 16);

    static JsonObject Geometry(IReadOnlyList<Row> rows, int first, int end)
    {
        var region = rows.Skip(first).Take(end - first).ToArray();
        var measured = region.Where(r => !double.IsNaN(r.Left)).ToArray();
        var value = new JsonObject { ["rows"] = region.Length, ["pages"] = new JsonArray(region.Select(r => (JsonNode)r.Page).Distinct().ToArray()) };
        if (measured.Length > 0) value["bbox"] = new JsonObject
        {
            ["left"] = measured.Min(r => r.Left), ["right"] = measured.Max(r => r.Right),
            ["bottom"] = measured.Min(r => r.Bottom), ["top"] = measured.Max(r => r.Top)
        };
        value["columnRows"] = region.Count(r => r.ColumnLefts.Count >= 2);
        return value;
    }

    static (double Left, double Right, double Bottom, double Top)? Box(AgentSourceLine line)
    {
        if (line.Bbox is not { } box || box.ValueKind != JsonValueKind.Object) return null;
        try { return (box.GetProperty("left").GetDouble(), box.GetProperty("right").GetDouble(), box.GetProperty("bottom").GetDouble(), box.GetProperty("top").GetDouble()); }
        catch (KeyNotFoundException) { return null; }
    }

    public static IReadOnlyList<PdfSelectionUnit> ForCore(AgentSource source, IReadOnlyList<PdfSelectionUnit> units, (int Start, int End) core)
    {
        var result = units.Where(unit => unit.Members.Any(i => i >= core.Start && i < core.End)).ToArray();
        if (result.Any(unit => unit.Members.Any(i => i < core.Start || i >= core.End)))
            throw new InvalidOperationException("PDF_SELECTION_UNIT_SPLITS_CORE");
        return result;
    }
}
