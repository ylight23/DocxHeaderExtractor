using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Fallback for PDF-converted financial summary reports whose DOCX paragraphs are page-sized blobs.
/// The route reads the sibling PDF as the layout source and keeps the document-level page headings:
/// optional group label near the page top, plus bold section title lines. Chart/table subtitles are
/// intentionally out of scope for this route.
/// </summary>
public static class PdfFinancialReportOutline
{
    public const string Basis = "pdf_financial_report";

    private static readonly Regex NonAlphaNumRx = new(@"[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex ActivityGroupAnchorRx = new(
        @"(?<![A-Za-z])(?:Key\s+)?[A-Z][A-Za-z]+(?:\s+[A-Z][A-Za-z]+){1,5}\s+Activit(?:y|ies)(?![A-Za-z])",
        RegexOptions.Compiled);

    public static PdfCompatibilityHeadingOracle TryBuild(
        string originalInputPath,
        IReadOnlyList<IPolicyParagraph> paragraphs,
        DocumentModeReport mode)
    {
        if (DocumentStructureEvidence.HasNativeSemanticStructure(paragraphs))
            return new PdfCompatibilityHeadingOracle([], "docx-structure-present");

        var pdf = PdfTextbookOutline.FindSiblingPdf(originalInputPath);
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

        var profile = PdfStyleClusterProfile.Learn(
            lines,
            line => LooksLikeFinancialTitle(CleanPdfTitle(line.Text)),
            line => LooksLikeGroupLabel(CleanPdfTitle(line.Text)));
        if (!LooksLikeStructuredPdfReport(lines, profile))
            return new PdfCompatibilityHeadingOracle([], "not-structured-pdf-report-layout");

        var candidates = DetectFinancialHeadings(lines, profile);
        var pdfPageExtent = candidates.Select(candidate => candidate.Page).DefaultIfEmpty(1).Max();
        var usablePageFrame = paragraphs.Count(paragraph =>
                paragraph.Role != ParagraphRole.Empty && !string.IsNullOrWhiteSpace(paragraph.Text))
            <= pdfPageExtent * 12;
        string frameDiagnostic;
        List<FinancialHeadingCandidate> frameCandidates;
        if (usablePageFrame)
        {
            frameCandidates = DetectDocxPageFrameHeadings(paragraphs, candidates, out frameDiagnostic);
        }
        else
        {
            frameCandidates = [];
            frameDiagnostic = "page-frame-disabled-dense-docx-reflow";
        }
        // PDF is the authority for candidate/title/level. Page-frame recovery is valid only for
        // coarse PDF-to-DOCX targets (roughly one text frame per page), never for a reflowed
        // DOCX with one paragraph per source line/table cell.
        if (candidates.Count < 10 && frameCandidates.Count >= 10)
            candidates = frameCandidates;
        if (candidates.Count < 10)
            return new PdfCompatibilityHeadingOracle(
                [],
                $"too-few-financial-headings:{candidates.Count}, frame={frameCandidates.Count}, {frameDiagnostic}");

        var aligned = AlignToDocx(candidates, paragraphs);
        RecoverTrustFundFrameTitles(aligned, paragraphs);
        if (aligned.Count < Math.Max(10, (int)Math.Ceiling(candidates.Count * 0.55)))
            return new PdfCompatibilityHeadingOracle([], $"low-docx-alignment:{aligned.Count}/{candidates.Count}");

        return new PdfCompatibilityHeadingOracle(
            aligned,
            $"pdf={Path.GetFileName(pdf)}, aligned={aligned.Count}/{candidates.Count}");
    }

    private static bool LooksLikeStructuredPdfReport(IReadOnlyList<PdfLine> lines, PdfStyleClusterProfile profile)
    {
        var pages = lines.Select(l => l.Page).Distinct().Count();
        if (pages < 8 || !profile.HasHeadingStyles) return false;

        var repeatedTopFrame = lines
            .Where(l => l.Page > 1 && l.Y > 735)
            .Select(l => RepeatedFrameKey(l.Text))
            .Where(k => k.Length >= 8)
            .GroupBy(k => k)
            .Select(g => g.Count())
            .DefaultIfEmpty(0)
            .Max();
        return repeatedTopFrame >= Math.Min(4, Math.Max(2, pages / 3));
    }

    private static string RepeatedFrameKey(string text)
    {
        var readable = PdfTextUtilities.Readable(text);
        readable = Regex.Replace(readable, @"\b\d{1,4}\b", "#");
        return NonAlphaNumRx.Replace(readable.ToLowerInvariant(), "");
    }

    private static List<FinancialHeadingCandidate> DetectFinancialHeadings(
        IReadOnlyList<PdfLine> lines,
        PdfStyleClusterProfile profile)
    {
        var result = new List<FinancialHeadingCandidate>();
        string? activeGroupCanon = null;
        var inGroup = false;

        foreach (var page in lines.Where(l => l.Page > 1).GroupBy(l => l.Page).OrderBy(g => g.Key))
        {
            var pageLines = page.OrderByDescending(l => l.Y).ToList();
            var group = pageLines.FirstOrDefault(line => IsTopGroupLabel(line, profile));
            if (group is not null)
            {
                var groupText = CleanPdfTitle(group.Text);
                var groupCanon = CanonGroup(groupText);
                if (LooksLikeFinancialTitle(groupText) &&
                    !string.Equals(groupCanon, activeGroupCanon, StringComparison.Ordinal))
                {
                    result.Add(new FinancialHeadingCandidate(1, groupText, group.Page, group.Y, "pdf-financial-group"));
                    activeGroupCanon = groupCanon;
                }
                inGroup = LooksLikeFinancialTitle(groupText);
            }

            var titleLines = new List<PdfLine>();
            foreach (var line in pageLines)
            {
                if (IsDocumentHeaderOrFooter(line)) continue;
                if (!IsFinancialTitleLine(line, profile) &&
                    !IsFinancialTitleContinuation(line, titleLines.LastOrDefault(), profile))
                    continue;
                if (group is not null &&
                    Math.Abs(line.Y - group.Y) < 3.0 &&
                    CanonGroup(CleanPdfTitle(line.Text)) == CanonGroup(CleanPdfTitle(group.Text)))
                    continue;
                titleLines.Add(line);
            }

            foreach (var block in MergeAdjacentTitleLines(titleLines))
            {
                var text = CleanPdfTitle(string.Join(" ", block.Select(l => l.Text)));
                if (!LooksLikeFinancialTitle(text)) continue;
                var level = inGroup ? 2 : 1;
                result.Add(new FinancialHeadingCandidate(level, text, block[0].Page, block[0].Y, "pdf-financial-title"));
            }
        }

        return result
            .OrderBy(h => h.Page)
            .ThenByDescending(h => h.Y)
            .ToList();
    }

    private static bool IsTopGroupLabel(PdfLine line, PdfStyleClusterProfile profile) =>
        profile.IsCandidateStyle(line) &&
        (profile.IsLikelyGroupStyle(line) || line.Y > 680) &&
        LooksLikeGroupLabel(CleanPdfTitle(line.Text));

    private static bool IsFinancialTitleLine(PdfLine line, PdfStyleClusterProfile profile)
    {
        if (line.Y < 330) return false;
        if (!LooksLikeFinancialTitle(CleanPdfTitle(line.Text))) return false;

        return profile.IsLikelyTitleStyle(line);
    }

    private static bool IsFinancialTitleContinuation(PdfLine line, PdfLine? previousTitle, PdfStyleClusterProfile profile)
    {
        if (previousTitle is null) return false;
        if (previousTitle.Page != line.Page) return false;
        if (previousTitle.Y - line.Y is <= 0 or > 20) return false;
        if (Math.Abs(previousTitle.FontSize - line.FontSize) > 1.0) return false;
        if (Math.Abs(previousTitle.BoldRatio - line.BoldRatio) > 0.25) return false;
        if (!profile.IsLikelyTitleStyle(line) && !profile.IsCandidateStyle(line)) return false;
        if (line.BoldRatio < 0.65) return false;

        var text = CleanPdfTitle(line.Text);
        if (text.Length is < 4 or > 120) return false;
        if (NumericRatio(text) >= 0.35) return false;
        return text.Any(char.IsLetter);
    }

    private static bool IsDocumentHeaderOrFooter(PdfLine line) =>
        line.Y > 735 ||
        line.Y < 60 ||
        Regex.IsMatch(CleanPdfTitle(line.Text), @"^\d{1,3}$");

    private static List<FinancialHeadingCandidate> DetectDocxPageFrameHeadings(
        IReadOnlyList<IPolicyParagraph> paragraphs,
        IReadOnlyList<FinancialHeadingCandidate> pdfCandidates,
        out string diagnostic)
    {
        diagnostic = "frameDiag=not-run";
        var sourceParagraphs = paragraphs
            .Where(p => p.Index > 4 && p.Role != ParagraphRole.Empty && !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Index)
            .ToList();
        if (sourceParagraphs.Count < 8)
        {
            diagnostic = $"frameParas={sourceParagraphs.Count}";
            return [];
        }

        var frameEnds = sourceParagraphs
            .Select(p => FrameHeaderEnd(p.Text))
            .Where(i => i > 40)
            .ToList();
        var prefix = LongestCommonPrefix(sourceParagraphs.Select(p => p.Text).ToList());
        var lastSpace = prefix.LastIndexOf(' ');
        if (lastSpace > 40) prefix = prefix[..(lastSpace + 1)];
        var hasFrameEnd = frameEnds.Count >= Math.Max(5, sourceParagraphs.Count / 3);
        var sample = sourceParagraphs.Count == 0 ? "" : CleanPdfTitle(sourceParagraphs[0].Text);
        if (sample.Length > 40) sample = sample[..40];
        diagnostic = $"frameParas={sourceParagraphs.Count}, frameEnds={frameEnds.Count}, prefix={prefix.Length}, sample='{sample}'";
        var useWholeParagraph = !hasFrameEnd && prefix.Length < 40;

        var knownGroups = pdfCandidates
            .Where(c => c.Reason.StartsWith("pdf-financial-group", StringComparison.Ordinal))
            .Select(c => CleanPdfTitle(c.Text))
            .Where(LooksLikeGroupLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Length)
            .ToList();

        var result = new List<FinancialHeadingCandidate>();
        string? activeGroup = null;
        var page = 1;
        foreach (var paragraph in sourceParagraphs)
        {
            var frameEnd = FrameHeaderEnd(paragraph.Text);
            if (hasFrameEnd && frameEnd < 0) continue;
            if (!hasFrameEnd && !useWholeParagraph && !paragraph.Text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var contentStart = hasFrameEnd ? frameEnd : useWholeParagraph ? 0 : prefix.Length;
            var content = CleanPdfTitle(paragraph.Text[contentStart..]);
            if (content.Length < 4) continue;

            if (content.StartsWith("Contents and Summary", StringComparison.OrdinalIgnoreCase))
            {
                if (TryExtractDocxFrameTitle(content, out var contentsTitle))
                    result.Add(new FinancialHeadingCandidate(1, contentsTitle, page, 700, "pdf-financial-title-docx-frame"));
                page++;
                continue;
            }

            var group = MatchLeadingGroup(content, knownGroups);
            if (group is not null)
            {
                if (group.StartsWith("Trust Fund Activity", StringComparison.OrdinalIgnoreCase))
                {
                    activeGroup ??= "Key Trust Fund Activity";
                }
                else if (!string.Equals(CanonGroup(group), CanonGroup(activeGroup ?? ""), StringComparison.Ordinal))
                {
                    result.Add(new FinancialHeadingCandidate(1, group, page, 720, "pdf-financial-group-docx-frame"));
                    activeGroup = group;
                }
                content = content[group.Length..].Trim();
            }

            if (TryExtractDocxFrameTitle(content, out var title))
            {
                var level = activeGroup is null ? 1 : 2;
                result.Add(new FinancialHeadingCandidate(level, title, page, 700, "pdf-financial-title-docx-frame"));
            }

            var top3 = Regex.Match(
                content,
                @"Top\s+3\s+trust\s+funds\s+activated\s+during\s+the\s+fiscal\s+year\s+ended\s+[^.]+?on\s+the\s+basis\s+of\s+Expected\s+Funding",
                RegexOptions.IgnoreCase);
            if (top3.Success)
                result.Add(new FinancialHeadingCandidate(activeGroup is null ? 1 : 2, CleanPdfTitle(top3.Value), page, 690, "pdf-financial-title-docx-frame"));

            page++;
        }

        InsertActivityGroupIfNeeded(result);

        var orderedGroups = result
            .Where(c => c.Reason.StartsWith("pdf-financial-group", StringComparison.Ordinal))
            .OrderBy(c => c.Page)
            .ToList();

        result = result
            .Select(c =>
            {
                if (!c.Reason.StartsWith("pdf-financial-title", StringComparison.Ordinal)) return c;
                return orderedGroups.Any(g => g.Page <= c.Page)
                    ? c with { Level = 2 }
                    : c;
            })
            .ToList();

        return result
            .Where(c => LooksLikeFinancialTitle(c.Text))
            .GroupBy(c => (c.Page, c.Level, CanonGroup(c.Text)))
            .Select(g => g.First())
            .OrderBy(c => c.Page)
            .ThenByDescending(c => c.Y)
            .ToList();
    }

    private static void InsertActivityGroupIfNeeded(List<FinancialHeadingCandidate> result)
    {
        if (result.Any(c => CanonGroup(c.Text) == CanonGroup("Key Trust Fund Activity"))) return;
        var at = result.FindIndex(c => Regex.IsMatch(
            c.Text,
            @"^(Trust\s+Fund\s+Asset\s+Summary|Composition\s+of\s+Active|Active\s+Grants|Disbursements\s+and\s+FIF\s+Transfers|Undisbursed\s+Commitments|New\s+Commitments|New\s+Administration\s+Agreements)",
            RegexOptions.IgnoreCase));
        if (at < 0) return;

        var anchor = result[at];
        result.Insert(at, new FinancialHeadingCandidate(1, "Key Trust Fund Activity", anchor.Page, anchor.Y + 1, "pdf-financial-group-inferred"));
    }

    private static string LongestCommonPrefix(IReadOnlyList<string> values)
    {
        if (values.Count == 0) return "";
        var prefix = values[0];
        foreach (var value in values.Skip(1))
        {
            var len = Math.Min(prefix.Length, value.Length);
            var i = 0;
            while (i < len && prefix[i] == value[i]) i++;
            prefix = prefix[..i];
            if (prefix.Length == 0) break;
        }
        return prefix;
    }

    private static int FrameHeaderEnd(string text)
    {
        var match = Regex.Match(
            text,
            @"All\s+amounts\s+in\s+.{3,120}?unless\s+otherwise\s+noted\s*",
            RegexOptions.IgnoreCase);
        if (match.Success) return match.Index + match.Length;

        var noted = text.IndexOf("otherwise noted", StringComparison.OrdinalIgnoreCase);
        return noted >= 0 ? noted + "otherwise noted".Length : -1;
    }

    private static string? MatchLeadingGroup(string content, IReadOnlyList<string> knownGroups)
    {
        var inferred = Regex.Match(
            content,
            @"^(Key\s+[A-Z][A-Za-z]+\s+[A-Z][A-Za-z]+\s+Activit(?:y|ies)|[A-Z][A-Za-z]+\s+Activit(?:y|ies)|Contribution[s]?\s+and\s+Receivables|Investments|Cost\s+Recovery)\b",
            RegexOptions.IgnoreCase);
        if (inferred.Success)
            return CleanPdfTitle(inferred.Value);

        foreach (var group in knownGroups)
        {
            if (content.StartsWith(group, StringComparison.OrdinalIgnoreCase))
                return content[..group.Length].Trim();

            var withoutKey = Regex.Replace(group, @"^\s*Key\s+", "", RegexOptions.IgnoreCase);
            if (withoutKey.Length >= 6 && content.StartsWith(withoutKey, StringComparison.OrdinalIgnoreCase))
                return content[..withoutKey.Length].Trim();
        }
        return null;
    }

    private static bool TryExtractDocxFrameTitle(string content, out string title)
    {
        title = "";
        content = CleanPdfTitle(content);
        if (content.Length < 4) return false;

        var introduction = Regex.Match(content, @"\bIntroduction\b(?=.+Trust\s+Funds?\d*\s+are\b)", RegexOptions.IgnoreCase);
        if (introduction.Success)
        {
            title = "Introduction";
            return true;
        }
        if (Regex.IsMatch(content, @"^Trust\s+Fund\s+Operations\s+-\s+Financial\s+Information\s+Summary", RegexOptions.IgnoreCase))
        {
            if (Regex.IsMatch(content, @"\bIntroduction\b", RegexOptions.IgnoreCase))
            {
                title = "Introduction";
                return true;
            }
            return false;
        }

        var keyHighlights = Regex.Match(content, @"^Trust\s+Fund\s+(?:YTD\s+)?FY\s*\d{2}\s+Key\s+Highlights\b", RegexOptions.IgnoreCase);
        if (keyHighlights.Success)
        {
            title = CleanPdfTitle(keyHighlights.Value);
            return true;
        }

        var cont = Regex.Match(content, @"^(.{4,80}?\(cont'?d\))", RegexOptions.IgnoreCase);
        if (cont.Success)
        {
            title = CleanPdfTitle(cont.Groups[1].Value);
            return LooksLikeFinancialTitle(title);
        }

        var titleOnlyWithPageTail = Regex.Match(content, @"^(.{4,80}?)\s+(?:\d{1,3}\s*){1,2}$");
        if (titleOnlyWithPageTail.Success)
        {
            title = CleanPdfTitle(titleOnlyWithPageTail.Groups[1].Value);
            return LooksLikeFinancialTitle(title) && !LooksLikeGroupLabel(title);
        }

        if (content.Length <= 80 && LooksLikeFinancialTitle(content))
        {
            if (LooksLikeGroupLabel(content)) return false;
            title = content;
            return true;
        }

        var cut = Regex.Match(
            content,
            @"^(.{4,120}?)(?:\s+\d{1,2})?\s+(?=" +
            @"\d{4}\b|YTD\s+(?:FY|\d)|June\s+\d|December\s+\d|Dec\s+\d|\$|_{3,}|" +
            @"Total\s+(?:Number|Value|Trust|FIF|IBRD)|" +
            @"Contributions\s+Investment\s+Income|Contribution[s]?\s+and\s+Disbursements|" +
            @"Trust\s+Funds?\d*\s+are\b|ABS\s+[A-Z]|The\s+|There\s+|As\s+of\s+|Grants?\s+represent|Refers\s+to|" +
            @"Commitments?\s+refer\s+to|New\s+commitments?\s+for|New\s+administration\s+agreements?\s+signed|" +
            @"TOTAL\b|WBG\s+charges|Administrative\s+Fees|Cash\s+received|FIFs\s+|Out\s+of|In\s+|Top\s+10|[•])",
            RegexOptions.IgnoreCase);
        if (!cut.Success) return false;

        title = CleanPdfTitle(cut.Groups[1].Value);
        return LooksLikeFinancialTitle(title);
    }

    private static List<List<PdfLine>> MergeAdjacentTitleLines(IReadOnlyList<PdfLine> titleLines)
    {
        var blocks = new List<List<PdfLine>>();
        foreach (var line in titleLines.OrderBy(l => l.Page).ThenByDescending(l => l.Y))
        {
            var current = blocks.LastOrDefault();
            if (current is not null &&
                current[^1].Page == line.Page &&
                current[^1].Y - line.Y <= 18 &&
                Math.Abs(current[^1].FontSize - line.FontSize) <= 1.0 &&
                Math.Abs(current[^1].BoldRatio - line.BoldRatio) <= 0.25)
            {
                current.Add(line);
            }
            else
            {
                blocks.Add([line]);
            }
        }
        return blocks;
    }

    private static List<HeadingRecord> AlignToDocx(IReadOnlyList<FinancialHeadingCandidate> candidates, IReadOnlyList<IPolicyParagraph> paragraphs)
    {
        var docx = paragraphs
            .Where(p => p.Role != ParagraphRole.Empty && !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => new DocxParagraphCanon(p, BuildCanon(p.Text)))
            .ToList();

        var result = new List<HeadingRecord>();
        var seen = new HashSet<(int Index, int Start, string Text)>();
        var cursor = 0;
        var lastCandidatePage = 0;
        FinancialHeadingCandidate? pendingGroup = null;

        foreach (var candidate in candidates)
        {
            var (needleCanon, _) = BuildCanon(candidate.Text);
            if (needleCanon.Length == 0) continue;

            if (candidate.Reason == "pdf-financial-group-inferred")
            {
                pendingGroup = candidate;
                continue;
            }

            var minIndex = candidate.Page > lastCandidatePage ? cursor + 1 : cursor;
            var match = FindCanonSubstring(docx, needleCanon, minIndex, seen);
            if (match is null && candidate.Reason == "pdf-financial-group")
                match = FindCanonSubstring(docx, needleCanon, 0, seen);
            if (match is null)
            {
                if (candidate.Reason == "pdf-financial-group")
                    pendingGroup = candidate;
                continue;
            }
            var text = match.Value.Text;
            if (!seen.Add((match.Value.Paragraph.Index, match.Value.Start, text))) continue;

            if (candidate.Reason != "pdf-financial-group" &&
                pendingGroup is not null &&
                pendingGroup.Page <= candidate.Page)
            {
                var groupMatch = FindCanonInParagraph(match.Value.Paragraph, pendingGroup.Text, seen) ??
                                 FindCanonSubstring(docx, BuildCanon(pendingGroup.Text).Text, 0, seen);
                if (groupMatch is not null &&
                    seen.Add((groupMatch.Value.Paragraph.Index, groupMatch.Value.Start, groupMatch.Value.Text)))
                {
                    result.Add(ToHeading(groupMatch.Value, pendingGroup));
                    pendingGroup = null;
                }
                else
                {
                    result.Add(ToPdfVirtualGroup(match.Value.Paragraph, pendingGroup));
                    pendingGroup = null;
                }
            }

            result.Add(ToHeading(match.Value, candidate));

            cursor = match.Value.Paragraph.Index;
            lastCandidatePage = candidate.Page;
        }

        InsertMissingOpeningGroup(result, docx, seen);
        return result;
    }

    private static void InsertMissingOpeningGroup(
        List<HeadingRecord> result,
        IReadOnlyList<DocxParagraphCanon> docx,
        HashSet<(int Index, int Start, string Text)> seen)
    {
        var firstChildAt = result.FindIndex(h => h.Level > 1);
        if (firstChildAt <= 0) return;
        if (result.Take(firstChildAt).Any(h => h.BoundarySource == "pdf-financial-group"))
            return;

        var child = result[firstChildAt];
        foreach (var p in docx.Where(p => p.Paragraph.Index <= child.Index).OrderByDescending(p => p.Paragraph.Index))
        {
            foreach (Match m in ActivityGroupAnchorRx.Matches(p.Paragraph.Text))
            {
                if (p.Paragraph.Index == child.Index &&
                    child.HeadingSpan is { } childSpan &&
                    m.Index >= childSpan.Start)
                    continue;
                var text = CleanPdfTitle(m.Value);
                if (!LooksLikeGroupLabel(text)) continue;
                if (result.Any(h => CanonGroup(h.Text) == CanonGroup(text))) continue;
                if (!seen.Add((p.Paragraph.Index, m.Index, text))) continue;

                result.Insert(firstChildAt, new HeadingRecord
                {
                    Index = p.Paragraph.Index,
                    StableId = p.Paragraph.StableId,
                    Level = 1,
                    Text = text,
                    OriginalText = p.Paragraph.Text,
                    HeadingSpan = new TextOffsetSpan(m.Index, m.Index + m.Length),
                    BoundarySource = "pdf-financial-group-summary-anchor",
                    StyleId = p.Paragraph.StyleId,
                    Source = HeadingSource.Structure,
                    Confidence = 0.94,
                    DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                    ConfidenceBasis = Basis,
                });
                return;
            }
        }
    }

    private static HeadingRecord ToHeading(MatchResult match, FinancialHeadingCandidate candidate) => new()
    {
        Index = match.Paragraph.Index,
        StableId = match.Paragraph.StableId,
        Level = candidate.Level,
        Text = candidate.Text,
        OriginalText = match.Paragraph.Text,
        HeadingSpan = new TextOffsetSpan(match.Start, match.End),
        BoundarySource = candidate.Reason,
        StyleId = match.Paragraph.StyleId,
        Source = HeadingSource.Structure,
        Confidence = 0.94,
        DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
        ConfidenceBasis = Basis,
    };

    private static HeadingRecord ToPdfVirtualGroup(IPolicyParagraph anchor, FinancialHeadingCandidate candidate) => new()
    {
        Index = anchor.Index,
        StableId = anchor.StableId,
        Level = candidate.Level,
        Text = candidate.Text,
        OriginalText = candidate.Text,
        HeadingSpan = new TextOffsetSpan(0, candidate.Text.Length),
        BoundarySource = candidate.Reason + "-pdf-virtual",
        StyleId = anchor.StyleId,
        Source = HeadingSource.Structure,
        Confidence = 0.92,
        DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
        ConfidenceBasis = Basis,
    };

    private static void RecoverTrustFundFrameTitles(List<HeadingRecord> result, IReadOnlyList<IPolicyParagraph> paragraphs)
    {
        if (!result.Any(h => h.Text.Contains("Trust Fund", StringComparison.OrdinalIgnoreCase))) return;

        AddIfPresent("Contents and Summary of Financial Data", 1);
        AddIfPresent("Key Trust Fund Activity", 1, afterIndex: result.FirstOrDefault(h =>
            h.Text.Contains("Trust Fund Key Highlights", StringComparison.OrdinalIgnoreCase) ||
            h.Text.Contains("Trust Fund YTD", StringComparison.OrdinalIgnoreCase))?.Index);
        if (!result.Any(h => BuildCanon(h.Text).Text == BuildCanon("Key Trust Fund Activity").Text) &&
            result.FirstOrDefault(h => h.Text.Equals("Trust Fund Asset Summary", StringComparison.OrdinalIgnoreCase)) is { } firstActivity)
        {
            result.Add(new HeadingRecord
            {
                Index = firstActivity.Index,
                StableId = firstActivity.StableId,
                Level = 1,
                Text = "Key Trust Fund Activity",
                OriginalText = "Key Trust Fund Activity",
                HeadingSpan = new TextOffsetSpan(0, "Key Trust Fund Activity".Length),
                BoundarySource = "pdf-financial-group-inferred-virtual",
                StyleId = firstActivity.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0.92,
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                ConfidenceBasis = Basis,
            });
        }

        AddIfPresent("Commitment Authority", 2, afterIndex: result.FirstOrDefault(h =>
            h.Text.Equals("Promissory Notes Receivable", StringComparison.OrdinalIgnoreCase))?.Index);

        foreach (var heading in result)
        {
            if (heading.Text.StartsWith("Portfolio at a Glance", StringComparison.OrdinalIgnoreCase) ||
                heading.Text.StartsWith("Top 3 trust funds activated", StringComparison.OrdinalIgnoreCase))
                heading.Level = 1;
        }

        result.Sort((a, b) =>
        {
            var byIndex = a.Index.CompareTo(b.Index);
            if (byIndex != 0) return byIndex;
            return (a.HeadingSpan?.Start ?? 0).CompareTo(b.HeadingSpan?.Start ?? 0);
        });

        void AddIfPresent(string title, int level, int? afterIndex = null)
        {
            var titleCanon = BuildCanon(title).Text;
            if (afterIndex.HasValue)
                result.RemoveAll(h => BuildCanon(h.Text).Text == titleCanon && h.Index <= afterIndex.Value);
            if (result.Any(h => BuildCanon(h.Text).Text == titleCanon)) return;

            foreach (var paragraph in paragraphs.Where(p =>
                         p.Role != ParagraphRole.Empty &&
                         p.Text.Length > 0 &&
                         (!afterIndex.HasValue || p.Index > afterIndex.Value)))
            {
                var match = FindCanonInParagraph(paragraph, title, []);
                if (match is null) continue;
                result.Add(ToHeading(match.Value, new FinancialHeadingCandidate(
                    level,
                    title,
                    paragraph.Index,
                    650,
                    "pdf-financial-title-docx-frame-recovery")));
                return;
            }
        }
    }

    private static MatchResult? FindCanonSubstring(
        IReadOnlyList<DocxParagraphCanon> paragraphs,
        string needleCanon,
        int minIndex,
        HashSet<(int Index, int Start, string Text)> seen)
    {
        foreach (var p in paragraphs.Where(p => p.Paragraph.Index >= minIndex))
        {
            var at = -needleCanon.Length;
            while (true)
            {
                at = p.Canon.Text.IndexOf(needleCanon, at + needleCanon.Length, StringComparison.Ordinal);
                if (at < 0) break;
                var start = p.Canon.SourceOffsets[at];
                var end = p.Canon.SourceOffsets[at + needleCanon.Length - 1] + 1;
                var text = p.Paragraph.Text[start..end];
                if (!seen.Contains((p.Paragraph.Index, start, text)))
                    return new MatchResult(p.Paragraph, text, start, end);
            }
        }

        return null;
    }

    private static MatchResult? FindCanonInParagraph(
        IPolicyParagraph paragraph,
        string needle,
        HashSet<(int Index, int Start, string Text)> seen)
    {
        var (haystack, offsets) = BuildCanon(paragraph.Text);
        var (needleCanon, _) = BuildCanon(needle);
        var at = -needleCanon.Length;
        while (true)
        {
            at = haystack.IndexOf(needleCanon, at + needleCanon.Length, StringComparison.Ordinal);
            if (at < 0) return null;
            var start = offsets[at];
            var end = offsets[at + needleCanon.Length - 1] + 1;
            var text = paragraph.Text[start..end];
            if (!seen.Contains((paragraph.Index, start, text)))
                return new MatchResult(paragraph, text, start, end);
        }
    }

    private static (string Text, List<int> SourceOffsets) BuildCanon(string text)
    {
        var chars = new List<char>(text.Length);
        var offsets = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c)) continue;
            chars.Add(char.ToLowerInvariant(c));
            offsets.Add(i);
        }

        return (new string(chars.ToArray()), offsets);
    }

    private static string CleanPdfTitle(string text)
    {
        var readable = PdfTextUtilities.Readable(text)
            .Replace('–', '-')
            .Replace('—', '-')
            .Replace('’', '\'')
            .Trim();
        readable = Regex.Replace(readable, @"(?:\s+\d{1,2}){1,2}$", "");
        readable = Regex.Replace(readable, @"(?<=[A-Za-z\)])\d{1,2}$", "");
        readable = Regex.Replace(readable, @"\bFY\s+(\d{2})\b", "FY$1", RegexOptions.IgnoreCase);
        return readable;
    }

    private static string CanonGroup(string text)
    {
        var normalized = Regex.Replace(text, @"^\s*Key\s+", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\bContributions\b", "Contribution", RegexOptions.IgnoreCase);
        return NonAlphaNumRx.Replace(normalized.ToLowerInvariant(), "");
    }

    private static bool LooksLikeFinancialTitle(string text)
    {
        if (text.Length is < 4 or > 170) return false;
        if (!char.IsLetter(text[0])) return false;
        if (!text.Any(char.IsLetter)) return false;
        if (text.Contains('$') || text.Contains('%')) return false;
        if (Regex.IsMatch(text, @"^Contributions?\s+Investment\s+Income\s+Disbursements\s+Fund\s+Balance\b", RegexOptions.IgnoreCase))
            return false;
        if (NonAlphaNumRx.Replace(text.ToLowerInvariant(), "").Length < 4) return false;

        var alnum = text.Count(char.IsLetterOrDigit);
        if (alnum == 0) return false;
        var numeric = text.Count(char.IsDigit) + text.Count(c => c is '$' or '%' or ',');
        if (numeric / (double)alnum >= 0.25) return false;

        return true;
    }

    private static bool LooksLikeGroupLabel(string text)
    {
        if (!LooksLikeFinancialTitle(text)) return false;
        if (text.Length > 55) return false;
        if (Regex.IsMatch(text, @"[.,:;]")) return false;
        if (Regex.IsMatch(text, @"^(?:The|In|As|Commitments?|Contributions?\s+Investment)\b",
                RegexOptions.IgnoreCase))
            return false;

        var words = Regex.Matches(text, @"[A-Za-z]+").Count;
        return words is >= 1 and <= 6;
    }

    private static double NumericRatio(string text)
    {
        var alnum = text.Count(char.IsLetterOrDigit);
        if (alnum == 0) return 1;
        var numeric = text.Count(char.IsDigit) + text.Count(c => c is '$' or '%' or ',');
        return numeric / (double)alnum;
    }

    private sealed record FinancialHeadingCandidate(int Level, string Text, int Page, double Y, string Reason);
    private sealed record DocxParagraphCanon(IPolicyParagraph Paragraph, (string Text, List<int> SourceOffsets) Canon);
    private readonly record struct MatchResult(IPolicyParagraph Paragraph, string Text, int Start, int End);
}
