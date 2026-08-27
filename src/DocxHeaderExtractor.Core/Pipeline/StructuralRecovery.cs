using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

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
/// <para>
/// TODO mục 7: cũng nhận <see cref="NumberKind.Labelled"/> (<c>PHỤ LỤC 1</c>, <c>PHỤ LỤC 2</c>) —
/// trước đây token này chỉ nuôi <c>HasStructuralEvidence</c> để cứu đoạn ĐÃ từng là ứng viên bị mô
/// hình gắn nhãn sai <c>DocumentTitle</c>; bốn đề mục thật (<c>PHỤ LỤC 1</c>, <c>PHỤ LỤC 2</c>...)
/// có <c>role=Normal</c> nên chưa từng tới được mô hình, đường cứu đó không chạm tới. Nhãn + số an
/// toàn hơn số trần một cấp: đòi một TỪ nhãn thật đứng trước (<c>LabelledRx</c>/<c>BareLabelledRx</c>
/// của <see cref="NumberingAudit"/>) nên không lẫn vào dòng số liệu trần như <c>Khong_cuu_danh_so_mot_cap</c>
/// đã kiểm.
/// </para>
/// </summary>
public static class StructuralRecovery
{
    /// <summary>Cứu dây chuyền: 3.2 được cứu lại mở đường cho 3.3. Chặn trên phòng vòng lặp bệnh lý.</summary>
    private const int MaxRounds = 8;

    /// <summary>Nhóm anh em + giá trị thứ tự trong nhóm đó — dùng chung cho cả đường dẫn Ả Rập nhiều
    /// cấp ("3." là nhóm của 3.1/3.2/...) lẫn nhãn+số ("label:phụ lục" là nhóm của PHỤ LỤC 1/2/...).</summary>
    private readonly record struct Series(string GroupKey, int Value);

    /// <summary>
    /// Tìm các đoạn cần cứu. <paramref name="reviewed"/> là những đoạn mô hình đã được hỏi —
    /// đoạn nằm ngoài tập này thì chưa từng được cân nhắc, cứu vào là vượt quyền.
    /// </summary>
    public static IReadOnlyList<RecoveredHeading> Find(
        IReadOnlyList<SlimParagraph> reviewed,
        IReadOnlyDictionary<int, HeadingRecord> accepted,
        ExtractionOptions? options = null)
    {
        options ??= new ExtractionOptions();
        var series = new Dictionary<int, Series>();
        foreach (var p in reviewed)
        {
            // Chỉ xét đánh số nhiều cấp ("3.2"). Với một cấp ("2.") thì không có tiền tố cha để
            // xác định anh em, mà tài liệu hành chính đầy dòng số liệu mở đầu bằng số — cứu bừa
            // ở đó sẽ đổ vào hàng loạt dòng không phải tiêu đề.
            // Đọc qua NumberLabel như StructuralHierarchyResolver.PathOf: Word đánh số bằng danh
            // sách nhiều cấp thì "3.2" không nằm trong text, và cứu-anh-em mù hẳn với nhóm đó.
            var numbering = NumberingAudit.TextWithNumberLabel(p, p.Text);
            if (NumberingAudit.ParseArabicPath(numbering) is { Length: >= 2 } path)
            {
                series[p.Index] = new Series(string.Join('.', path[..^1]), path[^1]);
                continue;
            }

            // Nhãn+số ("PHỤ LỤC 1") không cần độ sâu ≥ 2: bản thân yêu cầu có từ nhãn thật đứng
            // trước đã đủ để loại dòng số liệu trần — xem ghi chú ở đầu file.
            if (NumberingAudit.ParseParagraph(p, p.Text, options) is { Kind: NumberKind.Labelled } token)
                series[p.Index] = new Series($"label:{token.Label}", token.Value);
        }
        if (series.Count == 0) return [];

        var byIndex = reviewed.ToDictionary(p => p.Index);
        var current = new Dictionary<int, HeadingRecord>(accepted);
        var recovered = new Dictionary<int, RecoveredHeading>();

        for (var round = 0; round < MaxRounds; round++)
        {
            var addedThisRound = 0;

            foreach (var anchor in current.Values.OrderBy(h => h.Index).ToList())
            {
                if (!series.TryGetValue(anchor.Index, out var anchorSeries)) continue;

                var next = FindNextSibling(anchor, anchorSeries, series, byIndex, current, recovered);
                if (next is null) continue;
                // A sibling is recovered AT the anchor's level - with no anchor level there is no
                // level to give it, so recovery cannot proceed for this anchor.
                if (anchor.Level is not { } anchorLevel) continue;

                var nextSeries = series[next.Index];
                recovered[next.Index] = new RecoveredHeading(
                    next,
                    anchorLevel,
                    $"{Describe(nextSeries)} là em kế tiếp của {Describe(anchorSeries)} " +
                    $"(đã nhận ở cấp H{anchor.Level}) nhưng mô hình loại — bằng chứng định dạng yếu hơn" +
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

    private static string Describe(Series s) =>
        s.GroupKey.StartsWith("label:", StringComparison.Ordinal)
            ? $"{s.GroupKey["label:".Length..].ToUpperInvariant()} {s.Value}"
            : $"{s.GroupKey}{s.Value}";

    /// <summary>
    /// Em kế tiếp hợp lệ: cùng nhóm anh em, giá trị đúng bằng +1, nằm SAU neo, và giữa hai
    /// đoạn không có heading nào nông hơn hoặc bằng neo — nghĩa là vẫn còn trong phạm vi mục cha.
    /// </summary>
    private static SlimParagraph? FindNextSibling(
        HeadingRecord anchor,
        Series anchorSeries,
        Dictionary<int, Series> series,
        Dictionary<int, SlimParagraph> byIndex,
        Dictionary<int, HeadingRecord> current,
        Dictionary<int, RecoveredHeading> recovered)
    {
        var scopeEnd = current.Values
            .Where(h => h.Index > anchor.Index && h.Level <= anchor.Level)
            .Select(h => h.Index)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

        foreach (var (index, s) in series.OrderBy(kv => kv.Key))
        {
            if (index <= anchor.Index || index >= scopeEnd) continue;
            if (current.ContainsKey(index) || recovered.ContainsKey(index)) continue;
            if (s.GroupKey != anchorSeries.GroupKey || s.Value != anchorSeries.Value + 1) continue;
            if (byIndex.TryGetValue(index, out var p)) return p;
        }

        return null;
    }
}
