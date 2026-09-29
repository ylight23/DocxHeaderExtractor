using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The visual line a PDF actually has, reconstructed independently of the grouper so it can be
/// used as evidence against it.
/// <para>
/// A source occurrence is supposed to be the harness's reconstruction of a structural boundary.
/// To say whether that reconstruction is right, something other than the reconstruction has to
/// define the boundary. That is what this is: parser lines regrouped by vertical overlap, which is
/// the one thing a reader can see and the grouper does not use.
/// </para>
/// <para>
/// This is measurement only. Nothing here is wired into extraction, and it deliberately does not
/// propose the replacement rule - the census it feeds exists to decide what that rule should be.
/// </para>
/// </summary>
internal sealed record PdfVisualLine(
    int Index,
    int Page,
    double Top,
    double Bottom,
    IReadOnlyList<int> OccurrenceIndexes,
    string Text)
{
    /// <summary>More than one occurrence on one visual line: the line was split by the grouper.</summary>
    public bool SplitAcrossOccurrences => OccurrenceIndexes.Count > 1;
}

/// <summary>One approved heading, and how the occurrence universe represents it.</summary>
internal sealed record PdfBoundaryRow(
    string Claim,
    string SourceAlias,
    int OccurrenceIndex,
    int Page,
    string SelectionMode,
    string Boundary,
    string GoldText,
    string OccurrenceText,
    int OccurrenceLineCount,
    IReadOnlyList<string> HeadingVisualLines,
    IReadOnlyList<string> ExtraVisualLinesInOccurrence,
    IReadOnlyList<string> ForeignOccurrencesOnHeadingLine)
{
    /// <summary>
    /// Whether the complete heading exists as one occurrence, or as one exact selection inside
    /// one occurrence. This is the question a binder has to answer, so it is reported separately
    /// from the boundary label: an over-grouped occurrence still contains its heading, a
    /// fragmented one does not contain it anywhere.
    /// </summary>
    public bool FullyRepresentable => ForeignOccurrencesOnHeadingLine.Count == 0;
}

internal static class PdfSourceOccurrenceBoundary
{
    /// <summary>
    /// Groups the parser lines behind <paramref name="occurrences"/> into visual lines.
    /// <para>
    /// Two parser lines are the same visual line when one's vertical centre falls inside the
    /// other's extent. Containment rather than a distance threshold, so the rule does not need a
    /// tuned constant and cannot chain two body lines together through a run of near misses.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PdfVisualLine> VisualLines(IReadOnlyList<PdfSemanticBlock> occurrences)
    {
        var members = occurrences
            .SelectMany((block, index) => block.Lines.Select(line => (Block: index, Line: line)))
            .Where(item => item.Line.Top is not null && item.Line.Bottom is not null)
            .GroupBy(item => item.Line.Page)
            .OrderBy(group => group.Key);

        var lines = new List<PdfVisualLine>();
        foreach (var page in members)
        {
            // Descending Top, so the tallest glyph run on a baseline anchors its line. Punctuation
            // peeled off a line is shorter than the text it belongs to, so it always arrives second
            // and joins rather than anchoring.
            var ordered = page.OrderByDescending(item => item.Line.Top!.Value)
                .ThenBy(item => item.Line.Left)
                .ToArray();

            var open = new List<(int Block, PdfLine Line)>();
            double anchorTop = 0, anchorBottom = 0;

            void Close()
            {
                if (open.Count == 0) return;
                lines.Add(new PdfVisualLine(
                    lines.Count,
                    open[0].Line.Page,
                    open.Max(item => item.Line.Top!.Value),
                    open.Min(item => item.Line.Bottom!.Value),
                    open.Select(item => item.Block).Distinct().Order().ToArray(),
                    string.Join(" ", open.OrderBy(item => item.Line.Left).Select(item => item.Line.Text))));
                open.Clear();
            }

            foreach (var item in ordered)
            {
                var centre = (item.Line.Top!.Value + item.Line.Bottom!.Value) / 2;
                if (open.Count > 0 && centre <= anchorTop && centre >= anchorBottom)
                {
                    open.Add(item);
                    continue;
                }

                Close();
                open.Add(item);
                anchorTop = item.Line.Top!.Value;
                anchorBottom = item.Line.Bottom!.Value;
            }

            Close();
        }

        return lines;
    }

    /// <summary>
    /// Classifies every approved heading against an occurrence universe.
    /// <para>
    /// The comparison is between two sets of visual lines - the ones the heading occupies and the
    /// ones its occurrence covers - rather than between two strings. Strings would make the answer
    /// depend on the very punctuation a broken line reconstruction drops, so a candidate that
    /// restored a lost period would be scored as having added text. Lines do not move when
    /// punctuation is repaired, which is what makes one classifier usable on both sides of a
    /// change.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PdfBoundaryRow> Classify(
        IReadOnlyList<PdfSemanticBlock> occurrences,
        IReadOnlyList<PdfGoldHeading> headings,
        IReadOnlyList<int> occurrenceOfHeading)
    {
        var aliases = Aliases(occurrences.Count);
        var visualLines = VisualLines(occurrences);
        var linesOf = visualLines
            .SelectMany(line => line.OccurrenceIndexes.Select(occurrence => (occurrence, line)))
            .ToLookup(pair => pair.occurrence, pair => pair.line);

        return headings.Select((heading, ordinal) =>
        {
            var index = occurrenceOfHeading[ordinal];
            var occurrence = occurrences[index];
            var goldText = heading.VerbatimText ?? occurrence.VerbatimText;

            var covered = linesOf[index].OrderByDescending(line => line.Top).ToArray();
            var carried = LinesCarrying(occurrence, goldText);
            var headingLines = covered
                .Where(line => carried.Any(parser =>
                    parser.Page == line.Page && parser.Top <= line.Top && parser.Bottom >= line.Bottom))
                .ToArray();
            if (headingLines.Length == 0) headingLines = covered;

            var extra = covered.Except(headingLines).OrderByDescending(line => line.Top).ToArray();
            var foreign = headingLines
                .SelectMany(line => line.OccurrenceIndexes)
                .Where(other => other != index)
                .Distinct()
                .Order()
                .ToArray();

            // Fragmentation first. An occurrence that is both too wide and missing part of its
            // heading is reported as fragmented, because that is the defect no binder can work
            // around: over-grouping still leaves the heading somewhere inside one occurrence.
            var boundary =
                foreign.Length > 0 ? "FRAGMENTED"
                : extra.Length > 0 ? "OVER_GROUPED"
                : string.Equals(goldText, occurrence.VerbatimText, StringComparison.Ordinal)
                    ? "EXACT_SOURCE_BOUNDARY"
                : KeyWithoutPunctuation(goldText) == KeyWithoutPunctuation(occurrence.VerbatimText)
                    ? "NORMALIZATION_ONLY"
                    : "OTHER_REPRESENTATION_MISMATCH";

            return new PdfBoundaryRow(
                // Keyed exactly as occurrence-baseline-v1/causal-forensic.v1.json keys the same
                // claims, so the artifacts can be read side by side without a mapping table.
                $"{heading.SourceAlias}#{ordinal}",
                heading.SourceAlias,
                index,
                occurrence.Page,
                heading.SelectionMode,
                boundary,
                goldText,
                occurrence.VerbatimText,
                occurrence.LineCount,
                headingLines.Select(line => line.Text).ToArray(),
                extra.Select(line => line.Text).ToArray(),
                foreign.Select(other => $"{aliases[other]}: {occurrences[other].VerbatimText}").ToArray());
        }).ToArray();
    }

    /// <summary>
    /// Finds each heading in an occurrence universe by its text.
    /// <para>
    /// Aliases are positions, so they only address the universe they were written against. A
    /// candidate reconstruction renumbers everything, and resolving Gold by alias there would
    /// silently compare a heading with whatever occurrence inherited its number. The match ignores
    /// punctuation - punctuation is what the defect moves - and advances a cursor through the
    /// document, so a heading whose wording repeats binds to the occurrence in its own position
    /// rather than to the first one that looks like it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<int> Locate(
        IReadOnlyList<PdfSemanticBlock> occurrences,
        IReadOnlyList<PdfGoldHeading> headings,
        IReadOnlyList<string> goldTexts,
        out IReadOnlyList<string> unresolved)
    {
        var keys = occurrences.Select(item => KeyWithoutPunctuation(item.VerbatimText)).ToArray();
        var missing = new List<string>();
        var found = new int[headings.Count];
        var cursor = 0;

        for (var ordinal = 0; ordinal < headings.Count; ordinal++)
        {
            var wanted = KeyWithoutPunctuation(goldTexts[ordinal]);
            var at = -1;
            for (var index = cursor; index < keys.Length; index++)
                if (keys[index].Contains(wanted, StringComparison.Ordinal)) { at = index; break; }

            // Two claims can share one occurrence, so the cursor may not advance past it. It only
            // ever moves forward, which is what keeps repeated wording in document order.
            if (at < 0)
            {
                for (var index = 0; index < keys.Length && at < 0; index++)
                    if (keys[index].Contains(wanted, StringComparison.Ordinal)) at = index;
            }

            if (at < 0)
            {
                missing.Add($"{headings[ordinal].SourceAlias}#{ordinal}: {goldTexts[ordinal]}");
                found[ordinal] = -1;
                continue;
            }

            found[ordinal] = at;
            cursor = at;
        }

        unresolved = missing;
        return found;
    }

    /// <summary>
    /// The artifact shape of a row. Kept here rather than in a test so the before and after
    /// censuses cannot drift into reporting different fields for the same measurement.
    /// </summary>
    public static object Serialize(PdfBoundaryRow row) => new
    {
        claim = row.Claim,
        sourceAlias = row.SourceAlias,
        occurrenceIndex = row.OccurrenceIndex,
        page = row.Page,
        selectionMode = row.SelectionMode,
        boundary = row.Boundary,
        fullyRepresentable = row.FullyRepresentable,
        goldText = row.GoldText,
        occurrenceText = row.OccurrenceText,
        occurrenceLineCount = row.OccurrenceLineCount,
        // The whole visual line as the page has it, which is what the occurrence should have been
        // able to reproduce, and whatever else the occurrence swallowed alongside it.
        headingVisualLines = row.HeadingVisualLines,
        extraVisualLinesInOccurrence = row.ExtraVisualLinesInOccurrence,
        foreignOccurrencesOnHeadingLine = row.ForeignOccurrencesOnHeadingLine,
    };

    /// <summary>One punctuation mark, and how often the grouper gave it an occurrence of its own.</summary>
    public sealed record PdfPunctuationRow(
        string Mark,
        int Occurrences,
        int SharingAVisualLineWithText,
        int ImmediatelyBeforeAGoldOccurrence);

    /// <summary>The punctuation marks worth counting when a line reconstruction is in question.</summary>
    public static readonly string[] Marks = [".", ":", ";", ",", "-", "–", "—"];

    /// <summary>
    /// How much punctuation the grouper has separated from the text it belongs to.
    /// <para>
    /// Isolation on its own is not a defect - a mark can legitimately be the only thing on a line.
    /// What matters is <see cref="PdfPunctuationRow.SharingAVisualLineWithText"/>: an occurrence
    /// holding one mark while the rest of its visual line sits in another occurrence is a line the
    /// grouper took apart, and that is what destroys a heading boundary.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PdfPunctuationRow> PunctuationCensus(
        IReadOnlyList<PdfSemanticBlock> occurrences,
        IReadOnlyCollection<int> goldOccurrenceIndexes)
    {
        var linesOf = VisualLines(occurrences)
            .SelectMany(line => line.OccurrenceIndexes.Select(occurrence => (occurrence, line)))
            .ToLookup(pair => pair.occurrence, pair => pair.line);
        var gold = goldOccurrenceIndexes.ToHashSet();

        return Marks.Select(mark =>
        {
            var matching = occurrences
                .Select((occurrence, index) => (occurrence, index))
                .Where(item => item.occurrence.VerbatimText.Trim() == mark)
                .ToArray();
            return new PdfPunctuationRow(
                mark,
                matching.Length,
                matching.Count(item => linesOf[item.index].Any(line => line.SplitAcrossOccurrences)),
                matching.Count(item => gold.Contains(item.index + 1)));
        }).ToArray();
    }

    /// <summary>Positional alias names, the way the source catalog assigns them.</summary>
    public static string[] Aliases(int count) =>
        Enumerable.Range(1, count).Select(index => $"S{index:D4}").ToArray();

    /// <summary>
    /// The parser lines of <paramref name="occurrence"/> that carry <paramref name="goldText"/>.
    /// <para>
    /// Which lines a heading lands on is a harness fact, recovered here rather than asserted: a
    /// claim is attributed only to the lines it actually occupies, so a body line fused into the
    /// same occurrence cannot lend its defects to a heading that has none.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PdfLine> LinesCarrying(PdfSemanticBlock occurrence, string? goldText)
    {
        if (goldText is null) return occurrence.Lines;

        var start = occurrence.VerbatimText.IndexOf(goldText, StringComparison.Ordinal);
        if (start < 0) start = PunctuationInsensitiveStart(occurrence.VerbatimText, goldText);
        if (start < 0) return occurrence.Lines;
        var end = start + goldText.Length;

        var carried = new List<PdfLine>();
        var offset = 0;
        foreach (var line in occurrence.Lines)
        {
            var lineEnd = offset + line.Projection.VerbatimText.Length;
            if (offset < end && lineEnd > start) carried.Add(line);
            offset = lineEnd + Separator.Length;
        }

        return carried.Count > 0 ? carried : occurrence.Lines;
    }

    /// <summary>
    /// Where <paramref name="goldText"/> starts in <paramref name="text"/>, ignoring punctuation
    /// and case but reported as an offset into the original. Used when a candidate reconstruction
    /// has restored characters the Gold text was written without.
    /// </summary>
    private static int PunctuationInsensitiveStart(string text, string goldText)
    {
        var wanted = KeyWithoutPunctuation(goldText);
        if (wanted.Length == 0) return -1;

        var offsets = new List<int>(text.Length);
        var reduced = new System.Text.StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsWhiteSpace(character) || char.IsPunctuation(character)) continue;
            offsets.Add(index);
            reduced.Append(char.ToLowerInvariant(character));
        }

        var at = reduced.ToString().IndexOf(wanted, StringComparison.Ordinal);
        return at < 0 ? -1 : offsets[at];
    }

    /// <summary>
    /// How <see cref="PdfSemanticBlock.Projection"/> joins its lines. Asserted against the real
    /// occurrence text before the offsets above are trusted, rather than assumed.
    /// </summary>
    public const string Separator = " ";

    /// <summary>Whitespace-insensitive, case-insensitive identity. Punctuation is kept.</summary>
    public static string Key(string text) =>
        new(text.Where(character => !char.IsWhiteSpace(character)).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The same identity with punctuation removed, to separate it from a real difference.</summary>
    public static string KeyWithoutPunctuation(string text) =>
        new(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character))
            .Select(char.ToLowerInvariant).ToArray());
}
