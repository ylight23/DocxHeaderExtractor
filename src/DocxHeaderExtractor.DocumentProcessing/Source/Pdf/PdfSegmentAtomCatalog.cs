using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

/// <summary>
/// The coordinate atoms of a PDF: its visual-line segments, addressed by where they sit.
/// <para>
/// An alias here says which row and which part of it - <c>L0616:S0</c> - rather than a running
/// number. That is deliberate. A positional number is only meaningful in the universe that issued
/// it, and this work has already been caught once resolving a Gold alias against a universe that
/// had renumbered everything. Row and segment survive a rebuild of the document around them, and an
/// artifact naming them can be read without a mapping table.
/// </para>
/// <para>
/// Rows are recovered from the segments rather than passed alongside them: segments are emitted row
/// by row and left to right, so a segment starting to the right of the one before it on an
/// overlapping band is the next piece of the same row.
/// </para>
/// </summary>
internal static class PdfSegmentAtomCatalog
{
    public static IReadOnlyList<SemanticSourceAtom> FromSegments(IReadOnlyList<PdfLine> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var atoms = new List<SemanticSourceAtom>(segments.Count);
        var row = 0;
        var segment = 0;
        PdfLine? previous = null;

        foreach (var line in segments)
        {
            if (previous is not null)
            {
                if (Beside(previous, line)) segment++;
                else
                {
                    row++;
                    segment = 0;
                }
            }

            atoms.Add(new SemanticSourceAtom(
                $"L{row:0000}:S{segment}",
                PdfLineIdentity.Of(line),
                atoms.Count,
                line.Page,
                row,
                segment,
                line.Projection.VerbatimText));
            previous = line;
        }

        return atoms;
    }

    /// <summary>Two segments of one row: to the right, on a band that overlaps.</summary>
    public static bool Beside(PdfLine previous, PdfLine next) =>
        previous.Page == next.Page &&
        next.Left > previous.Right &&
        next.Top > previous.Bottom && next.Bottom < previous.Top;
}
