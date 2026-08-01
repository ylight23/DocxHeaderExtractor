using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Một đoạn được cứu lại nhờ quan hệ anh em số học, kèm lý do để ghi log.</summary>
public sealed record RecoveredHeading(SlimParagraph Paragraph, int Level, string Reason);

/// <summary>
/// Cứu heading bị mô hình loại hoàn toàn, dựa trên đánh số của chính tài liệu.
/// <para>
/// Ca đo được: <c>3.1. Trong dự báo</c> nằm ngoài bảng, có <c>b=1</c> nên được nhận H3; còn
/// <c>3.2. Ngoài dự báo</c> nằm trong ô bảng, style Normal, và OpenXML KHÔNG ghi <c>b=1</c> dù
/// nhìn trong Word có vẻ đậm — bằng chứng định dạng chỉ còn mỗi mẫu <c>3.2.</c>, nên mô hình gán
/// <c>l=0</c>. Bộ chuẩn hoá cấp không cứu được: nó chỉ sắp lại cấp cho heading ĐÃ được chọn.
/// </para>
/// <para>
/// Bất biến dùng ở đây: nếu <c>3.1</c> đã được nhận thì <c>3.2</c> nằm cùng phạm vi mục <c>3</c>
/// phải ít nhất được giữ lại để người duyệt xem — cấu trúc do người soạn đánh số ra, không phải
/// suy đoán của mô hình. Không hardcode tên mục nào; chỉ dùng quan hệ số học.
/// </para>
/// </summary>
public static class StructuralRecovery
{
    /// <summary>Cứu dây chuyền: 3.2 được cứu lại mở đường cho 3.3. Chặn trên phòng vòng lặp bệnh lý.</summary>
    private const int MaxRounds = 8;

    /// <summary>
    /// Tìm các đoạn cần cứu. <paramref name="reviewed"/> là những đoạn mô hình đã được hỏi —
    /// đoạn nằm ngoài tập này thì chưa từng được cân nhắc, cứu vào là vượt quyền.
    /// </summary>
    public static IReadOnlyList<RecoveredHeading> Find(
        IReadOnlyList<SlimParagraph> reviewed,
        IReadOnlyDictionary<int, HeadingRecord> accepted)
    {
        var paths = new Dictionary<int, int[]>();
        foreach (var p in reviewed)
        {
            // Chỉ xét đánh số nhiều cấp ("3.2"). Với một cấp ("2.") thì không có tiền tố cha để
            // xác định anh em, mà tài liệu hành chính đầy dòng số liệu mở đầu bằng số — cứu bừa
            // ở đó sẽ đổ vào hàng loạt dòng không phải tiêu đề.
            if (NumberingAudit.ParseArabicPath(p.Text) is { Length: >= 2 } path) paths[p.Index] = path;
        }
        if (paths.Count == 0) return [];

        var byIndex = reviewed.ToDictionary(p => p.Index);
        var current = new Dictionary<int, HeadingRecord>(accepted);
        var recovered = new Dictionary<int, RecoveredHeading>();

        for (var round = 0; round < MaxRounds; round++)
        {
            var addedThisRound = 0;

            foreach (var anchor in current.Values.OrderBy(h => h.Index).ToList())
            {
                if (!paths.TryGetValue(anchor.Index, out var anchorPath)) continue;

                var next = FindNextSibling(anchor, anchorPath, paths, byIndex, current, recovered);
                if (next is null) continue;

                var label = string.Join('.', anchorPath);
                var nextLabel = string.Join('.', paths[next.Index]);
                recovered[next.Index] = new RecoveredHeading(
                    next,
                    anchor.Level,
                    $"{nextLabel} là em kế tiếp của {label} (đã nhận ở cấp H{anchor.Level}) " +
                    $"nhưng mô hình loại — bằng chứng định dạng yếu hơn" +
                    (next.TableDepth > 0 ? ", đoạn nằm trong bảng" : ""));
                addedThisRound++;
            }

            // Đưa phần vừa cứu vào tập neo để vòng sau nối tiếp được 3.3, 3.4…
            foreach (var r in recovered.Values)
            {
                if (current.ContainsKey(r.Paragraph.Index)) continue;
                current[r.Paragraph.Index] = new HeadingRecord
                {
                    Index = r.Paragraph.Index,
                    StableId = r.Paragraph.StableId,
                    Level = r.Level,
                    Text = r.Paragraph.Text,
                    Source = HeadingSource.Structure,
                    Confidence = 0.5,
                };
            }

            if (addedThisRound == 0) break;
        }

        return [.. recovered.Values.OrderBy(r => r.Paragraph.Index)];
    }

    /// <summary>
    /// Em kế tiếp hợp lệ: cùng tiền tố cha, giá trị cuối đúng bằng +1, nằm SAU neo, và giữa hai
    /// đoạn không có heading nào nông hơn hoặc bằng neo — nghĩa là vẫn còn trong phạm vi mục cha.
    /// </summary>
    private static SlimParagraph? FindNextSibling(
        HeadingRecord anchor,
        int[] anchorPath,
        Dictionary<int, int[]> paths,
        Dictionary<int, SlimParagraph> byIndex,
        Dictionary<int, HeadingRecord> current,
        Dictionary<int, RecoveredHeading> recovered)
    {
        var scopeEnd = current.Values
            .Where(h => h.Index > anchor.Index && h.Level <= anchor.Level)
            .Select(h => h.Index)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

        foreach (var (index, path) in paths.OrderBy(kv => kv.Key))
        {
            if (index <= anchor.Index || index >= scopeEnd) continue;
            if (current.ContainsKey(index) || recovered.ContainsKey(index)) continue;
            if (!IsNextSibling(anchorPath, path)) continue;
            if (byIndex.TryGetValue(index, out var p)) return p;
        }

        return null;
    }

    private static bool IsNextSibling(int[] anchor, int[] candidate)
    {
        if (anchor.Length != candidate.Length) return false;
        for (var i = 0; i < anchor.Length - 1; i++)
            if (anchor[i] != candidate[i]) return false;
        return candidate[^1] == anchor[^1] + 1;
    }
}
