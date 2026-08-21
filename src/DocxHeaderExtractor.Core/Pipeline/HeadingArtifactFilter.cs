using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

internal sealed record HeadingArtifactFilterResult(
    int Removed,
    IReadOnlyDictionary<string, int> Reasons);

/// <summary>
/// Removes outline rows that are not corrupt source text, but layout artifacts accidentally promoted
/// as headings: dot-leader TOC rows, fill-in-the-blank form labels, and pure decorative filler.
/// This is deliberately separate from <see cref="CorruptParagraphDetector"/>: the source text is valid,
/// the heading decision is the artifact.
/// </summary>
internal static class HeadingArtifactFilter
{
    private static readonly Regex LongDotLeaderRx = new(@"(?:\.\s*){6,}|(?:…\s*){4,}", RegexOptions.Compiled);
    private static readonly Regex LongUnderlineRx = new(@"_{8,}", RegexOptions.Compiled);
    private static readonly Regex TableOfContentsRx = new(@"\b(?:table\s+of\s+contents?|contents?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TocPageRunRx = new(
        @"(?:\.|…){4,}\s*\d{1,4}(?:\s+[A-Z][\p{L}'’()\-/]+|\s+\d{1,2}\.?)",
        RegexOptions.Compiled);
    private static readonly Regex WordRx = new(@"\p{L}+", RegexOptions.Compiled);

    public static HeadingArtifactFilterResult Apply(IList<HeadingRecord> headings, SlimDocument document)
    {
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var before = headings.Count;
        for (var i = headings.Count - 1; i >= 0; i--)
        {
            var heading = headings[i];
            var paragraph = document.ByIndex(heading.Index);
            if (!ShouldRemove(heading, paragraph, out var reason)) continue;
            headings.RemoveAt(i);
            reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        }

        return new HeadingArtifactFilterResult(before - headings.Count, reasons);
    }

    internal static bool ShouldRemove(HeadingRecord heading, SlimParagraph? paragraph, out string reason)
    {
        reason = "";
        if (IsEvidenceBackedTitleSource(heading)) return false;

        var text = Normalize(heading.Text);
        var source = Normalize(heading.OriginalText ?? paragraph?.Text ?? text);
        if (text.Length == 0) return false;

        if (LooksLikePureFiller(text))
        {
            reason = "pure-filler";
            return true;
        }

        if (LooksLikeFormFillHeading(text))
        {
            reason = "form-fill-heading";
            return true;
        }

        if (LooksLikeTocBlob(text) || LooksLikeTocBlob(source) && HeadingIsLongOrFillerHeavy(text))
        {
            reason = "toc-blob";
            return true;
        }

        return false;
    }

    private static bool IsEvidenceBackedTitleSource(HeadingRecord heading) =>
        heading.Source == HeadingSource.HumanCorrection ||
        heading.ConfidenceBasis == PdfTocDictionaryOutline.Basis ||
        // Tagged PDF titles have already been grounded to a PDF line and a DOCX span.
        // A text-shape heuristic has no stronger evidence with which to remove them.
        heading.ConfidenceBasis == PdfTaggedEvidenceOutline.Basis ||
        heading.ConfidenceBasis == PdfFinancialReportOutline.Basis ||
        heading.ConfidenceBasis == RfcTocDictionaryOutline.Basis ||
        heading.ConfidenceBasis == BookTocDictionaryOutline.Basis ||
        heading.ConfidenceBasis == PartSectionOutline.Basis;

    private static bool LooksLikePureFiller(string text)
    {
        var compact = Regex.Replace(text, @"\s+", "");
        if (compact.Length < 8) return false;
        var meaningful = compact.Count(char.IsLetterOrDigit);
        var filler = compact.Count(IsFillerChar);
        return meaningful == 0 && filler >= 8 ||
               filler >= 16 && filler / (double)compact.Length >= 0.85 && meaningful <= 2;
    }

    private static bool LooksLikeFormFillHeading(string text)
    {
        var filler = FillerCount(text);
        if (filler < 8) return false;

        var words = WordRx.Matches(text).Count;
        var letters = text.Count(char.IsLetter);
        var nonSpace = text.Count(c => !char.IsWhiteSpace(c));
        if (nonSpace == 0) return false;

        var fillerRatio = filler / (double)nonSpace;
        var hasLongFill = LongUnderlineRx.IsMatch(text) || LongDotLeaderRx.IsMatch(text);
        return hasLongFill &&
               (fillerRatio >= 0.35 && words <= 8 ||
                fillerRatio >= 0.55 && letters <= 60);
    }

    private static bool LooksLikeTocBlob(string text)
    {
        if (!LongDotLeaderRx.IsMatch(text)) return false;
        if (TableOfContentsRx.IsMatch(text)) return true;
        if (TocPageRunRx.Matches(text).Count >= 2) return true;
        return text.Length > 120 && TocPageRunRx.IsMatch(text);
    }

    private static bool HeadingIsLongOrFillerHeavy(string text) =>
        text.Length > 90 || FillerCount(text) >= 12;

    private static int FillerCount(string text) => text.Count(IsFillerChar);

    private static bool IsFillerChar(char c) => c is '_' or '.' or '…' or '·' or '─' or '━';

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
