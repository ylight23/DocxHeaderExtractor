using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Kiểm chéo hình dạng anh em — spec gọi là <i>"kiểm tra rẻ và mạnh"</i>.
/// <para>
/// Ý tưởng: các mục ANH EM (cùng cha, cùng cấp) phải tương đồng về hình dạng. <c>KQ Mỹ</c> và
/// <c>KQ Philippin</c> là anh em nên độ dài xấp xỉ nhau; nếu luật tách heading/body cho ra
/// <c>KQ Mỹ</c> nhưng <c>KQ Philippin 0/0 (0/0)</c> thì chính sự bất đối xứng đó là dấu hiệu tách
/// sai — bắt được mà không cần LLM.
/// </para>
/// <para>
/// Chỉ ĐÁNH DẤU, không sửa: một mục lệch hình dạng vẫn có thể đúng (mục cuối chương thường dài hơn).
/// Đánh dấu để đẩy lên lượt phản biện, đúng tinh thần "escalate, không tự quyết" của spec §8.
/// </para>
/// </summary>
public static class SiblingShapeAudit
{
    /// <summary>Nhóm anh em phải có ít nhất bấy nhiêu mục thì "trung vị" mới có nghĩa.</summary>
    public const int MinimumSiblings = 3;

    /// <summary>Lệch bao nhiêu lần trung vị thì coi là bất thường.</summary>
    public const double LengthDeviationFactor = 3.0;

    /// <summary>Đánh dấu các mục lệch hình dạng so với anh em; trả về số mục đã đánh dấu.</summary>
    public static int Apply(IList<HeadingRecord> headings, SourceDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var textByIndex = source.Paragraphs.ToDictionary(p => p.SourceOrdinal, p => p.Text);
        var ordered = headings.OrderBy(h => h.Index).ToList();
        var marked = 0;

        foreach (var group in GroupByParent(ordered))
        {
            if (group.Count < MinimumSiblings) continue;

            var lengths = group.Select(h => TextOf(h, textByIndex).Length).OrderBy(x => x).ToList();
            var median = lengths[lengths.Count / 2];
            if (median == 0) continue;

            foreach (var heading in group)
            {
                var length = TextOf(heading, textByIndex).Length;
                if (length <= median * LengthDeviationFactor && length * LengthDeviationFactor >= median)
                    continue;
                heading.Disputed = true;
                marked++;
            }
        }
        return marked;
    }

    /// <summary>
    /// Gom theo CHA gần nhất — mục đứng trước gần nhất có cấp nhỏ hơn. Cùng định nghĩa cây mà
    /// <see cref="Eval.Evaluator"/> dùng cho metric parent-finding, nên hai chỗ không lệch nhau.
    /// </summary>
    private static List<List<HeadingRecord>> GroupByParent(IReadOnlyList<HeadingRecord> ordered)
    {
        var groups = new Dictionary<(int Parent, int? Level), List<HeadingRecord>>();
        var stack = new List<HeadingRecord>();

        foreach (var heading in ordered)
        {
            while (stack.Count > 0 && stack[^1].Level >= heading.Level) stack.RemoveAt(stack.Count - 1);
            var key = (stack.Count > 0 ? stack[^1].Index : -1, heading.Level);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(heading);
            stack.Add(heading);
        }
        return [.. groups.Values];
    }

    private static string TextOf(HeadingRecord heading, IReadOnlyDictionary<int, string> textByIndex) =>
        textByIndex.TryGetValue(heading.Index, out var text) ? text : heading.Text ?? "";
}
