namespace DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

/// <summary>
/// Paragraph HỎNG trong chính file nguồn — không phải lỗi parser. Spec §3.6.
/// <para>
/// Ca gốc: <c>HHììnnhh 11.1</c>. Hai luồng run xen kẽ, phân biệt bởi <c>w:position</c>, nên mỗi ký
/// tự có một bản thường và một bản bị đẩy baseline. Phiên phân tích trước đã <b>render ra ảnh để
/// kiểm chứng</b>: Word vẽ đúng như vậy. Nếu không kiểm bằng ảnh thì kết luận "lỗi parser" — và đó
/// là kết luận sai.
/// </para>
/// <para>
/// Đoạn hỏng phải bị loại khỏi tập ứng viên trước khi tới mô hình, nếu không mô hình sẽ cố suy luận
/// trên rác.
/// </para>
/// </summary>
public static class CorruptParagraphDetector
{
    /// <summary>Dưới độ dài này thì tỉ lệ cặp trùng không có ý nghĩa thống kê.</summary>
    public const int MinimumLength = 12;

    /// <summary>Tỉ lệ cặp ký tự trùng nhau để coi là hỏng. Số của spec §3.6.</summary>
    public const double DoubledThreshold = 0.55;

    /// <summary>
    /// Văn bản có bị nhân đôi từng ký tự không. Bỏ khoảng trắng trước khi ghép cặp vì luồng thứ hai
    /// không nhất thiết mang theo dấu cách.
    /// <para>
    /// CHỈ đếm cặp CHỮ CÁI và CHỮ SỐ. Dấu câu bị loại khỏi cả tử số lẫn mẫu số vì dot-leader
    /// (<c>Summary . . . . . . 42</c>), gạch dưới điền form (<c>Country: ______</c>) và dấu chấm lửng
    /// (<c>………………</c>) là chuỗi ký tự lặp HỢP LỆ — ghép cặp trong đó tất nhiên khớp, không liên quan
    /// gì tới hiện tượng hai luồng run của ca gốc.
    /// </para>
    /// <para>
    /// Đo trên toàn corpus trước khi sửa: <b>601/601 đoạn bị gắn cờ (100%) là dương giả kiểu này</b>,
    /// KHÔNG đoạn nào có cặp chữ cái chiếm đa số như ca gốc. Nặng nhất là nhóm hợp đồng mua sắm WB
    /// (036: 114 đoạn, 037: 108 đoạn) — toàn biểu mẫu gạch dưới. Xem handoff §174.
    /// </para>
    /// </summary>
    public static bool IsDoubled(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var c = text.Where(char.IsLetterOrDigit).ToArray();
        if (c.Length < MinimumLength) return false;

        var pairs = c.Length / 2;
        var same = 0;
        for (var i = 0; i + 1 < c.Length; i += 2)
            if (char.ToLowerInvariant(c[i]) == char.ToLowerInvariant(c[i + 1])) same++;

        return (double)same / pairs >= DoubledThreshold;
    }
}
