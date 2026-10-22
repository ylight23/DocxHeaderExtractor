using System.Text.RegularExpressions;
using DocxHeaderExtractor.Sources.Pdf;
using DocxHeaderExtractor.Sources.Snapshot;

namespace DocxHeaderExtractor.Sources.Markers;

/// <summary>
/// Reads a loosely labelled marker - a word followed by a number, as in "Điều 3", "Article 12",
/// "Chapter IV" - and reduces it to a comparable signature.
/// <para>
/// This is parser evidence about shape, never a claim that the text is a heading. It survived the
/// removal of the old PDF lane because the live DOCX path uses it for semantic source context
/// and marker hierarchy; leaving it inside a deleted strategy would have made those consumers
/// depend on a file that no longer had a reason to exist.
/// </para>
/// </summary>
internal static class LooseMarkerParser
{
    // Compiled once: the same pattern the static call used.
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

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
        var numeral = WhitespaceRun.Replace(match.Groups[2].Value, string.Empty).ToLowerInvariant();
        return label.Length == 0 || numeral.Length == 0 ? null : $"{label}:{numeral}";
    }
}
