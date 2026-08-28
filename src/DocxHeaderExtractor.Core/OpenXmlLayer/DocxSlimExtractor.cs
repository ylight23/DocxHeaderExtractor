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

    public SlimDocument Extract(string path) => ExtractWithSourceFacts(path).Slim;

    public DocxSourceExtractionResult ExtractWithSourceFacts(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = WordprocessingDocument.Open(stream, false);
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

        // Mục lục gõ tay phải nhận diện TRƯỚC hai lượt dưới: cả MarkParagraphsBeforeTables lẫn
        // PostProcess đều đọc InTableOfContents, đặt sau thì chúng đọc phải cờ chưa cập nhật.
        MarkTypedTableOfContentsRuns(paragraphs);

        // Quan hệ vị trí với bảng phải biết TRƯỚC khi chấm điểm: luật chú thích trong Classify dựa
        // vào nó, mà PostProcess thì chạy sau nên đặt ở đó là cờ luôn false lúc cần.
        MarkParagraphsBeforeTables(paragraphs);
        foreach (var p in paragraphs) HeadingHeuristics.Classify(p, _options);

        // Style của TÀI LIỆU NÀY có đáng tin không — chấm sau lượt Classify đầu vì vế "trông không
        // phải đề mục" dùng lại chính các luật hình dạng ở đó. Không tin thì chấm LẠI, lần này style
        // không được thoát sớm; đoạn vẫn giữ bằng chứng, chỉ mất quyền phủ quyết. Xem StyleTrustAudit.
        TableRoleClassifier.Apply(paragraphs);
        var styleTrust = StyleTrustAudit.Measure(paragraphs);

        // Đếm TRƯỚC khi hạ quyền. Các luật hình dạng bên dưới chỉ chạy trên tài liệu "có đánh dấu
        // cấu trúc bài bản", và chúng đo điều đó bằng chính HasBuiltInHeadingStyle — thứ mà nhánh
        // hạ quyền ngay dưới đây xoá sạch. Đếm sau thì hạ quyền style không chỉ chuyển quyền cho một
        // chỗ trống (§11.2) mà còn TẮT LUÔN luật lẽ ra tiếp quản: số đếm về 0, chốt không đạt, luật
        // trả về ngay. Đây là lý do StyleTrust "nhận đúng mà kết quả không đổi một chữ số".
        var structuralMarkers = CountStructuralMarkers(paragraphs);

        if (_options.UseStyleTrust && !styleTrust.SelectionTrusted)
        {
            foreach (var p in paragraphs)
            {
                p.HasBuiltInHeadingStyle = false;
                HeadingHeuristics.Classify(p, _options, trustStyleSelection: false);
            }
        }

        // Cần IsCandidate nên phải chạy SAU Classify; và chạy TRƯỚC PostProcess để lượt cộng điểm
        // ngữ cảnh ở đó không kéo ngược dòng bìa vừa hạ lên lại.
        DemoteCoverPageBlock(paragraphs);
        DemoteInlineEmphasis(paragraphs, structuralMarkers);
        DemoteRunsWithoutOwnProse(paragraphs, structuralMarkers);
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

        // Đầu trang chảy vào thân bài (bản chuyển PDF). Phải bóc TRƯỚC khi đo chế độ và trước
        // khi tầng ứng viên đọc mốc, nếu không số trang sẽ được đọc thành mốc đánh số — §106.
        RunningHeaderAudit.Strip(paragraphs);

        var slim = new SlimDocument
        {
            FileName = Path.GetFileName(path),
            SourcePath = path,
            Paragraphs = paragraphs,
            DefaultFontSizePt = resolver.DefaultFontSizePt,
            StyleTrust = styleTrust,
            Mode = DocumentModeClassifier.Measure(paragraphs),
            PageHeaders = headers,
            PageFooters = footers,
        }.Build();

        var source = DocxSourceFactsBuilder.Build(path, paragraphs, headers, footers);
        return new DocxSourceExtractionResult(slim, source);
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

        var built = BuildTextAndSpans(p, nestedTextBoxes, style);
        var text = built.Text;
        var textSpans = built.Spans;

        // Chữ hoa toàn bộ do người dùng gõ tay cũng tính là AllCaps.
        if (!caps && text.Length > 3 && HasLetters(text) && text == text.ToUpperInvariant())
            caps = true;

        var alignment = pPr?.Justification?.Val?.InnerText ?? style?.Alignment;
        var numPr = pPr?.NumberingProperties;

        return new SlimParagraph
        {
            Index = index,
            StableId = walked.StableId,
            InContentControl = p.Ancestors<SdtElement>().Any(),
            Corrupt = CorruptParagraphDetector.IsDoubled(text),
            Text = text,
            TextSpans = textSpans,
            LineBreakOffsets = built.LineBreaks,
            SourceSegments = built.Sources,
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

    private readonly record struct ParagraphText(
        string Text,
        IReadOnlyList<SlimTextSpan> Spans,
        IReadOnlyList<int> LineBreaks,
        IReadOnlyList<SlimSourceSegment> Sources);

    /// <summary>
    /// Dựng text đã chuẩn hoá khoảng trắng, kèm ba thứ mà bản chuẩn hoá tự nó làm mất:
    /// ranh giới định dạng, vị trí <c>w:br</c>, và đường về nguồn của từng ký tự.
    /// </summary>
    private static ParagraphText BuildTextAndSpans(
        Paragraph paragraph,
        IReadOnlySet<TextBoxContent> excludedTextBoxes,
        ResolvedStyle? paragraphStyle)
    {
        var text = new StringBuilder();
        var spans = new List<SlimTextSpan>();
        var lineBreaks = new List<int>();
        var sources = new List<SlimSourceSegment>();
        var runIndex = -1;

        foreach (var run in paragraph.Descendants<Run>())
        {
            if (run.Ancestors<TextBoxContent>().Any(excludedTextBoxes.Contains)) continue;
            if (run.Ancestors<DeletedRun>().Any()) continue;

            runIndex++;
            var (raw, breakOffsets) = GetRunText(run, excludedTextBoxes);
            if (raw.Length == 0) continue;
            var rPr = run.RunProperties;
            var bold = StyleResolver.OnOff(rPr?.Bold) ?? paragraphStyle?.Bold ?? false;
            var italic = StyleResolver.OnOff(rPr?.Italic) ?? paragraphStyle?.Italic ?? false;
            var underline = rPr?.Underline?.Val is { } u
                ? !string.Equals(u.InnerText, "none", StringComparison.OrdinalIgnoreCase)
                : paragraphStyle?.Underline ?? false;
            var size = StyleResolver.HalfPointToPt(rPr?.FontSize?.Val?.Value)
                ?? paragraphStyle?.FontSizePt;

            for (var rawIndex = 0; rawIndex < raw.Length; rawIndex++)
            {
                var c = raw[rawIndex];

                // Vị trí w:br ghi lại TRƯỚC khi biết dấu cách của nó có bị gộp hay không: điều đáng
                // giữ là "nguồn xuống dòng ở CHỖ NÀY", không phải "có một dấu cách sống sót".
                if (breakOffsets.Contains(rawIndex)) AddLineBreak(text.Length);

                if (char.IsWhiteSpace(c))
                {
                    if (text.Length == 0 || text[^1] == ' ') continue;
                    Append(' ', rawIndex);
                }
                else
                {
                    Append(c, rawIndex);
                }
            }

            void AddLineBreak(int at)
            {
                if (lineBreaks.Count == 0 || lineBreaks[^1] != at) lineBreaks.Add(at);
            }

            void Append(char c, int rawIndex)
            {
                var at = text.Length;
                // Nối tiếp segment cũ chỉ khi CẢ HAI phía đều liền mạch: cùng run, và raw offset đi
                // đúng một bước. Vế thứ hai tự nó bắt mọi chỗ ký tự nguồn bị bỏ (khoảng trắng gộp),
                // nên không cần thêm cờ "vừa bỏ ký tự" — đã thử, và kiểm đột biến chứng minh cờ đó
                // không phân biệt được gì.
                var continues = sources.Count > 0 && sources[^1] is var prev
                    && prev.End == at && prev.RunIndex == runIndex
                    && prev.RawStart + (at - prev.Start) == rawIndex;

                if (continues) sources[^1] = sources[^1] with { End = at + 1 };
                else sources.Add(new SlimSourceSegment(at, at + 1, runIndex, rawIndex));

                AppendSpan(c);
            }

            void AppendSpan(char c)
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
            if (sources.Count > 0)
            {
                var last = sources[^1];
                if (last.Start == text.Length) sources.RemoveAt(sources.Count - 1);
                else sources[^1] = last with { End = text.Length };
            }
        }

        // Text bị Trim ở hai đầu nên một w:br ngay đầu/cuối đoạn có thể trỏ ra ngoài chuỗi.
        // Kẹp về biên thay vì vứt: "đoạn này CÓ ngắt dòng" là thông tin, vị trí chỉ là chi tiết.
        var clamped = lineBreaks.Select(b => Math.Clamp(b, 0, text.Length)).Distinct().ToList();

        return new ParagraphText(text.ToString(), spans, clamped, sources);
    }

    /// <summary>
    /// Text thô của một run, kèm vị trí các <c>w:br</c> trong chính chuỗi thô đó.
    /// <para>
    /// Tách khỏi <see cref="GetText"/> vì hàm kia phục vụ header/footer và không cần biết ngắt dòng;
    /// gộp lại thì mọi caller phải gánh thêm một giá trị trả về không dùng đến.
    /// </para>
    /// </summary>
    private static (string Raw, HashSet<int> BreakOffsets) GetRunText(
        Run run, IReadOnlySet<TextBoxContent> excludedTextBoxes)
    {
        var sb = new StringBuilder();
        var breaks = new HashSet<int>();

        foreach (var el in run.Descendants())
        {
            if (el.Ancestors<TextBoxContent>().Any(excludedTextBoxes.Contains)) continue;
            switch (el)
            {
                case Text t:
                    if (!t.Ancestors<DeletedRun>().Any()) sb.Append(t.Text);
                    break;
                case TabChar:
                    sb.Append('\t');
                    break;
                case Break:
                    breaks.Add(sb.Length);
                    sb.Append(' ');
                    break;
                case NoBreakHyphen:
                    sb.Append('-');
                    break;
            }
        }

        return (sb.ToString(), breaks);
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

    /// <summary>Đoạn văn xuôi thân bài: không phải ứng viên và đủ dài để là nội dung thật.</summary>
    private const int BodyProseMinLength = 120;

    /// <summary>Số đề mục có dấu hiệu cấu trúc tối thiểu để coi tài liệu là "có đánh dấu bài bản".</summary>
    private const int MinStructuralMarkersForEmphasisRule = 5;

    /// <summary>
    /// Tài liệu này có đánh dấu đề mục bài bản không. Phải gọi TRƯỚC nhánh hạ quyền của StyleTrust:
    /// nhánh đó xoá <see cref="SlimParagraph.HasBuiltInHeadingStyle"/> nên gọi sau sẽ luôn ra 0 trên
    /// đúng những tài liệu cần luật hình dạng nhất.
    /// </summary>
    private static int CountStructuralMarkers(List<SlimParagraph> ps) => ps.Count(p =>
        p.HasBuiltInHeadingStyle || p.NumberingStyleLevel is not null || p.NumberingId is not null);

    /// <summary>
    /// Nhấn mạnh trong THÂN BÀI, không phải đề mục: đậm + nghiêng cùng lúc mà KHÔNG có một dấu hiệu
    /// cấu trúc nào — không numbering của Word, không style Heading, không outlineLvl.
    /// <para>
    /// ĐO ĐƯỢC trên khoá luận thật 1498 đoạn: 86 đoạn khớp mô tả này, trong đó 83 KHÔNG phải đề mục
    /// và 21 đang bị nhận nhầm; chỉ 3 đề mục thật dính vào. Trong cùng vùng văn bản, đề mục thật
    /// (403, 420, 477, 480, 498) là đậm + CÓ numbering, còn câu liệt kê ("Một là,… / Hai là,…") là
    /// đậm + nghiêng + KHÔNG numbering — hai nhóm tách nhau sạch bằng đúng hai vế đó.
    /// </para>
    /// <para>
    /// Riêng "nghiêng" KHÔNG dùng được một mình: cùng tài liệu có 113 đoạn nghiêng mà 13 trong số đó
    /// là đề mục thật, nên luật một vế đổi 26 mục thừa lấy 13 mục thiếu — tệ hơn không làm gì.
    /// </para>
    /// <para>
    /// Vế MỨC TÀI LIỆU là chốt quan trọng nhất và được chính bench dạy cho: bản đầu không có nó làm
    /// <c>02-dinh-dang-thu-cong</c> mất 2 đề mục (recall 100% → 94,9%). Tài liệu đó KHÔNG dùng style
    /// hay numbering ở bất kỳ đâu — đậm/nghiêng là cách duy nhất tác giả đánh dấu đề mục. Việc
    /// *thiếu* dấu hiệu cấu trúc chỉ mang thông tin khi tài liệu có dùng dấu hiệu đó ở chỗ khác.
    /// </para>
    /// </summary>
    private static void DemoteInlineEmphasis(List<SlimParagraph> ps, int structuralMarkers)
    {
        if (structuralMarkers < MinStructuralMarkersForEmphasisRule) return;

        foreach (var p in ps)
        {
            if (!p.IsCandidate) continue;
            if (p.HasBuiltInHeadingStyle || p.NumberingId is not null
                || p.NumberingStyleLevel is not null || p.OutlineLevel is not null) continue;

            // Hai dạng "không có tuyên bố cấu trúc nào" đều thuộc thân bài, không phải đề mục:
            //   • đậm + nghiêng → câu dẫn liệt kê trong đoạn ("Một là,… / Hai là,…")
            //   • KHÔNG đậm     → dòng ghi nguồn dưới bảng/hình, dòng ngày tháng, câu hỏi phiếu khảo sát
            // Vế "không đậm" gánh phần lớn: trong tài liệu đánh dấu bài bản, đề mục nào cũng ít nhất
            // được làm đậm — không đậm mà cũng không style, không numbering, không outlineLvl thì
            // không còn dấu hiệu nào để gọi là đề mục.
            // ĐO ĐƯỢC trên khoá luận thật: 932 đoạn khớp vế "không đậm", và theo HỢP của hai đáp án
            // độc lập chỉ ĐÚNG MỘT đoạn trong đó là đề mục — mà đoạn ấy cả hai người gán nhãn đều tự
            // đánh dấu là không chắc. Dùng HỢP chứ không phải GIAO là cố ý: lấy tiêu chuẩn rộng nhất
            // mà vẫn không có phản ví dụ thì kết luận mới chắc.
            var listRunIn = p.Bold && p.Italic;
            if (!listRunIn && p.Bold) continue;

            p.Role = ParagraphRole.Normal;
            p.Score = 0;
        }
    }

    /// <summary>Số ứng viên tối thiểu của khối bìa. Dưới mức này thì đó là tài liệu mở đầu bằng đề mục.</summary>
    private const int MinCoverBlockCandidates = 5;

    /// <summary>
    /// Trang bìa: khối ứng viên đứng TRƯỚC đoạn văn xuôi đầu tiên của tài liệu.
    /// <para>
    /// Một tiêu đề phải MỞ RA nội dung bên dưới nó. Dòng bìa thì không mở ra gì — sau nó lại là dòng
    /// bìa nữa, cho tới tận đoạn văn xuôi đầu tiên. Nên trong vùng trước prose đầu tiên, chỉ ứng
    /// viên CUỐI CÙNG là tiêu đề thật (nó mở ra chính đoạn prose đó); phần còn lại là siêu dữ liệu
    /// bìa: tên trường, tên tác giả, ngành, năm.
    /// </para>
    /// <para>
    /// ĐO ĐƯỢC trên một khoá luận thật: trang bìa LẶP HAI LẦN, 19 ứng viên liên tiếp không một đoạn
    /// văn xuôi nào, mãi tới đoạn thứ 83 mới có prose — và ứng viên cuối cùng trước nó đúng là một
    /// đề mục thật. §5 từng ghi ca bìa lặp là "không sửa được bằng đổi mô hình"; nó sửa được bằng
    /// cấu trúc.
    /// </para>
    /// <para>
    /// Ba chốt chống ăn nhầm: cần ít nhất <see cref="MinCoverBlockCandidates"/> ứng viên (tài liệu mở
    /// đầu bằng một chuỗi đề mục lồng nhau rồi mới tới prose thì không đủ dài để thành khối bìa);
    /// đoạn mang style Heading built-in hoặc numbering của Word thì miễn trừ (người soạn đã tuyên bố
    /// tường minh — §1); và ứng viên cuối cùng luôn được giữ.
    /// </para>
    /// </summary>
    private static void DemoteCoverPageBlock(List<SlimParagraph> ps)
    {
        var firstProse = ps.FindIndex(p =>
            p.Role != ParagraphRole.Empty && !p.IsCandidate && p.Text.Length >= BodyProseMinLength);
        if (firstProse < 0) return;

        var block = new List<SlimParagraph>();
        for (var i = 0; i < firstProse; i++)
        {
            var p = ps[i];
            if (p.Role == ParagraphRole.Empty || !p.IsCandidate) continue;
            if (p.HasBuiltInHeadingStyle || p.NumberingId is not null) continue;
            block.Add(p);
        }

        if (block.Count < MinCoverBlockCandidates) return;
        foreach (var p in block.SkipLast(1))
        {
            p.Role = ParagraphRole.Normal;
            p.Score = 0;
        }
    }

    /// <summary>
    /// Cùng nguyên tắc với <see cref="DemoteCoverPageBlock"/> nhưng áp cho MỌI vị trí trong tài liệu:
    /// trong một dãy ứng viên liên tiếp không có đoạn văn xuôi nào xen giữa, chỉ ứng viên CUỐI CÙNG
    /// mở ra được văn xuôi — những ứng viên trước nó chỉ mở ra… ứng viên khác.
    /// <para>
    /// Bắt nhóm "đuôi mục": dòng ký tên, chức danh, tên người đứng cuối một phần trước khi phần kế
    /// tiếp bắt đầu. ĐO ĐƯỢC trên khoá luận thật: 4 dòng như vậy nằm cuối hai phần mở đầu, cả bốn
    /// đều bị nhận nhầm là đề mục.
    /// </para>
    /// <para>
    /// Hai chốt bắt buộc, nếu không sẽ giết cả cây đề mục thật: đoạn có style Heading built-in hoặc
    /// numbering của Word được MIỄN TRỪ (chuỗi "CHƯƠNG 1 → 1.1 → 1.1.1" cũng là một dãy liên tiếp
    /// không xen văn xuôi, và cả ba đều là đề mục thật — chúng thoát nhờ vế này); và luật chỉ chạy
    /// trên tài liệu có đánh dấu cấu trúc bài bản, vì ở tài liệu gõ tay thuần thì việc thiếu dấu hiệu
    /// không mang thông tin gì.
    /// </para>
    /// </summary>
    internal static void DemoteRunsWithoutOwnProse(List<SlimParagraph> ps, int structuralMarkers)
    {
        if (structuralMarkers < MinStructuralMarkersForEmphasisRule) return;

        var customStylesUnderOutlineAnchor = OutlineAnchorCustomStyles.Find(ps);
        var run = new List<SlimParagraph>();
        void Flush()
        {
            foreach (var p in run.SkipLast(1))
            {
                p.Role = ParagraphRole.Normal;
                p.Score = 0;
            }
            run.Clear();
        }

        foreach (var p in ps)
        {
            if (p.Role == ParagraphRole.Empty) continue;
            if (p.IsCandidate)
            {
                // Tuyên bố cấu trúc tường minh thì không bị dãy cuốn theo (§1).
                // §63: tài liệu form-based (World Bank procurement templates) có cụm heading liên
                // tiếp không mở ngay ra prose dài. Các heading phụ trong cụm thường dùng style tự
                // đặt lặp lại dưới một anchor outlineLvl; nếu không miễn trừ, luật prose-based này
                // xoá sạch chúng trước khi route đa nguồn có cơ hội dùng.
                if (IsOwnProseRunExempt(p, customStylesUnderOutlineAnchor))
                {
                    Flush();
                    continue;
                }
                run.Add(p);
                continue;
            }

            // Gặp văn xuôi thân bài ⇒ chỉ ứng viên CUỐI dãy mở ra được nó; những cái trước chỉ mở ra
            // ứng viên khác. Bản đầu viết `run.Clear()` — tha cả dãy — nên "mở ra văn xuôi" trở thành
            // quan hệ BẮC CẦU: một nhãn khối chữ ký đứng ngay trước đề mục của phần sau cũng được
            // tính là đã mở ra văn xuôi của phần ấy. Đo được trên 09-style-ap-sai: cả `Người lập
            // biểu` lẫn `Nguyễn Văn A` thoát nhờ đúng kẽ hở đó.
            if (p.Text.Length >= BodyProseMinLength) Flush();
        }
        Flush();
    }

    private static bool IsOwnProseRunExempt(
        SlimParagraph p,
        HashSet<string> customStylesUnderOutlineAnchor) =>
        p.HasBuiltInHeadingStyle ||
        p.NumberingId is not null ||
        p.NumberingStyleLevel is not null ||
        OutlineAnchorCustomStyles.IsAnchoredCustomStyle(p, customStylesUnderOutlineAnchor);

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

    /// <summary>
    /// Số trang ở cuối dòng mục lục: khoảng trắng rồi 1–4 chữ số kết thúc đoạn, và phải còn phần
    /// tên mục ở trước. Tab và dấu chấm dẫn (dot leader) KHÔNG dùng được vì lớp trích text đã nuốt
    /// chúng thành một dấu cách.
    /// </summary>
    private static readonly Regex TrailingPageNumberRx = new(
        @"^(?<title>.*\S)\s+(?<page>\d{1,4})$", RegexOptions.Compiled);

    /// <summary>
    /// Nhận diện mục lục GÕ TAY — thứ mà <see cref="IsTableOfContentsEntry"/> không thấy vì nó chỉ
    /// đọc neo <c>_Toc</c> và style TOC1..TOC9.
    /// <para>
    /// ĐO ĐƯỢC: lấy đúng <c>04-bia-muc-luc-chu-thich</c> và chỉ gỡ ba neo <c>_Toc</c> đi, giữ nguyên
    /// mọi thứ khác — tầng OpenXML từ 7 ứng viên (3 thừa) lên 10 ứng viên (6 thừa), P 57,1% → 40%;
    /// qua cả mô hình thì P 100% → 66,7% và <b>R 100% → 50%</b>. Mất neo không chỉ thêm rác: mô hình
    /// không phân biệt được bản sao với bản gốc nên loại nhầm chính heading thật. Tài liệu chuyển từ
    /// PDF hoặc gõ tay đều rơi vào ca này.
    /// </para>
    /// <para>
    /// Nhận theo DÃY chứ không theo từng đoạn, và cả ba vế phải cùng đúng — đây là chốt chống ăn
    /// nhầm, không phải trang trí:
    /// </para>
    /// <list type="number">
    /// <item>kết thúc bằng số trang và còn phần tên mục ở trước;</item>
    /// <item>ít nhất <see cref="MinTocRunLength"/> đoạn LIỀN NHAU cùng dạng — một đề mục lẻ kết thúc
    /// bằng số ("Phụ lục 2") không đủ làm thành dãy;</item>
    /// <item>số trang không giảm dần — nhưng dãy được CẮT tại mỗi chỗ tụt chứ không bị loại cả cụm,
    /// vì mục lục thật hay tụt một lần: phần đầu (mục lục, danh mục bảng/hình) đánh số trang riêng
    /// rồi phần thân quay về 1. ĐO ĐƯỢC trên khoá luận thật: 21 dòng liên tiếp với dãy
    /// <c>5,6,6,7,1,16,16,37,…</c> — chốt "cả dãy phải không giảm" loại sạch cả 21 dòng, trong khi
    /// cắt tại chỗ tụt cho hai đoạn con 4 và 17 dòng, cả hai đều hợp lệ. "PHỤ LỤC 1/2/3" thì vẫn
    /// trượt vì chúng nằm rải rác, đã bị vế 2 loại từ trước.</item>
    /// </list>
    /// <para>
    /// Thêm hai chốt nữa: đoạn trong bảng bị loại (bảng số liệu đầy dòng kết thúc bằng số), và đoạn
    /// mang numbering của Word bị loại (mục lục không bao giờ được Word đánh số).
    /// </para>
    /// </summary>
    private const int MinTocRunLength = 3;

    private static void MarkTypedTableOfContentsRuns(List<SlimParagraph> ps)
    {
        var run = new List<SlimParagraph>();
        var pages = new List<int>();

        void Flush()
        {
            if (run.Count >= MinTocRunLength)
                foreach (var p in run) p.InTableOfContents = true;
            run.Clear();
            pages.Clear();
        }

        foreach (var p in ps)
        {
            if (p.Role == ParagraphRole.Empty) continue;   // dòng trống không cắt dãy
            if (!LooksLikeTocLine(p, out var page))
            {
                Flush();
                continue;
            }

            // Số trang tụt ⇒ kết thúc đoạn con hiện tại, mở đoạn mới TỪ chính dòng này.
            if (pages.Count > 0 && page < pages[^1]) Flush();
            run.Add(p);
            pages.Add(page);
        }
        Flush();
    }

    private static bool LooksLikeTocLine(SlimParagraph p, out int page)
    {
        page = 0;
        if (p.TableDepth > 0 || p.NumberingId is not null) return false;
        var m = TrailingPageNumberRx.Match(p.Text.Trim());
        if (!m.Success) return false;
        return int.TryParse(m.Groups["page"].Value, out page);
    }

    /// <summary>
    /// Chú thích bảng ("Bảng 1.2: Tình hình huy động vốn") đứng ngay trước chính bảng nó đặt tên.
    /// Đây là quan hệ VỊ TRÍ, đọc được cho mọi ngôn ngữ — khác hẳn danh sách từ khoá trong
    /// <c>CaptionRx</c> vốn chỉ đúng với tiếng Việt/Anh VÀ bị tắt cùng cờ luật từ ngữ, nên ở chế độ
    /// mà giao diện chạy mặc định thì không còn bộ lọc chú thích nào.
    /// <para>
    /// Cửa sổ 4 đoạn chứ không phải 1: giữa chú thích và bảng thường còn dòng đơn vị tính, dòng năm
    /// hoặc số trang ("ĐVT: Tỷ đồng", "2022-2024", "12"). Đo trên báo cáo thật: cửa sổ 1 bắt 9/13,
    /// cửa sổ 3 bắt 11/13, cửa sổ 4 bắt 12/13; nới lên 5 KHÔNG bắt thêm gì nên dừng ở 4.
    /// </para>
    /// </summary>
    private static void MarkParagraphsBeforeTables(List<SlimParagraph> ps)
    {
        for (var i = 0; i < ps.Count; i++)
            ps[i].PrecedesTable = ps[i].TableDepth == 0 && !ps[i].InTableOfContents
                                  && StartsTableWithin(ps, i, 4);
    }

    /// <summary>Có đoạn nằm trong bảng xuất hiện trong <paramref name="window"/> đoạn không rỗng kế tiếp.</summary>
    private static bool StartsTableWithin(List<SlimParagraph> ps, int i, int window)
    {
        var seen = 0;
        for (var k = i + 1; k < ps.Count && seen < window; k++)
        {
            // Chạy TRƯỚC Classify nên Role chưa được gán; xét trực tiếp nội dung rỗng.
            if (string.IsNullOrWhiteSpace(ps[k].Text) && ps[k].TableDepth == 0) continue;
            seen++;
            if (ps[k].TableDepth > 0) return true;
        }
        return false;
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
