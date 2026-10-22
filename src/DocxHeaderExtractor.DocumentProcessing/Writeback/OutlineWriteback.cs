using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxHeaderExtractor.DocumentProcessing.Writeback;

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

    /// <summary>Shared with <c>ProductWriteback</c> — the split mechanics are data-shape agnostic.</summary>
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
