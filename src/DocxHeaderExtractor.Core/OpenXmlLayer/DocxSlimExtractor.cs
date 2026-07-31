using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>
/// Đọc .docx bằng OpenXML SDK và rút ra danh sách đoạn đã tinh gọn.
/// Chỉ giữ những thuộc tính liên quan tới cấu trúc: style, outlineLvl, bold/caps/size,
/// canh lề, numbering, keepNext, pageBreakBefore.
/// </summary>
public sealed class DocxSlimExtractor
{
    private static readonly Regex WhitespaceRx = new(@"\s+", RegexOptions.Compiled);

    private readonly ExtractionOptions _options;

    public DocxSlimExtractor(ExtractionOptions? options = null) => _options = options ?? new ExtractionOptions();

    public SlimDocument Extract(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var main = doc.MainDocumentPart
                   ?? throw new InvalidOperationException($"File không có MainDocumentPart: {path}");

        var resolver = new StyleResolver(main);
        var paragraphs = new List<SlimParagraph>();
        var state = new WalkState(resolver, _options, paragraphs);

        var body = main.Document?.Body;
        if (body is not null) Walk(body, state, tableDepth: 0);

        var bodySize = EstimateBodyFontSize(paragraphs) ?? resolver.DefaultFontSizePt;
        foreach (var p in paragraphs) p.BodyFontSizePt = bodySize;

        foreach (var p in paragraphs) HeadingHeuristics.Classify(p, _options);
        PostProcess(paragraphs);

        var headers = new List<string>();
        var footers = new List<string>();
        if (_options.IncludePageHeadersFooters)
        {
            foreach (var hp in main.HeaderParts)
                AddIfNotEmpty(headers, Normalize(GetText(hp.Header)));
            foreach (var fp in main.FooterParts)
                AddIfNotEmpty(footers, Normalize(GetText(fp.Footer)));
        }

        return new SlimDocument
        {
            FileName = Path.GetFileName(path),
            SourcePath = path,
            Paragraphs = paragraphs,
            DefaultFontSizePt = resolver.DefaultFontSizePt,
            PageHeaders = headers,
            PageFooters = footers,
        }.Build();
    }

    private sealed class WalkState(StyleResolver resolver, ExtractionOptions options, List<SlimParagraph> sink)
    {
        public StyleResolver Resolver { get; } = resolver;
        public ExtractionOptions Options { get; } = options;
        public List<SlimParagraph> Sink { get; } = sink;
        public int Index;
        public int SectionIndex;
    }

    private void Walk(OpenXmlElement parent, WalkState state, int tableDepth)
    {
        foreach (var child in parent.ChildElements)
        {
            switch (child)
            {
                case Paragraph p:
                    state.Sink.Add(BuildParagraph(p, state, tableDepth));
                    if (p.ParagraphProperties?.SectionProperties is not null) state.SectionIndex++;
                    break;

                case Table t when _options.IncludeTables:
                    Walk(t, state, tableDepth + 1);
                    break;

                case Table:
                    break;

                case SectionProperties:
                    state.SectionIndex++;
                    break;

                default:
                    // sdt, customXml, TableRow, TableCell, bookmark container… – đệ quy nếu còn đoạn bên trong.
                    if (child.HasChildren && child.Descendants<Paragraph>().Any())
                        Walk(child, state, tableDepth);
                    break;
            }
        }
    }

    private static SlimParagraph BuildParagraph(Paragraph p, WalkState state, int tableDepth)
    {
        var pPr = p.ParagraphProperties;
        var styleId = pPr?.ParagraphStyleId?.Val?.Value ?? state.Resolver.DefaultParagraphStyleId;
        var style = state.Resolver.Resolve(styleId);

        // Định dạng trực tiếp trên đoạn ghi đè style; nếu không có thì lấy từ style.
        var markRun = pPr?.ParagraphMarkRunProperties;
        var runFmt = AggregateRunFormat(p);

        bool bold = runFmt.Bold
                    ?? StyleResolver.OnOff(markRun?.GetFirstChild<Bold>())
                    ?? style?.Bold ?? false;
        bool italic = runFmt.Italic
                      ?? StyleResolver.OnOff(markRun?.GetFirstChild<Italic>())
                      ?? style?.Italic ?? false;
        bool caps = runFmt.Caps
                    ?? StyleResolver.OnOff(markRun?.GetFirstChild<Caps>())
                    ?? style?.AllCaps ?? false;
        bool underline = runFmt.Underline ?? style?.Underline ?? false;
        double? size = runFmt.FontSizePt
                       ?? StyleResolver.HalfPointToPt(markRun?.GetFirstChild<FontSize>()?.Val?.Value)
                       ?? style?.FontSizePt
                       ?? state.Resolver.DefaultFontSizePt;

        var text = Normalize(GetText(p));

        // Chữ hoa toàn bộ do người dùng gõ tay cũng tính là AllCaps.
        if (!caps && text.Length > 3 && HasLetters(text) && text == text.ToUpperInvariant())
            caps = true;

        var alignment = pPr?.Justification?.Val?.InnerText ?? style?.Alignment;
        var numPr = pPr?.NumberingProperties;

        return new SlimParagraph
        {
            Index = state.Index++,
            Text = text,
            StyleId = styleId,
            StyleName = style?.Name,
            OutlineLevel = pPr?.OutlineLevel?.Val?.Value ?? style?.OutlineLevel,
            Bold = bold,
            Italic = italic,
            Underline = underline,
            AllCaps = caps,
            FontSizePt = size,
            BodyFontSizePt = state.Resolver.DefaultFontSizePt,   // sẽ được ghi đè bằng cỡ chữ thân bài thực tế
            Alignment = alignment,
            NumberingId = numPr?.NumberingId?.Val?.Value ?? style?.NumberingId,
            NumberingLevel = numPr?.NumberingLevelReference?.Val?.Value ?? style?.NumberingLevel,
            KeepNext = StyleResolver.OnOff(pPr?.KeepNext) ?? style?.KeepNext ?? false,
            PageBreakBefore = StyleResolver.OnOff(pPr?.PageBreakBefore) ?? style?.PageBreakBefore ?? false,
            TableDepth = tableDepth,
            SectionIndex = state.SectionIndex,
            InTableOfContents = IsTableOfContentsEntry(p, style?.Name ?? styleId),
        };
    }

    /// <summary>
    /// Nhận diện dòng mục lục. Word và Google Docs đều bọc mỗi dòng mục lục trong
    /// w:hyperlink trỏ tới neo của tiêu đề (_Toc… hoặc _heading…); ngoài ra còn có
    /// nhóm style TOC1..TOC9. Cả hai đều chính xác hơn nhiều so với đoán theo số trang cuối dòng.
    /// </summary>
    private static bool IsTableOfContentsEntry(Paragraph p, string? styleName)
    {
        if (styleName is not null)
        {
            var s = styleName.Replace(" ", "");
            if (s.StartsWith("toc", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("tocheading", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var link in p.Descendants<Hyperlink>())
        {
            var anchor = link.Anchor?.Value;
            if (anchor is null) continue;
            if (anchor.StartsWith("_Toc", StringComparison.OrdinalIgnoreCase) ||
                anchor.StartsWith("_heading", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private readonly record struct RunFormat(bool? Bold, bool? Italic, bool? Underline, bool? Caps, double? FontSizePt);

    /// <summary>
    /// Gộp định dạng của các run có chữ: chỉ coi là bold/caps khi TOÀN BỘ đoạn như vậy.
    /// Cỡ chữ lấy giá trị lớn nhất.
    /// </summary>
    private static RunFormat AggregateRunFormat(Paragraph p)
    {
        bool any = false, allBold = true, allItalic = true, allUnderline = true, allCaps = true;
        double? maxSize = null;

        foreach (var run in p.Descendants<Run>())
        {
            if (run.Ancestors<DeletedRun>().Any()) continue;
            if (!run.Descendants<Text>().Any(t => !string.IsNullOrWhiteSpace(t.Text))) continue;

            any = true;
            var rPr = run.RunProperties;
            allBold &= StyleResolver.OnOff(rPr?.Bold) ?? false;
            allItalic &= StyleResolver.OnOff(rPr?.Italic) ?? false;
            allCaps &= StyleResolver.OnOff(rPr?.Caps) ?? false;
            allUnderline &= rPr?.Underline?.Val is { } u &&
                            !string.Equals(u.InnerText, "none", StringComparison.OrdinalIgnoreCase);

            if (StyleResolver.HalfPointToPt(rPr?.FontSize?.Val?.Value) is { } s)
                maxSize = maxSize is null ? s : Math.Max(maxSize.Value, s);
        }

        if (!any) return new RunFormat(null, null, null, null, null);

        // false ở đây nghĩa là "không phải toàn bộ đoạn" → để null cho tầng style quyết định.
        return new RunFormat(
            allBold ? true : null,
            allItalic ? true : null,
            allUnderline ? true : null,
            allCaps ? true : null,
            maxSize);
    }

    /// <summary>Lấy text hiển thị, bỏ qua nội dung đã xoá (track changes) và field code.</summary>
    private static string GetText(OpenXmlElement? root)
    {
        if (root is null) return string.Empty;
        var sb = new StringBuilder();

        foreach (var el in root.Descendants())
        {
            switch (el)
            {
                case Text t:
                    if (!t.Ancestors<DeletedRun>().Any()) sb.Append(t.Text);
                    break;
                case TabChar:
                    sb.Append('\t');
                    break;
                case Break:
                    sb.Append(' ');
                    break;
                case NoBreakHyphen:
                    sb.Append('-');
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Cỡ chữ thân bài = cỡ chiếm nhiều KÝ TỰ nhất. Đếm theo ký tự (không phải theo số đoạn)
    /// để các đoạn văn dài áp đảo, còn tiêu đề ngắn không kéo lệch kết quả.
    /// </summary>
    private static double? EstimateBodyFontSize(List<SlimParagraph> paragraphs)
    {
        var weight = new Dictionary<double, long>();

        foreach (var p in paragraphs)
        {
            if (p.FontSizePt is not { } size || p.Text.Length == 0) continue;
            weight[size] = weight.GetValueOrDefault(size) + p.Text.Length;
        }

        if (weight.Count == 0) return null;

        return weight.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
    }

    private static string Normalize(string s) => WhitespaceRx.Replace(s, " ").Trim();

    private static bool HasLetters(string s) => s.Any(char.IsLetter);

    private static void AddIfNotEmpty(List<string> list, string s)
    {
        if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s)) list.Add(s);
    }

    /// <summary>
    /// Hậu xử lý dựa trên ngữ cảnh: một dòng in đậm đứng ngay trước đoạn thân bài dài
    /// khả năng cao là tiêu đề; ngược lại một "ứng viên" nằm giữa hai đoạn ngắn thì đáng ngờ.
    /// </summary>
    private static void PostProcess(List<SlimParagraph> ps)
    {
        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            if (p.Role != ParagraphRole.HeadingCandidate) continue;

            var next = NextNonEmpty(ps, i);
            if (next is { Text.Length: > 200 }) p.Score = Math.Min(1, p.Score + 0.10);

            var prev = PrevNonEmpty(ps, i);
            if (prev is { Role: ParagraphRole.StyledHeading }) p.Score = Math.Min(1, p.Score + 0.05);
        }
    }

    private static SlimParagraph? NextNonEmpty(List<SlimParagraph> ps, int i)
    {
        for (int k = i + 1; k < ps.Count; k++)
            if (ps[k].Role != ParagraphRole.Empty) return ps[k];
        return null;
    }

    private static SlimParagraph? PrevNonEmpty(List<SlimParagraph> ps, int i)
    {
        for (int k = i - 1; k >= 0; k--)
            if (ps[k].Role != ParagraphRole.Empty) return ps[k];
        return null;
    }
}
