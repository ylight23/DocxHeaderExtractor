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
    public static List<HeadingRecord> Build(SlimDocument document)
    {
        var units = Units(document);
        if (units.Count == 0) return [];

        // Thứ hạng = thứ tự XUẤT HIỆN LẦN ĐẦU của chữ ký. "I." gặp trước "1." nên nông hơn.
        Dictionary<string, int> rank = new(StringComparer.Ordinal);
        foreach (var u in units)
            if (!rank.ContainsKey(u.Token.Signature)) rank[u.Token.Signature] = rank.Count;
        if (rank.Count < 2) return [];

        List<HeadingRecord> result = [];
        List<(int Rank, int Level)> stack = [];

        foreach (var u in units)
        {
            var r = rank[u.Token.Signature];
            while (stack.Count > 0 && stack[^1].Rank >= r) stack.RemoveAt(stack.Count - 1);
            var level = Math.Clamp(stack.Count > 0 ? stack[^1].Level + 1 : 1, 1, 9);
            stack.Add((r, level));

            var (heading, body) = SplitHeadingBody(u.Text);
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
            });
        }

        return result;
    }

    private readonly record struct Unit(SlimParagraph Paragraph, string Text, NumberToken Token);

    /// <summary>
    /// Đơn vị đo là LÁT CẮT, không phải đoạn — bản chuyển PDF gộp cả trang vào một <c>w:p</c> nên
    /// 94% mốc nằm giữa đoạn (§47.1). Đoạn không bị gộp trả về chính nó.
    /// </summary>
    private static List<Unit> Units(SlimDocument document)
    {
        List<Unit> units = [];
        foreach (var p in document.Paragraphs.OrderBy(x => x.Index))
        {
            if (p.Corrupt || p.TableDepth > 0 || string.IsNullOrWhiteSpace(p.Text)) continue;
            foreach (var seg in ParagraphHeadingSplitter.Segments(p.Text))
                if (NumberingAudit.Parse(seg) is { } token)
                    units.Add(new Unit(p, seg, token));
        }
        return units;
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
    internal static (string Heading, string? Body) SplitHeadingBody(string text)
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
        return (text, null);
    }
}
