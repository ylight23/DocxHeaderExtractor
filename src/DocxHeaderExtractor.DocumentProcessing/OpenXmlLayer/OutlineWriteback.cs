using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

public sealed record OutlineWritebackOptions
{
    /// <summary>
    /// Gán thêm <c>w:pStyle</c> Heading N khi styles.xml đã có sẵn style đó. Mặc định tắt:
    /// đổi style làm thay đổi hình thức tài liệu, còn <c>w:outlineLvl</c> thì không.
    /// Không tự tạo style mới — tài liệu nguồn quyết định bảng style của chính nó.
    /// </summary>
    public bool ApplyHeadingStyles { get; init; }

    public bool Overwrite { get; init; }
}

public sealed record OutlineWritebackSkip(int Index, string Reason);

public sealed record OutlineWritebackResult(
    string OutputPath,
    int Applied,
    IReadOnlyList<OutlineWritebackSkip> Skipped);

/// <summary>
/// Ghi cấp heading đã chốt vào một BẢN SAO của tài liệu nguồn.
/// <para>
/// Ba bất biến, tất cả đều fail-closed:
/// 1. Không sửa file nguồn — luôn ghi ra đường dẫn đích riêng.
/// 2. Không chạm vào một ký tự nội dung nào; chỉ đặt <c>w:outlineLvl</c> (và tuỳ chọn
///    <c>w:pStyle</c>) trong <c>w:pPr</c>.
/// 3. Sau khi ghi, đọc lại bản đích bằng source-native reader và đối chiếu
///    stableId + text + outline level. Lệch một mục là xoá file đích và ném lỗi, vì một tài
///    liệu bị đánh dấu sai chỗ nguy hiểm hơn hẳn việc không ghi được.
/// </para>
/// </summary>
public static class OutlineWriteback
{
    public static OutlineWritebackResult Apply(
        string sourceDocxPath,
        string targetPath,
        DocumentOutline outline,
        ExtractionOptions extraction,
        OutlineWritebackOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentNullException.ThrowIfNull(extraction);
        options ??= new OutlineWritebackOptions();

        var source = Path.GetFullPath(sourceDocxPath);
        var target = Path.GetFullPath(targetPath);
        if (!File.Exists(source))
            throw new FileNotFoundException($"Không tìm thấy tài liệu nguồn: {source}", source);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Đích ghi trùng file nguồn; writeback luôn ghi ra bản sao.");
        if (File.Exists(target) && !options.Overwrite)
            throw new InvalidOperationException($"File đích đã tồn tại: {target}");

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.Copy(source, target, options.Overwrite);

        var skipped = new List<OutlineWritebackSkip>();
        var applied = new List<HeadingRecord>();
        // Ngoài khối using: Verify chạy sau khi đóng file và cần biết đã tách ở những chỉ số nào.
        var splits = new List<PendingSplit>();

        // Đường về nguồn của từng ký tự — thứ duy nhất cho phép tách đoạn mà không đoán mò offset.
        // Đọc từ NGUỒN chứ không từ đích: hai file lúc này giống hệt nhau từng byte, còn đích thì
        // sắp bị mở để ghi và đọc song song sẽ tranh khoá.
        var sourceDocument = new OpenXmlDocumentSource(extraction).Read(source);
        var mappings = WritebackMappingSet.FromSourceDocument(sourceDocument);
        try
        {
            using (var doc = WordprocessingDocument.Open(target, true))
            {
                var main = doc.MainDocumentPart
                           ?? throw new InvalidOperationException($"File không có MainDocumentPart: {target}");
                var body = main.Document?.Body
                           ?? throw new InvalidOperationException($"File không có body: {target}");

                var paragraphs = ParagraphWalker.Enumerate(body, extraction).ToList();
                var headingStyles = options.ApplyHeadingStyles ? HeadingStyleIds(main) : [];


                foreach (var heading in outline.Headings)
                {
                    if (Skip(heading, paragraphs.Count) is { } reason)
                    {
                        skipped.Add(new OutlineWritebackSkip(heading.Index, reason));
                        continue;
                    }

                    var walked = paragraphs[heading.Index];

                    // Heading dính nội dung cùng dòng: tách được thì tách, không thì từ chối như cũ.
                    if (heading.InlineBodySpan is { } bodySpan)
                    {
                        if (TrySplitPoint(mappings.Values.FirstOrDefault(mapping =>
                                mapping.Locator.ParagraphIndex == heading.Index), walked.Element, bodySpan.Start)
                            is not { } runIndex)
                        {
                            skipped.Add(new OutlineWritebackSkip(heading.Index, "inline_body_not_splittable"));
                            continue;
                        }
                        splits.Add(new PendingSplit(heading.Index, walked.Element, runIndex));
                    }
                    if (heading.StableId is { Length: > 0 } stableId && walked.StableId != stableId)
                    {
                        skipped.Add(new OutlineWritebackSkip(heading.Index, "stable_id_mismatch"));
                        continue;
                    }

                    var pPr = walked.Element.ParagraphProperties;
                    if (pPr is null)
                    {
                        pPr = new ParagraphProperties();
                        walked.Element.PrependChild(pPr);
                    }

                    // Skip() above already rejected a null Level ("level_unresolved") before this point.
                    var level = heading.Level!.Value;
                    pPr.OutlineLevel = new OutlineLevel { Val = level - 1 };
                    if (headingStyles.TryGetValue(level, out var styleId))
                        pPr.ParagraphStyleId = new ParagraphStyleId { Val = styleId };

                    applied.Add(heading);
                }

                // Tách SAU khi đã đặt xong outlineLvl: chèn w:p mới làm mọi chỉ số phía sau lệch,
                // nên mọi thao tác dựa trên `paragraphs` phải xong trước.
                foreach (var split in splits) SplitParagraph(split);

                main.Document!.Save();
            }

            Verify(target, extraction, applied, splits.Select(x => x.Index).ToList());
        }
        catch
        {
            TryDelete(target);
            throw;
        }

        return new OutlineWritebackResult(target, applied.Count, skipped);
    }

    private static string? Skip(HeadingRecord heading, int paragraphCount)
    {
        if (heading.Index < 0 || heading.Index >= paragraphCount) return "index_out_of_range";

        // Cổng precision chưa cho mục này đi qua thì writeback cũng không được đi trước nó.
        if (heading.DecisionStatus == HeadingDecisionStatus.RequiresReview) return "requires_review";

        // Đoạn chứa cả heading lẫn nội dung cùng dòng: đặt outlineLvl cho cả đoạn sẽ kéo phần thân
        // bài vào cây điều hướng. Nay tách được — nhưng chỉ khi ranh giới rơi đúng đầu một run là
        // con trực tiếp của w:p (xem TrySplitPoint); quyết định đó nằm ở vòng lặp chính vì nó cần
        // SourceSegments. Ca không tách được vẫn trả về "inline_body_not_splittable" ở đó.
        if (heading.InlineBody is not null && heading.InlineBodySpan is null)
            return "inline_body_not_splittable";

        if (heading.Level is null) return "level_unresolved";
        if (heading.Level is < 1 or > 9) return "invalid_level";
        return null;
    }

    /// <param name="splitIndexes">
    /// Chỉ số (theo bản GỐC) của những đoạn đã bị tách làm hai. Mỗi lần tách chèn thêm một
    /// <c>w:p</c> nên mọi đoạn phía sau dịch +1 — không có bản đồ này thì khâu xác minh đi tìm nhầm
    /// đoạn ngay ở mục kế tiếp, và cả `stableId` (địa chỉ theo vị trí) cũng lệch theo.
    /// </param>
    private static void Verify(
        string target,
        ExtractionOptions extraction,
        IReadOnlyList<HeadingRecord> applied,
        IReadOnlyCollection<int> splitIndexes)
    {
        var written = new OpenXmlDocumentSource(extraction).Read(target);
        foreach (var heading in applied)
        {
            var shift = splitIndexes.Count(i => i < heading.Index);
            var wasSplit = splitIndexes.Contains(heading.Index);
            var at = heading.Index + shift;

            var paragraph = written.Paragraphs.FirstOrDefault(item => item.SourceOrdinal == at)
                            ?? throw new InvalidOperationException(
                                $"Sau khi ghi, đoạn {heading.Index} không còn tồn tại trong bản đích.");

            // Địa chỉ XML là theo vị trí nên chỉ so được khi phía trước chưa có lần tách nào.
            if (shift == 0 && heading.StableId is { Length: > 0 } stableId && paragraph.SourceId != stableId)
                throw new InvalidOperationException(
                    $"Sau khi ghi, đoạn {heading.Index} đổi địa chỉ XML ({paragraph.SourceId} ≠ {stableId}).");

            // Đoạn đã tách thì phần còn lại đúng bằng phần TIÊU ĐỀ, không còn là text gốc.
            var expected = wasSplit ? heading.Text : heading.OriginalText ?? heading.Text;
            if (paragraph.Text != expected)
                throw new InvalidOperationException(
                    $"Sau khi ghi, nội dung đoạn {heading.Index} không còn khớp nguồn.");

            if (paragraph.Style.OutlineLevel != heading.Level - 1)
                throw new InvalidOperationException(
                    $"Sau khi ghi, đoạn {heading.Index} có outline level {paragraph.Style.OutlineLevel}, " +
                    $"khác cấp {heading.Level} đã chốt.");
        }
    }


    /// <summary>Shared with <c>PdfProductWriteback</c> — the split mechanics are data-shape agnostic.</summary>
    internal sealed record PendingSplit(int Index, Paragraph Element, int RunIndex);

    /// <summary>
    /// Ranh giới heading/thân bài có tách được thành hai <c>w:p</c> không, và nếu có thì ở run nào.
    /// <para>
    /// FAIL-CLOSED theo hai vế, cả hai đều cần thiết:
    /// </para>
    /// <list type="number">
    /// <item>ranh giới phải rơi ĐÚNG đầu một run (<c>Start == offset</c> và <c>RawStart == 0</c>) —
    /// nếu không thì phải cắt đôi text bên trong run, việc đó đổi cách chia run của tài liệu;</item>
    /// <item>MỌI run của đoạn phải là con trực tiếp của <c>w:p</c>. <c>SourceSegments.RunIndex</c>
    /// đếm theo <c>Descendants&lt;Run&gt;()</c> nên nó tính cả run lồng trong <c>w:hyperlink</c>;
    /// tách ở một run như vậy đòi tách cả hyperlink bao ngoài, và chỉ số run cũng không còn khớp
    /// với <c>Elements&lt;Run&gt;()</c>.</item>
    /// </list>
    /// </summary>
    internal static int? TrySplitPoint(WritebackMapping? mapping, Paragraph element, int bodyStart)
    {
        if (mapping is null || bodyStart <= 0) return null;

        var descendants = element.Descendants<Run>().ToList();
        if (descendants.Count == 0 || descendants.Any(r => !ReferenceEquals(r.Parent, element))) return null;

        foreach (var segment in mapping.Locator.SourceSegments)
        {
            if (segment.Start != bodyStart || segment.RawStart != 0) continue;
            return segment.RunIndex >= 0 && segment.RunIndex < descendants.Count ? segment.RunIndex : null;
        }
        return null;
    }

    /// <summary>
    /// Cắt đoạn làm hai tại đầu run <see cref="PendingSplit.RunIndex"/>: phần đầu giữ nguyên vị trí
    /// và giữ <c>outlineLvl</c> vừa đặt, phần sau thành một <c>w:p</c> mới chèn ngay sau.
    /// <para>
    /// Không một ký tự nội dung nào bị đổi — các <c>w:r</c> được DI CHUYỂN nguyên vẹn, không dựng
    /// lại. <c>w:pPr</c> của phần sau là bản sao của phần đầu nhưng BỎ <c>outlineLvl</c> và
    /// <c>pStyle</c>: phần thân bài không được vào cây điều hướng, và cũng không được mang hình thức
    /// tiêu đề.
    /// </para>
    /// </summary>
    internal static void SplitParagraph(PendingSplit split)
    {
        var runs = split.Element.Elements<Run>().ToList();
        if (split.RunIndex >= runs.Count) return;

        var tail = new Paragraph();
        if (split.Element.ParagraphProperties is { } pPr)
        {
            var clone = (ParagraphProperties)pPr.CloneNode(true);
            clone.OutlineLevel?.Remove();
            clone.ParagraphStyleId?.Remove();
            tail.PrependChild(clone);
        }

        foreach (var run in runs.Skip(split.RunIndex))
        {
            run.Remove();
            tail.AppendChild(run);
        }

        split.Element.InsertAfterSelf(tail);
    }

    /// <summary>Chỉ nhận style Heading 1..9 CÓ SẴN trong tài liệu; không tạo style mới.</summary>
    internal static Dictionary<int, string> HeadingStyleIds(MainDocumentPart main)
    {
        var map = new Dictionary<int, string>();
        var styles = main.StyleDefinitionsPart?.Styles;
        if (styles is null) return map;

        foreach (var style in styles.Elements<Style>())
        {
            if (style.Type?.Value != StyleValues.Paragraph) continue;
            var id = style.StyleId?.Value;
            if (id is null) continue;

            var name = style.StyleName?.Val?.Value ?? id;
            var level = HeadingLevel(name) ?? HeadingLevel(id);
            if (level is { } n && !map.ContainsKey(n)) map[n] = id;
        }

        return map;

        static int? HeadingLevel(string value)
        {
            var compact = value.Replace(" ", "");
            if (!compact.StartsWith("heading", StringComparison.OrdinalIgnoreCase)) return null;
            var suffix = compact[7..];
            return int.TryParse(suffix, out var level) && level is >= 1 and <= 9 ? level : null;
        }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* để lại file lỗi còn hơn nuốt mất ngoại lệ gốc */ }
        catch (UnauthorizedAccessException) { }
    }
}
