using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// P7-F1 V4 (Issue #7) source enrichment: deterministic, read-only observations derived from the parser-owned evidence store
/// (<see cref="P7F1QEvidenceTools"/> over <c>P7_RAW_SOURCE_EVIDENCE_STORE_V1</c>, unchanged) and the parser layout blocks.
/// Same aliases, pages, text and PDF coordinate basis. Every inferred relation carries a <c>status</c>
/// (SUPPORTED_BY_LAYOUT / SUPPORTED_BY_PARSER / UNRESOLVED / NOT_AVAILABLE), an <c>origin</c> and the aliases it rests on.
/// It never asserts that something is or is not a heading, never assigns caption ownership, cross-page continuity or a
/// navigation target, and never filters candidates. No Gold, labels or model output are read.
/// </summary>
internal sealed class P7F1SourceEnrichment
{
    public const string Version = "P7_F1_SOURCE_ENRICHMENT_V1";
    public const double BaselineTolerance = P7F1QEvidenceTools.SameBaselineTolerance;
    public const int NeighbourRows = 6, MaxPeers = 4, MaxTargets = 3, CaptionRowWindow = 25, PeerTextChars = 40;
    public const string Basis = "Observations from parser geometry and text only. status says how an inferred relation is supported; "
        + "NEAREST caption is a candidate, never an owner; continuation and navigation targets are never asserted.";

    static readonly Regex NumericCell = new(@"^[\s$€£¥()\[\]\-–—+,.%*\d]*\d[\s$€£¥()\[\]\-–—+,.%*\d]*$", RegexOptions.CultureInvariant);
    static readonly Regex CaptionStart = new(@"^\s*(Table|Figure|Fig\.|Chart|Graph|Box|Exhibit|Bảng|Hình|Biểu\s*đồ|Sơ\s*đồ)\s*[A-Z]?\d+(\.\d+)*\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly Regex TrailingPage = new(@"(?:\.{2,}|…+|\s)\s*(\d{1,4})\s*$", RegexOptions.CultureInvariant);
    static readonly Regex DotLeaders = new(@"\.{4,}|…{2,}|(\.\s){3,}", RegexOptions.CultureInvariant);

    readonly P7F1QEvidenceTools tools;
    readonly Dictionary<int, List<Row>> rowsByPage = new();
    readonly Dictionary<string, (int Page, int Row)> rowOf = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> blockOf = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> blockSize = new(StringComparer.Ordinal);
    readonly Dictionary<int, (double Bottom, double Top)> extent = new();
    readonly Dictionary<string, List<Line>> byRepeatKey = new(StringComparer.Ordinal);
    readonly Dictionary<object, object> memo = new();

    sealed record Line(string Alias, int Ordinal, int Page, string Text, double Left, double Right, double Bottom, double Top);
    sealed record Row(IReadOnlyList<Line> Cells, double Baseline)
    {
        public int NumericCells => Cells.Count(c => NumericCell.IsMatch(c.Text));
        public bool DataRow => NumericCells >= 2 && Cells.Any(c => !NumericCell.IsMatch(c.Text));
    }

    public P7F1SourceEnrichment(P7F1QEvidenceTools tools, DocumentSourceSnapshot source, PdfSourceDetails details)
    {
        this.tools = tools;
        var idByAlias = source.Evidence.ToDictionary(e => e.SourceAlias, e => e.SourceId, StringComparer.Ordinal);
        foreach (var page in source.Atoms.Select(a => a.Page).Distinct().Order())
        {
            var lines = tools.LinesOnPage(page).Select(l => ToLine(l)).Where(l => l is not null).Select(l => l!).OrderBy(l => l.Ordinal).ToList();
            // Rows: lines whose bottoms lie within the baseline tolerance of the row's first line, in reading order of the row's top.
            var rows = new List<Row>();
            foreach (var l in lines.OrderByDescending(l => l.Bottom).ThenBy(l => l.Left))
            {
                var row = rows.FirstOrDefault(r => Math.Abs(r.Baseline - l.Bottom) <= BaselineTolerance);
                if (row is null) rows.Add(new Row(new List<Line> { l }, l.Bottom));
                else ((List<Line>)row.Cells).Add(l);
            }
            rows = rows.OrderByDescending(r => r.Baseline).Select(r => r with { Cells = r.Cells.OrderBy(c => c.Left).ToList() }).ToList();
            rowsByPage[page] = rows;
            for (var i = 0; i < rows.Count; i++) foreach (var c in rows[i].Cells) rowOf[c.Alias] = (page, i);
            if (lines.Count > 0) extent[page] = (lines.Min(l => l.Bottom), lines.Max(l => l.Top));
            foreach (var l in lines)
            {
                var k = P7F1QEvidenceTools.RepeatKey(l.Text);
                if (k.Length == 0) continue;
                if (!byRepeatKey.TryGetValue(k, out var list)) byRepeatKey[k] = list = [];
                list.Add(l);
            }
        }
        foreach (var (alias, id) in idByAlias)
            if (details.LayoutBlockByAtom.TryGetValue(id, out var block)) blockOf[alias] = block;
        foreach (var g in blockOf.GroupBy(p => p.Value)) blockSize[g.Key] = g.Count();
    }

    static Line? ToLine(F1QSourceLine l)
    {
        if (l.Page is null || l.Bbox is not { ValueKind: JsonValueKind.Object } b) return null;
        return new(l.Alias, l.Ordinal, l.Page.Value, l.Text, b.GetProperty("left").GetDouble(), b.GetProperty("right").GetDouble(),
            b.GetProperty("bottom").GetDouble(), b.GetProperty("top").GetDouble());
    }


    /// <summary>Enrichment record for one alias (issued subject or context line). Bounded, deterministic.</summary>
    public object Of(string alias)
    {
        if (memo.TryGetValue(alias, out var cached)) return cached;
        return memo[alias] = Compute(alias);
    }

    object Compute(string alias)
    {
        if (!rowOf.TryGetValue(alias, out var at))
            return new { version = Version, status = "NOT_AVAILABLE", reason = "NO_PARSER_GEOMETRY_FOR_ALIAS" };
        var rows = rowsByPage[at.Page]; var row = rows[at.Row]; var self = row.Cells.Single(c => c.Alias == alias);
        var peers = row.Cells.Where(c => c.Alias != alias).ToArray();
        var lo = Math.Max(0, at.Row - NeighbourRows); var hi = Math.Min(rows.Count - 1, at.Row + NeighbourRows);
        var near = Enumerable.Range(lo, hi - lo + 1).Where(i => i != at.Row).Select(i => rows[i]).ToArray();
        var dataNear = near.Where(r => r.DataRow).ToArray();
        double fraction = near.Length == 0 ? 0 : Math.Round((double)dataNear.Length / near.Length, 2);

        // Caption candidates: nearest caption-pattern line above on this page; else, only if this row sits in the page's
        // leading run of data rows, the nearest one on the previous page (cross-page, never asserted as continuity).
        object? caption = null;
        for (var i = at.Row - 1; i >= Math.Max(0, at.Row - CaptionRowWindow) && caption is null; i--)
            if (rows[i].Cells.FirstOrDefault(c => CaptionStart.IsMatch(c.Text)) is { } cap)
                caption = new { st = "SUPPORTED_BY_LAYOUT", rel = "ABOVE", alias = cap.Alias, page = cap.Page, text = Clip(cap.Text, PeerTextChars), rowsAbove = at.Row - i };
        if (caption is null && CaptionStart.IsMatch(self.Text))
            caption = new { st = "SUPPORTED_BY_PARSER", rel = "SELF", alias = self.Alias, page = self.Page, text = Clip(self.Text, PeerTextChars), rowsAbove = 0 };
        var leadingDataRun = Enumerable.Range(0, at.Row + 1).All(i => rows[i].DataRow || i == at.Row) && rowsByPage.ContainsKey(at.Page - 1);
        object? previousPage = null;
        if (rowsByPage.TryGetValue(at.Page - 1, out var prev) && prev.Count > 0)
        {
            var tail = prev.Skip(Math.Max(0, prev.Count - 3)).ToArray();
            var prevCaption = leadingDataRun && caption is null
                ? prev.AsEnumerable().Reverse().Take(CaptionRowWindow).SelectMany(r => r.Cells).FirstOrDefault(c => CaptionStart.IsMatch(c.Text)) : null;
            if (prevCaption is not null)
                caption = new { st = "UNRESOLVED", rel = "PREV_PAGE", alias = prevCaption.Alias, page = prevCaption.Page, text = Clip(prevCaption.Text, PeerTextChars), rowsAbove = -1 };
            var dataLast3 = tail.Count(r => r.DataRow);
            previousPage = dataLast3 == 0 ? null : new { dataLast3, continuity = "UNRESOLVED" }; // never asserted across a page break
        }

        // Navigation: trailing page number / dot leaders on the line or its row, and the same normalized text elsewhere.
        var rowText = string.Join(" ", row.Cells.Select(c => c.Text));
        var trailing = TrailingPage.Match(rowText); int? pageRef = trailing.Success ? int.Parse(trailing.Groups[1].Value) : null;
        var key = P7F1QEvidenceTools.RepeatKey(self.Text);
        var elsewhere = key.Length == 0 || !byRepeatKey.TryGetValue(key, out var same) ? []
            : same.Where(c => c.Page != at.Page).OrderBy(c => c.Page).ThenBy(c => c.Ordinal).Take(MaxTargets).ToArray();
        var (bottom, top) = extent[at.Page];
        var band = top - bottom <= 0 ? (double?)null : Math.Round((self.Top - bottom) / (top - bottom), 2);
        blockOf.TryGetValue(alias, out var blockId);

        // Compact wire form; key meanings, origins and statuses are defined once in Legend (sent once per request).
        var dots = DotLeaders.IsMatch(rowText);
        return new
        {
            row = peers.Length == 0 ? null : new
            {
                st = "SUPPORTED_BY_LAYOUT", peers = peers.Length, num = peers.Count(c => NumericCell.IsMatch(c.Text)),
                cells = peers.Take(MaxPeers).Select(c => new object[] { c.Alias, Clip(c.Text, PeerTextChars), NumericCell.IsMatch(c.Text) ? 1 : 0 }),
            },
            tbl = dataNear.Length == 0 && !row.DataRow ? null : new { st = "SUPPORTED_BY_LAYOUT", data = dataNear.Length, of = near.Length, self = row.DataRow },
            cap = caption,
            prev = previousPage,
            rep = tools.RepeatPageCount(alias),
            nav = pageRef is null && !dots && elsewhere.Length == 0 ? null : new
            {
                pageNo = pageRef, dots, same = elsewhere.Select(e => new object[] { e.Alias, e.Page }),
                target = elsewhere.Length == 0 ? "NOT_AVAILABLE" : pageRef is not null && elsewhere.Any(e => e.Page == pageRef) ? "SAME_TEXT_ON_REFERENCED_PAGE_CANDIDATE" : "UNRESOLVED",
            },
            pos = band,
            blk = blockId is null ? null : new object[] { blockId, blockSize[blockId] },
        };
    }

    /// <summary>Minimal signals for context-only lines.</summary>
    public object? Compact(string alias)
    {
        if (!rowOf.TryGetValue(alias, out var at)) return null;
        var row = rowsByPage[at.Page][at.Row]; var self = row.Cells.Single(c => c.Alias == alias);
        var num = row.Cells.Count(c => c.Alias != alias && NumericCell.IsMatch(c.Text));
        var rep = tools.RepeatPageCount(alias); var cap = CaptionStart.IsMatch(self.Text);
        return num == 0 && !row.DataRow && rep <= 1 && !cap ? null : new { num, data = row.DataRow, rep, cap };
    }

    static string Clip(string t, int n) => t.Length <= n ? t : t[..n] + "…";

    public const string Legend = """
        sourceEnrichment (issued lines; null/absent key = nothing observed):
        row: same-baseline peers on this page (parser bbox bottoms within 2pt) - peers count, num = numeric peer cells, cells = [alias, text, numeric 1/0].
        tbl: among up to 6 rows above/below on this page, data = rows with 2+ numeric cells, of = rows considered, self = this row has 2+ numeric cells. Table membership is NOT asserted: captionless tables and headings laid out inside tables both exist.
        cap: nearest caption-pattern line (Table/Figure/Chart/Box/Exhibit/Bảng/Hình + number) above on this page (rel ABOVE), or this line itself (rel SELF), or on the previous page when this row starts the page inside data rows (rel PREV_PAGE, status UNRESOLVED). A candidate only; ownership is never asserted.
        prev: previous page's last 3 rows - dataLast3 = how many have 2+ numeric cells; continuity across the page break is UNRESOLVED (never asserted).
        rep: number of pages carrying the same normalized text (digits masked, trailing page numbers and dot leaders removed).
        nav: pageNo = trailing page number on this row, dots = dot leaders, same = [alias, page] of the same normalized text on other pages, target = SAME_TEXT_ON_REFERENCED_PAGE_CANDIDATE / UNRESOLVED / NOT_AVAILABLE (a candidate, never asserted).
        pos: vertical position of the line top within the page text extent (0 bottom, 1 top). blk: [parser layout block id, lines in block].
        context lines carry only {num, data, rep, cap}: numeric peer cells, own row has 2+ numeric cells, pages with same text, caption pattern.
        Statuses: SUPPORTED_BY_LAYOUT (parser geometry), SUPPORTED_BY_PARSER (parser text), UNRESOLVED, NOT_AVAILABLE. All are observations, not labels.
        """;
}
