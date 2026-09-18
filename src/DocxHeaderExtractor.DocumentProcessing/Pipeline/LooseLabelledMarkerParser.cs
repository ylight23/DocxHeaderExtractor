using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Reads a loosely labelled marker - a word followed by a number, as in "Điều 3", "Article 12",
/// "Chapter IV" - and reduces it to a comparable signature.
/// <para>
/// This is parser evidence about shape, never a claim that the text is a heading. It survived the
/// removal of the legacy PDF lane because the live DOCX path uses it for candidate context,
/// ranking and marker hierarchy; leaving it inside a deleted strategy would have made those three
/// depend on a file that no longer had a reason to exist.
/// </para>
/// </summary>
internal static class LooseLabelledMarkerParser
{
    private static readonly Regex LooseLabelledMarkerRx = new(
        @"^\s*(\p{L}{2,24})\s+((?:\d\s*){1,3}|[IVXLCDM]{1,7})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The canonical "label:numeral" signature, or null when the text carries no marker.</summary>
    internal static string? ParseCanonical(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = LooseLabelledMarkerRx.Match(text);
        if (!match.Success) return null;

        var label = PdfTextUtilities.CanonicalForMatch(match.Groups[1].Value);
        var numeral = Regex.Replace(match.Groups[2].Value, @"\s+", string.Empty).ToLowerInvariant();
        return label.Length == 0 || numeral.Length == 0 ? null : $"{label}:{numeral}";
    }
}
