using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Tách tiêu đề nằm LỌT GIỮA một paragraph.
///
/// <para>
/// Toàn bộ pipeline coi paragraph là đơn vị nguyên tử: một đoạn hoặc là heading, hoặc không.
/// Giả định đó đúng với tài liệu soạn trong Word, và sai với tài liệu chuyển từ PDF. Đo trên
/// corpus 95 file của <c>todo10_8</c>: 83 file là bản chuyển PDF→DOCX, và 4.590/6.858 mục
/// (67%) có ranh giới heading nằm giữa đoạn chứ không trùng ranh giới paragraph. Với các file
/// đó tầng paragraph không có gì để bắt — <c>001_Bo_luat_Dan_su</c> ra đúng 1 mục trên 151 đoạn,
/// và mục duy nhất đó là tên file PDF.
/// </para>
///
/// <para>
/// <b>Không dùng danh sách từ khoá.</b> Bản Python tách 3.303 mục bằng từ khoá tiếng Việt.
/// Ở đây mốc cắt là DẠNG "nhãn + số" tổng quát, cùng hình dạng với
/// <c>NumberingAudit.LabelledRx</c>: một từ viết hoa 2–12 chữ cái theo sau là số Ả Rập hoặc La Mã.
/// "Điều 4." khớp vì nó có dạng đó, không phải vì chữ "Điều" nằm trong bảng nào. Hệ quả là luật
/// này chạy được trên tài liệu tiếng Anh (<c>Article 4.</c>, <c>Section 2.</c>) mà không sửa gì.
/// </para>
///
/// <para>
/// <b>Chỉ số paragraph không đổi.</b> Bộ cắt KHÔNG chèn paragraph mới; nó trả về các lát cắt
/// cùng trỏ về một chỉ số. Nếu tách đoạn thật thì mọi chỉ số phía sau dịch đi, và mọi đáp án
/// trong <c>keys/</c> — vốn tham chiếu theo chỉ số — hỏng toàn bộ. Cái giá phải trả là nhiều
/// heading dùng chung một <c>Index</c>, nên nơi tiêu thụ phải phân biệt bằng <c>Text</c>.
/// </para>
/// </summary>
public static class ParagraphHeadingSplitter
{
    /// <summary>
    /// Mốc có thể bắt đầu một mục mới. Hai dạng, cùng một lần quét để giữ đúng thứ tự vị trí:
    /// nhãn + số (<c>Điều 4.</c>) và số thuần có thể nhiều cấp (<c>1.</c>, <c>2.3.</c>).
    /// Chỉ dạng đầu được nhận làm tiêu đề; dạng sau chỉ dùng làm mốc KẾT THÚC tiêu đề, vì
    /// "1. Bộ luật này là luật chung điều chỉnh…" là một khoản văn xuôi, không phải nhan đề.
    /// </summary>
    /// <para>
    /// Lookbehind là <c>(?&lt;![\p{Lu}\d])</c> chứ không phải <c>(?&lt;![\p{L}\d])</c>, và đó là
    /// điểm mấu chốt. Bản chuyển PDF xoá ký tự xuống dòng mà KHÔNG chèn dấu cách thay thế, nên
    /// mốc bị dán vào từ trước: <c>…Bộ luật dân sự1. Bộ luật này…</c>, <c>…quốc tế.Điều 5.…</c>.
    /// Đòi ký tự đứng trước không phải chữ cái thì mọi mốc kiểu đó trượt hết — đo được trên
    /// 001_Bo_luat_Dan_su: 149 đoạn dài, chỉ 3 mục lọt. Cho phép chữ THƯỜNG đứng trước (dấu hiệu
    /// của chỗ dán) nhưng vẫn chặn chữ HOA và chữ số, vì hai thứ đó nghĩa là đang ở giữa một
    /// token thật (<c>khoản 2Điều này</c> là tham chiếu chéo, không phải đề mục mới).
    /// </para>
    /// <para>
    /// Cái thực sự chặn tham chiếu chéo là dấu ngắt bắt buộc sau số: <c>Điều 3 của Bộ luật này</c>
    /// không có dấu ngắt nên không bao giờ khớp, dù đứng ở đâu.
    /// </para>
    private static readonly Regex MarkerRx = new(
        @"(?<![\p{Lu}\d])(?:(?<label>\p{Lu}[\p{L}]{1,11})\s+(?<num>\d{1,3}|[IVXLCDM]{1,7})|(?<bare>\d{1,3}(?:\.\d{1,3}){0,3}))\s*[\.\):\-–:]\s*",
        RegexOptions.Compiled);

    /// <summary>Nhan đề dài hơn ngần này gần như chắc chắn đã nuốt luôn thân bài.</summary>
    internal const int MaxHeadingLength = 200;

    /// <summary>Phải còn chữ sau ký hiệu số thì mới là nhan đề, không phải mẩu số liệu.</summary>
    private static readonly Regex TitleWordRx = new(@"\p{L}{2,}", RegexOptions.Compiled);

    public readonly record struct Slice(int Start, int Length, string Text);

    /// <summary>
    /// Phân hoạch ĐẦY ĐỦ đoạn thành các lát tại mọi mốc — khác <see cref="Split"/> ở chỗ giữ cả
    /// lát không phải tiêu đề. Dùng cho tầng phân loại, nơi cần biết tỉ lệ mốc trên toàn tài liệu
    /// chứ không chỉ các mốc đủ điều kiện làm tiêu đề.
    /// <para>
    /// Vì sao tầng phân loại cần cái này: mọi luật nhận dạng chế độ đều neo <c>^</c>, trong khi
    /// bản chuyển PDF gộp cả trang vào một đoạn (~1.900 ký tự). Đo trên 55 tài liệu không phân
    /// loại được: <b>1.596 mốc nằm ở đầu đoạn, 24.220 mốc nằm bên trong</b> — 94% cấu trúc vô
    /// hình. Sửa ngưỡng ở tầng phân loại mà chưa cắt đoạn thì không thể ăn, vì tử số bằng 0.
    /// </para>
    /// Đoạn không phải đoạn gộp trả về chính nó, nên nơi gọi không cần phân nhánh.
    /// </summary>
    public static IReadOnlyList<string> Segments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var marks = MarkerRx.Matches(text);
        if (marks.Count < 2) return [text];

        List<string> parts = [];
        var starts = marks.Select(m => m.Index).ToList();
        if (starts[0] > 0) starts.Insert(0, 0);
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : text.Length;
            var seg = text[starts[i]..end].Trim();
            if (seg.Length > 0) parts.Add(seg);
        }
        return parts.Count > 0 ? parts : [text];
    }

    /// <summary>
    /// Trả về các tiêu đề tìm được bên trong <paramref name="text"/>. Rỗng nghĩa là đoạn này
    /// không chứa mốc nào — nơi gọi giữ nguyên hành vi cũ, coi cả đoạn là một đơn vị.
    /// </summary>
    public static IReadOnlyList<Slice> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var marks = MarkerRx.Matches(text);
        if (marks.Count == 0) return [];

        // Một đoạn mà TOÀN BỘ nội dung nằm sau đúng một mốc ở đầu là đoạn bình thường, không
        // phải đoạn gộp. Trả rỗng để tầng trên xử lý như cũ, tránh chẻ đôi heading lành lặn.
        if (marks.Count == 1 && marks[0].Index == 0) return [];

        var slices = new List<Slice>();
        for (var i = 0; i < marks.Count; i++)
        {
            if (!marks[i].Groups["label"].Success) continue;

            var start = marks[i].Index;
            var end = i + 1 < marks.Count ? marks[i + 1].Index : text.Length;
            var body = text[start..end].TrimEnd();
            if (body.Length > MaxHeadingLength) continue;

            var remainder = text[(marks[i].Index + marks[i].Length)..Math.Max(marks[i].Index + marks[i].Length, end)];
            if (!TitleWordRx.IsMatch(remainder)) continue;

            slices.Add(new Slice(start, body.Length, body));
        }

        return slices;
    }
}
