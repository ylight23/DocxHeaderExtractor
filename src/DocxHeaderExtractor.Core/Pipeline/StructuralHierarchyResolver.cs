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
        var changed = 0;

        for (var i = 0; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var path = paths[current.Index];
            if (path is null) continue;

            var target = FindSiblingLevel(i, ordered, paths, path)
                         ?? FindParentLevel(i, ordered, paths, path)
                         ?? FindUnnumberedParentLevel(i, ordered, paths, path);
            if (target is not { } level || level == current.Level) continue;

            current.Level = Math.Clamp(level, 1, 9);
            changed++;
        }

        return changed;
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
