namespace DocxHeaderExtractor.Core.OpenXmlLayer;

public sealed class ExtractionOptions
{
    /// <summary>Cắt ngắn text khi ghi ra XML tinh gọn (ký tự).</summary>
    public int MaxTextLength { get; set; } = 160;

    /// <summary>Đoạn dài hơn ngưỡng này không bao giờ được coi là ứng viên tiêu đề.</summary>
    public int MaxCandidateTextLength { get; set; } = 200;

    /// <summary>Ngưỡng điểm heuristic để giữ lại một đoạn không có style heading.</summary>
    public double CandidateThreshold { get; set; } = 0.45;

    /// <summary>
    /// Bật các luật dựa trên TỪ NGỮ: danh sách từ khoá mở đầu ("Chương", "Điều", "Phụ lục",
    /// "Chapter"…) và mẫu chú thích ("Hình 2.4.", "Bảng 1.2", "Figure 3:").
    /// Tắt (<c>--structural-only</c>) để chỉ dùng tín hiệu thuần cấu trúc OOXML — không phụ
    /// thuộc ngôn ngữ tài liệu — và nhường toàn bộ phán đoán ngữ nghĩa cho mô hình.
    /// Các luật về đánh số (1.2.3, I., A.), gạch đầu dòng và dấu câu vẫn giữ vì chúng
    /// là quy ước ký hiệu chung, không gắn với một ngôn ngữ cụ thể.
    /// </summary>
    public bool UseLexicalRules { get; set; } = true;

    /// <summary>Đi vào cả đoạn nằm trong bảng.</summary>
    public bool IncludeTables { get; set; } = true;

    /// <summary>Đọc thêm w:hdr / w:ftr (header–footer trang in).</summary>
    public bool IncludePageHeadersFooters { get; set; }

    /// <summary>Gom các đoạn Normal liên tiếp thành &lt;n c="k"/&gt; thay vì bỏ hẳn.</summary>
    public bool CollapseNormalRuns { get; set; } = true;

    /// <summary>Kèm 1 đoạn Normal ngay sau ứng viên làm ngữ cảnh (giúp LLM phân biệt tiêu đề / câu mở đầu).</summary>
    public bool IncludeFollowingContext { get; set; } = true;

    /// <summary>Độ dài tối đa của đoạn ngữ cảnh đi kèm.</summary>
    public int ContextTextLength { get; set; } = 60;
}
