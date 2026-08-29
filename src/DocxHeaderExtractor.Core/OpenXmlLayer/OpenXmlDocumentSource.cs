using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>
/// Reads observed DOCX facts directly from OpenXML. This adapter deliberately does not construct
/// SlimParagraph or any candidate/policy/demotion state.
/// </summary>
public sealed class OpenXmlDocumentSource : IDocumentSource
{
    private static readonly Regex WhitespaceRx = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex TrailingPageNumberRx = new(@"^(?<title>.*\S)\s+(?<page>\d{1,4})$", RegexOptions.Compiled);
    private const int MinTypedTableOfContentsRunLength = 3;
    private readonly ExtractionOptions _options;

    public OpenXmlDocumentSource(ExtractionOptions? options = null) => _options = options ?? new ExtractionOptions();

    public SourceDocument Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = WordprocessingDocument.Open(stream, false);
        var main = doc.MainDocumentPart
                   ?? throw new InvalidOperationException($"File không có MainDocumentPart: {path}");
        var resolver = new StyleResolver(main);
        var paragraphs = new List<OpenXmlSourceParagraph>();
        var body = main.Document?.Body;
        if (body is not null)
        {
            var index = 0;
            foreach (var walked in ParagraphWalker.Enumerate(body, _options))
                paragraphs.Add(ParseParagraph(walked, resolver, index++));
        }

        NumberingResolver.Apply(main, paragraphs);
        MarkTypedTableOfContentsRuns(paragraphs);
        var headers = new List<string>();
        var footers = new List<string>();
        if (_options.IncludePageHeadersFooters)
        {
            foreach (var hp in main.HeaderParts) AddIfNotEmpty(headers, Normalize(GetText(hp.Header)));
            foreach (var fp in main.FooterParts) AddIfNotEmpty(footers, Normalize(GetText(fp.Footer)));
        }

        return new SourceDocument
        {
            DocumentId = path,
            FileName = Path.GetFileName(path),
            SourcePath = path,
            SourceKind = "docx",
            Paragraphs = ReadOnly(paragraphs.Select(ToSourceParagraph)),
            PageHeaders = ReadOnly(headers),
            PageFooters = ReadOnly(footers),
        };
    }

    private static OpenXmlSourceParagraph ParseParagraph(WalkedParagraph walked, StyleResolver resolver, int index)
    {
        var paragraph = walked.Element;
        var properties = paragraph.ParagraphProperties;
        var styleId = properties?.ParagraphStyleId?.Val?.Value ?? resolver.DefaultParagraphStyleId;
        var style = resolver.Resolve(styleId);
        var nestedTextBoxes = paragraph.Descendants<TextBoxContent>().ToHashSet();
        var runFormat = AggregateRunFormat(paragraph, nestedTextBoxes);
        var bold = runFormat.Bold ?? StyleResolver.OnOff(properties?.ParagraphMarkRunProperties?.GetFirstChild<Bold>()) ?? style?.Bold ?? false;
        var italic = runFormat.Italic ?? StyleResolver.OnOff(properties?.ParagraphMarkRunProperties?.GetFirstChild<Italic>()) ?? style?.Italic ?? false;
        var caps = runFormat.Caps ?? StyleResolver.OnOff(properties?.ParagraphMarkRunProperties?.GetFirstChild<Caps>()) ?? style?.AllCaps ?? false;
        var underline = runFormat.Underline ?? style?.Underline ?? false;
        var size = runFormat.FontSizePt
                   ?? StyleResolver.HalfPointToPt(properties?.ParagraphMarkRunProperties?.GetFirstChild<FontSize>()?.Val?.Value)
                   ?? style?.FontSizePt ?? resolver.DefaultFontSizePt;
        var built = BuildTextAndSpans(paragraph, nestedTextBoxes, style);
        if (!caps && built.Text.Length > 3 && HasLetters(built.Text) && built.Text == built.Text.ToUpperInvariant()) caps = true;
        var numbering = properties?.NumberingProperties;
        return new OpenXmlSourceParagraph
        {
            SourceId = walked.StableId,
            SourceOrdinal = index,
            Text = built.Text,
            TextSpans = built.Spans,
            LineBreakOffsets = built.LineBreaks,
            SourceSegments = built.Sources,
            StyleId = styleId,
            StyleName = style?.Name,
            BuiltInHeadingStyleLevel = BuiltInHeadingStyleIdentity.LevelFromResolvedStyle(style?.Name, styleId),
            OutlineLevel = properties?.OutlineLevel?.Val?.Value ?? style?.OutlineLevel,
            Bold = bold,
            Italic = italic,
            Underline = underline,
            AllCaps = caps,
            FontSizePt = size,
            Alignment = properties?.Justification?.Val?.InnerText ?? style?.Alignment,
            NumberingId = numbering?.NumberingId?.Val?.Value ?? style?.NumberingId,
            NumberingLevel = numbering?.NumberingLevelReference?.Val?.Value ?? style?.NumberingLevel,
            KeepNext = StyleResolver.OnOff(properties?.KeepNext) ?? style?.KeepNext ?? false,
            PageBreakBefore = StyleResolver.OnOff(properties?.PageBreakBefore) ?? style?.PageBreakBefore ?? false,
            InContentControl = paragraph.Ancestors<SdtElement>().Any(),
            TableDepth = walked.TableDepth,
            SectionIndex = walked.SectionIndex,
            InTableOfContents = IsTableOfContentsEntry(paragraph, style?.Name ?? styleId),
        };
    }

    private static SourceParagraph ToSourceParagraph(OpenXmlSourceParagraph paragraph) => new()
    {
        SourceId = string.IsNullOrWhiteSpace(paragraph.SourceId) ? $"p:{paragraph.SourceOrdinal}" : paragraph.SourceId,
        SourceOrdinal = paragraph.SourceOrdinal,
        Text = paragraph.Text,
        TextSpans = ReadOnly(paragraph.TextSpans),
        LineBreakOffsets = ReadOnly(paragraph.LineBreakOffsets),
        SourceSegments = ReadOnly(paragraph.SourceSegments),
        Style = new SourceStyleFacts
        {
            StyleId = paragraph.StyleId,
            StyleName = paragraph.StyleName,
            BuiltInHeadingStyleLevel = paragraph.BuiltInHeadingStyleLevel,
            OutlineLevel = paragraph.OutlineLevel,
            Bold = paragraph.Bold,
            Italic = paragraph.Italic,
            Underline = paragraph.Underline,
            AllCaps = paragraph.AllCaps,
            FontSizePt = paragraph.FontSizePt,
            Alignment = paragraph.Alignment,
        },
        Numbering = new SourceNumberingFacts
        {
            NumberingId = paragraph.NumberingId,
            NumberingLevel = paragraph.NumberingLevel,
            NumberLabel = paragraph.NumberLabel,
            NumberingFormat = paragraph.NumberingFormat,
            NumberingStyleHeadingLevel = paragraph.NumberingStyleLevel,
        },
        Layout = new SourceLayoutFacts
        {
            InContentControl = paragraph.InContentControl,
            KeepNext = paragraph.KeepNext,
            PageBreakBefore = paragraph.PageBreakBefore,
            TableDepth = paragraph.TableDepth,
            SectionIndex = paragraph.SectionIndex,
        },
        InTableOfContents = paragraph.InTableOfContents,
    };

    private static bool IsTableOfContentsEntry(Paragraph paragraph, string? styleName)
    {
        if (styleName is not null)
        {
            var normalized = styleName.Replace(" ", "");
            if (normalized.StartsWith("toc", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("tocheading", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return paragraph.Descendants<Hyperlink>().Any(link =>
        {
            var anchor = link.Anchor?.Value;
            return anchor is not null && (anchor.StartsWith("_Toc", StringComparison.OrdinalIgnoreCase) ||
                                          anchor.StartsWith("_heading", StringComparison.OrdinalIgnoreCase));
        });
    }

    // Preserve the legacy source fact for manually typed TOCs. A single numbered title is
    // insufficient; only a monotonic run is accepted, with a new run after a page reset.
    private static void MarkTypedTableOfContentsRuns(List<OpenXmlSourceParagraph> paragraphs)
    {
        var run = new List<OpenXmlSourceParagraph>();
        var pages = new List<int>();

        void Flush()
        {
            if (run.Count >= MinTypedTableOfContentsRunLength)
                foreach (var paragraph in run) paragraph.InTableOfContents = true;
            run.Clear();
            pages.Clear();
        }

        foreach (var paragraph in paragraphs)
        {
            if (string.IsNullOrWhiteSpace(paragraph.Text) || paragraph.TableDepth > 0 || paragraph.NumberingId is not null)
            {
                Flush();
                continue;
            }

            var match = TrailingPageNumberRx.Match(paragraph.Text.Trim());
            if (!match.Success || !int.TryParse(match.Groups["page"].Value, out var page))
            {
                Flush();
                continue;
            }
            if (pages.Count > 0 && page < pages[^1]) Flush();
            run.Add(paragraph);
            pages.Add(page);
        }
        Flush();
    }

    private readonly record struct RunFormat(bool? Bold, bool? Italic, bool? Underline, bool? Caps, double? FontSizePt);

    private static RunFormat AggregateRunFormat(Paragraph paragraph, IReadOnlySet<TextBoxContent> nestedTextBoxes)
    {
        var any = false;
        var allBold = true; var allItalic = true; var allUnderline = true; var allCaps = true;
        double? maxSize = null;
        foreach (var run in paragraph.Descendants<Run>())
        {
            if (run.Ancestors<TextBoxContent>().Any(nestedTextBoxes.Contains) || run.Ancestors<DeletedRun>().Any() ||
                !run.Descendants<Text>().Any(t => !string.IsNullOrWhiteSpace(t.Text))) continue;
            any = true;
            var properties = run.RunProperties;
            allBold &= StyleResolver.OnOff(properties?.Bold) ?? false;
            allItalic &= StyleResolver.OnOff(properties?.Italic) ?? false;
            allCaps &= StyleResolver.OnOff(properties?.Caps) ?? false;
            allUnderline &= properties?.Underline?.Val is { } underline &&
                            !string.Equals(underline.InnerText, "none", StringComparison.OrdinalIgnoreCase);
            if (StyleResolver.HalfPointToPt(properties?.FontSize?.Val?.Value) is { } size)
                maxSize = maxSize is null ? size : Math.Max(maxSize.Value, size);
        }
        return !any
            ? new RunFormat(null, null, null, null, null)
            : new RunFormat(allBold ? true : null, allItalic ? true : null, allUnderline ? true : null,
                allCaps ? true : null, maxSize);
    }

    private readonly record struct ParagraphText(string Text, IReadOnlyList<SourceTextRunSpan> Spans,
        IReadOnlyList<int> LineBreaks, IReadOnlyList<SourceSegment> Sources);

    private static ParagraphText BuildTextAndSpans(Paragraph paragraph, IReadOnlySet<TextBoxContent> excluded,
        ResolvedStyle? paragraphStyle)
    {
        var text = new StringBuilder();
        var spans = new List<SourceTextRunSpan>();
        var lineBreaks = new List<int>();
        var sources = new List<SourceSegment>();
        var runIndex = -1;
        foreach (var run in paragraph.Descendants<Run>())
        {
            if (run.Ancestors<TextBoxContent>().Any(excluded.Contains) || run.Ancestors<DeletedRun>().Any()) continue;
            runIndex++;
            var (raw, breaks) = GetRunText(run, excluded);
            if (raw.Length == 0) continue;
            var properties = run.RunProperties;
            var bold = StyleResolver.OnOff(properties?.Bold) ?? paragraphStyle?.Bold ?? false;
            var italic = StyleResolver.OnOff(properties?.Italic) ?? paragraphStyle?.Italic ?? false;
            var underline = properties?.Underline?.Val is { } u
                ? !string.Equals(u.InnerText, "none", StringComparison.OrdinalIgnoreCase)
                : paragraphStyle?.Underline ?? false;
            var size = StyleResolver.HalfPointToPt(properties?.FontSize?.Val?.Value) ?? paragraphStyle?.FontSizePt;
            for (var rawIndex = 0; rawIndex < raw.Length; rawIndex++)
            {
                if (breaks.Contains(rawIndex) && (lineBreaks.Count == 0 || lineBreaks[^1] != text.Length)) lineBreaks.Add(text.Length);
                var c = raw[rawIndex];
                if (char.IsWhiteSpace(c))
                {
                    if (text.Length == 0 || text[^1] == ' ') continue;
                    Append(' ', rawIndex);
                }
                else Append(c, rawIndex);

                void Append(char value, int sourceOffset)
                {
                    var start = text.Length;
                    var continues = sources.Count > 0 && sources[^1].End == start && sources[^1].RunIndex == runIndex &&
                                    sources[^1].RawStart + (start - sources[^1].Start) == sourceOffset;
                    if (continues) sources[^1] = sources[^1] with { End = start + 1 };
                    else sources.Add(new SourceSegment(start, start + 1, runIndex, sourceOffset));
                    text.Append(value);
                    if (spans.Count > 0 && spans[^1].End == start && spans[^1].Bold == bold && spans[^1].Italic == italic &&
                        spans[^1].Underline == underline && spans[^1].FontSizePt == size)
                        spans[^1] = spans[^1] with { End = start + 1 };
                    else spans.Add(new SourceTextRunSpan(start, start + 1, bold, italic, underline, size));
                }
            }
        }
        if (text.Length > 0 && text[^1] == ' ')
        {
            text.Length--;
            if (spans.Count > 0) spans[^1] = spans[^1] with { End = Math.Min(spans[^1].End, text.Length) };
            if (sources.Count > 0) sources[^1] = sources[^1] with { End = Math.Min(sources[^1].End, text.Length) };
            spans.RemoveAll(span => span.End <= span.Start);
            sources.RemoveAll(segment => segment.End <= segment.Start);
        }
        return new ParagraphText(text.ToString(), spans, lineBreaks.Select(b => Math.Clamp(b, 0, text.Length)).Distinct().ToList(), sources);
    }

    private static (string Raw, HashSet<int> BreakOffsets) GetRunText(Run run, IReadOnlySet<TextBoxContent> excluded)
    {
        var text = new StringBuilder();
        var breaks = new HashSet<int>();
        foreach (var element in run.Descendants())
        {
            if (element.Ancestors<TextBoxContent>().Any(excluded.Contains)) continue;
            switch (element)
            {
                case Text t when !t.Ancestors<DeletedRun>().Any(): text.Append(t.Text); break;
                case TabChar: text.Append('\t'); break;
                case Break: breaks.Add(text.Length); text.Append(' '); break;
                case NoBreakHyphen: text.Append('-'); break;
            }
        }
        return (text.ToString(), breaks);
    }

    private static string GetText(OpenXmlElement? root)
    {
        if (root is null) return string.Empty;
        var text = new StringBuilder();
        foreach (var element in root.Descendants())
            switch (element)
            {
                case Text t when !t.Ancestors<DeletedRun>().Any(): text.Append(t.Text); break;
                case TabChar: text.Append('\t'); break;
                case Break: text.Append(' '); break;
                case NoBreakHyphen: text.Append('-'); break;
            }
        return text.ToString();
    }

    private static string Normalize(string value) => WhitespaceRx.Replace(value, " ").Trim();
    private static bool HasLetters(string value) => value.Any(char.IsLetter);
    private static void AddIfNotEmpty(List<string> values, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !values.Contains(value)) values.Add(value);
    }
    private static ReadOnlyCollection<T> ReadOnly<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
}

internal sealed class OpenXmlSourceParagraph
{
    public required string SourceId { get; init; }
    public required int SourceOrdinal { get; init; }
    public required string Text { get; init; }
    public IReadOnlyList<SourceTextRunSpan> TextSpans { get; init; } = [];
    public IReadOnlyList<int> LineBreakOffsets { get; init; } = [];
    public IReadOnlyList<SourceSegment> SourceSegments { get; init; } = [];
    public string? StyleId { get; init; }
    public string? StyleName { get; init; }
    public int? BuiltInHeadingStyleLevel { get; init; }
    public int? OutlineLevel { get; init; }
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool AllCaps { get; init; }
    public double? FontSizePt { get; init; }
    public string? Alignment { get; init; }
    public int? NumberingId { get; init; }
    public int? NumberingLevel { get; init; }
    public string? NumberLabel { get; set; }
    public string? NumberingFormat { get; set; }
    public int? NumberingStyleLevel { get; set; }
    public bool InContentControl { get; init; }
    public bool KeepNext { get; init; }
    public bool PageBreakBefore { get; init; }
    public int TableDepth { get; init; }
    public int SectionIndex { get; init; }
    public bool InTableOfContents { get; set; }
}
