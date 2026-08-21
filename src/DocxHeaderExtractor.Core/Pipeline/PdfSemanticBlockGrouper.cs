namespace DocxHeaderExtractor.Core.Pipeline;

internal sealed record PdfSemanticBlock(
    string Id,
    IReadOnlyList<PdfLine> Lines,
    PdfStyleKey PrimaryStyle,
    int Page,
    double TopY,
    double BottomY,
    double Left,
    double Right,
    string Text)
{
    public int LineCount => Lines.Count;
    public string DisplayText => PdfTextUtilities.HeadingReadable(Text);
    public string CanonicalText => PdfTextUtilities.CanonicalForMatch(Text);
}

internal sealed record PdfSemanticBlockSummary(
    int TotalBlocks,
    int SingleLineBlocks,
    int MultiLineBlocks,
    int MaxLinesPerBlock);

internal static class PdfSemanticBlockGrouper
{
    public static IReadOnlyList<PdfSemanticBlock> Build(
        IReadOnlyList<PdfLineBlockAnnotation> annotations,
        int maxLinesPerBlock = 4,
        bool allowSemicolonContinuation = false)
    {
        var candidates = annotations
            .Where(a => !a.ExcludeFromSemanticSamples)
            .Select(a => a.Line)
            .OrderBy(l => l.Page)
            .ThenByDescending(l => l.Y)
            .ThenBy(l => l.Left)
            .ToList();

        var blocks = new List<List<PdfLine>>();
        foreach (var line in candidates)
        {
            var current = blocks.LastOrDefault();
            if (current is not null &&
                CanMerge(current, line, maxLinesPerBlock, allowSemicolonContinuation))
            {
                current.Add(line);
            }
            else
            {
                blocks.Add([line]);
            }
        }

        var id = 1;
        return blocks.Select(lines =>
        {
            var primaryStyle = lines
                .GroupBy(l => PdfStyleClusterProfile.StyleOf(l))
                .OrderByDescending(g => g.Sum(l => PdfTextUtilities.Readable(l.Text).Length))
                .Select(g => g.Key)
                .First();
            return new PdfSemanticBlock(
                $"b{id++}",
                lines,
                primaryStyle,
                lines[0].Page,
                lines.Max(l => l.Y),
                lines.Min(l => l.Y),
                lines.Min(l => l.Left),
                lines.Max(l => l.Right),
                PdfTextUtilities.Readable(string.Join(" ", lines.Select(l => l.Text))));
        }).ToList();
    }

    public static PdfSemanticBlockSummary Summarize(IReadOnlyList<PdfSemanticBlock> blocks) =>
        new(
            blocks.Count,
            blocks.Count(b => b.LineCount == 1),
            blocks.Count(b => b.LineCount > 1),
            blocks.Count == 0 ? 0 : blocks.Max(b => b.LineCount));

    private static bool CanMerge(
        IReadOnlyList<PdfLine> current,
        PdfLine next,
        int maxLinesPerBlock,
        bool allowSemicolonContinuation)
    {
        if (current.Count >= maxLinesPerBlock) return false;
        var previous = current[^1];
        if (previous.Page != next.Page) return false;
        if (previous.Y - next.Y is <= 0 or > 22) return false;
        if (Math.Abs(previous.Left - next.Left) > 24) return false;
        if (Math.Abs(previous.FontSize - next.FontSize) > 1.1) return false;
        if (!SameVisualFamily(previous, next)) return false;

        var previousText = PdfTextUtilities.Readable(previous.Text);
        if (previousText.EndsWith('.') || (!allowSemicolonContinuation && previousText.EndsWith(';'))) return false;
        if (previousText.Length > 130) return false;
        return true;
    }

    private static bool SameVisualFamily(PdfLine a, PdfLine b) =>
        a.FontName == b.FontName &&
        a.FillColorKey == b.FillColorKey &&
        Math.Abs(a.BoldRatio - b.BoldRatio) <= 0.30 &&
        Math.Abs(a.ItalicRatio - b.ItalicRatio) <= 0.30;
}
