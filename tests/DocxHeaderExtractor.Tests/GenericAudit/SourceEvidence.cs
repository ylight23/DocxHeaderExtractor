using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests.GenericAudit;

/// <summary>
/// Layer A - Source Evidence Profile (GENERIC_AUDIT_ENGINE_V1).
/// <para>
/// Turns any source, DOCX or PDF, into one list of occurrences in the coordinate universe Gold uses
/// (the DOCX alias catalog; the PDF structured lane's atoms), each carrying what the source shows about
/// it, and derives document-level statistics from the document itself: the body text's typography,
/// repetition of the same normalized text, the band of the page an occurrence sits in, and the content
/// that follows it. It records evidence only. No threshold here belongs to a document, and nothing
/// here says what is a heading - that is Layer B's hypothesis and the reviewer's decision.
/// </para>
/// <para>
/// The lexical shapes below are the only text patterns in the engine, and they are shapes common to
/// documents in general (a date, a numbered caption, a numbered structural label, a fill-in field, a
/// figures-only cell). None names a heading of any particular document.
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
    public int WordCount { get; } = Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>General lexical shapes: evidence, never a membership list.</summary>
internal static partial class LexicalShape
{
    [GeneratedRegex(@"^[\s$€£¥(),.%\d—–\-+*/#]+$")] private static partial Regex FiguresOnly();
    [GeneratedRegex(@"^(?:\(?[A-Z][a-z]+\.?\s+\d{1,2},?\s+\d{4}\)?|\d{1,2}\s+[A-Z][a-z]+\s+\d{4}|[A-Z][a-z]+\s+\d{4}|\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{2,4})$")] private static partial Regex DateOnly();
    [GeneratedRegex(@"^(?:Table|Figure|Chart|Exhibit|Graph|Box|Bảng|Hình|Biểu đồ)\s*[A-Z]?\d+(?:\.\d+)*\s*[:.\-–—]?")] private static partial Regex NumberedCaption();
    [GeneratedRegex(@"^(?:Part|Chapter|Section|Sub-?Clause|Clause|Article|Annex|Appendix|Schedule|Note|Attachment|Exhibit|Title|Book|Phần|Chương|Mục|Điều|Phụ lục|Tiểu mục)\s+(?:[IVXLCDM]+|[A-Z]|\d+(?:[.\s]\d+)*)(?:\b|$)", RegexOptions.IgnoreCase)] private static partial Regex StructuralLabelPrefix();
    [GeneratedRegex(@"^(?:\d+(?:\.\d+)*\.?|[A-Z]\.|[IVXLC]+\.|\(\w{1,3}\))\s+\S")] private static partial Regex EnumeratorPrefix();
    [GeneratedRegex(@"\[\s*(?:insert|enter|name|date|specify)|_{4,}|\.{6,}", RegexOptions.IgnoreCase)] private static partial Regex FillInPlaceholder();
    [GeneratedRegex(@"^(?:[a-z][.)]\s|\([a-z]\)\s|\*\s|[ᵃᵇᶜᵈᵉ]|\d{1,2}\s+[A-Z])")] private static partial Regex FootnoteLead();
    [GeneratedRegex(@"^\(\s*[A-Za-z][A-Za-z ]{1,30}\)$")] private static partial Regex ParentheticalStatus();
    [GeneratedRegex(@"(?:\.{3,}|\s)\d{1,4}$")] private static partial Regex TrailingPageNumber();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\d+")] private static partial Regex Digits();

    public static bool IsFiguresOnly(string text) => FiguresOnly().IsMatch(text);

    /// <summary>Word's built-in contents-entry styles (TOC 1..9): a naming convention of the format, not a heading text.</summary>
    public static bool IsBuiltInContentsStyle(string? styleId) => styleId is not null && styleId.StartsWith("TOC", StringComparison.OrdinalIgnoreCase);
    public static bool IsDate(string text) => DateOnly().IsMatch(text.Trim());
    public static bool IsNumberedCaption(string text) => NumberedCaption().IsMatch(text);
    public static bool IsStructuralLabel(string text) => StructuralLabelPrefix().IsMatch(text) || EnumeratorPrefix().IsMatch(text);

    /// <summary>A structural label with nothing after its number ("Chapter I", "Sub-Clause 1.1.4").</summary>
    public static bool IsBareStructuralLabel(string text)
    {
        var m = StructuralLabelPrefix().Match(text.Trim());
        return m.Success && text.Trim().Length - m.Length <= 1;
    }

    public static bool IsFillInField(string text) => FillInPlaceholder().IsMatch(text);
    public static bool IsFootnoteLead(string text) => FootnoteLead().IsMatch(text);
    public static bool IsParentheticalStatus(string text) => ParentheticalStatus().IsMatch(text.Trim());
    public static bool HasTrailingPageNumber(string text) => TrailingPageNumber().IsMatch(text.Trim());
    public static bool EndsSentence(string text) => text.TrimEnd().EndsWith('.') && !text.TrimEnd().EndsWith("..", StringComparison.Ordinal);
    public static bool EndsWithColon(string text) => text.TrimEnd().EndsWith(':');

    /// <summary>Repetition key: case, spacing and numbers ignored ("22 X: March 31, 2025" repeats "23 X: ...").</summary>
    public static string RepetitionKey(string text) => Digits().Replace(Spaces().Replace(text.Trim().ToLowerInvariant(), ""), "#");

    public static string WithoutTrailingPageNumber(string text) => Spaces().Replace(TrailingPageNumber().Replace(text.Trim(), ""), "").TrimEnd('.').ToLowerInvariant();
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
            return new SourceOccurrence(
                a.Alias, a.SourceId, i, a.Text.Trim(), "DOCX", null, style.FontSizePt, style.Bold, style.Italic, style.AllCaps,
                string.Equals(style.Alignment, "center", StringComparison.OrdinalIgnoreCase),
                style.StyleName ?? style.StyleId,
                style.BuiltInHeadingStyleLevel ?? (style.OutlineLevel is >= 0 and <= 8 ? style.OutlineLevel + 1 : null),
                p.Numbering.NumberLabel, p.Layout.TableDepth,
                p.InTableOfContents || LexicalShape.IsBuiltInContentsStyle(style.StyleId),
                1, 0, p.Layout.TableDepth > 0 && LexicalShape.IsFiguresOnly(a.Text.Trim()), null);
        }).ToArray();
        return Build("DOCX", occurrences, pageCount: 0);
    }

    public static SourceEvidenceProfile FromPdf(string path)
    {
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(path).Atoms;
        Dictionary<string, PdfLine> lines;
        using (var document = UglyToad.PdfPig.PdfDocument.Open(path))
            lines = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3)
                .GroupBy(PdfLineIdentity.Of, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var extents = lines.Values.GroupBy(l => l.Page).ToDictionary(g => g.Key, g => (Min: g.Min(l => l.Y), Max: g.Max(l => l.Y)));
        var rows = atoms.GroupBy(a => (a.Page, a.Row)).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Segment).ToArray());
        var occurrences = atoms.Select((a, i) =>
        {
            var l = lines[a.SourceId];
            var (min, max) = extents[a.Page];
            var row = rows[(a.Page, a.Row)];
            var band = max > min ? (max - l.Y) / (max - min) : 0.5;
            return new SourceOccurrence(
                a.Alias, a.SourceId, i, a.Text.Trim(), "PDF", a.Page, Math.Round(l.FontSize, 1), l.BoldRatio >= 0.5, l.ItalicRatio >= 0.5,
                a.Text.Any(char.IsLetter) && a.Text.Where(char.IsLetter).All(char.IsUpper), false, null, null, null, 0, false,
                row.Length, Array.IndexOf(row, a), row.Any(o => o != a && LexicalShape.IsFiguresOnly(o.Text.Trim()) && o.Text.Any(char.IsDigit)), band);
        }).ToArray();
        return Build("PDF", occurrences, extents.Count);
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
