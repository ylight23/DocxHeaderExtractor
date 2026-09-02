using System.Globalization;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Core.Pipeline;

public sealed record PdfTextbookOutlineResult(
    StructuralAuthorityResult Authority,
    string Reason,
    RouteExecutionAudit? Audit = null)
{
    /// <summary>
    /// The PDF-specific validated facts retained alongside the generic authority while the
    /// product serializer is still PDF-aware.
    /// </summary>
    public PdfFinalStructure? FinalStructure { get; init; }

    /// <summary>Parser-owned PDF source inventory used by generic sections and chunks.</summary>
    public DocumentSourceCatalog? SourceCatalog { get; init; }

    public IReadOnlyList<PdfOutputDecision> OutputDecisions { get; init; } = [];

    public IReadOnlyList<Task> DetachedTasks { get; init; } = [];

    public static PdfTextbookOutlineResult NotApplicable(string reason) => new(
        new StructuralAuthorityResult(new ValidatedStructure([]), null, reason), reason)
    {
        SourceCatalog = new DocumentSourceCatalog([]),
    };
}

/// <summary>
/// Fallback hẹp cho typed textbook PDF→DOCX text-layout.
/// <para>
/// PDF chỉ cung cấp tín hiệu layout để chọn occurrence/title boundary; kết quả vẫn align ngược về
/// paragraph native của DOCX để evaluator/writeback không mất neo OOXML.
/// </para>
/// </summary>
public static class PdfTextbookOutline
{
    private static readonly Regex TypedSectionRx = new(@"^\s*\d+(?:\.\d+){1,5}\s+\S", RegexOptions.Compiled);
    private static readonly Regex ChapterNumberRx = new(@"^\s*\d{1,2}\s*$", RegexOptions.Compiled);
    private static readonly Regex NumberedChapterLineRx = new(@"^\s*\d{1,2}\s+\D", RegexOptions.Compiled);
    private static readonly Regex ExcludedTitleRx = new(
        @"^(?:contents|preface|chapter outline|introduction|learning outcomes?|assessment questions|endnotes|answer key|index)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumRx = new(@"[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRx = new(@"\s+", RegexOptions.Compiled);

    public static PdfCompatibilityHeadingOracle TryBuild(
        string originalInputPath,
        IReadOnlyList<IPolicyParagraph> paragraphs,
        DocumentModeReport mode)
    {
        if (DocumentStructureEvidence.HasNativeSemanticStructure(paragraphs))
            return new PdfCompatibilityHeadingOracle([], "docx-structure-present");

        var pdf = FindSiblingPdf(originalInputPath);
        if (pdf is null)
            return new PdfCompatibilityHeadingOracle([], "no-pdf");

        IReadOnlyList<PdfLine> lines;
        try
        {
            using var doc = PdfDocument.Open(pdf);
            lines = PdfLineExtraction.ExtractLines(doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new PdfCompatibilityHeadingOracle([], "pdf-read-failed");
        }

        if (!IsFontStrong(lines, out var bodyFont))
            return new PdfCompatibilityHeadingOracle([], "pdf-not-font-strong");

        var pdfHeadings = DetectTextbookHeadings(lines, bodyFont);
        if (pdfHeadings.Count < 10)
            return new PdfCompatibilityHeadingOracle([], $"too-few-pdf-headings:{pdfHeadings.Count}");

        var aligned = AlignToDocx(pdfHeadings, paragraphs);
        if (aligned.Count < Math.Max(10, (int)Math.Ceiling(pdfHeadings.Count * 0.60)))
            return new PdfCompatibilityHeadingOracle([], $"low-docx-alignment:{aligned.Count}/{pdfHeadings.Count}");

        return new PdfCompatibilityHeadingOracle(aligned, $"pdf={Path.GetFileName(pdf)}, bodyFs={bodyFont.ToString("F1", CultureInfo.InvariantCulture)}, aligned={aligned.Count}/{pdfHeadings.Count}");
    }

    public static string? FindSiblingPdf(string inputPath)
    {
        var direct = Path.ChangeExtension(inputPath, ".pdf");
        return File.Exists(direct) ? direct : null;
    }

    private static bool IsFontStrong(IReadOnlyList<PdfLine> lines, out double bodyFont)
    {
        bodyFont = 0;
        if (lines.Count == 0) return false;

        var groups = lines
            .GroupBy(l => Math.Round(l.FontSize, 1))
            .Select(g => new { Font = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ToList();
        bodyFont = groups[0].Font;
        if (groups[0].Count / (double)lines.Count >= 0.98) return false;

        var minSig = Math.Max(5, (int)Math.Ceiling(lines.Count * 0.005));
        var significant = groups.Where(g => g.Count >= minSig).Select(g => g.Font).Order().ToList();
        return significant.Count >= 2 && significant[^1] - significant[0] >= 2.0;
    }

    private static List<PdfHeadingCandidate> DetectTextbookHeadings(IReadOnlyList<PdfLine> lines, double bodyFont)
    {
        var minHeadingFont = bodyFont + 1.5;
        var headings = new List<PdfHeadingCandidate>();

        foreach (var line in lines)
        {
            if (line.FontSize < minHeadingFont) continue;
            var text = CompactLeadingMarker(line.Text);
            if (!TypedSectionRx.IsMatch(text)) continue;
            headings.Add(new PdfHeadingCandidate(
                LevelFromTypedMarker(text),
                CleanTitle(text),
                line.Page,
                line.Y,
                "pdf-section-marker"));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.FontSize < minHeadingFont) continue;

            var text = CompactLeadingMarker(line.Text);
            if (NumberedChapterLineRx.IsMatch(text) && !TypedSectionRx.IsMatch(text) && !Excluded(text))
            {
                headings.Add(new PdfHeadingCandidate(1, CleanTitle(text), line.Page, line.Y, "pdf-chapter-line"));
                continue;
            }

            if (!ChapterNumberRx.IsMatch(text) || i + 1 >= lines.Count) continue;
            var next = lines[i + 1];
            var nextText = CleanTitle(next.Text);
            if (next.Page != line.Page ||
                Math.Abs(next.FontSize - line.FontSize) > 0.8 ||
                next.FontSize < minHeadingFont ||
                nextText.Length < 4 ||
                Excluded(nextText))
                continue;

            headings.Add(new PdfHeadingCandidate(1, $"{text} {nextText}", line.Page, line.Y, "pdf-chapter-number-plus-title"));
        }

        return headings
            .Where(h => !Excluded(h.Text))
            .GroupBy(h => Canon(h.Text))
            .Select(g => g.OrderBy(h => h.Page).ThenByDescending(h => h.Y).Last())
            .OrderBy(h => h.Page).ThenByDescending(h => h.Y).ToList();
    }

    private static List<HeadingRecord> AlignToDocx(IReadOnlyList<PdfHeadingCandidate> pdfHeadings, IReadOnlyList<IPolicyParagraph> paragraphs)
    {
        var docx = paragraphs
            .Where(p => p.Role != ParagraphRole.Empty && !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => new DocxParagraphTokens(p, Tokenize(p.Text)))
            .ToList();
        var result = new List<HeadingRecord>();
        var cursor = 0;
        var previousLevel = 0;

        foreach (var heading in pdfHeadings)
        {
            var headingTokens = Tokenize(heading.Text);
            if (headingTokens.Count == 0) continue;

            var minIndex = heading.Level > previousLevel ? cursor : cursor + 1;
            var match = FindTokenSequence(docx, headingTokens, minIndex, preferParagraphStart: heading.Level > 1);
            if (match is null) continue;

            var text = CleanTitle(match.Value.Text);
            result.Add(new HeadingRecord
            {
                Index = match.Value.Paragraph.Index,
                StableId = match.Value.Paragraph.StableId,
                Level = heading.Level,
                Text = text,
                OriginalText = match.Value.Paragraph.Text,
                HeadingSpan = new TextOffsetSpan(match.Value.Start, match.Value.End),
                BoundarySource = heading.Reason,
                StyleId = match.Value.Paragraph.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0.97,
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                ConfidenceBasis = "pdf_textbook_layout",
            });
            cursor = match.Value.Paragraph.Index;
            previousLevel = heading.Level;
        }

        return result;
    }

    private static MatchResult? FindTokenSequence(
        IReadOnlyList<DocxParagraphTokens> paragraphs,
        IReadOnlyList<TokenSpan> needle,
        int minIndex,
        bool preferParagraphStart)
    {
        MatchResult? first = null;
        foreach (var p in paragraphs.Where(p => p.Paragraph.Index >= minIndex))
        {
            var tokens = p.Tokens;
            if (tokens.Count < needle.Count) continue;
            for (var i = 0; i <= tokens.Count - needle.Count; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Count; j++)
                {
                    if (!string.Equals(tokens[i + j].Text, needle[j].Text, StringComparison.Ordinal))
                    {
                        ok = false;
                        break;
                    }
                }
                if (!ok) continue;
                var start = tokens[i].Start;
                var end = tokens[i + needle.Count - 1].End;
                if (LooksLikeTocOccurrence(p.Paragraph.Text, end)) continue;
                if (LooksLikeChapterOutlineOccurrence(p.Paragraph.Text, start)) continue;
                var match = new MatchResult(p.Paragraph, p.Paragraph.Text[start..end], start, end);
                if (!preferParagraphStart) return match;
                first ??= match;
                if (LooksLikeParagraphStartHeading(p.Paragraph.Text, start)) return match;
            }
        }
        return first;
    }

    private static bool LooksLikeParagraphStartHeading(string paragraphText, int titleStart)
    {
        if (titleStart <= 3) return true;
        var prefix = paragraphText[..titleStart].Trim();
        return Regex.IsMatch(prefix, @"^\d{1,4}\s+\d{1,2}\s*[•\.]?\s*$");
    }

    private static bool LooksLikeTocOccurrence(string paragraphText, int titleEnd)
    {
        if (titleEnd >= paragraphText.Length) return false;
        var tail = paragraphText[titleEnd..];
        var m = Regex.Match(tail, @"^\s+\d{1,4}\s+(?:Introduction|Preface|\d+(?:\.\d+)?\s+\D|Assessment Questions|Endnotes)\b",
            RegexOptions.IgnoreCase);
        return m.Success;
    }

    private static bool LooksLikeChapterOutlineOccurrence(string paragraphText, int titleStart)
    {
        var prefix = paragraphText[..titleStart];
        var outlineAt = prefix.LastIndexOf("Chapter Outline", StringComparison.OrdinalIgnoreCase);
        if (outlineAt < 0) return false;
        var afterOutline = prefix[outlineAt..];
        return !afterOutline.Contains("Learning Outcome", StringComparison.OrdinalIgnoreCase);
    }

    private static int LevelFromTypedMarker(string text)
    {
        var marker = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return Math.Clamp(marker.Count(c => c == '.') + 1, 1, 9);
    }

    private static string CompactLeadingMarker(string text)
    {
        var t = NormalizeSpace(text);
        if (Regex.IsMatch(t, @"^(?:\d\s*){1,2}$"))
            return Regex.Replace(t, @"\s+", "");
        var dec = Regex.Match(t, @"^((?:\d\s*){1,2})\s*\.\s*((?:\d\s*){1,2})\s*(.*)$");
        if (dec.Success)
            return $"{Regex.Replace(dec.Groups[1].Value, @"\s+", "")}.{Regex.Replace(dec.Groups[2].Value, @"\s+", "")} {dec.Groups[3].Value}".Trim();
        var chap = Regex.Match(t, @"^((?:\d\s*){1,2})\s+(.*)$");
        return chap.Success
            ? $"{Regex.Replace(chap.Groups[1].Value, @"\s+", "")} {chap.Groups[2].Value}".Trim()
            : t;
    }

    private static string CleanTitle(string text)
    {
        var cleaned = NormalizeSpace(text.Replace('•', ' '));
        // Textbook TOC lines often carry a trailing page number while the body occurrence carries
        // the same full title without it. Strip only the common "numbered title + final page" shape;
        // duplicate selection still uses the full title, not marker-only, so registry/body markers
        // cannot steal the real occurrence.
        cleaned = Regex.Replace(cleaned, @"^(\d+(?:\.\d+)?\s+\D.+?)\s+\d{1,4}$", "$1");
        return NormalizeSpace(cleaned);
    }

    private static bool Excluded(string text) => ExcludedTitleRx.IsMatch(NormalizeSpace(text));

    private static string NormalizeSpace(string text) => WhitespaceRx.Replace(text, " ").Trim();

    private static string Canon(string text) => NonAlphaNumRx.Replace(text.ToLowerInvariant(), "");

    private static List<TokenSpan> Tokenize(string text)
    {
        var list = new List<TokenSpan>();
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+"))
            list.Add(new TokenSpan(m.Value, m.Index, m.Index + m.Length));
        return list;
    }

    private sealed record PdfHeadingCandidate(int Level, string Text, int Page, double Y, string Reason);
    private sealed record TokenSpan(string Text, int Start, int End);
    private sealed record DocxParagraphTokens(IPolicyParagraph Paragraph, IReadOnlyList<TokenSpan> Tokens);
    private readonly record struct MatchResult(IPolicyParagraph Paragraph, string Text, int Start, int End);
}
