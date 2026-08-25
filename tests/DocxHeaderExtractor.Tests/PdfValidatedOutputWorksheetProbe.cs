using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Lane C: reusable precision-review worksheet, formalizing the by-hand inspection this session did
/// for 054 and 092's validated outputs (25/27 and 16/31 respectively - see TODO.md) into tooling, so
/// B4 does not re-invent it per document. For every validated item in a frozen artifact, emits page,
/// full <c>sourceBlockText</c> (not the possibly-truncated <c>headingText</c> span - the false positive
/// in 054's "Level 1 :" items was only visible in the full block text) and structural context, with a
/// blank slot for the reviewer's <see cref="Labels"/> classification.
/// <para>
/// Precision review is not blind the way Lane A's recall packets are: the reviewer is judging the
/// model's specific claim, so seeing what it claimed is the point. What stays constant with the recall
/// side is occurrence identity - page and lineId, never a candidate id, never text alone.
/// </para>
/// </summary>
public sealed class PdfValidatedOutputWorksheetProbe
{
    internal const string Labels = "TRUE_HEADING | TOC_ENTRY | MULTI_HEADING_COMPOSITE | NON_HEADING | UNCERTAIN";

    [Fact]
    public void Report()
    {
        var artifactPath = Environment.GetEnvironmentVariable("BENCH_WORKSHEET_ARTIFACT");
        var output = Environment.GetEnvironmentVariable("BENCH_WORKSHEET_REPORT");
        if (string.IsNullOrWhiteSpace(artifactPath) || string.IsNullOrWhiteSpace(output)) return;

        using var document = JsonDocument.Parse(File.ReadAllText(artifactPath));
        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var file = row.GetProperty("file").GetString();
            var items = row.GetProperty("items").EnumerateArray().ToList();
            Line($"WORKSHEET - {file} - {items.Count} validated items - label each: {Labels}");
            Line(new string('-', 100));

            foreach (var item in items)
            {
                var factId = item.GetProperty("factId").GetString();
                var sourceFactId = item.GetProperty("sourceFactId").GetString();
                var page = item.GetProperty("page").GetInt32();
                var scope = item.GetProperty("structuralScope").GetString();
                var markerFamily = item.TryGetProperty("markerFamily", out var mf) ? mf.GetString() : null;
                var headingText = item.GetProperty("headingText").GetString();
                var fullText = item.GetProperty("sourceBlockText").GetString();

                Line($"[{sourceFactId}] page={page} scope={scope} markerFamily={markerFamily ?? "-"}");
                Line($"      headingText (validated span): {headingText}");
                Line($"      sourceBlockText (full)      : {fullText}");
                Line($"      LABEL: ");
            }
            Line("");
        }

        File.WriteAllText(output, report.ToString());
    }
}
