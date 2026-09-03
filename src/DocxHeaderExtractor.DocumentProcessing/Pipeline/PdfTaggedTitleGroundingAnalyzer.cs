using UglyToad.PdfPig;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Refines broad /H* marked-content text to the first matching visual line on the same PDF page.
/// This is an audit probe: /H* supplies structure, while the line supplies the display title.
/// </summary>
public static class PdfTaggedTitleGroundingAnalyzer
{
    public static PdfTaggedTitleGroundingReport Analyze(string pdfPath, PdfTaggedHeadingAnalyzerReport tags)
    {
        try
        {
            using var document = PdfDocument.Open(pdfPath);
            var linesByPage = PdfLineExtraction.ExtractLines(document)
                .GroupBy(line => line.Page)
                .ToDictionary(group => group.Key, group => group.ToList());
            var candidates = tags.Candidates.Select(tag => Ground(tag, linesByPage)).ToList();
            return new PdfTaggedTitleGroundingReport(pdfPath, "ok", candidates);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new PdfTaggedTitleGroundingReport(pdfPath, $"pdf-read-failed:{ex.GetType().Name}", []);
        }
    }

    private static PdfTaggedTitleGroundingCandidate Ground(
        PdfTaggedHeadingCandidate tag,
        IReadOnlyDictionary<int, List<PdfLine>> linesByPage)
    {
        if (!linesByPage.TryGetValue(tag.Page, out var pageLines))
            return new PdfTaggedTitleGroundingCandidate(tag.Page, tag.MarkedContentIdentifier, tag.Text, null, "no-page-lines");

        var structural = tag.CanonicalText;
        var matches = pageLines
            .Select(line => new { Line = line, Canonical = PdfTextUtilities.CanonicalForMatch(line.Text) })
            .Where(item => item.Canonical.Length >= 3 && structural.StartsWith(item.Canonical, StringComparison.Ordinal))
            .OrderByDescending(item => item.Canonical.Length)
            .ThenByDescending(item => item.Line.Y)
            .ToList();
        if (matches.Count == 0)
            return new PdfTaggedTitleGroundingCandidate(tag.Page, tag.MarkedContentIdentifier, tag.Text, null, "no-prefix-line");

        var chosen = PdfTextUtilities.HeadingReadable(matches[0].Line.Text);
        return new PdfTaggedTitleGroundingCandidate(tag.Page, tag.MarkedContentIdentifier, tag.Text, chosen, "prefix-line");
    }
}

public sealed record PdfTaggedTitleGroundingReport(
    string Pdf,
    string Status,
    IReadOnlyList<PdfTaggedTitleGroundingCandidate> Candidates);

public sealed record PdfTaggedTitleGroundingCandidate(
    int Page,
    int Mcid,
    string StructuralText,
    string? GroundedTitle,
    string Reason);
