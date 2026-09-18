namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

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

    /// <summary>
    /// The canonical text of this occurrence, composed from its lines' projections, with the span
    /// map rebased onto it. <see cref="Text"/> remains the raw parser concatenation for audit.
    /// </summary>
    public PdfSourceTextProjection Projection =>
        PdfSourceTextProjection.Join(Lines.Select(line => line.Projection).ToArray());

    /// <summary>What the model is shown and what the binder binds against.</summary>
    public string VerbatimText => Projection.VerbatimText;
    public string DisplayText => PdfTextUtilities.HeadingReadable(Text);
    public string CanonicalText => string.Concat(Lines.Select(line => line.CanonicalMatchText ??
        PdfTextUtilities.CanonicalForMatch(line.Text)));
    public bool HasKerningJoinEvidence => Lines.Any(line => line.MatchText is not null &&
        !string.Equals(line.MatchText, PdfTextUtilities.Readable(line.Text), StringComparison.Ordinal));
}

internal sealed record PdfSemanticBlockSummary(
    int TotalBlocks,
    int SingleLineBlocks,
    int MultiLineBlocks,
    int MaxLinesPerBlock);

internal static class PdfSemanticBlockGrouper
{
    /// <summary>
    /// Groups parser lines into the occurrences the model reasons over.
    /// <para>
    /// Risk classification - a page number, a repeated running header, a table-like line - travels
    /// through grouping as data and is never re-derived here. With
    /// <paramref name="includeRiskLines"/> it keeps the line in the source universe, which is the
    /// point: attention, routing and evidence may use the classification, but it must not delete
    /// source text. It must equally not deform it. A risk line sits in its own occurrence and never
    /// fuses with a clean neighbour, because geometry and font alone will happily merge a running
    /// header into the heading beneath it, and the result would be a source occurrence whose text
    /// no heading actually has - an artificial partial-span problem manufactured by the harness.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PdfSemanticBlock> Build(
        IReadOnlyList<PdfLineBlockAnnotation> annotations,
        int maxLinesPerBlock = 4,
        bool allowSemicolonContinuation = false,
        bool includeRiskLines = false)
    {
        var candidates = annotations
            .Where(a => includeRiskLines || !a.ExcludeFromCandidateGrouping)
            .OrderBy(a => a.Line.Page)
            .ThenByDescending(a => a.Line.Y)
            .ThenBy(a => a.Line.Left)
            .ToList();

        var blocks = new List<List<PdfLineBlockAnnotation>>();
        foreach (var annotation in candidates)
        {
            var current = blocks.LastOrDefault();
            if (current is not null &&
                !IsRisk(current[^1]) && !IsRisk(annotation) &&
                CanMerge(current.Select(item => item.Line).ToArray(), annotation.Line,
                    maxLinesPerBlock, allowSemicolonContinuation))
            {
                current.Add(annotation);
            }
            else
            {
                blocks.Add([annotation]);
            }
        }

        var id = 1;
        return blocks.Select(group =>
        {
            var lines = group.Select(item => item.Line).ToArray();
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

    /// <summary>
    /// Read from the annotation the filter produced, never recomputed. A second derivation here
    /// could disagree with the first, and then the block boundary and the evidence attached to it
    /// would be describing different things.
    /// </summary>
    private static bool IsRisk(PdfLineBlockAnnotation annotation) =>
        annotation.PageNumber || annotation.Repeated || annotation.HeaderFooterZone || annotation.TableLike;

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
