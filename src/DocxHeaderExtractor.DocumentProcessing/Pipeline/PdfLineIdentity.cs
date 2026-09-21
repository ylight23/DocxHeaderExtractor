namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// A stable identity for one parser line, so a block can say which lines it was built from and an
/// audit can correlate the two without comparing text.
/// <para>
/// Page, position and text together, because none of them identifies a line alone: two lines can
/// share text on different pages, and a page can hold repeated text at different positions. Text
/// comparison in particular would merge a running header with the heading it sits above.
/// </para>
/// <para>
/// Extracted when the legacy PDF lane was deleted. It was the only part of that lane's provenance
/// record the canonical PDF lane still needed - the rest described candidate windows, which the
/// source-universe design no longer has.
/// </para>
/// </summary>
internal static class PdfLineIdentity
{
    public static string Of(PdfLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{line.Page}|{line.Y:R}|{line.Left:R}|{line.Right:R}|{line.Text}");
    }
}
