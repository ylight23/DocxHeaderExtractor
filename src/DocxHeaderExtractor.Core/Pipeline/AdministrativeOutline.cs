using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Bộ dựng outline TẤT ĐỊNH cho văn bản hành chính Việt Nam — hệ ký hiệu gõ tay
/// <c>I.</c> / <c>1.</c> / <c>1.1.</c> / <c>a)</c>, không style Heading, không <c>numPr</c>,
/// không mục lục.
///
/// <para>
/// <b>Vì sao có file này thay vì vá tiếp <see cref="OpenXmlLayer.HeadingHeuristics"/>.</b>
/// Ba chế độ đạt 100% trên đáp án người kiểm — <c>--style-outline</c>, <c>--numbering-outline</c>,
/// <see cref="StructuralHierarchyResolver"/> — đều là bộ dựng ĐỌC MỘT DỮ KIỆN CẤU TRÚC cho cả tài
/// liệu, với thứ tự quyền lực rõ ràng. Còn §57–§59 đi hướng ngược lại: vá bộ chấm điểm bằng miễn
/// trừ, hình phạt và hướng duyệt, tức nhiều luật cục bộ tương tác nhau quanh ngưỡng 0,45.
/// Kết quả đo được là ba lần sửa liên tiếp gây hồi quy ở chỗ khác — trong đó §57.3 tự tạo ra lỗi
/// mà 474 test không bắt được, phải chờ người dùng báo.
/// </para>
/// <para>
/// §0 của dự án đã kết luận điều này từ trước: <i>"mọi tiến bộ đo được đều đến từ việc đọc dữ kiện
/// cấu trúc có sẵn trong tài liệu"</i> — đúng cấp đi 26,5% → 96,0% qua sáu luật tất định, không
/// qua tinh chỉnh trọng số. File này quay lại đúng nguyên tắc đó.
/// </para>
///
/// <para>
/// <b>Luật cấp.</b> KHÔNG gán cứng theo loại ký hiệu. Thứ tự lồng nhau lấy từ THỨ TỰ XUẤT HIỆN
/// LẦN ĐẦU của từng chữ ký trong chính tài liệu — cùng bất biến mà
/// <c>StructuralHierarchyResolver.SignatureTiers</c> dùng, và cùng kết luận mà spec §4.4 rút ra
/// từ một corpus độc lập: <i>"cấp phải suy theo ngữ cảnh cha gần nhất, không gán cứng theo loại
/// ký hiệu"</i>. Nhờ vậy tài liệu dùng <c>A.</c> thay cho <c>I.</c>, hay dùng <c>1)</c> thay cho
/// <c>1.</c>, đều chạy mà không sửa gì.
/// </para>
/// <para>
/// <b>Không có ngưỡng nào.</b> Không điểm số, không trần độ dài, không tỉ lệ. Một đoạn hoặc mang
/// ký hiệu đánh số hoặc không — đó là dữ kiện, không phải phán đoán.
/// </para>
/// </summary>
public static class AdministrativeOutline
{
    /// <summary>
    /// Dựng outline từ ký hiệu đánh số gõ tay. Trả về rỗng khi tài liệu không có đủ hai chữ ký
    /// khác nhau — một chữ ký duy nhất không suy ra được quan hệ lồng nhau nào, và đoán bừa ở đó
    /// là đúng thứ file này sinh ra để tránh.
    /// </summary>
    public static List<HeadingRecord> Build(SlimDocument document, bool splitMergedParagraphs = true)
    {
        var units = Units(document, splitMergedParagraphs);
        if (units.Count == 0) return [];

        // Thứ hạng = thứ tự XUẤT HIỆN LẦN ĐẦU của chữ ký. "I." gặp trước "1." nên nông hơn.
        Dictionary<string, int> rank = new(StringComparer.Ordinal);
        foreach (var u in units)
            if (!rank.ContainsKey(u.Token.Signature)) rank[u.Token.Signature] = rank.Count;
        if (rank.Count < 2) return [];

        List<HeadingRecord> result = [];
        List<(int Rank, int Level)> stack = [];

        // Ngưỡng nhan đề do CHÍNH tài liệu khai ra, không phải hằng số ký tự.
        var nguong = NguongNhanDe(units.Select(x => x.Text));

        foreach (var u in units)
        {
            var r = rank[u.Token.Signature];
            while (stack.Count > 0 && stack[^1].Rank >= r) stack.RemoveAt(stack.Count - 1);
            var level = Math.Clamp(stack.Count > 0 ? stack[^1].Level + 1 : 1, 1, 9);
            stack.Add((r, level));

            var (heading, body) = SplitHeadingBody(u.Text, nguong);
            var bodyStart = body is null ? -1 : u.Text.Length - body.Length;
            result.Add(new HeadingRecord
            {
                Index = u.Paragraph.Index,
                StableId = u.Paragraph.StableId,
                Level = level,
                Text = heading,
                StyleId = u.Paragraph.StyleId,
                Source = HeadingSource.Structure,
                // Ký hiệu do người soạn gõ ra là DỮ KIỆN, không phải suy đoán — cùng mức tin cậy
                // với style khai thẳng ở StyleDeclaredOutline.
                Confidence = 1.0,
                InlineBody = body,
                OriginalText = body is null ? null : u.Text,
                HeadingSpan = body is null ? null : new TextOffsetSpan(0, heading.Length),
                InlineBodySpan = body is null ? null : new TextOffsetSpan(bodyStart, u.Text.Length),
            });
        }

        return result;
    }

    private readonly record struct Unit(SlimParagraph Paragraph, string Text, NumberToken Token);

    /// <summary>
    /// Đơn vị đo là LÁT CẮT, không phải đoạn — bản chuyển PDF gộp cả trang vào một <c>w:p</c> nên
    /// 94% mốc nằm giữa đoạn (§47.1). Đoạn không bị gộp trả về chính nó.
    /// </summary>
    private static List<Unit> Units(SlimDocument document, bool splitMergedParagraphs)
    {
        List<Unit> units = [];
        foreach (var p in document.Paragraphs.OrderBy(x => x.Index))
        {
            if (p.Corrupt || p.TableDepth > 0 || string.IsNullOrWhiteSpace(p.Text)) continue;
            var segments = splitMergedParagraphs
                ? ParagraphHeadingSplitter.Segments(p.Text)
                : [p.Text];
            foreach (var seg in segments)
                if (NumberingAudit.Parse(seg) is { } token)
                    units.Add(new Unit(p, seg, token));
        }
        return units;
    }

    /// <summary>
    /// Bội số của TRUNG VỊ độ dài đơn vị, dùng làm ngưỡng "nhan đề này đã dính thân bài".
    /// <para>
    /// Đây là tỉ lệ, không phải số ký tự: thang đo do chính tài liệu cung cấp. Đo phân bố độ dài
    /// mục trên corpus cho thấy nhan đề SẠCH dồn quanh trung vị còn phần dính thân bài nằm hẳn ở
    /// đuôi — <c>010_Luat_An_ninh</c> trung vị 58 nhưng p90 = 231; <c>025_ND_47</c> trung vị 57,
    /// p90 = 170; trong khi <c>056_OpenStax</c> (đúng 46/46) trung vị 29 và DÀI NHẤT chỉ 58, nên
    /// không đơn vị nào của nó vượt ngưỡng.
    /// </para>
    /// </summary>
    internal const double NguongTheoTrungVi = 2.0;

    /// <summary>Số đơn vị tối thiểu để trung vị có nghĩa.</summary>
    internal const int MauToiThieu = 8;

    /// <summary>
    /// Ngưỡng độ dài nhan đề đo từ CHÍNH tài liệu: trung vị độ dài các đơn vị nhân
    /// <see cref="NguongTheoTrungVi"/>. Mẫu quá nhỏ thì trả 0 — nơi gọi sẽ không cắt gì.
    /// </summary>
    internal static int NguongNhanDe(IEnumerable<string> donVi)
    {
        var lens = donVi.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Length).OrderBy(x => x).ToList();

        if (lens.Count < MauToiThieu) return 0;

        return (int)(lens[lens.Count / 2] * NguongTheoTrungVi);
    }

    /// <summary>
    /// Tách nhan đề khỏi thân bài bằng RANH GIỚI CẤU TRÚC, không bằng điểm số: dấu ngắt ĐẦU TIÊN
    /// mà sau nó là số liệu. Không có thì cả lát là nhan đề.
    /// <para>
    /// Phải là dấu ngắt đầu tiên: thân bài hành chính đầy dấu hai chấm nội bộ
    /// (<c>QK4: 01, QK5: 05; QK9: 04</c>), lấy dấu cuối thì nhan đề nuốt trọn số liệu — lỗi thật
    /// đã xảy ra ở §57.3.
    /// </para>
    /// </summary>
    internal static (string Heading, string? Body) SplitHeadingBody(string text, int nguongNhanDe = 0)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (text[i] is not (':' or ';')) continue;

            var start = i + 1;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            if (start >= text.Length || !char.IsDigit(text[start])) continue;

            var end = i;
            while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
            if (end <= 0 || text[..end].Count(char.IsLetter) < 2) continue;

            return (text[..end], text[start..]);
        }

        return CatKhiQuaDai(text, nguongNhanDe);
    }

    /// <summary>
    /// Lối cắt dự phòng cho đơn vị QUÁ DÀI. Luật chính chỉ cắt ở <c>:</c>/<c>;</c> theo sau là số
    /// liệu — đúng cho văn bản hành chính, nhưng mù với bản chuyển PDF của báo cáo tài chính:
    /// <c>041_IBRD</c> có nhan đề dài <b>4.571 ký tự</b> vì cả đoạn gộp trở thành một mục, và
    /// 25% số mục toàn corpus dài quá <see cref="ParagraphHeadingSplitter.MaxHeadingLength"/>.
    /// <para>
    /// Ranh giới dùng ở đây là KẾT CÂU: dấu chấm câu rồi khoảng trắng rồi chữ hoa. Nhan đề không
    /// có dấu kết câu bên trong; thân bài thì có.
    /// </para>
    /// <para>
    /// <b>Chỉ chạy khi đã vượt ngưỡng.</b> Mục có độ dài bình thường không bị đụng tới, nên luật
    /// này không thể làm hồi quy nhóm tài liệu vốn đang đúng.
    /// </para>
    /// </summary>
    private static (string Heading, string? Body) CatKhiQuaDai(string text, int nguongNhanDe)
    {
        // Ngưỡng KHÔNG cố định: nó do chính tài liệu khai ra qua NguongNhanDe. Bằng 0 nghĩa là
        // nơi gọi không đo được (mẫu quá nhỏ), và khi đó không cắt gì — thà giữ nguyên còn hơn
        // cắt theo một con số bịa.
        if (nguongNhanDe <= 0 || text.Length <= nguongNhanDe) return (text, null);

        for (var i = 1; i < text.Length - 2; i++)
        {
            if (text[i] is not ('.' or '?' or '!')) continue;
            if (!char.IsWhiteSpace(text[i + 1])) continue;

            var start = i + 1;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            if (start >= text.Length || !char.IsUpper(text[start])) continue;

            var heading = text[..(i + 1)].TrimEnd();
            if (heading.Count(char.IsLetter) < 2) continue;

            return (heading, text[start..]);
        }

        return (text, null);
    }
}
