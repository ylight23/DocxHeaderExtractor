using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Core.Pipeline;

internal sealed record PdfTocDictionaryOutlineResult(
    bool Accepted,
    IReadOnlyList<HeadingRecord> Headings,
    string Reason,
    PdfTocDictionaryProbeResult Probe)
{
    public static PdfTocDictionaryOutlineResult NotApplicable(string reason) =>
        new(false, [], reason, PdfTocDictionaryProbeResult.Empty);
}

/// <summary>
/// PDF route for documents whose own table of contents gives a clean page-level outline.
/// The PDF TOC is the title dictionary; DOCX/body text is only used for stable anchors/spans.
/// </summary>
internal static class PdfTocDictionaryOutline
{
    public const string Basis = "pdf_toc_dictionary";

    private static readonly Regex WhitespaceRx = new(@"\s+", RegexOptions.Compiled);

    public static PdfTocDictionaryOutlineResult TryBuild(
        string originalInputPath,
        IReadOnlyList<IPolicyParagraph> paragraphs,
        DocumentModeReport mode)
    {
        var pdf = PdfTextbookOutline.FindSiblingPdf(originalInputPath);
        if (pdf is null)
            return PdfTocDictionaryOutlineResult.NotApplicable("no-pdf");

        IReadOnlyList<PdfLine> lines;
        try
        {
            using var doc = PdfDocument.Open(pdf);
            lines = PdfLineExtraction.ExtractLines(doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return PdfTocDictionaryOutlineResult.NotApplicable("pdf-read-failed");
        }

        var probe = PdfTocDictionaryProbe.Analyze(lines);
        if (probe.Entries < 8)
            return new PdfTocDictionaryOutlineResult(false, [], "no-strong-pdf-toc", probe);

        var pdfAnchorRatio = probe.RelaxedPageAnchors / (double)probe.Entries;
        if (pdfAnchorRatio < 0.85)
            return new PdfTocDictionaryOutlineResult(false, [], $"low-pdf-page-anchor-ratio:{probe.RelaxedPageAnchors}/{probe.Entries}", probe);

        var headings = AlignToDocx(probe.Items, paragraphs);
        var ratio = headings.Count / (double)probe.Entries;
        if (headings.Count < 8 || ratio < 0.70)
            return new PdfTocDictionaryOutlineResult(false, headings, $"low-docx-alignment:{headings.Count}/{probe.Entries}", probe);

        return new PdfTocDictionaryOutlineResult(
            true,
            headings,
            $"pdf={Path.GetFileName(pdf)}, toc={probe.Entries}, pageAnchors={probe.RelaxedPageAnchors}, docxAligned={headings.Count}",
            probe);
    }

    private static List<HeadingRecord> AlignToDocx(
        IReadOnlyList<PdfTocDictionaryEntry> entries,
        IReadOnlyList<IPolicyParagraph> paragraphs)
    {
        var sourceParagraphs = paragraphs
            .Where(p => p.Role != ParagraphRole.Empty &&
                        !p.InTableOfContents &&
                        !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Index)
            .Select(p => new CanonParagraph(p, CanonicalMap(p.Text)))
            .Where(p => p.Map.Canonical.Length > 0)
            .ToList();

        var result = new List<HeadingRecord>();
        var cursor = 0;
        foreach (var entry in entries)
        {
            var match = FindAnchor(sourceParagraphs, entry, cursor);
            if (match is null) continue;

            var title = CleanTitle(entry.Title);
            result.Add(new HeadingRecord
            {
                Index = match.Value.Paragraph.Index,
                StableId = match.Value.Paragraph.StableId,
                Level = 1,
                Text = title,
                OriginalText = match.Value.Paragraph.Text,
                HeadingSpan = new TextOffsetSpan(match.Value.Start, match.Value.End),
                BoundarySource = "PdfTocDictionary",
                StyleId = match.Value.Paragraph.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0.98,
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                ConfidenceBasis = Basis,
            });
            cursor = match.Value.Paragraph.Index;
        }

        return result;
    }

    private static MatchResult? FindAnchor(
        IReadOnlyList<CanonParagraph> paragraphs,
        PdfTocDictionaryEntry entry,
        int minIndex)
    {
        foreach (var needle in AnchorVariants(entry.Title).Select(Canon).Where(s => s.Length >= 6).Distinct())
        {
            foreach (var p in paragraphs.Where(p => p.Paragraph.Index >= minIndex))
            {
                var at = p.Map.Canonical.IndexOf(needle, StringComparison.Ordinal);
                if (at < 0) continue;

                var start = p.Map.SourceIndexes[at];
                var end = p.Map.SourceIndexes[at + needle.Length - 1] + 1;
                if (LooksLikeTocOccurrence(p.Paragraph.Text, end)) continue;
                return new MatchResult(p.Paragraph, start, end);
            }
        }

        return null;
    }

    private static IEnumerable<string> AnchorVariants(string title)
    {
        yield return title;

        var navigation = Regex.Match(title, @"^\s*[\p{L}\s]{3,40}?\s+(?:to|of|about|về)\s+(?<tail>.+)$",
            RegexOptions.IgnoreCase);
        if (navigation.Success)
            yield return navigation.Groups["tail"].Value.Trim();

        var parts = Regex.Split(title, @"\s*(?:—|–|-|:)\s+");
        if (parts.Length >= 2)
        {
            var prefix = parts[0].Trim();
            if (prefix.Length >= 6 && Regex.Matches(prefix, @"\p{L}+").Count >= 2)
                yield return prefix;
        }
    }

    private static bool LooksLikeTocOccurrence(string paragraphText, int titleEnd)
    {
        if (titleEnd >= paragraphText.Length) return false;
        var tail = paragraphText[titleEnd..];
        return Regex.IsMatch(tail, @"^\s*(?:\.{3,}|\d{1,3}\s+(?:[A-Z][\p{L}'’-]+|\d{1,2}\.?))");
    }

    private static CanonMap CanonicalMap(string text)
    {
        var canonical = new System.Text.StringBuilder(text.Length);
        var indexes = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c)) continue;
            canonical.Append(char.ToLowerInvariant(c));
            indexes.Add(i);
        }

        return new CanonMap(canonical.ToString(), indexes);
    }

    private static string Canon(string text) => PdfTextUtilities.CanonicalForMatch(text);

    private static string CleanTitle(string text) =>
        WhitespaceRx.Replace(PdfTextUtilities.HeadingReadable(text), " ").Trim(' ', '.');

    private sealed record CanonMap(string Canonical, IReadOnlyList<int> SourceIndexes);
    private sealed record CanonParagraph(IPolicyParagraph Paragraph, CanonMap Map);
    private readonly record struct MatchResult(IPolicyParagraph Paragraph, int Start, int End);
}
