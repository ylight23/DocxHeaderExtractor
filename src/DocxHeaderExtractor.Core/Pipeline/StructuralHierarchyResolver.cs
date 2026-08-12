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
        // Khoá theo THAM CHIẾU HeadingRecord, không theo Index. Từ §51 hai tính năng gặp nhau:
        // --split-merged sinh nhiều mục dùng chung một Index (chủ đích, để đáp án trong keys/ không
        // hỏng vì dịch chỉ số) còn DeterministicHierarchy nay mặc định BẬT (§51). Khoá theo Index thì
        // ToDictionary ném ArgumentException "same key" — crash tiềm ẩn, có test riêng ghim lại.
        // Khoá theo tham chiếu cũng ĐÚNG NGHĨA hơn: hai lát cắt có text khác nhau nên phải có
        // đường dẫn đánh số khác nhau, gộp chúng vào một khoá là mất thông tin.
        var paths = new Dictionary<HeadingRecord, int[]?>(ReferenceEqualityComparer.Instance);
        foreach (var h in ordered) paths[h] = PathOf(h, document);
        var tiers = SignatureTiers(ordered, document, respectStyleTrust);
        var nesting = StyleNestingDepths(ordered, document, respectStyleTrust);
        var changed = 0;

        for (var i = 0; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var path = paths[current];

            // Style không khai đúng CẤP nhưng khai đúng THỨ TỰ LỒNG NHAU (xem StyleNestingDepths).
            // Đứng trước nhánh độ sâu đánh số vì nó mạnh hơn hẳn trên chính tập mục nó phủ: đo
            // riêng lớp style-only trên khoá luận, đúng cấp 41,2% → 100% (§26).
            if (nesting.TryGetValue(current, out var depth))
            {
                if (depth != current.Level) { current.Level = depth; changed++; }
                continue;
            }

            // Cùng một chốt mà nhánh chữ ký đã có (xem SignatureTiers): cấu trúc đã khai cấp thì
            // không suy lại. Đoạn vẫn nằm trong `paths` vì nó là NEO cha/anh em cho các mục khác —
            // chỉ riêng việc GHI cấp của chính nó là bị cấm.
            if (Declared(current, document, respectStyleTrust)) continue;

            if (path is null)
            {
                // Item của danh sách đa cấp thì NEO CỤC BỘ thắng tầng chữ ký — xem LocalListDepth.
                if (LocalListDepth(i, ordered, document) is { } anchored)
                {
                    if (anchored != current.Level) { current.Level = anchored; changed++; }
                    continue;
                }

                // Đường dẫn chỉ đọc được số Ả Rập có dấu chấm, nên "PHẦN I." hay "A)" rơi ra ngoài
                // và cấp của chúng phải trông chờ vào mô hình. Tầng chữ ký lấp đúng chỗ đó.
                if (tiers.TryGetValue(current, out var tier) && tier != current.Level)
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
    private static Dictionary<HeadingRecord, int> SignatureTiers(
        IReadOnlyList<HeadingRecord> ordered, SlimDocument document, bool respectStyleTrust)
    {
        // Khoá theo THAM CHIẾU, không theo Index — xem chú thích ở Apply và handoff §55.3. Dictionary<int,…> không
        // ném khi trùng khoá, nó GHI ĐÈ: hai lát cắt cùng Index nhưng khác chữ ký ("Chương I" và
        // "Điều 1" trong cùng một đoạn gộp) sẽ nhận chung một tầng, sai mà không có dấu hiệu nào.
        var result = new Dictionary<HeadingRecord, int>(ReferenceEqualityComparer.Instance);
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        var tokens = new Dictionary<HeadingRecord, NumberToken>(ReferenceEqualityComparer.Instance);

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

            tokens[heading] = token;
            if (!rank.ContainsKey(token.Signature)) rank[token.Signature] = rank.Count + 1;
        }

        if (rank.Count < 2) return result;
        foreach (var (record, token) in tokens) result[record] = rank[token.Signature];
        return result;
    }

    /// <summary>
    /// Cấp suy từ THỨ TỰ LỒNG NHAU của style Heading, không từ con số trong tên style.
    /// <para>
    /// Xuất phát từ một phép đo theo metric <i>parent finding</i> của HRDoc (AAAI 2023, arXiv
    /// 2303.13839) — bài toán con mà nhánh nghiên cứu tái dựng cấu trúc phân cấp dùng, thay cho việc
    /// chấm cấp tuyệt đối. Trên khoá luận thật, lớp style-only chấm được:
    /// </para>
    /// <list type="bullet">
    /// <item>đúng cấp tuyệt đối: <b>41,2%</b> (28/68) — và 40/40 lỗi đều lệch ĐỀU một bậc;</item>
    /// <item>đúng cha: <b>100%</b> (68/68) — cây không sai một cạnh nào.</item>
    /// </list>
    /// <para>
    /// Tức tác giả dùng Heading3 ở chỗ ngữ nghĩa là cấp 2: con số sai, quan hệ đúng. Gán cấp = độ
    /// sâu trong cây do chính style dựng nên đưa 41,2% → <b>100%</b> trên đúng 68 mục đó.
    /// </para>
    /// <para>
    /// CHỈ chạy khi <c>StyleTrust.NestingTrusted</c>: cần style thực sự biến thiên. Đo trên
    /// <c>10-cap-style-thoai-hoa</c> (mọi đề mục đều Heading2) thì luật này sập cả cây về một cấp và
    /// TỆ HƠN cách cũ — 44,4% → 33,3%. Điều kiện không phải để cho chắc, nó là điều kiện tồn tại.
    /// </para>
    /// </summary>
    private static Dictionary<HeadingRecord, int> StyleNestingDepths(
        IReadOnlyList<HeadingRecord> ordered, SlimDocument document, bool respectStyleTrust)
    {
        var result = new Dictionary<HeadingRecord, int>(ReferenceEqualityComparer.Instance);
        if (!respectStyleTrust) return result;
        // LevelTrusted đúng ⇒ Declared() đã chặn từ trước và cấp lấy thẳng từ style; không tới đây.
        if (document.StyleTrust is not { LevelTrusted: false, NestingTrusted: true }) return result;

        var ancestors = new List<int>();
        foreach (var heading in ordered)
        {
            if (document.ByIndex(heading.Index)
                is not { HasBuiltInHeadingStyle: true, GuessedLevel: { } styleLevel }) continue;

            // Mục không đánh style thì không đụng tới ngăn xếp: cấp của chúng đến từ độ sâu đánh số,
            // và trộn hai thang vào một ngăn xếp là lấy cái sai của thang này đè lên cái đúng của thang kia.
            while (ancestors.Count > 0 && ancestors[^1] >= styleLevel) ancestors.RemoveAt(ancestors.Count - 1);
            result[heading] = Math.Clamp(ancestors.Count + 1, 1, 9);
            ancestors.Add(styleLevel);
        }
        return result;
    }

    /// <summary>
    /// Cấp của một item danh sách đa cấp, neo vào mục ĐỨNG NGAY TRƯỚC nó mà KHÔNG thuộc cùng danh
    /// sách: cấp của mục đó cộng một.
    /// <para>
    /// Sửa một sai lệch có hệ thống của <see cref="SignatureTiers"/>. Tầng chữ ký xếp hạng theo
    /// THỨ TỰ XUẤT HIỆN LẦN ĐẦU TRONG CẢ TÀI LIỆU, tức một con số TOÀN CỤC; nhưng "a., b., c." là
    /// quan hệ CỤC BỘ với mục cha ngay trên nó. Khi cùng một chữ ký được dùng ở hai độ sâu khác
    /// nhau trong tài liệu, con số toàn cục chỉ đúng ở chỗ đầu tiên.
    /// </para>
    /// <para>
    /// ĐO ĐƯỢC trên khoá luận (§31): ba cụm "a./b./c." nằm dưới ba mục cha ở ba độ sâu khác nhau,
    /// tầng chữ ký gán cả ba là cấp 5 trong khi đáp án là 4, 4 và 3. Cả ba đều đúng bằng
    /// <c>cha + 1</c>. Đây chính là hình dạng mà metric cây chỉ ra (§30.3): cha đúng 97,2% còn cấp
    /// tuyệt đối chỉ 81,1% — nhánh đúng hình, sai gốc.
    /// </para>
    /// <para>
    /// Chỉ áp cho đoạn CÓ <c>NumberingId</c> và không đọc được đường dẫn số Ả Rập. Đoạn đánh số
    /// "1.1.2" đã có đường dẫn nên đi nhánh khác, và đoạn không đánh số thì không có "cùng danh sách"
    /// để loại trừ nên neo sẽ bám nhầm vào chính anh em của nó.
    /// </para>
    /// </summary>
    private static int? LocalListDepth(
        int at, IReadOnlyList<HeadingRecord> ordered, SlimDocument document)
    {
        if (document.ByIndex(ordered[at].Index) is not { NumberingId: { } listId }) return null;

        for (var i = at - 1; i >= 0; i--)
        {
            var previous = document.ByIndex(ordered[i].Index);
            // Cùng danh sách ⇒ anh em, không phải cha. Bỏ qua để đi tiếp lên trên.
            if (previous?.NumberingId == listId) continue;
            return Math.Clamp(ordered[i].Level + 1, 1, 9);
        }
        return null;
    }

    private static int? FindSiblingLevel(
        int at, IReadOnlyList<HeadingRecord> ordered, IReadOnlyDictionary<HeadingRecord, int[]?> paths, int[] current)
    {
        for (var i = at - 1; i >= 0; i--)
        {
            var previous = paths[ordered[i]];
            if (previous is null || previous.Length != current.Length || !SameParent(previous, current)) continue;
            if (previous[^1] + 1 == current[^1]) return ordered[i].Level;
            // Số giảm/reset là một danh sách khác; đừng nối nhầm qua phần mới.
            if (previous[^1] >= current[^1]) return null;
        }
        return null;
    }

    private static int? FindParentLevel(
        int at, IReadOnlyList<HeadingRecord> ordered, IReadOnlyDictionary<HeadingRecord, int[]?> paths, int[] current)
    {
        if (current.Length < 2) return null;
        for (var i = at - 1; i >= 0; i--)
        {
            var previous = paths[ordered[i]];
            if (previous is null || previous.Length != current.Length - 1) continue;
            if (previous.SequenceEqual(current[..^1])) return ordered[i].Level + 1;
        }
        return null;
    }

    private static int? FindUnnumberedParentLevel(
        int at, IReadOnlyList<HeadingRecord> ordered, IReadOnlyDictionary<HeadingRecord, int[]?> paths, int[] current)
    {
        if (current.Length != 1) return null;
        for (var i = at - 1; i >= 0; i--)
        {
            // Một heading không đánh số ngay trước một danh sách 1., 2., … là cha tiềm năng.
            // Chỉ dùng khi model đã gán nó nông hơn mục hiện tại, tránh nâng cấp tùy tiện.
            if (paths[ordered[i]] is null && ordered[i].Level < current.Length + 1)
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
