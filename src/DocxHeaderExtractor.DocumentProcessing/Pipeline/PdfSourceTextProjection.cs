namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// One run of glyphs, and where it sits in both the raw parser string and the canonical one.
/// </summary>
internal sealed record PdfVerbatimSpanMapEntry(
    int VerbatimStart,
    int VerbatimLength,
    int RawStart,
    int RawLength,
    int GlyphOrdinal,
    int Page,
    double Left,
    double Right,
    double Bottom,
    double Top);

/// <summary>
/// The canonical text of a PDF source occurrence, and the way back to the glyphs it came from.
/// <para>
/// A PDF has no text. It has ordered glyphs with positions, and any string is a reconstruction. So
/// "verbatim" cannot mean the first string a parser happened to concatenate - on a real document
/// that string reads "M I N UTES OF TH E I NTE RNATIONAL", and a contract requiring the model to
/// echo an exact substring of it would be requiring it to echo that.
/// </para>
/// <para>
/// <see cref="VerbatimText"/> is therefore a declared projection: same ordered glyphs, with word
/// gaps decided by geometry alone. It repairs spacing and nothing else. No dictionary, no spelling
/// correction, no guessing at words - a wrong reconstruction stays a parser defect that
/// <see cref="SpanMap"/> can be audited against, rather than becoming a semantic error attributed
/// to the model.
/// </para>
/// <para>
/// <see cref="SpanMap"/> is what makes the projection safe to bind against. A span the binder finds
/// in <see cref="VerbatimText"/> maps back to the glyphs and their page positions, so the canonical
/// text never becomes a thing the system can only talk about and never point at.
/// </para>
/// </summary>
internal sealed record PdfSourceTextProjection(
    string RawParserText,
    string VerbatimText,
    IReadOnlyList<PdfVerbatimSpanMapEntry> SpanMap,
    string ProjectionVersion)
{
    /// <summary>Bump when the gap rule changes: a stored span is only meaningful under its own version.</summary>
    public const string CurrentVersion = "pdf-glyph-gap-v1";

    public static readonly PdfSourceTextProjection Empty =
        new(string.Empty, string.Empty, [], CurrentVersion);

    /// <summary>
    /// A text that is its own projection, with no glyph provenance.
    /// <para>
    /// For a line that did not come from glyph extraction - a test fixture, or any caller that
    /// constructs a line from a string - there is nothing to reconstruct and nothing to map back to.
    /// Saying that explicitly is better than an empty projection, which would silently erase the
    /// text of every such line. An empty span map is the honest signal that no glyph provenance
    /// exists, and <see cref="Resolve"/> returns nothing rather than a wrong box.
    /// </para>
    /// </summary>
    public static PdfSourceTextProjection Identity(string text) =>
        new(text, text, [], CurrentVersion);

    /// <summary>True when this projection carries glyph provenance rather than standing in for it.</summary>
    public bool HasGlyphProvenance => SpanMap.Count > 0;

    /// <summary>
    /// Where a canonical span came from. Returns every glyph run the span touches, so a caller can
    /// report the page and box of a heading the model identified purely by text.
    /// </summary>
    public IReadOnlyList<PdfVerbatimSpanMapEntry> Resolve(int start, int length)
    {
        if (length <= 0) return [];
        var end = start + length;
        return SpanMap
            .Where(entry => entry.VerbatimStart < end && entry.VerbatimStart + entry.VerbatimLength > start)
            .ToArray();
    }

    /// <summary>
    /// Joins occurrence projections into one, as a block joins its lines. Offsets are rebased onto
    /// the combined string; nothing is re-derived from text, so the mapping survives composition.
    /// </summary>
    public static PdfSourceTextProjection Join(IReadOnlyList<PdfSourceTextProjection> parts, string separator = " ")
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0) return Empty;
        if (parts.Count == 1) return parts[0];

        var verbatim = new System.Text.StringBuilder();
        var raw = new System.Text.StringBuilder();
        var map = new List<PdfVerbatimSpanMapEntry>();
        foreach (var part in parts)
        {
            if (verbatim.Length > 0)
            {
                verbatim.Append(separator);
                raw.Append(separator);
            }

            var verbatimOffset = verbatim.Length;
            var rawOffset = raw.Length;
            foreach (var entry in part.SpanMap)
                map.Add(entry with
                {
                    VerbatimStart = entry.VerbatimStart + verbatimOffset,
                    RawStart = entry.RawStart + rawOffset,
                });
            verbatim.Append(part.VerbatimText);
            raw.Append(part.RawParserText);
        }

        return new PdfSourceTextProjection(raw.ToString(), verbatim.ToString(), map, CurrentVersion);
    }
}
