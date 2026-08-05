using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

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
/// 3. Sau khi ghi, đọc lại bản đích bằng đúng <see cref="DocxSlimExtractor"/> và đối chiếu
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

                    pPr.OutlineLevel = new OutlineLevel { Val = heading.Level - 1 };
                    if (headingStyles.TryGetValue(heading.Level, out var styleId))
                        pPr.ParagraphStyleId = new ParagraphStyleId { Val = styleId };

                    applied.Add(heading);
                }

                main.Document!.Save();
            }

            Verify(target, extraction, applied);
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

        // Đoạn chứa cả heading lẫn nội dung cùng dòng: đặt outlineLvl cho cả đoạn sẽ kéo phần
        // thân bài vào cây điều hướng. Muốn ghi được thì phải tách đoạn trong file, mà tách đoạn
        // là sửa nội dung — nằm ngoài phạm vi của tool này.
        if (heading.InlineBody is not null) return "inline_body_not_splittable";

        if (heading.Level is < 1 or > 9) return "invalid_level";
        return null;
    }

    private static void Verify(
        string target,
        ExtractionOptions extraction,
        IReadOnlyList<HeadingRecord> applied)
    {
        var written = new DocxSlimExtractor(extraction).Extract(target);
        foreach (var heading in applied)
        {
            var paragraph = written.ByIndex(heading.Index)
                            ?? throw new InvalidOperationException(
                                $"Sau khi ghi, đoạn {heading.Index} không còn tồn tại trong bản đích.");

            if (heading.StableId is { Length: > 0 } stableId && paragraph.StableId != stableId)
                throw new InvalidOperationException(
                    $"Sau khi ghi, đoạn {heading.Index} đổi địa chỉ XML ({paragraph.StableId} ≠ {stableId}).");

            var expected = heading.OriginalText ?? heading.Text;
            if (paragraph.Text != expected)
                throw new InvalidOperationException(
                    $"Sau khi ghi, nội dung đoạn {heading.Index} không còn khớp nguồn.");

            if (paragraph.OutlineLevel != heading.Level - 1)
                throw new InvalidOperationException(
                    $"Sau khi ghi, đoạn {heading.Index} có outline level {paragraph.OutlineLevel}, " +
                    $"khác cấp {heading.Level} đã chốt.");
        }
    }

    /// <summary>Chỉ nhận style Heading 1..9 CÓ SẴN trong tài liệu; không tạo style mới.</summary>
    private static Dictionary<int, string> HeadingStyleIds(MainDocumentPart main)
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* để lại file lỗi còn hơn nuốt mất ngoại lệ gốc */ }
        catch (UnauthorizedAccessException) { }
    }
}
