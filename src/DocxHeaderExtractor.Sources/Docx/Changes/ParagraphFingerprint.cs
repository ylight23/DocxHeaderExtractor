using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Contracts.Docx;
using DocxHeaderExtractor.Sources.Docx.OpenXmlParser;

namespace DocxHeaderExtractor.Sources.Docx.Changes;

/// <summary>
/// Direct character formatting of one run, with its offsets in the paragraph's raw text (UTF-16, end exclusive). Only what the run
/// declares itself: style inheritance is not resolved here, because the display rule for effective formatting is not decided yet.
/// </summary>
public sealed record RunFormat(int Start, int End, bool? Bold, bool? Italic, string? Underline,
    string? FontAscii, string? FontHighAnsi, string? FontEastAsia, string? FontComplexScript, string? FontSize, string? Color,
    EffectiveFormat? Effective = null);

/// <summary>
/// One paragraph of a DOCX file as the comparison reads it. <see cref="RawText"/> keeps tabs, line breaks, page breaks and non-breaking hyphens
/// as their own characters ('\t', '\n', '\f', U+2011). <see cref="NormalizedKey"/> is only a matching key and never replaces the raw text.
/// <see cref="Location"/> is the same path the heading parser uses (<see cref="ParagraphWalker"/>), so both readers agree on identity.
/// <see cref="ParaId"/> is a signal, not an identity. <see cref="NumId"/> is the raw instance id, which Word renumbers on save;
/// <see cref="NumberingKey"/> is the definition it points to (see <see cref="NumberingKeys"/>), null when there is no direct numbering or when
/// <see cref="NumberingResolved"/> is false. <see cref="EffectiveNumId"/>/<see cref="EffectiveIlvl"/> are the list the paragraph is numbered
/// in, directly or through its paragraph style (<see cref="NumberingSource"/> "direct" or "style"); <see cref="NumberingKey"/> and the list
/// relation follow them. <see cref="FromStyle"/> is what the paragraph style gives (alignment, outline level, numbering, gaps).
/// </summary>
public sealed record ParagraphFingerprint(int Ordinal, string Location, string Container, string? ParaId, string RawText, IReadOnlyList<int> LineBreakOffsets,
    string NormalizedKey, string KeyHash, string? NumId, int? Ilvl, string? StyleId, string? Alignment, IReadOnlyList<RunFormat> Runs,
    TableCellFacts? Cell = null, string? NumberingKey = null, bool NumberingResolved = true, string? NumberingRelation = null,
    string? EffectiveNumId = null, int? EffectiveIlvl = null, string? NumberingSource = null, StyleParagraphFormat? FromStyle = null);

/// <summary>
/// The table cell a paragraph sits in, as declared: horizontal span (gridSpan), vertical merge (restart/continue), legacy horizontal merge,
/// and how many tables enclose it (2 or more is a nested table). Merged cells are not resolved into a grid; they are reported so the
/// comparison can mark its cell pairing uncertain.
/// </summary>
public sealed record TableCellFacts(int? GridSpan, string? VerticalMerge, bool HorizontalMerge, int TableDepth)
{
    public bool Merged => GridSpan > 1 || VerticalMerge is not null || HorizontalMerge;
}

/// <summary>
/// A DOCX file read for comparison: its SHA-256, its paragraphs in the heading parser's order, and the regions that are present but not read
/// in full. Anything listed in <see cref="UnsupportedRegions"/> must be shown as not compared, never as unchanged.
/// </summary>
public sealed record DocxVersion(string FileSha256, IReadOnlyList<ParagraphFingerprint> Paragraphs, IReadOnlyList<string> UnsupportedRegions)
{
    /// <summary>What this reader takes from each style (see <see cref="StyleContext.Definitions"/>), to name the styles that changed.</summary>
    public IReadOnlyDictionary<string, string> StyleDefinitions { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Reads a DOCX read-only. Deterministic code: no model call, no change to the heading lane. Paragraphs come from <see cref="ParagraphWalker"/>,
/// so text boxes are separate paragraphs and are never merged into the anchor paragraph. Accepted view: deleted and moved-away text is left out,
/// inserted and moved-to text is kept. Tracked changes are also reported as unsupported, so the accepted view is never read as "unchanged".
/// </summary>
public static class DocxVersionReader
{
    const string W14 = "http://schemas.microsoft.com/office/word/2010/wordml";
    static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static DocxVersion Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // One read: the hash and the parse see the same bytes, so a file replaced mid-read cannot pair one version's hash with another's text.
        var bytes = File.ReadAllBytes(path);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var stream = new MemoryStream(bytes, writable: false);
        using var document = WordprocessingDocument.Open(stream, isEditable: false);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("DOCX_MAIN_PART_MISSING");
        var body = main.Document?.Body ?? throw new InvalidDataException("DOCX_BODY_MISSING");
        var walked = ParagraphWalker.Enumerate(body, new ExtractionOptions()).ToArray();
        var styles = new StyleContext(main);
        var numbering = new NumberingKeys(main);
        var paragraphs = NumberingKeys.Relate(walked.Select((item, ordinal) => Fingerprint(ordinal, item.StableId, item.Element, styles, numbering)).ToArray(),
            numbering);
        return new DocxVersion(sha, paragraphs, UnsupportedRegions(main, body)) { StyleDefinitions = styles.Definitions() };
    }

    internal static ParagraphFingerprint Fingerprint(int ordinal, string location, Paragraph paragraph, StyleContext styles, NumberingKeys numberingKeys)
    {
        var runs = new List<RunFormat>();
        var lineBreaks = new List<int>();
        var raw = new StringBuilder();
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        foreach (var run in OwnRuns(paragraph))
        {
            var start = raw.Length;
            foreach (var element in run.ChildElements)
                AppendContent(element, raw, lineBreaks, run);
            var end = raw.Length;
            if (end > start) runs.Add(FormatOf(run, start, end, styles, styleId));
        }
        var text = raw.ToString();
        var key = NormalizeKey(text);
        var properties = paragraph.ParagraphProperties;
        var numbering = properties?.NumberingProperties;
        var id = paragraph.GetAttributes().FirstOrDefault(a => a.LocalName == "paraId" && a.NamespaceUri == W14).Value;
        var numId = numbering?.NumberingId?.Val?.Value.ToString();
        var ilvl = numbering?.NumberingLevelReference?.Val?.Value;
        // Numbering through the paragraph style: the style's list, at its level, or the level of that list that names the style, else level 0.
        var fromStyle = styles.ResolveParagraph(styleId);
        var (effectiveNumId, effectiveIlvl, source) = numId is not null ? (numId, ilvl, "direct")
            : fromStyle.NumId is { } styleNumId ? (styleNumId, fromStyle.Ilvl ?? numberingKeys.LevelForStyle(styleNumId, styleId) ?? 0, "style")
            : ((string?)null, (int?)null, (string?)null);
        var numberingKey = numberingKeys.Key(effectiveNumId, effectiveIlvl, out var numberingResolved);
        // A style chain that could not be read may hide the numbering a paragraph without its own gets: not resolved.
        if (numId is null && fromStyle.Gaps.Count > 0) numberingResolved = false;
        return new ParagraphFingerprint(
            ordinal,
            location,
            Container(location),
            string.IsNullOrEmpty(id) ? null : id,
            text,
            lineBreaks,
            key,
            Hash(key),
            numId,
            ilvl,
            properties?.ParagraphStyleId?.Val?.Value,
            properties?.Justification?.Val?.InnerText is { Length: > 0 } alignment ? alignment : null,
            runs,
            CellFacts(paragraph),
            numberingKey,
            numberingResolved,
            EffectiveNumId: effectiveNumId,
            EffectiveIlvl: effectiveIlvl,
            NumberingSource: source,
            FromStyle: fromStyle);
    }

    static TableCellFacts? CellFacts(Paragraph paragraph)
    {
        if (paragraph.Ancestors<TableCell>().FirstOrDefault() is not { } cell) return null;
        var properties = cell.TableCellProperties;
        var verticalMerge = properties?.VerticalMerge is { } merge ? merge.Val?.InnerText is { Length: > 0 } value ? value : "continue" : null;
        return new TableCellFacts(properties?.GridSpan?.Val?.Value, verticalMerge, properties?.HorizontalMerge is not null,
            paragraph.Ancestors<Table>().Count());
    }

    /// <summary>
    /// Runs that belong to this paragraph itself: not inside a text box (those are their own paragraphs), not in a deleted or moved-away range.
    /// </summary>
    internal static IEnumerable<Run> OwnRuns(Paragraph paragraph) =>
        paragraph.Descendants<Run>().Where(run =>
            run.Ancestors<Paragraph>().FirstOrDefault() == paragraph
            && !run.Ancestors<DeletedRun>().Any()
            && !run.Ancestors().Any(a => a.LocalName == "moveFrom"));

    /// <summary>Appends one run child in the parser's own mapping, but keeps each break as its own character.</summary>
    static void AppendContent(OpenXmlElement element, StringBuilder raw, List<int> lineBreaks, Run run)
    {
        switch (element)
        {
            case Text text: raw.Append(text.Text); break;
            case DeletedText: break;
            case TabChar: raw.Append('\t'); break;
            case Break br when br.Type is null || br.Type.Value == BreakValues.TextWrapping:
                lineBreaks.Add(raw.Length);
                raw.Append('\n');
                break;
            case Break br when br.Type?.Value == BreakValues.Column: raw.Append('\v'); break;
            case Break: raw.Append('\f'); break;
            case CarriageReturn:
                lineBreaks.Add(raw.Length);
                raw.Append('\n');
                break;
            case NoBreakHyphen: raw.Append('‑'); break;
        }
    }

    static RunFormat FormatOf(Run run, int start, int end, StyleContext styles, string? paragraphStyleId)
    {
        var p = run.RunProperties;
        var fonts = p?.RunFonts;
        return new RunFormat(start, end,
            p?.Bold is { } bold ? bold.Val?.Value ?? true : null,
            p?.Italic is { } italic ? italic.Val?.Value ?? true : null,
            p?.Underline?.Val?.InnerText is { Length: > 0 } underline ? underline : null,
            fonts?.Ascii?.Value,
            fonts?.HighAnsi?.Value,
            fonts?.EastAsia?.Value,
            fonts?.ComplexScript?.Value,
            p?.FontSize?.Val?.Value,
            p?.Color?.Val?.Value,
            styles.Resolve(run, paragraphStyleId));
    }

    /// <summary>Matching key only: Unicode NFC and whitespace runs collapsed. The raw text is kept as written.</summary>
    internal static string NormalizeKey(string raw) =>
        Whitespace.Replace(raw.Normalize(NormalizationForm.FormC), " ").Trim();

    internal static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>Container from the walker's path: a text box and a table cell are both nested paths, so the innermost one wins.</summary>
    static string Container(string location) =>
        location.Contains("/txbxContent[", StringComparison.Ordinal) ? "text_box" :
        location.Contains("/tbl[", StringComparison.Ordinal) ? "table" : "body";

    static IReadOnlyList<string> UnsupportedRegions(MainDocumentPart main, Body body)
    {
        var regions = new List<string>();
        if (body.Descendants<TextBoxContent>().Any()) regions.Add("text_box");
        if (body.Descendants<Drawing>().Any()) regions.Add("drawing_or_image");
        if (body.Descendants<InsertedRun>().Any() || body.Descendants<DeletedRun>().Any()
            || body.Descendants().Any(e => e.LocalName is "moveFrom" or "moveTo")) regions.Add("tracked_changes");
        if (body.Descendants<RunPropertiesChange>().Any() || body.Descendants<ParagraphPropertiesChange>().Any()) regions.Add("tracked_formatting");
        if (body.Descendants<GridSpan>().Any(g => g.Val?.Value > 1) || body.Descendants<VerticalMerge>().Any() || body.Descendants<HorizontalMerge>().Any())
            regions.Add("merged_table_cells");
        if (body.Descendants<Table>().Any(t => t.Ancestors<Table>().Any())) regions.Add("nested_tables");
        // Word commonly creates note parts containing only the required separator entries.
        // Those are not user notes, but a real note or dangling reference must remain a gap.
        if (body.Descendants<FootnoteReference>().Any() || (main.FootnotesPart is { } footnotes &&
            (footnotes.Footnotes is null || footnotes.Footnotes.Elements<Footnote>().Any(n => !IsNoteSeparator(n))))) regions.Add("footnotes");
        if (body.Descendants<EndnoteReference>().Any() || (main.EndnotesPart is { } endnotes &&
            (endnotes.Endnotes is null || endnotes.Endnotes.Elements<Endnote>().Any(n => !IsNoteSeparator(n))))) regions.Add("endnotes");
        if (main.WordprocessingCommentsPart is not null) regions.Add("comments");
        if (main.HeaderParts.Any()) regions.Add("headers");
        if (main.FooterParts.Any()) regions.Add("footers");
        return regions;
    }

    static bool IsNoteSeparator(OpenXmlElement note)
    {
        const string wordprocessing = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var type = note.GetAttributes().FirstOrDefault(a => a.LocalName == "type" && a.NamespaceUri == wordprocessing).Value;
        return type is "separator" or "continuationSeparator";
    }
}
