using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Sửa các cấp LLM bị trôi bằng quan hệ cấu trúc có sẵn: anh em đánh số liên tiếp cùng cấp,
/// còn 3.1 là con của 3. Không dùng từ khoá, font, ngôn ngữ hay vị trí hardcode.
/// </summary>
public static class StructuralHierarchyResolver
{
    /// <param name="respectStyleTrust">
    /// Cờ <c>--style-trust</c>. Mặc định FALSE để giữ đúng hợp đồng của cờ: StyleTrust luôn được ĐO
    /// và ghi vào <see cref="SlimDocument.StyleTrust"/> để báo cáo, nhưng chỉ được phép ĐỔI HÀNH VI
    /// khi người dùng bật. Chỗ tương đương ở <c>HeaderExtractionPipeline</c> cũng kiểm cờ như vậy.
    /// </param>
    public static int Apply(IList<HeadingRecord> headings, SlimDocument document,
        bool respectStyleTrust = false)
    {
        var ordered = headings.OrderBy(h => h.Index).ToList();
        var paths = ordered.ToDictionary(h => h.Index, h => PathOf(h, document));
        var tiers = SignatureTiers(ordered, document, respectStyleTrust);
        var changed = 0;

        for (var i = 0; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var path = paths[current.Index];

            // Cùng một chốt mà nhánh chữ ký đã có (xem SignatureTiers): cấu trúc đã khai cấp thì
            // không suy lại. Đoạn vẫn nằm trong `paths` vì nó là NEO cha/anh em cho các mục khác —
            // chỉ riêng việc GHI cấp của chính nó là bị cấm.
            if (Declared(current, document, respectStyleTrust)) continue;

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

            // Khi style của tài liệu KHÔNG bám độ sâu đánh số (StyleTrust hạ quyền gán cấp), thì
            // chính độ sâu ấy là câu trả lời — không cần suy từ hàng xóm. "1.1.1." sâu 3 thì cấp 3.
            //
            // ĐO ĐƯỢC vì sao cần vế này: §17 cài BỘ DÒ (hạ quyền style) nhưng bộ chấp hành vẫn đi
            // qua FindSibling/FindParent, tức suy cấp từ hàng xóm — mà hàng xóm cũng đang sai cùng
            // một kiểu. Trên khoá luận thật, 39/51 lỗi cấp là "sâu hơn đúng một cấp" (5→4: 24 mục,
            // 4→3: 15 mục), đúng nhóm Heading4/Heading5 mà §16.2 đã truy ra.
            if (respectStyleTrust && document.StyleTrust is { LevelTrusted: false }
                && path.Length is >= 1 and <= 9 && path.Length != current.Level)
            {
                current.Level = path.Length;
                changed++;
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
        IReadOnlyList<HeadingRecord> ordered, SlimDocument document, bool respectStyleTrust)
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
            if (Declared(heading, document, respectStyleTrust)) continue;

            if (NumberingAudit.ParseParagraph(paragraph, heading.Text) is not { } token) continue;

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

    /// <summary>
    /// Đoạn đã được chính tài liệu khai cấp: danh sách đa cấp gắn style Heading N
    /// (<c>w:lvl/w:pStyle</c>) hoặc style Heading built-in trên chính đoạn. Đây là hai nguồn đứng
    /// trên suy luận trong thứ tự quyền lực của <c>HeaderExtractionPipeline.ResolveLevel</c> — nếu rồi thì không suy lại (§6.2).
    /// <para>
    /// NGOẠI LỆ: style Heading chỉ được tính là "đã khai" khi nó THẬT SỰ mang thông tin cấp.
    /// <c>StyleTrust.LevelTrusted</c> sai nghĩa là mọi đề mục dùng chung một cấp style hoặc con số
    /// trong tên style không phải độ sâu — lúc đó coi nó là tuyên bố cấp thì chốt này khoá luôn bộ
    /// suy cấp tất định duy nhất đọc được chuỗi đánh số.
    /// </para>
    /// <para>
    /// ĐO ĐƯỢC (§13.4): trên <c>10-cap-style-thoai-hoa</c>, cả 9 đề mục mang <c>Heading2</c> nên
    /// chốt này đứng chặn, và nhánh <c>LevelTrusted</c> chuyển quyền cho mô hình — vốn trả về đúng
    /// cấp 2 cho tất cả. Danh sách đa cấp (<c>NumberingStyleLevel</c>) thì KHÔNG nới: nó khai cấp
    /// bằng cấu hình một lần cho cả tài liệu, không nhiễm lỗi copy định dạng như style.
    /// </para>
    /// </summary>
    private static bool Declared(HeadingRecord heading, SlimDocument document, bool respectStyleTrust)
    {
        var p = document.ByIndex(heading.Index);
        if (p is { NumberingStyleLevel: not null }) return true;
        if (p is not { HasBuiltInHeadingStyle: true }) return false;
        if (!respectStyleTrust) return true;
        return document.StyleTrust is null || document.StyleTrust.LevelTrusted;
    }

    private static int[]? PathOf(HeadingRecord heading, SlimDocument document)
    {
        var paragraph = document.ByIndex(heading.Index);
        // Trước đây truyền NHÃN TRƠ khi có NumberLabel ("3.1." không kèm tên mục), nên
        // ParseArabicPath (đòi HasTitleRemainder) luôn loại nó — cùng lỗi mà 13ac456 đã gom về
        // NumberingAudit.ParseParagraph ở sáu chỗ khác nhưng bỏ sót đúng chỗ này. Hệ quả đo được:
        // với văn bản Word đánh số bằng danh sách đa cấp (numPr), path luôn null nên
        // FindSiblingLevel/FindParentLevel không bao giờ chạy, phải rơi xuống tầng chữ ký — vốn chỉ
        // xếp hạng theo THỨ TỰ XUẤT HIỆN chữ ký chứ không tính đúng quan hệ cha–con, nên có thể ghi
        // đè nhầm cả cấp của chính mục cha (xem test kèm theo).
        return NumberingAudit.ParseArabicPath(NumberingAudit.TextWithNumberLabel(paragraph, heading.Text));
    }

    private static bool SameParent(int[] left, int[] right) =>
        left.Length == right.Length && left.Length > 0 && left[..^1].SequenceEqual(right[..^1]);
}
