using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Outline = ĐÚNG những gì tác giả đã khai bằng style Heading của Word, cấp suy từ ký hiệu đánh số.
/// Không gọi mô hình, hoàn toàn tất định.
/// <para>
/// Đây là định nghĩa outline do NGƯỜI DÙNG xác nhận (2026-08-10, §41). Nó khác định nghĩa cũ ở một
/// điểm bản lề: mục không mang style Heading thì <b>không</b> thuộc outline, dù nó có đánh số, in
/// đậm hay đứng riêng một dòng.
/// </para>
/// <para>
/// ĐO ĐƯỢC trên khoá luận, chấm với đáp án người dùng xác nhận (68 mục):
/// tập 68 mục mang style trùng KHÍT tập đáp án — <b>68 có style / 0 mục thừa nào có style</b>;
/// 59 mục pipeline trả thêm thì 46 chỉ có <c>numPr</c> và 13 không có bằng chứng nào.
/// Luật cấp tái tạo <b>68/68</b>.
/// </para>
/// </summary>
public static class StyleDeclaredOutline
{
    /// <summary>Số gõ tay nhiều cấp ở đầu dòng: <c>1.1</c>, <c>2.3.4</c>.</summary>
    private static readonly Regex TypedNumber = new(@"^\s*(\d+(?:\.\d+)+)", RegexOptions.Compiled);

    /// <summary>
    /// Cấp theo bằng chứng, đúng ba nhánh mà người dùng ghi trong cột <c>evidence</c>:
    /// <list type="bullet">
    /// <item>số gõ tay độ sâu <c>d</c> → cấp <c>d + 1</c> (<c>1.1</c> sâu 2 ⇒ cấp 3);</item>
    /// <item>danh sách Word (<c>numPr</c>) không có số trong text → cấp 2;</item>
    /// <item>còn lại (style, không đánh số) → cấp 1.</item>
    /// </list>
    /// <para>
    /// Vì sao <c>d + 1</c> chứ không phải <c>d</c>: mục không đánh số (<c>CHƯƠNG 1</c>, <c>MỞ ĐẦU</c>)
    /// chiếm cấp 1, nên mọi thứ có số phải lùi xuống một bậc. Hệ quả nhìn có vẻ lạ — <c>1.1</c> là
    /// cấp 3 nên dưới <c>CHƯƠNG 1</c> không có mục cấp 2 nào — nhưng đó đúng là hình dạng của tài
    /// liệu, không phải lỗi.
    /// </para>
    /// </summary>
    public static int LevelOf(SlimParagraph paragraph)
    {
        if (TypedNumber.Match(paragraph.Text ?? "") is { Success: true } m)
            return Math.Clamp(m.Groups[1].Value.Count(c => c == '.') + 2, 1, 9);
        return paragraph.NumberingId is not null ? 2 : 1;
    }

    /// <summary>
    /// Dựng outline từ các đoạn mang style Heading built-in, theo thứ tự tài liệu.
    /// </summary>
    public static List<HeadingRecord> Build(SlimDocument document) =>
    [
        .. document.Paragraphs
            .Where(p => p.HasBuiltInHeadingStyle && !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Index)
            .Select(p => new HeadingRecord
            {
                Index = p.Index,
                Level = LevelOf(p),
                Text = p.Text,
                Source = HeadingSource.Style,
                Confidence = 1.0,
                ConfidenceBasis = "style_declared",
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
            }),
    ];
}
