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

        var body = main.Document?.Body;
        if (body is not null)
        {
            var index = 0;
            foreach (var walked in ParagraphWalker.Enumerate(body, _options))
                paragraphs.Add(BuildParagraph(walked, resolver, index++));
        }

        // numbering.xml chứa phần số Word hiển thị nhưng không nằm trong text paragraph.
        NumberingResolver.Apply(main, paragraphs);

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

    private static SlimParagraph BuildParagraph(WalkedParagraph walked, StyleResolver resolver, int index)
    {
        var p = walked.Element;
        var pPr = p.ParagraphProperties;
        var styleId = pPr?.ParagraphStyleId?.Val?.Value ?? resolver.DefaultParagraphStyleId;
        var style = resolver.Resolve(styleId);

        // Định dạng trực tiếp trên đoạn ghi đè style; nếu không có thì lấy từ style.
        var markRun = pPr?.ParagraphMarkRunProperties;
        var nestedTextBoxes = p.Descendants<TextBoxContent>().ToHashSet();
        var runFmt = AggregateRunFormat(p, nestedTextBoxes);

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
                       ?? resolver.DefaultFontSizePt;

        var (text, textSpans) = BuildTextAndSpans(p, nestedTextBoxes, style);

        // Chữ hoa toàn bộ do người dùng gõ tay cũng tính là AllCaps.
        if (!caps && text.Length > 3 && HasLetters(text) && text == text.ToUpperInvariant())
            caps = true;

        var alignment = pPr?.Justification?.Val?.InnerText ?? style?.Alignment;
        var numPr = pPr?.NumberingProperties;

        return new SlimParagraph
        {
            Index = index,
            StableId = walked.StableId,
            Text = text,
            TextSpans = textSpans,
            StyleId = styleId,
            StyleName = style?.Name,
            OutlineLevel = pPr?.OutlineLevel?.Val?.Value ?? style?.OutlineLevel,
            Bold = bold,
            Italic = italic,
            Underline = underline,
            AllCaps = caps,
            FontSizePt = size,
            BodyFontSizePt = resolver.DefaultFontSizePt,   // sẽ được ghi đè bằng cỡ chữ thân bài thực tế
            Alignment = alignment,
            NumberingId = numPr?.NumberingId?.Val?.Value ?? style?.NumberingId,
            NumberingLevel = numPr?.NumberingLevelReference?.Val?.Value ?? style?.NumberingLevel,
            KeepNext = StyleResolver.OnOff(pPr?.KeepNext) ?? style?.KeepNext ?? false,
            PageBreakBefore = StyleResolver.OnOff(pPr?.PageBreakBefore) ?? style?.PageBreakBefore ?? false,
            TableDepth = walked.TableDepth,
            SectionIndex = walked.SectionIndex,
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
    private static RunFormat AggregateRunFormat(Paragraph p, IReadOnlySet<TextBoxContent> nestedTextBoxes)
    {
        bool any = false, allBold = true, allItalic = true, allUnderline = true, allCaps = true;
        double? maxSize = null;

        foreach (var run in p.Descendants<Run>())
        {
            if (run.Ancestors<TextBoxContent>().Any(nestedTextBoxes.Contains)) continue;
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
    private static string GetText(OpenXmlElement? root, IReadOnlySet<TextBoxContent>? excludedTextBoxes = null)
    {
        if (root is null) return string.Empty;
        var sb = new StringBuilder();

        foreach (var el in root.Descendants())
        {
            if (excludedTextBoxes is not null &&
                el.Ancestors<TextBoxContent>().Any(excludedTextBoxes.Contains)) continue;
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

    private static (string Text, IReadOnlyList<SlimTextSpan> Spans) BuildTextAndSpans(
        Paragraph paragraph,
        IReadOnlySet<TextBoxContent> excludedTextBoxes,
        ResolvedStyle? paragraphStyle)
    {
        var text = new StringBuilder();
        var spans = new List<SlimTextSpan>();

        foreach (var run in paragraph.Descendants<Run>())
        {
            if (run.Ancestors<TextBoxContent>().Any(excludedTextBoxes.Contains)) continue;
            if (run.Ancestors<DeletedRun>().Any()) continue;

            var raw = GetText(run, excludedTextBoxes);
            if (raw.Length == 0) continue;
            var rPr = run.RunProperties;
            var bold = StyleResolver.OnOff(rPr?.Bold) ?? paragraphStyle?.Bold ?? false;
            var italic = StyleResolver.OnOff(rPr?.Italic) ?? paragraphStyle?.Italic ?? false;
            var underline = rPr?.Underline?.Val is { } u
                ? !string.Equals(u.InnerText, "none", StringComparison.OrdinalIgnoreCase)
                : paragraphStyle?.Underline ?? false;
            var size = StyleResolver.HalfPointToPt(rPr?.FontSize?.Val?.Value)
                ?? paragraphStyle?.FontSizePt;

            foreach (var c in raw)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (text.Length == 0 || text[^1] == ' ') continue;
                    Append(' ');
                }
                else
                {
                    Append(c);
                }
            }

            void Append(char c)
            {
                var start = text.Length;
                text.Append(c);
                if (spans.Count > 0 && spans[^1] is var last && last.End == start &&
                    last.Bold == bold && last.Italic == italic && last.Underline == underline &&
                    last.FontSizePt == size)
                    spans[^1] = last with { End = start + 1 };
                else
                    spans.Add(new SlimTextSpan(start, start + 1, bold, italic, underline, size));
            }
        }

        if (text.Length > 0 && text[^1] == ' ')
        {
            text.Length--;
            if (spans.Count > 0)
            {
                var last = spans[^1];
                if (last.Start == text.Length) spans.RemoveAt(spans.Count - 1);
                else spans[^1] = last with { End = text.Length };
            }
        }

        return (text.ToString(), spans);
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

            // Đoạn đứng ngay trước các DÒNG MỤC của mục lục chính là TIÊU ĐỀ của mục lục
            // ("MỤC LỤC", "Contents", "Danh mục hình ảnh"). Quan hệ này là bằng chứng cấu trúc —
            // dòng mục lục do Word đánh dấu bằng hyperlink neo _Toc, không phải do đoán từ chữ.
            // Nhờ đó không cần một danh sách từ khoá nào cho họ tiêu đề này.
            // `!p.InTableOfContents` là điều kiện bắt buộc: một DÒNG MỤC của mục lục cũng đứng ngay
            // trước dòng mục kế tiếp. Thiếu vế này thì cả danh sách mục lục thành heading — đo được
            // trên bench: recall lên 100% nhưng 04-bia-muc-luc-chu-thich thừa đúng hai dòng mục.
            if (!p.InTableOfContents && NextNonEmpty(ps, i) is { InTableOfContents: true })
            {
                p.PrecedesTableOfContents = true;
                if (p.Role is ParagraphRole.Normal or ParagraphRole.HeadingCandidate)
                {
                    p.Role = ParagraphRole.HeadingCandidate;
                    p.Score = Math.Max(p.Score, 0.80);
                }
            }

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
