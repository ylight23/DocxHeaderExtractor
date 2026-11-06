using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.DocumentProcessing.Changes;

/// <summary>A direct XML property change. Offsets, when present, address unchanged text in UTF-16.</summary>
public sealed record FormatChange(string Property, string? Before, string? After, int? Start = null, int? End = null);

/// <summary>
/// Compares only properties read by DocxVersionReader: the paragraph's own properties, what its paragraph style gives (alignment, outline level,
/// numbering and its source), and each run's direct and effective formatting after style inheritance. It is not complete Word formatting:
/// layers the style reader does not resolve are reported as gaps ("style.gaps", "effective.gaps"), and a change there is not seen.
/// </summary>
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
        Add("numbering.effectiveLevel", a.EffectiveIlvl, b.EffectiveIlvl);
        Add("numbering.source", a.NumberingSource, b.NumberingSource);
        Add("style.alignment", a.FromStyle?.Alignment, b.FromStyle?.Alignment);
        Add("style.outlineLevel", a.FromStyle?.OutlineLevel, b.FromStyle?.OutlineLevel);
        Add("style.gaps", Gaps(a.FromStyle?.Gaps), Gaps(b.FromStyle?.Gaps));
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
            // An effective change is reported only where the run's own value did not change: it then comes from a style or the defaults.
            void Inherited(string name, object? ownBefore, object? ownAfter, object? before, object? after)
            {
                if (Equals(ownBefore, ownAfter)) Add(name, before, after, start, end);
            }
            Inherited("effective.bold", x?.Bold, y?.Bold, x?.Effective?.Bold, y?.Effective?.Bold);
            Inherited("effective.italic", x?.Italic, y?.Italic, x?.Effective?.Italic, y?.Effective?.Italic);
            Inherited("effective.fontAscii", x?.FontAscii, y?.FontAscii, x?.Effective?.FontAscii, y?.Effective?.FontAscii);
            Inherited("effective.fontSize", x?.FontSize, y?.FontSize, x?.Effective?.FontSize, y?.Effective?.FontSize);
            Inherited("effective.color", x?.Color, y?.Color, x?.Effective?.Color, y?.Effective?.Color);
            Add("effective.gaps", Gaps(x?.Effective?.Gaps), Gaps(y?.Effective?.Gaps), start, end);
        }
        return result;
    }

    static string? Gaps(IReadOnlyList<string>? gaps) => gaps is { Count: > 0 } ? string.Join(",", gaps) : null;

    /// <summary>A change that rests on a layer not read in full: the values are evidence, not what Word shows.</summary>
    public static bool Unverified(FormatChange change) => change.Property is "numbering.unverified" or "style.gaps" or "effective.gaps";

    /// <summary>The style ids whose definitions, as this reader takes them, were added, removed or changed between two versions.</summary>
    public static IReadOnlyList<string> StyleChanges(DocxVersion a, DocxVersion b) =>
        a.StyleDefinitions.Keys.Union(b.StyleDefinitions.Keys, StringComparer.Ordinal)
            .Where(id => a.StyleDefinitions.GetValueOrDefault(id) != b.StyleDefinitions.GetValueOrDefault(id)).Order(StringComparer.Ordinal).ToList();
}
