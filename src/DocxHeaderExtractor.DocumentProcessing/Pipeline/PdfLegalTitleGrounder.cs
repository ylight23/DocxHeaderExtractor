using UglyToad.PdfPig;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Uses a PDF line carrying the same labelled marker to ground the title boundary of a legal
/// heading. The PDF establishes where the title ends; the displayed text remains the matching
/// DOCX source slice so writeback offsets stay valid.
/// </summary>
internal static class PdfLegalTitleGrounder
{
    // PDF producers often omit the punctuation after a labelled marker ("Dieu 1 Title").
    // This relaxed parser is used only after LegalStructuredOutline has established the document
    // as a labelled legal hierarchy; it is never a general heading detector.
    private static readonly Regex BareLabelledMarkerRx = new(
        @"^\s*(\p{L}{2,12})\s+(\d{1,3}|[IVXLCDM]{1,7})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static int Apply(string inputPath, IList<HeadingRecord> headings)
    {
        var pdf = PdfTextbookOutline.FindSiblingPdf(inputPath);
        if (pdf is null) return 0;

        IReadOnlyList<PdfLine> lines;
        try
        {
            using var document = PdfDocument.Open(pdf);
            lines = PdfLineExtraction.ExtractLines(document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return 0;
        }

        // A legal title can wrap across visual PDF lines. Group only lines sharing a visual
        // family before matching, so title wrapping is restored without absorbing body prose.
        var titles = PdfSemanticBlockGrouper.Build(
            PdfLineBlockFilter.Analyze(lines),
            maxLinesPerBlock: 12,
            allowSemicolonContinuation: true)
            .Select(block => new { Block = block, Marker = ParseLabelledMarker(block.Text), Canonical = block.CanonicalText })
            .Where(x => x.Marker is not null && x.Canonical.Length >= 5)
            .ToList();
        var changed = 0;

        foreach (var heading in headings.Where(h => h.ConfidenceBasis == "legal_marker_declared"))
        {
            var marker = ParseLabelledMarker(heading.Text);
            if (marker is null) continue;
            var sourceCanonical = PdfTextUtilities.CanonicalForMatch(heading.Text);
            var pdfTitle = titles
                .Where(x => SameMarker(marker, x.Marker!) && HasTitleBeyondMarker(x.Block.Text) &&
                            sourceCanonical.StartsWith(x.Canonical, StringComparison.Ordinal))
                .OrderByDescending(x => x.Canonical.Length)
                .Select(x => x.Canonical)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(pdfTitle) || pdfTitle.Length >= sourceCanonical.Length) continue;

            var end = SourceEndForCanonicalPrefix(heading.Text, pdfTitle.Length);
            if (end <= 0 || end >= heading.Text.Length) continue;
            var source = heading.Text;
            var bodyStart = end;
            while (bodyStart < source.Length && char.IsWhiteSpace(source[bodyStart])) bodyStart++;
            heading.OriginalText = source;
            heading.Text = source[..end].TrimEnd();
            heading.HeadingSpan = new TextOffsetSpan(0, end);
            heading.InlineBody = bodyStart < source.Length ? source[bodyStart..] : null;
            heading.InlineBodySpan = bodyStart < source.Length
                ? new TextOffsetSpan(bodyStart, source.Length)
                : null;
            heading.BoundarySource = "PdfLegalTitleLine";
            changed++;
        }

        return changed;
    }

    private static LabelledMarker? ParseLabelledMarker(string text)
    {
        if (NumberingAudit.Parse(text) is { Kind: NumberKind.Labelled } parsed)
            return new(parsed.Label, parsed.Value);

        var match = BareLabelledMarkerRx.Match(text);
        if (!match.Success) return null;
        var value = int.TryParse(match.Groups[2].Value, out var arabic)
            ? arabic
            : RomanValue(match.Groups[2].Value);
        return value > 0 ? new(match.Groups[1].Value, value) : null;
    }

    private static bool SameMarker(LabelledMarker expected, LabelledMarker candidate) =>
        expected.Value == candidate.Value &&
        string.Equals(expected.Label, candidate.Label, StringComparison.OrdinalIgnoreCase);

    private static bool HasTitleBeyondMarker(string text)
    {
        var match = BareLabelledMarkerRx.Match(text);
        return match.Success && PdfTextUtilities.CanonicalForMatch(text[match.Length..]).Length >= 3;
    }

    private static int RomanValue(string text)
    {
        var values = new Dictionary<char, int>
        {
            ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50,
            ['C'] = 100, ['D'] = 500, ['M'] = 1000,
        };
        var total = 0;
        var previous = 0;
        foreach (var character in text.ToUpperInvariant().Reverse())
        {
            if (!values.TryGetValue(character, out var current)) return 0;
            total += current < previous ? -current : current;
            previous = Math.Max(previous, current);
        }
        return total;
    }

    private sealed record LabelledMarker(string Label, int Value);

    private static int SourceEndForCanonicalPrefix(string source, int canonicalLength)
    {
        var seen = 0;
        for (var index = 0; index < source.Length; index++)
        {
            if (!char.IsLetterOrDigit(source[index])) continue;
            seen++;
            if (seen == canonicalLength) return index + 1;
        }
        return 0;
    }
}
