using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Nhãn lặp lại — <c>Nguồn: Facebook</c>, <c>Nhận xét:</c> — dòng đóng vai một Ô CẤU TRÚC lặp đi
/// lặp lại chứ không phải một đề mục trong mục lục. Spec §6.3c.
/// <para>
/// Vì sao KHÔNG chỉ dựa vào "lặp nhiều lần": §34.3 đã đo đúng luật đó và nó hỏng — loại được 2
/// dương tính giả nhưng làm mất <b>4 đề mục thật</b>, vì khoá luận dùng cấu trúc song song
/// (<c>Về ngôn ngữ</c> là đề mục thật, lặp ở ba chương). Spec thêm hai điều kiện nữa, và chính hai
/// điều kiện đó phân biệt hai nhóm:
/// </para>
/// <list type="number">
/// <item>lặp từ <see cref="MinimumRepeats"/> lần trở lên;</item>
/// <item><b>không</b> mang ký hiệu đánh số nào — nhãn lặp là <c>*</c>, <c>-</c> hoặc trơn, còn
/// <c>Về ngôn ngữ</c> nằm trong danh sách đa cấp và mang nhãn <c>b.</c>;</item>
/// <item><b>không</b> có anh em cùng cấp liền kề mang cùng ký hiệu — mục lặp đứng rải rác dưới
/// nhiều cha khác nhau, còn đề mục song song có anh em ngay bên cạnh.</item>
/// </list>
/// <para>
/// Mặc định TẮT. Spec nói rõ đây là <i>quyết định cấu hình một lần cho cả tập</i>, phụ thuộc mục
/// đích outline: dùng để ĐIỀU HƯỚNG thì nhãn lặp là nhiễu; dùng để TÁI DỰNG CẤU TRÚC đầy đủ thì nó
/// là dữ liệu. Không phải phán đoán từng ca, nên không được tự bật.
/// </para>
/// </summary>
public static class RepeatedLabelAudit
{
    /// <summary>Số lần lặp tối thiểu để một dòng bị coi là nhãn cấu trúc chứ không phải đề mục.</summary>
    public const int MinimumRepeats = 3;

    /// <summary>
    /// Đánh dấu các mục là nhãn lặp và trả về số mục đã đánh dấu. Chỉ HẠ vai trò bằng cách gắn cờ
    /// <see cref="HeadingRecord.Disputed"/>; việc bỏ chúng khỏi outline điều hướng là quyết định của
    /// người gọi, đúng theo tinh thần "một quyết định cho cả tập" của spec.
    /// </summary>
    public static int Apply(IList<HeadingRecord> headings, SlimDocument document)
    {
        if (headings.Count < MinimumRepeats) return 0;

        var ordered = headings.OrderBy(h => h.Index).ToList();

        // Đếm lặp trên TOÀN TÀI LIỆU, không trên tập heading đã nhận. Bản đầu đếm trên tập đã nhận
        // và ra SỐ KHÔNG: `Nguồn: Tik Tok` lặp 12 lần trong tài liệu nhưng chỉ LỌT một lần vào kết
        // quả (11 lần kia mô hình đã bác), nên nhóm không bao giờ đủ ngưỡng. Nhãn cấu trúc là thuộc
        // tính của TÀI LIỆU; việc mô hình đã chặn phần lớn không làm nó bớt là nhãn.
        var documentRepeats = document.Paragraphs
            .GroupBy(p => Normalize(p.Text))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.Count());

        var byText = ordered
            .GroupBy(h => Normalize(TextOf(h, document)))
            .Where(g => g.Key.Length > 0 &&
                        documentRepeats.GetValueOrDefault(g.Key) >= MinimumRepeats)
            .ToList();

        var marked = 0;
        foreach (var group in byText)
        {
            // Điều kiện 2: mang đánh số thì KHÔNG phải nhãn lặp — nó là đề mục song song.
            if (group.Any(h => HasNumbering(h, document))) continue;

            // Điều kiện 3: có anh em liền kề cùng cấp thì là đề mục thật, không phải ô lặp.
            if (group.Any(h => HasAdjacentSibling(h, ordered, document))) continue;

            foreach (var heading in group)
            {
                heading.Disputed = true;
                marked++;
            }
        }
        return marked;
    }

    /// <summary>
    /// Có mục nào NGAY TRƯỚC hoặc NGAY SAU trong cây, cùng cấp, mà không phải chính nó không.
    /// <para>
    /// Đây là vế cứu <c>Về ngôn ngữ</c>: nó đứng cạnh <c>Về thành phần kết cấu</c>, <c>Về định dạng
    /// bài đăng</c> — một dãy anh em thật. Còn <c>Nguồn: Facebook</c> nằm rải rác dưới nhiều mục cha
    /// khác nhau, mỗi lần một mình.
    /// </para>
    /// </summary>
    private static bool HasAdjacentSibling(
        HeadingRecord heading, List<HeadingRecord> ordered, SlimDocument document)
    {
        var at = ordered.IndexOf(heading);
        var self = Normalize(TextOf(heading, document));
        foreach (var neighbour in new[] { at - 1, at + 1 })
        {
            if (neighbour < 0 || neighbour >= ordered.Count) continue;
            var other = ordered[neighbour];
            if (other.Level == heading.Level && Normalize(TextOf(other, document)) != self) return true;
        }
        return false;
    }

    private static bool HasNumbering(HeadingRecord heading, SlimDocument document) =>
        document.ByIndex(heading.Index) is { } p &&
        (p.NumberingId is not null || NumberingAudit.ParseParagraph(p, heading.Text) is not null);

    private static string TextOf(HeadingRecord heading, SlimDocument document) =>
        document.ByIndex(heading.Index)?.Text ?? heading.Text;

    private static string Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : string.Join(' ', text.ToLowerInvariant().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
