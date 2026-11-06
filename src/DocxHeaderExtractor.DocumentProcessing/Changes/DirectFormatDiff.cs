using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.DocumentProcessing.Changes;

/// <summary>A direct XML property change. Offsets, when present, address unchanged text in UTF-16.</summary>
public sealed record FormatChange(string Property, string? Before, string? After, int? Start = null, int? End = null);

/// <summary>Compares only properties read by DocxVersionReader, not complete or effective Word formatting.</summary>
internal static class DirectFormatDiff
{
    public static IReadOnlyList<FormatChange> Compare(ParagraphFingerprint a, ParagraphFingerprint b)
    {
        var result = new List<FormatChange>();
        void Add(string name, object? left, object? right, int? start = null, int? end = null)
        {
            var x = left?.ToString(); var y = right?.ToString();
            if (!string.Equals(x, y, StringComparison.Ordinal)) result.Add(new(name, x, y, start, end));
        }
        Add("paragraph.styleId", a.StyleId, b.StyleId);
        Add("paragraph.alignment", a.Alignment, b.Alignment);
        Add("numbering.level", a.Ilvl, b.Ilvl);
        if (a.NumberingResolved && b.NumberingResolved)
        {
            Add("numbering.definition", a.NumberingKey, b.NumberingKey);
            Add("numbering.relation", a.NumberingRelation, b.NumberingRelation);
        }
        else result.Add(new("numbering.unverified", a.NumberingResolved.ToString(), b.NumberingResolved.ToString()));
        Add("cell.gridSpan", a.Cell?.GridSpan, b.Cell?.GridSpan);
        Add("cell.verticalMerge", a.Cell?.VerticalMerge, b.Cell?.VerticalMerge);
        Add("cell.horizontalMerge", a.Cell?.HorizontalMerge, b.Cell?.HorizontalMerge);
        Add("cell.tableDepth", a.Cell?.TableDepth, b.Cell?.TableDepth);

        // Run boundaries alone are not formatting changes. Compare properties over equal text intervals.
        if (!string.Equals(a.RawText, b.RawText, StringComparison.Ordinal)) return result;
        var boundaries = a.Runs.SelectMany(r => new[] { r.Start, r.End })
            .Concat(b.Runs.SelectMany(r => new[] { r.Start, r.End })).Distinct().Order().ToArray();
        var ai = 0; var bi = 0;
        for (var i = 0; i + 1 < boundaries.Length; i++)
        {
            var start = boundaries[i]; var end = boundaries[i + 1];
            while (ai < a.Runs.Count && a.Runs[ai].End <= start) ai++;
            while (bi < b.Runs.Count && b.Runs[bi].End <= start) bi++;
            var x = ai < a.Runs.Count && a.Runs[ai].Start <= start ? a.Runs[ai] : null;
            var y = bi < b.Runs.Count && b.Runs[bi].Start <= start ? b.Runs[bi] : null;
            Add("run.bold", x?.Bold, y?.Bold, start, end);
            Add("run.italic", x?.Italic, y?.Italic, start, end);
            Add("run.underline", x?.Underline, y?.Underline, start, end);
            Add("run.fontAscii", x?.FontAscii, y?.FontAscii, start, end);
            Add("run.fontHighAnsi", x?.FontHighAnsi, y?.FontHighAnsi, start, end);
            Add("run.fontEastAsia", x?.FontEastAsia, y?.FontEastAsia, start, end);
            Add("run.fontComplexScript", x?.FontComplexScript, y?.FontComplexScript, start, end);
            Add("run.fontSize", x?.FontSize, y?.FontSize, start, end);
            Add("run.color", x?.Color, y?.Color, start, end);
        }
        return result;
    }
}
