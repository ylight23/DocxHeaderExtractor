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

/// <summary>How consecutive lines are judged to continue one another.</summary>
internal enum PdfBlockGrouping
{
    /// <summary>
    /// An absolute 22pt ceiling on the vertical gap. The behaviour every frozen source universe
    /// was built with, and the active default until a migration says otherwise.
    /// </summary>
    LegacyV1,

    /// <summary>
    /// The gap measured against the document's own line pitch. See <see cref="PdfLinePitch"/>.
    /// </summary>
    ContinuationV2,
}

/// <summary>
/// The distance between consecutive lines of one paragraph, measured from the document itself.
/// <para>
/// A block is supposed to be a run of lines that continue each other, and the question is where
/// that run ends. Two signals are present on the page: lines inside a paragraph sit one leading
/// apart, and a paragraph break adds space on top of it. Measured across this repository's PDF
/// corpus the two populations are always separate and never in the same place twice - one document
/// wraps at 1.05 line-heights and breaks at 1.45, another wraps at 1.40 and breaks at 2.25. A fixed
/// ceiling cannot sit between both pairs, and the incumbent 22pt one sits above both for 11pt text,
/// which is why a heading merges with the paragraph beneath it.
/// </para>
/// <para>
/// So the pitch is read off the document. The leading is its <em>smallest recurring</em> gap: a
/// paragraph break is by construction larger than the leading it adds to, and a gap that occurs
/// throughout a document is a typesetting rule rather than an accident of layout. Taking the most
/// common gap instead would fail on a document of short articles, where breaks outnumber wrapped
/// lines - which is the case in this corpus's legal texts.
/// </para>
/// </summary>
internal static class PdfLinePitch
{
    /// <summary>How much beyond the measured leading still counts as the same paragraph.</summary>
    public const double ContinuationTolerance = 1.15;

    /// <summary>A gap must recur at least this often to be read as the document's leading.</summary>
    public const double RecurrenceShare = 0.05;

    /// <summary>Resolution of the histogram, in line-heights.</summary>
    public const double Bucket = 0.05;

    /// <summary>
    /// Bounds on a plausible leading, in line-heights. A document with too few wrapped lines to
    /// measure would otherwise hand back a paragraph gap, or nothing at all, and the grouper would
    /// inherit whichever accident that was.
    /// </summary>
    public const double MinimumPitch = 1.0;
    public const double MaximumPitch = 2.0;

    /// <summary>The vertical size of a line, for a document whose declared font size is unusable.</summary>
    public static double ScaleOf(PdfLine line) =>
        Math.Max(line.FontSize, (line.Top ?? 0) - (line.Bottom ?? 0));

    /// <summary>The gap to the next line, in line-heights, or null when it cannot be measured.</summary>
    public static double? NormalizedGap(PdfLine previous, PdfLine next)
    {
        if (previous.Page != next.Page) return null;
        var scale = Math.Max(ScaleOf(previous), ScaleOf(next));
        if (scale <= 0) return null;
        var gap = previous.Y - next.Y;
        return gap <= 0 ? null : gap / scale;
    }

    /// <summary>The document's leading, in line-heights.</summary>
    public static double Estimate(IReadOnlyList<PdfLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var gaps = new List<double>();
        for (var index = 1; index < lines.Count; index++)
            if (NormalizedGap(lines[index - 1], lines[index]) is { } gap) gaps.Add(gap);

        if (gaps.Count == 0) return MinimumPitch;

        var recurring = gaps
            .GroupBy(gap => Math.Round(gap / Bucket) * Bucket)
            .Where(group => group.Count() >= gaps.Count * RecurrenceShare)
            .Select(group => group.Key)
            .DefaultIfEmpty(gaps.Min())
            .Min();

        return Math.Clamp(recurring, MinimumPitch, MaximumPitch);
    }

    /// <summary>The largest gap, in line-heights, that still reads as the same paragraph.</summary>
    public static double ContinuationCeiling(IReadOnlyList<PdfLine> lines) =>
        Estimate(lines) * ContinuationTolerance;
}

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
        bool includeRiskLines = false,
        PdfBlockGrouping grouping = PdfBlockGrouping.LegacyV1)
    {
        // Measured over every line the document has, not only the ones grouping will consider, so
        // the leading does not change depending on which lines a caller filtered out.
        var ceiling = grouping == PdfBlockGrouping.ContinuationV2
            ? PdfLinePitch.ContinuationCeiling(annotations.Select(item => item.Line).ToArray())
            : (double?)null;

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
                    maxLinesPerBlock, allowSemicolonContinuation, ceiling))
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
        bool allowSemicolonContinuation,
        double? continuationCeiling = null)
    {
        if (current.Count >= maxLinesPerBlock) return false;
        var previous = current[^1];
        if (previous.Page != next.Page) return false;
        if (previous.Y - next.Y <= 0) return false;

        if (continuationCeiling is { } ceiling)
        {
            // The gap has to look like this document's leading. Without that, the only thing
            // stopping a heading from absorbing the paragraph below it is an absolute ceiling set
            // wide enough to clear a paragraph break at body size.
            if (PdfLinePitch.NormalizedGap(previous, next) is not { } normalized) return false;
            if (normalized > ceiling) return false;
        }
        else if (previous.Y - next.Y > 22)
        {
            return false;
        }

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
