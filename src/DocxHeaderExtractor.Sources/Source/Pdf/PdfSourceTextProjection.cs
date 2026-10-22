using MessagePack;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

/// <summary>
/// The canonical text of a PDF source occurrence.
/// <para>
/// A PDF has no text. It has ordered glyphs with positions, and any string is a reconstruction. So
/// "verbatim" cannot mean the first string a parser happened to concatenate - on a real document
/// that string reads "M I N UTES OF TH E I NTE RNATIONAL", and a contract requiring the model to
/// echo an exact substring of it would be requiring it to echo that.
/// </para>
/// <para>
/// <see cref="VerbatimText"/> is therefore a declared projection: same ordered glyphs, with word
/// gaps decided by geometry alone. It repairs spacing and nothing else. No dictionary, no spelling
/// correction, no guessing at words - a wrong reconstruction stays a parser defect, rather than
/// becoming a semantic error attributed to the model.
/// </para>
/// </summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record PdfSourceTextProjection(
    [property: Key(0)] string RawParserText,
    [property: Key(1)] string VerbatimText,
    [property: Key(2)] string ProjectionVersion)
{
    /// <summary>Bump when the gap rule changes: a stored text is only meaningful under its own version.</summary>
    public const string CurrentVersion = "pdf-glyph-gap-v1";

    public static readonly PdfSourceTextProjection Empty =
        new(string.Empty, string.Empty, CurrentVersion);

    /// <summary>
    /// A text that is its own projection. For a line that did not come from glyph extraction - a test fixture, or any caller that
    /// constructs a line from a string - there is nothing to reconstruct, so the text is kept as it is rather than blanked.
    /// </summary>
    public static PdfSourceTextProjection Identity(string text) =>
        new(text, text, CurrentVersion);

    /// <summary>
    /// Joins occurrence projections into one, as a block joins its lines. The verbatim and raw texts are concatenated in order.
    /// </summary>
    public static PdfSourceTextProjection Join(IReadOnlyList<PdfSourceTextProjection> parts, string separator = " ")
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0) return Empty;
        if (parts.Count == 1) return parts[0];

        var verbatim = new System.Text.StringBuilder();
        var raw = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (verbatim.Length > 0)
            {
                verbatim.Append(separator);
                raw.Append(separator);
            }

            verbatim.Append(part.VerbatimText);
            raw.Append(part.RawParserText);
        }

        return new PdfSourceTextProjection(raw.ToString(), verbatim.ToString(), CurrentVersion);
    }
}