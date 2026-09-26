using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests.GenericAudit.V1_4;

/// <summary>
/// Layer A - Source Evidence Profile, GENERIC_AUDIT_ENGINE_V1.4: V1.2's, read under PDF_SOURCE_FACTS_V3 (a line's
/// size is its dominant glyph size), plus the gap after a bold lead and four lexical shapes (dot leaders, period
/// lines, a parenthetical suffix, a trailing date). V1.2's own layer is kept below unchanged.
/// <para>
/// V1's evidence (GenericAudit/SourceEvidence.cs, frozen with the SRC-029 pre-registration) plus what
/// the six generic gaps of SRC029_BLIND_GENERALIZATION_AUDIT_V1 showed was missing: where an occurrence
/// sits on its page as a column (left and right edge, row, vertical position), and the bold lead of a
/// line whose remainder is not bold, taken back to the occurrence's exact text. Evidence only; no
/// threshold belongs to a document and nothing here says what is a heading.
/// </para>
/// </summary>
internal sealed record SourceOccurrence(
    string Alias,
    string SourceId,
    int Ordinal,
    string Text,
    string Media,
    int? Page,
    double? FontSize,
    bool Bold,
    bool Italic,
    bool AllCaps,
    bool Centered,
    string? StyleName,
    int? DeclaredHeadingLevel,
    string? NumberLabel,
    int TableDepth,
    bool InTableOfContents,
    int RowSegmentCount,
    int RowSegmentIndex,
    bool RowHasFigures,
    double? PageBand)
{
    /// <summary>Computed, not stored, so a fragment made with <c>with { Text = ... }</c> counts its own words.</summary>
    public int WordCount => Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>PDF: the visual row (atom coordinate), the line's left and right edge and its baseline.</summary>
    public int? Row { get; init; }

    public double? Left { get; init; }

    public double? Right { get; init; }

    public double? Y { get; init; }

    /// <summary>
    /// The bold lead of an occurrence whose remainder is not bold, as the exact leading substring of
    /// <see cref="Text"/> (never a normalized copy). Null when the occurrence is wholly bold or wholly not.
    /// </summary>
    public string? BoldLead { get; init; }

    /// <summary>DOCX: the paragraph starts on a new page.</summary>
    public bool PageBreakBefore { get; init; }

    /// <summary>
    /// V1.3, PDF: the horizontal distance in points from the last glyph of <see cref="BoldLead"/> to the first glyph
    /// after it. A lead set a column's width away from the rest of its line is not a run-in label: the two only share an
    /// extraction segment. Null without a lead.
    /// </summary>
    public double? LeadGap { get; init; }

    /// <summary>V1.3, PDF: the smallest glyph size on the line (PDF_SOURCE_FACTS_V3), beside its dominant <see cref="FontSize"/>.</summary>
    public double? MinFontSize { get; init; }
}

/// <summary>General lexical shapes: evidence, never a membership list. V1's shapes, plus four.</summary>
internal static partial class LexicalShape
{
    [GeneratedRegex(@"^[\s$€£¥(),.%\d—–\-+*/#]+$")] private static partial Regex FiguresOnly();
    [GeneratedRegex(@"^(?:\(?[A-Z][a-z]+\.?\s+\d{1,2},?\s+\d{4}\)?|\d{1,2}\s+[A-Z][a-z]+\s+\d{4}|[A-Z][a-z]+\s+\d{4}|\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{2,4})$")] private static partial Regex DateOnly();
    [GeneratedRegex(@"^(?:Table|Figure|Chart|Exhibit|Graph|Box|Bảng|Hình|Biểu đồ)\s*(?:[A-Z]?\d+(?:\.\d+)*\s*[:.\-–—]?|[A-Z][.:]\s|:)")] private static partial Regex NumberedCaption();
    [GeneratedRegex(@"^(?:\[[^\]]*\]?|\([^)]{12,}\))$")] private static partial Regex BracketedNote();
    [GeneratedRegex(@"^(?:Part|Chapter|Section|Sub-?Clause|Clause|Article|Annex|Appendix|Schedule|Note|Attachment|Exhibit|Title|Book|Phần|Chương|Mục|Điều|Phụ lục|Tiểu mục)\s+(?:[IVXLCDM]+|[A-Z]|\d+(?:[.\s]\d+)*)(?:\b|$)", RegexOptions.IgnoreCase)] private static partial Regex StructuralLabelPrefix();
    [GeneratedRegex(@"^(?:\d+(?:\.\d+)*\.?|[A-Z]\.|[IVXLC]+\.|\(\w{1,3}\))\s+\S")] private static partial Regex EnumeratorPrefix();
    [GeneratedRegex(@"\[\s*(?:insert|enter|name|date|specify)|_{4,}|\.{6,}", RegexOptions.IgnoreCase)] private static partial Regex FillInPlaceholder();
    [GeneratedRegex(@"^(?:[a-z][.)]\s|\([a-z]\)\s|\*\s|[ᵃᵇᶜᵈᵉ]|\d{1,2}\s+[A-Z])")] private static partial Regex FootnoteLead();
    [GeneratedRegex(@"^\(\s*[A-Za-z][A-Za-z ]{1,30}\)$")] private static partial Regex ParentheticalStatus();
    [GeneratedRegex(@"(?:\.{3,}|\s)\d{1,4}$")] private static partial Regex TrailingPageNumber();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"^(?:and|or|&|and/or|et|và|hoặc)$", RegexOptions.IgnoreCase)] private static partial Regex ConnectiveLine();
    [GeneratedRegex(@"(?:^|\s)(?:to|of|and|or|for|the|a|an|in|on|with|by|from|at|và|của|cho|về|các|những)$", RegexOptions.IgnoreCase)] private static partial Regex FunctionWordEnd();
    [GeneratedRegex(@"\d+")] private static partial Regex Digits();

    // V1.4: one cross-reference ("Section 4.1, Paragraph 14", "Appendix A.2.3", "Table 4"); a contact line.
    [GeneratedRegex(@"^(?:Section|Sections|Appendix|Annex|Table|Figure|Chapter|Article|Clause|Part|Paragraph|Item|Note|Point)\s+(?:[A-Z]|\d+)(?:\.\d+)*\.?(?:,\s*(?:Paragraph|Item|Point|Clause|Note|Table|Figure)\s+\d+(?:\.\d+)*)*$")] private static partial Regex ReferenceItem();
    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+|https?://|^(?:e-?mail|phone|tel|fax|mobile|uri|url|web)\s*:", RegexOptions.IgnoreCase)] private static partial Regex ContactLine();

    // V1.3: dot leaders to a figure; a period line ("As of June 30, 2025 ...", "For the fiscal years ended ...", a date
    // or dates joined by "and"); a parenthetical status after a title; a date at the end of a line.
    [GeneratedRegex(@"(?:\.\s?){4,}")] private static partial Regex DotLeaders();
    [GeneratedRegex(@"^(?:(?:as\s+(?:of|at)|for\s+the\s+(?:(?:fiscal|financial)\s+)?(?:years?|quarters?|months?|periods?|(?:three|six|nine|twelve)\s+months)\s+ended)\s+.*\b\d{4}\b.*|(?:(?:[A-Z][a-z]+\.?\s+\d{1,2},?\s+\d{4})(?:\s*(?:,|and)\s*)?)+)$", RegexOptions.IgnoreCase)] private static partial Regex PeriodLine();
    [GeneratedRegex(@"^(?<head>.*\S)\s+(?<tail>\(\s*[A-Za-z][A-Za-z ]{1,30}\))$")] private static partial Regex ParentheticalSuffix();
    [GeneratedRegex(@"^(?<head>.*[A-Za-z])\s+(?<date>(?:[A-Z][a-z]+|[A-Z]+)\s+\d{1,2},?\s+\d{4})$")] private static partial Regex TrailingDate();

    // V1.1: an enumerator standing alone ("A.", "1.", "IV.", "(a)"); the first item of an enumeration;
    // the enumeration family of a leading enumerator.
    [GeneratedRegex(@"^(?:\d+(?:\.\d+)*\.?|[A-Z]\.|[IVXLC]+\.|\(\w{1,3}\))$")] private static partial Regex BareEnumerator();
    [GeneratedRegex(@"^(?:(?:1|A|a|I|i)[.)]|\((?:1|a|i|A)\)|1\.1|1\.1\.1)(?:\s|$)")] private static partial Regex FirstEnumeratorItem();
    [GeneratedRegex(@"^(?:(?<arabic>\d+(?:\.\d+)*)[.)]?|(?<upper>[A-Z])[.)]|(?<roman>[IVXLC]+)[.)]|\((?<paren>\w{1,3})\))\s")] private static partial Regex EnumeratorFamily();

    public static bool IsFiguresOnly(string text) => FiguresOnly().IsMatch(text);

    /// <summary>V1.3: dot leaders running to a figure - a table row or a contents line, never a label of its own.</summary>
    public static bool HasDotLeaders(string text) => DotLeaders().IsMatch(text);

    /// <summary>V1.3: a line that states a period or date(s) and nothing else - reporting metadata, not a title.</summary>
    public static bool IsPeriodLine(string text) => PeriodLine().IsMatch(text.Trim()) || IsDate(text);

    /// <summary>V1.3: the lead before a parenthetical status ("... (Continued)"): the whole line is one title.</summary>
    public static bool IsParentheticalSuffix(string text, string lead) =>
        ParentheticalSuffix().Match(text.Trim()) is { Success: true } m && m.Groups["head"].Value.Trim() == lead.Trim();

    /// <summary>V1.3: the text before a date that ends the line ("REPORTS JUNE 30, 2025" gives "REPORTS"), or null.</summary>
    public static string? BeforeTrailingDate(string text) =>
        TrailingDate().Match(text.Trim()) is { Success: true } m ? m.Groups["head"].Value : null;

    /// <summary>V1.3: the line ends in a year (1900-2099) - the end of a date, however it was spaced, not a page number.</summary>
    public static bool EndsWithYear(string text)
    {
        var last = text.TrimEnd().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return last.Length == 4 && int.TryParse(last, out var year) && year is >= 1900 and < 2100;
    }

    /// <summary>V1.3: a line that begins in lower case continues the sentence of the line above.</summary>
    public static bool StartsLowerCase(string text) => text.TrimStart().FirstOrDefault() is var c && char.IsLower(c);

    /// <summary>Word's built-in contents-entry styles (TOC 1..9): a naming convention of the format, not a heading text.</summary>
    public static bool IsBuiltInContentsStyle(string? styleId) => styleId is not null && styleId.StartsWith("TOC", StringComparison.OrdinalIgnoreCase);
    public static bool IsDate(string text) => DateOnly().IsMatch(text.Trim());
    /// <summary>V1.4: a list of cross-references is no caption, whatever its first word (gap 24).</summary>
    public static bool IsNumberedCaption(string text) => NumberedCaption().IsMatch(text) && !IsReferenceList(text);

    /// <summary>
    /// V1.4 (gap 24): a line that is a list of cross-references separated by semicolons - an index entry's locators,
    /// a "see" list - with at least two whole references; only its first and last items may be pieces of a wrapped
    /// reference. It points to places by their labels' numbers: not a caption, not a contents line, not a label.
    /// </summary>
    public static bool IsReferenceList(string text)
    {
        var items = text.Split(';').Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
        if (items.Length < 2) return false;
        for (var k = 1; k < items.Length - 1; k++)
            if (!ReferenceItem().IsMatch(items[k])) return false;
        return items.Count(t => ReferenceItem().IsMatch(t)) >= 2;
    }

    /// <summary>V1.4 (gap 25): an e-mail address, a web address, or a labelled phone / e-mail / web field.</summary>
    public static bool IsContactLine(string text) => ContactLine().IsMatch(text.Trim());
    public static bool IsStructuralLabel(string text) => StructuralLabelPrefix().IsMatch(text) || EnumeratorPrefix().IsMatch(text);

    /// <summary>A label-word structural label with its number ("Section III", "Sub-Clause 4.2") - not a bare enumerator.</summary>
    public static bool IsNumberedStructuralLabel(string text) => StructuralLabelPrefix().IsMatch(text.Trim());

    /// <summary>A structural label with nothing after its number ("Chapter I", "Sub-Clause 1.1.4").</summary>
    public static bool IsBareStructuralLabel(string text)
    {
        var m = StructuralLabelPrefix().Match(text.Trim());
        return m.Success && text.Trim().Length - m.Length <= 1;
    }

    /// <summary>An enumerator with no text of its own ("A.", "12.", "(iv)").</summary>
    public static bool IsBareEnumerator(string text) => BareEnumerator().IsMatch(text.Trim());

    /// <summary>Begins at the first item of an enumeration: 1., A., a), (i), 1.1 ...</summary>
    public static bool IsFirstEnumeratorItem(string text) => FirstEnumeratorItem().IsMatch(text.Trim());

    /// <summary>arabic / upper / roman / paren / null: which kind of enumerator leads the text.</summary>
    public static string? EnumeratorFamilyOf(string text)
    {
        var m = EnumeratorFamily().Match(text.Trim() + " ");
        if (!m.Success) return null;
        return m.Groups["arabic"].Success ? "arabic" : m.Groups["upper"].Success ? "upper" : m.Groups["roman"].Success ? "roman" : "paren";
    }

    public static bool IsFillInField(string text) => FillInPlaceholder().IsMatch(text);

    /// <summary>A whole occurrence in brackets or a long parenthesis: an editorial note to the template's user.</summary>
    public static bool IsBracketedNote(string text) => BracketedNote().IsMatch(text.Trim());
    public static bool IsFootnoteLead(string text) => FootnoteLead().IsMatch(text);
    public static bool IsParentheticalStatus(string text) => ParentheticalStatus().IsMatch(text.Trim());
    public static bool HasTrailingPageNumber(string text) => TrailingPageNumber().IsMatch(text.Trim());
    public static bool EndsSentence(string text) => text.TrimEnd().EndsWith('.') && !text.TrimEnd().EndsWith("..", StringComparison.Ordinal);
    public static bool EndsWithColon(string text) => text.TrimEnd().EndsWith(':');

    /// <summary>
    /// The text before a bracketed instruction that follows a label on the same line ("2. Other
    /// Proposers[INSTRUCTIONS: ..."), or null. The bracket must open after at least one word.
    /// </summary>
    public static string? LabelBeforeBracket(string text)
    {
        var at = text.IndexOf('[');
        if (at < 3) return null;
        var head = text[..at].TrimEnd();
        return head.Any(char.IsLetter) ? head : null;
    }

    /// <summary>A line that is only a connective word between parts of one title or between alternatives.</summary>
    public static bool IsConnectiveLine(string text) => ConnectiveLine().IsMatch(text.Trim());

    /// <summary>A line whose last word is a preposition, article or conjunction: it cannot end a title.</summary>
    public static bool EndsWithFunctionWord(string text) => FunctionWordEnd().IsMatch(text.TrimEnd());

    /// <summary>What follows a lead on its line opens with a bracketed instruction.</summary>
    public static bool RemainderIsBracketed(string text, string lead) =>
        text.Length > lead.Length && text[lead.Length..].TrimStart().StartsWith('[');

    /// <summary>Opening and closing square brackets on a line.</summary>
    public static (int Open, int Close) Brackets(string text) => (text.Count(c => c == '['), text.Count(c => c == ']'));

    /// <summary>Repetition key: case, spacing and numbers ignored ("22 X: March 31, 2025" repeats "23 X: ...").</summary>
    public static string RepetitionKey(string text) => Digits().Replace(Spaces().Replace(text.Trim().ToLowerInvariant(), ""), "#");

    public static string WithoutTrailingPageNumber(string text) => Spaces().Replace(TrailingPageNumber().Replace(text.Trim(), ""), "").TrimEnd('.').ToLowerInvariant();

    /// <summary>
    /// A normalized lead (spacing may differ) taken back to the exact leading substring of
    /// <paramref name="text"/>: non-space characters are matched one to one. Null when they differ.
    /// </summary>
    public static string? ExactLead(string text, string normalizedLead)
    {
        var want = normalizedLead.Where(c => !char.IsWhiteSpace(c)).ToArray();
        if (want.Length == 0) return null;
        var k = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            if (text[i] != want[k]) return null;
            if (++k == want.Length) return text[..(i + 1)];
        }
        return null;
    }
}

/// <summary>One document's occurrences and the statistics the document itself yields.</summary>
internal sealed class SourceEvidenceProfile
{
    public required string Media { get; init; }
    public required IReadOnlyList<SourceOccurrence> Occurrences { get; init; }
    public required double? BodyFontSize { get; init; }
    public required bool BodyBold { get; init; }
    public required bool BodyItalic { get; init; }
    public required string? BodyStyle { get; init; }
    public required int PageCount { get; init; }
    public required IReadOnlyDictionary<string, (int Count, int Pages, double BandShare)> Repetition { get; init; }

    /// <summary>Texts that some contents-like occurrence points to (text without its page number).</summary>
    public required IReadOnlySet<string> PointedTo { get; init; }

    public static SourceEvidenceProfile FromDocx(string path)
    {
        var document = new OpenXmlDocumentSource().Read(path);
        var byId = document.Paragraphs.ToDictionary(p => p.SourceId, StringComparer.Ordinal);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(DocumentSourceCatalogBuilder.FromSourceDocument(document)).ToArray();
        var occurrences = aliases.Select((a, i) =>
        {
            var p = byId[a.SourceId];
            var style = p.Style;
            var text = a.Text.Trim();
            return new SourceOccurrence(
                a.Alias, a.SourceId, i, text, "DOCX", null, style.FontSizePt, style.Bold, style.Italic, style.AllCaps,
                string.Equals(style.Alignment, "center", StringComparison.OrdinalIgnoreCase),
                style.StyleName ?? style.StyleId,
                style.BuiltInHeadingStyleLevel ?? (style.OutlineLevel is >= 0 and <= 8 ? style.OutlineLevel + 1 : null),
                p.Numbering.NumberLabel, p.Layout.TableDepth,
                p.InTableOfContents || LexicalShape.IsBuiltInContentsStyle(style.StyleId),
                1, 0, p.Layout.TableDepth > 0 && LexicalShape.IsFiguresOnly(text), null)
            {
                BoldLead = string.Equals(a.Text, p.Text, StringComparison.Ordinal) ? DocxBoldLead(p) : null,
                PageBreakBefore = p.Layout.PageBreakBefore,
            };
        }).ToArray();
        return Build("DOCX", occurrences, pageCount: 0);
    }

    /// <summary>The leading bold runs of a paragraph whose later text is not bold, trimmed; else null.</summary>
    private static string? DocxBoldLead(SourceParagraph p)
    {
        var spans = p.TextSpans.Where(s => s.End > s.Start).OrderBy(s => s.Start).ToArray();
        if (spans.Length < 2) return null;
        var end = 0;
        foreach (var s in spans)
        {
            var visible = p.Text[s.Start..s.End].Trim().Length > 0;
            if (!s.Bold && visible) break;
            end = s.End;
        }
        var lead = p.Text[..end].Trim();
        var rest = p.Text[end..].Trim();
        if (lead.Length == 0 || rest.Length == 0 || !lead.Any(char.IsLetterOrDigit)) return null;
        var leading = p.Text.Length - p.Text.TrimStart().Length;
        return p.Text[leading..].StartsWith(lead, StringComparison.Ordinal) ? lead : null;
    }

    /// <summary>V1.3 reads PDF_SOURCE_FACTS_V3; the atom universe is the same under every facts version.</summary>
    public static SourceEvidenceProfile FromPdf(string path, PdfSourceFactsVersion facts = PdfSourceFactsVersion.V3_RobustGlyphStatistics)
    {
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(path, facts).Atoms;
        Dictionary<string, PdfLine> lines;
        using (var document = UglyToad.PdfPig.PdfDocument.Open(path))
            lines = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3, facts)
                .GroupBy(PdfLineIdentity.Of, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var extents = lines.Values.GroupBy(l => l.Page).ToDictionary(g => g.Key, g => (Min: g.Min(l => l.Y), Max: g.Max(l => l.Y)));
        var rows = atoms.GroupBy(a => (a.Page, a.Row)).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Segment).ToArray());
        var occurrences = atoms.Select((a, i) =>
        {
            var l = lines[a.SourceId];
            var (min, max) = extents[a.Page];
            var row = rows[(a.Page, a.Row)];
            var band = max > min ? (max - l.Y) / (max - min) : 0.5;
            var text = a.Text.Trim();
            var lead = l.LeadingBoldPrefix.Length > 0 ? LexicalShape.ExactLead(text, l.LeadingBoldPrefix) : null;
            // A lead is only a lead when something that is not bold follows it on the line.
            if (lead is not null && text[lead.Length..].Trim().Length == 0) lead = null;
            // V1.3 (gap 10): a parenthetical status after a title ("... (Continued)") is part of the title, not body.
            if (lead is not null && LexicalShape.IsParentheticalSuffix(text, lead)) lead = null;
            return new SourceOccurrence(
                a.Alias, a.SourceId, i, text, "PDF", a.Page, Math.Round(l.FontSize, 1), l.BoldRatio >= 0.5 && lead is null, l.ItalicRatio >= 0.5,
                a.Text.Any(char.IsLetter) && a.Text.Where(char.IsLetter).All(char.IsUpper), false, null, null, null, 0, false,
                row.Length, Array.IndexOf(row, a), row.Any(o => o != a && LexicalShape.IsFiguresOnly(o.Text.Trim()) && o.Text.Any(char.IsDigit)), band)
            {
                Row = a.Row,
                Left = l.Left,
                Right = l.Right,
                Y = l.Y,
                BoldLead = lead,
                LeadGap = lead is null ? null : GapAfter(l, lead),
                MinFontSize = l.Typography?.Glyphs is { } g ? Math.Round(g.MinPointSize, 1) : null,
            };
        }).ToArray();
        return Build("PDF", occurrences, extents.Count);
    }

    /// <summary>
    /// The distance from the glyph that ends <paramref name="lead"/> to the next glyph of the line, from the line's glyph
    /// map; the lead's end is found by counting its non-space characters, so spacing differences do not matter.
    /// </summary>
    private static double? GapAfter(PdfLine line, string lead)
    {
        var want = lead.Count(c => !char.IsWhiteSpace(c));
        var map = line.Projection.SpanMap;
        var seen = 0;
        for (var k = 0; k < map.Count; k++)
        {
            seen += map[k].VerbatimLength;
            if (seen < want) continue;
            return k + 1 < map.Count ? map[k + 1].Left - map[k].Right : null;
        }
        return null;
    }

    private static SourceEvidenceProfile Build(string media, SourceOccurrence[] occurrences, int pageCount)
    {
        // Body text: the typography carrying most characters among sentence-length occurrences.
        var body = occurrences.Where(o => o.WordCount >= 8 && o.TableDepth == 0)
            .GroupBy(o => (Size: o.FontSize is { } s ? Math.Round(s * 2) / 2 : (double?)null, o.Bold, o.Italic, o.StyleName))
            .OrderByDescending(g => g.Sum(o => o.Text.Length)).First().Key;

        var repetition = occurrences.GroupBy(o => LexicalShape.RepetitionKey(o.Text)).ToDictionary(
            g => g.Key,
            g => (g.Count(), g.Select(o => o.Page ?? -1).Distinct().Count(),
                g.Count(o => o.PageBand is <= 0.06 or >= 0.94) / (double)g.Count()),
            StringComparer.Ordinal);

        var pointed = occurrences.Where(o => LexicalShape.HasTrailingPageNumber(o.Text) && !LexicalShape.IsFiguresOnly(o.Text))
            .Select(o => LexicalShape.WithoutTrailingPageNumber(o.Text)).Where(t => t.Length > 2).ToHashSet(StringComparer.Ordinal);

        return new SourceEvidenceProfile
        {
            Media = media,
            Occurrences = occurrences,
            BodyFontSize = body.Size,
            BodyBold = body.Bold,
            BodyItalic = body.Italic,
            BodyStyle = body.StyleName,
            PageCount = pageCount,
            Repetition = repetition,
            PointedTo = pointed,
        };
    }
}
