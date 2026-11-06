using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.DocumentProcessing.Changes;

/// <summary>One table cell: its walker location, its column position in the row, its paragraphs, and whether it is merged or holds a nested table.</summary>
internal sealed record CellShape(string Location, int Column, IReadOnlyList<ParagraphFingerprint> Paragraphs, bool Merged, bool Nested)
{
    public string Key { get; } = TableDiff.KeyOf(Paragraphs.Select(p => p.KeyHash));
    public string[] Tokens { get; } = Paragraphs.SelectMany(p => TextDiff.WordTokens(p.NormalizedKey)).ToArray();
    public string Text => string.Join("\n", Paragraphs.Select(p => p.RawText));
}

/// <summary>One table row and its cells in order.</summary>
internal sealed record RowShape(string Location, IReadOnlyList<CellShape> Cells)
{
    public string Key { get; } = TableDiff.KeyOf(Cells.Select(c => c.Key));
    public string[] Tokens { get; } = Cells.SelectMany(c => c.Tokens).ToArray();
    public string[] CellKeys { get; } = Cells.Where(c => c.Tokens.Length > 0).Select(c => c.Key).ToArray();
    public string Text => string.Join(" | ", Cells.Select(c => c.Text));
    public IEnumerable<ParagraphFingerprint> Paragraphs => Cells.SelectMany(c => c.Paragraphs);
}

/// <summary>One top-level table. A nested table is kept inside its outer cell's paragraphs and flagged (not compared as a table of its own).</summary>
internal sealed record TableShape(string Location, IReadOnlyList<RowShape> Rows)
{
    public string Key { get; } = TableDiff.KeyOf(Rows.Select(r => r.Key));
    public string[] Tokens { get; } = Rows.SelectMany(r => r.Tokens).ToArray();
    /// <summary>Keys of the non-empty cells, row by row: each cell counts once, so one long added cell cannot outweigh the rest.</summary>
    public string[] CellKeys { get; } = Rows.SelectMany(r => r.Cells).Where(c => c.Tokens.Length > 0).Select(c => c.Key).ToArray();
    public string Text => string.Join("\n", Rows.Select(r => r.Text));
    public bool Merged => Rows.Any(r => r.Cells.Any(c => c.Merged));
    public bool Nested => Rows.Any(r => r.Cells.Any(c => c.Nested));
    public IEnumerable<ParagraphFingerprint> Paragraphs => Rows.SelectMany(r => r.Paragraphs);
}

/// <summary>
/// P1b (experimental): table-aware pairing. Tables are paired with tables, rows with rows, cells with cells, and only then are the paragraphs
/// of a cell pair diffed. Rows present on one side only, rows whose content moved, cells present on one side only, and columns added or
/// removed across every paired row are reported as such. Merged cells and nested tables are not resolved into a grid: every change of a
/// table pair that has them is marked uncertain with the reason. Structure comes from the walker locations (tbl/tr/tc) and the cell facts.
/// </summary>
internal static class TableDiff
{
    /// <summary>Minimum token similarity to pair two tables, two rows or two cells that are not identical.</summary>
    public const double PairThreshold = 0.5;

    /// <summary>Body paragraphs stay single units; consecutive paragraphs of the same top-level table become one <see cref="TableShape"/>.</summary>
    public static List<object> Units(IReadOnlyList<ParagraphFingerprint> paragraphs)
    {
        var units = new List<object>();
        string? table = null;
        var buffer = new List<ParagraphFingerprint>();
        void FlushTable()
        {
            if (buffer.Count > 0) units.Add(Shape(table!, buffer));
            buffer = [];
            table = null;
        }
        foreach (var p in paragraphs)
        {
            var current = Segment(p.Location, "tbl", 0);
            if (current is null) { FlushTable(); units.Add(p); continue; }
            if (current != table) { FlushTable(); table = current; }
            buffer.Add(p);
        }
        FlushTable();
        return units;
    }

    public static bool SameUnit(object x, object y) =>
        x is ParagraphFingerprint p ? y is ParagraphFingerprint q && p.KeyHash == q.KeyHash
        : x is TableShape s && y is TableShape u && s.Key == u.Key;

    static TableShape Shape(string table, List<ParagraphFingerprint> paragraphs)
    {
        var rows = new List<(string Location, List<(string Location, List<ParagraphFingerprint> Paragraphs, bool Nested)> Cells)>();
        foreach (var p in paragraphs)
        {
            var row = Segment(p.Location, "tr", table.Length) ?? table;
            var cell = Segment(p.Location, "tc", row.Length) ?? row;
            var nested = p.Location.IndexOf("/tbl[", cell.Length, StringComparison.Ordinal) >= 0;
            if (rows.Count == 0 || rows[^1].Location != row) rows.Add((row, []));
            var cells = rows[^1].Cells;
            if (cells.Count == 0 || cells[^1].Location != cell) cells.Add((cell, [], false));
            cells[^1].Paragraphs.Add(p);
            if (nested) cells[^1] = cells[^1] with { Nested = true };
        }
        return new TableShape(table, rows.Select(r => new RowShape(r.Location,
            r.Cells.Select((c, i) => new CellShape(c.Location, i, c.Paragraphs, c.Paragraphs.Any(x => x.Cell?.Merged == true), c.Nested)).ToArray())).ToArray());
    }

    /// <summary>The location up to and including the first "/name[n]" segment found at or after <paramref name="from"/>, or null.</summary>
    static string? Segment(string location, string name, int from)
    {
        var at = location.IndexOf("/" + name + "[", from, StringComparison.Ordinal);
        if (at < 0) return null;
        var end = location.IndexOf(']', at);
        return end < 0 ? null : location[..(end + 1)];
    }

    internal static string KeyOf(IEnumerable<string> parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("␟", parts))));

    /// <summary>Pairs the tables of one unaligned block in order; a table left over is reported on its side only.</summary>
    public static void Block(List<TableShape> t, List<TableShape> d, DocxComparer.Context context)
    {
        if (t.Count == 0 && d.Count == 0) return;
        foreach (var (i, j) in Align(t.Count, d.Count, (x, y) => Similarity(t[x], d[y])))
        {
            if (i >= 0 && j >= 0) CompareTables(t[i], d[j], "paired_by_similarity", false, context);
            else if (i >= 0) context.Changes.Add(new Change("table_only_in_template", "unmatched", false, null, TableSide(t[i]), null,
                TemplateGroup: t[i].Paragraphs.Select(DocxComparer.Whole).ToArray()));
            else context.Changes.Add(new Change("table_only_in_document", "unmatched", false, null, null, TableSide(d[j]),
                DocumentGroup: d[j].Paragraphs.Select(DocxComparer.Whole).ToArray()));
        }
    }

    /// <summary>
    /// Table similarity: the higher of token Dice and Dice over the non-empty cell keys. Token Dice alone drops below the threshold when a row or
    /// column of long text is added to a small table, although every original cell is still there unchanged.
    /// </summary>
    internal static double Similarity(TableShape s, TableShape u) =>
        Math.Max(DocxComparer.Dice(s.Tokens, u.Tokens), DocxComparer.Dice(s.CellKeys, u.CellKeys));

    /// <summary>Row similarity, by the same rule: a column of long text added to the row must not unpair it.</summary>
    internal static double Similarity(RowShape s, RowShape u) =>
        Math.Max(DocxComparer.Dice(s.Tokens, u.Tokens), DocxComparer.Dice(s.CellKeys, u.CellKeys));

    /// <summary>Compares two paired tables row by row, then cell by cell, then paragraph by paragraph.</summary>
    public static void CompareTables(TableShape s, TableShape u, string status, bool uncertain, DocxComparer.Context context)
    {
        var changes = context.Changes;
        var start = changes.Count;
        var note = DocxComparer.Join(s.Merged || u.Merged ? "merged_cells_present" : null, s.Nested || u.Nested ? "nested_table_flattened" : null);

        var oneT = new List<RowShape>();
        var oneD = new List<RowShape>();
        var gaps = new List<(List<int> Template, List<int> Document)>();
        var deleted = new List<RowShape>();
        var inserted = new List<RowShape>();
        void FlushRows()
        {
            foreach (var (i, j) in Align(deleted.Count, inserted.Count, (x, y) => Similarity(deleted[x], inserted[y])))
            {
                if (i >= 0 && j >= 0) CompareRow(deleted[i], inserted[j], context, gaps);
                else if (i >= 0) oneT.Add(deleted[i]);
                else oneD.Add(inserted[j]);
            }
            deleted.Clear();
            inserted.Clear();
        }
        var rowEdits = Myers.Diff(s.Rows, u.Rows, (x, y) => x.Key == y.Key, Myers.Limit, out var rowsComplete);
        if (!rowsComplete) context.Alignment.Incomplete = true;
        foreach (var edit in rowEdits)
        {
            switch (edit.Kind)
            {
                case EditKind.Equal: FlushRows(); CompareRow(s.Rows[edit.OldIndex], u.Rows[edit.NewIndex], context, gaps); break;
                case EditKind.Delete: deleted.Add(s.Rows[edit.OldIndex]); break;
                case EditKind.Insert: inserted.Add(u.Rows[edit.NewIndex]); break;
            }
        }
        FlushRows();

        // A row present once on each side with the same content, at a different place, moved. With several copies the pairing is uncertain.
        foreach (var row in oneT.ToArray())
        {
            var match = oneD.FirstOrDefault(x => x.Key == row.Key);
            if (match is null) continue;
            var ambiguous = oneT.Count(x => x.Key == row.Key) > 1 || oneD.Count(x => x.Key == row.Key) > 1;
            oneT.Remove(row);
            oneD.Remove(match);
            changes.Add(new Change("row_moved", "matched", ambiguous, "moved", RowSide(row), RowSide(match),
                TemplateGroup: row.Paragraphs.Select(DocxComparer.Whole).ToArray(), DocumentGroup: match.Paragraphs.Select(DocxComparer.Whole).ToArray()));
        }
        foreach (var row in oneT)
            changes.Add(new Change("row_only_in_template", "unmatched", false, null, RowSide(row), null,
                TemplateGroup: row.Paragraphs.Select(DocxComparer.Whole).ToArray()));
        foreach (var row in oneD)
            changes.Add(new Change("row_only_in_document", "unmatched", false, null, null, RowSide(row),
                DocumentGroup: row.Paragraphs.Select(DocxComparer.Whole).ToArray()));

        // A column: every paired row (at least two) leaves the same cell positions unpaired on one side and none on the other.
        Columns(gaps.Select(g => (g.Document, g.Template)).ToList(), u, "column_only_in_document", side: c => (null, c), changes);
        Columns(gaps.Select(g => (g.Template, g.Document)).ToList(), s, "column_only_in_template", side: c => (c, null), changes);

        if (note is null && !uncertain) return;
        for (var k = start; k < changes.Count; k++)
            changes[k] = changes[k] with { Uncertain = true, Note = DocxComparer.Join(changes[k].Note, note) };
    }

    static void Columns(List<(List<int> Side, List<int> Other)> gaps, TableShape table, string kind,
        Func<ChangeSide, (ChangeSide? Template, ChangeSide? Document)> side, List<Change> changes)
    {
        if (gaps.Count < 2 || gaps.Any(g => g.Side.Count == 0 || g.Other.Count > 0)) return;
        var first = gaps[0].Side;
        if (gaps.Any(g => !g.Side.SequenceEqual(first))) return;
        foreach (var column in first)
        {
            var cells = table.Rows.Where(r => r.Cells.Count > column).Select(r => r.Cells[column]).ToArray();
            var columnSide = new ChangeSide($"{table.Location}#col[{column + 1}]", cells.FirstOrDefault()?.Paragraphs[0].Ordinal ?? -1,
                string.Join("\n", cells.Select(c => c.Text)), null);
            var (template, document) = side(columnSide);
            changes.Add(new Change(kind, "unmatched", false, null, template, document));
        }
    }

    /// <summary>Pairs the cells of two paired rows: by position when the counts agree, else in order by content; unpaired cells are reported.</summary>
    static void CompareRow(RowShape rt, RowShape rd, DocxComparer.Context context, List<(List<int> Template, List<int> Document)> gaps)
    {
        var missingT = new List<int>();
        var missingD = new List<int>();
        if (rt.Cells.Count == rd.Cells.Count)
        {
            for (var c = 0; c < rt.Cells.Count; c++) DocxComparer.CompareSequence(rt.Cells[c].Paragraphs, rd.Cells[c].Paragraphs, context);
            gaps.Add((missingT, missingD));
            return;
        }
        foreach (var (i, j) in Align(rt.Cells.Count, rd.Cells.Count, (x, y) =>
            rt.Cells[x].Key == rd.Cells[y].Key ? 1.0 : DocxComparer.Dice(rt.Cells[x].Tokens, rd.Cells[y].Tokens)))
        {
            if (i >= 0 && j >= 0) DocxComparer.CompareSequence(rt.Cells[i].Paragraphs, rd.Cells[j].Paragraphs, context);
            else if (i >= 0)
            {
                missingT.Add(i);
                context.Changes.Add(new Change("cell_only_in_template", "unmatched", false, "cell_count_differs", CellSide(rt.Cells[i]), null,
                    TemplateGroup: rt.Cells[i].Paragraphs.Select(DocxComparer.Whole).ToArray()));
            }
            else
            {
                missingD.Add(j);
                context.Changes.Add(new Change("cell_only_in_document", "unmatched", false, "cell_count_differs", null, CellSide(rd.Cells[j]),
                    DocumentGroup: rd.Cells[j].Paragraphs.Select(DocxComparer.Whole).ToArray()));
            }
        }
        gaps.Add((missingT, missingD));
    }

    /// <summary>
    /// Order-preserving one-to-one alignment maximizing the summed similarity of pairs at or above <see cref="PairThreshold"/>. Returns the
    /// path as (template index or -1, document index or -1). Blocks too large to align are returned unpaired.
    /// </summary>
    internal static List<(int I, int J)> Align(int n, int m, Func<int, int, double> similarity)
    {
        var path = new List<(int I, int J)>();
        if ((long)n * m > DocxComparer.MaxBlockCells)
        {
            for (var i = 0; i < n; i++) path.Add((i, -1));
            for (var j = 0; j < m; j++) path.Add((-1, j));
            return path;
        }
        var score = new double[n + 1, m + 1];
        var step = new byte[n + 1, m + 1];
        for (var i = 0; i <= n; i++)
            for (var j = 0; j <= m; j++)
            {
                if (i == 0 && j == 0) continue;
                var best = double.NegativeInfinity;
                if (i > 0) { best = score[i - 1, j]; step[i, j] = 0; }
                if (j > 0 && score[i, j - 1] > best) { best = score[i, j - 1]; step[i, j] = 1; }
                if (i > 0 && j > 0 && similarity(i - 1, j - 1) is var s && s >= PairThreshold && score[i - 1, j - 1] + s > best)
                {
                    best = score[i - 1, j - 1] + s;
                    step[i, j] = 2;
                }
                score[i, j] = best;
            }
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            switch (step[i, j])
            {
                case 0: path.Add((i - 1, -1)); i--; break;
                case 1: path.Add((-1, j - 1)); j--; break;
                default: path.Add((i - 1, j - 1)); i--; j--; break;
            }
        }
        path.Reverse();
        return path;
    }

    static ChangeSide TableSide(TableShape table) => new(table.Location, table.Paragraphs.First().Ordinal, table.Text, null);
    static ChangeSide RowSide(RowShape row) => new(row.Location, row.Paragraphs.First().Ordinal, row.Text, null);
    static ChangeSide CellSide(CellShape cell) => new(cell.Location, cell.Paragraphs[0].Ordinal, cell.Text, null);
}
