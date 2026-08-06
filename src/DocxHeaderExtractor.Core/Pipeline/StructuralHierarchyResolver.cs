using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Sửa các cấp LLM bị trôi bằng quan hệ cấu trúc có sẵn: anh em đánh số liên tiếp cùng cấp,
/// còn 3.1 là con của 3. Không dùng từ khoá, font, ngôn ngữ hay vị trí hardcode.
/// </summary>
public static class StructuralHierarchyResolver
{
    public static int Apply(IList<HeadingRecord> headings, SlimDocument document)
    {
        var ordered = headings.OrderBy(h => h.Index).ToList();
        var paths = ordered.ToDictionary(h => h.Index, h => PathOf(h, document));
        var tiers = SignatureTiers(ordered, document);
        var changed = 0;

        for (var i = 0; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var path = paths[current.Index];
            if (path is null)
            {
                // Đường dẫn chỉ đọc được số Ả Rập có dấu chấm, nên "PHẦN I." hay "A)" rơi ra ngoài
                // và cấp của chúng phải trông chờ vào mô hình. Tầng chữ ký lấp đúng chỗ đó.
                if (tiers.TryGetValue(current.Index, out var tier) && tier != current.Level)
                {
                    current.Level = Math.Clamp(tier, 1, 9);
                    changed++;
                }
                continue;
            }

            // Tầng chữ ký CHỈ dùng cho mục mà đường dẫn số không đọc được (La Mã, chữ cái). Đưa nó
            // vào cả nhánh này thì nó ghi đè cả những mục đường dẫn vốn xử lý đúng: đo được ở ca
            // "3. Cha" / "3.1. Con" — nó kéo cấp của "3." từ 2 xuống 1 rồi "3.1." tụt theo.
            var target = FindSiblingLevel(i, ordered, paths, path)
                         ?? FindParentLevel(i, ordered, paths, path)
                         ?? FindUnnumberedParentLevel(i, ordered, paths, path);
            if (target is not { } level || level == current.Level) continue;

            current.Level = Math.Clamp(level, 1, 9);
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Suy cấp từ CHỮ KÝ đánh số của chính tài liệu, cho cả những dạng mà đường dẫn Ả Rập không
    /// đọc được ("PHẦN I.", "A)", "Chương 2").
    /// <para>
    /// Dựa trên bất biến đã có trong <see cref="NumberingAudit"/>: hai tiêu đề cùng chữ ký
    /// (<c>Kind:Depth</c>, ví dụ <c>Roman:1</c> hay <c>Arabic:2</c>) thì phải cùng cấp. Từ đó, thứ
    /// tự XUẤT HIỆN LẦN ĐẦU của các chữ ký chính là thứ tự lồng nhau: trong "PHẦN I → 1. → 1.1.",
    /// Roman:1 xuất hiện trước nên là cấp 1, Arabic:1 cấp 2, Arabic:2 cấp 3.
    /// </para>
    /// <para>
    /// Không hardcode "PHẦN" hay "Chương" — luật chỉ nhìn loại ký hiệu và độ sâu, nên áp được cho
    /// tài liệu ngôn ngữ khác. Chỉ chạy khi có từ hai chữ ký trở lên: một chữ ký duy nhất thì không
    /// suy ra được quan hệ lồng nhau nào.
    /// </para>
    /// </summary>
    private static Dictionary<int, int> SignatureTiers(
        IReadOnlyList<HeadingRecord> ordered, SlimDocument document)
    {
        var result = new Dictionary<int, int>();
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        var tokens = new Dictionary<int, NumberToken>();

        foreach (var heading in ordered)
        {
            var paragraph = document.ByIndex(heading.Index);
            // Cấu trúc đã khai cấp cho đoạn này (style Heading built-in, hoặc danh sách đa cấp gắn
            // style) thì không suy lại. ĐO ĐƯỢC khi thiếu chốt này: trên 01-style-chuan — tài liệu
            // dùng toàn style chuẩn — tầng chữ ký ghi đè 5 cấp và kéo độ chính xác cấp từ 100%
            // xuống 87,2%. Lý do kép: vừa vi phạm thứ tự quyền lực (cấu trúc trên suy luận), vừa
            // xếp hạng sai vì "Chương 1." không phân tích được nên chữ ký đầu tiên gặp lại là
            // Arabic:2 của "1.1." và nó bị coi là tầng ngoài cùng.
            if (paragraph is { NumberingStyleLevel: not null } or { HasBuiltInHeadingStyle: true }) continue;

            var text = paragraph?.NumberLabel is { Length: > 0 } label
                ? label + " " + (paragraph.Text ?? heading.Text)
                : paragraph?.Text ?? heading.Text;
            if (NumberingAudit.Parse(text) is not { } token) continue;

            tokens[heading.Index] = token;
            if (!rank.ContainsKey(token.Signature)) rank[token.Signature] = rank.Count + 1;
        }

        if (rank.Count < 2) return result;
        foreach (var (index, token) in tokens) result[index] = rank[token.Signature];
        return result;
    }

    private static int? FindSiblingLevel(
        int at, IReadOnlyList<HeadingRecord> ordered, IReadOnlyDictionary<int, int[]?> paths, int[] current)
    {
        for (var i = at - 1; i >= 0; i--)
        {
            var previous = paths[ordered[i].Index];
            if (previous is null || previous.Length != current.Length || !SameParent(previous, current)) continue;
            if (previous[^1] + 1 == current[^1]) return ordered[i].Level;
            // Số giảm/reset là một danh sách khác; đừng nối nhầm qua phần mới.
            if (previous[^1] >= current[^1]) return null;
        }
        return null;
    }

    private static int? FindParentLevel(
        int at, IReadOnlyList<HeadingRecord> ordered, IReadOnlyDictionary<int, int[]?> paths, int[] current)
    {
        if (current.Length < 2) return null;
        for (var i = at - 1; i >= 0; i--)
        {
            var previous = paths[ordered[i].Index];
            if (previous is null || previous.Length != current.Length - 1) continue;
            if (previous.SequenceEqual(current[..^1])) return ordered[i].Level + 1;
        }
        return null;
    }

    private static int? FindUnnumberedParentLevel(
        int at, IReadOnlyList<HeadingRecord> ordered, IReadOnlyDictionary<int, int[]?> paths, int[] current)
    {
        if (current.Length != 1) return null;
        for (var i = at - 1; i >= 0; i--)
        {
            // Một heading không đánh số ngay trước một danh sách 1., 2., … là cha tiềm năng.
            // Chỉ dùng khi model đã gán nó nông hơn mục hiện tại, tránh nâng cấp tùy tiện.
            if (paths[ordered[i].Index] is null && ordered[i].Level < current.Length + 1)
                return ordered[i].Level + 1;
        }
        return null;
    }

    private static int[]? PathOf(HeadingRecord heading, SlimDocument document)
    {
        var paragraph = document.ByIndex(heading.Index);
        var label = paragraph?.NumberLabel;
        return NumberingAudit.ParseArabicPath(label ?? paragraph?.Text ?? heading.Text);
    }

    private static bool SameParent(int[] left, int[] right) =>
        left.Length == right.Length && left.Length > 0 && left[..^1].SequenceEqual(right[..^1]);
}
