using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Finds repeated structural labels such as "Day 1:", "Part 2:", or their language-specific
/// equivalents. A marker is evidence only when the same text label forms a consecutive numeric run;
/// an isolated numbered line is deliberately ignored to avoid treating table rows as headings.
/// </summary>
public static class PdfRepeatedLabelMarkerProbe
{
    private static readonly Regex LabelNumber = new(
        @"^\s*(?<label>\p{L}(?:[\p{L}\s-]{0,30}\p{L})?)\s+(?<number>\d{1,3})\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static PdfRepeatedLabelMarkerReport Analyze(string pdfPath)
    {
        try
        {
            using var document = PdfDocument.Open(pdfPath);
            var raw = PdfLineExtraction.ExtractLines(document)
                .Select(line => (Line: line, Match: LabelNumber.Match(line.Text)))
                .Where(item => item.Match.Success)
                .Select(item => new RawMarker(
                    item.Line.Page,
                    item.Line.Y,
                    Normalize(item.Match.Groups["label"].Value),
                    item.Match.Groups["number"].Value is var value && int.TryParse(value, out var number) ? number : 0,
                    item.Line.Text))
                .Where(marker => marker.Number > 0)
                .ToList();

            var series = raw
                .GroupBy(marker => marker.Label, StringComparer.Ordinal)
                .SelectMany(group => ConsecutiveRuns(group.OrderBy(marker => marker.Number).ThenBy(marker => marker.Page).ToList())
                    .Select(run => new PdfRepeatedLabelMarkerSeries(
                        group.Key,
                        run.Select(marker => new PdfRepeatedLabelMarker(
                            marker.Page, marker.Y, marker.Number, marker.Text)).ToList())))
                .ToList();

            return new PdfRepeatedLabelMarkerReport(pdfPath, "ok", series);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new PdfRepeatedLabelMarkerReport(pdfPath, $"pdf-read-failed:{ex.GetType().Name}", []);
        }
    }

    private static IEnumerable<IReadOnlyList<RawMarker>> ConsecutiveRuns(IReadOnlyList<RawMarker> markers)
    {
        var run = new List<RawMarker>();
        foreach (var marker in markers)
        {
            if (run.Count == 0 || marker.Number == run[^1].Number + 1) run.Add(marker);
            else
            {
                if (run.Count >= 2) yield return run;
                run = [marker];
            }
        }
        if (run.Count >= 2) yield return run;
    }

    private static string Normalize(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();

    private sealed record RawMarker(int Page, double Y, string Label, int Number, string Text);
}

public sealed record PdfRepeatedLabelMarkerReport(
    string Pdf,
    string Status,
    IReadOnlyList<PdfRepeatedLabelMarkerSeries> Series);

public sealed record PdfRepeatedLabelMarkerSeries(
    string Label,
    IReadOnlyList<PdfRepeatedLabelMarker> Markers);

public sealed record PdfRepeatedLabelMarker(
    int Page,
    double Y,
    int Number,
    string Text);
