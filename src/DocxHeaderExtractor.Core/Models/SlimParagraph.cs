namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Vai trò của đoạn văn sau khi lọc bằng OpenXML (trước khi hỏi LLM).
/// </summary>
public enum ParagraphRole
{
    /// <summary>Đoạn thân bài bình thường – sẽ bị gom lại để tiết kiệm token.</summary>
    Normal = 0,

    /// <summary>Style trong document.xml/styles.xml khẳng định đây là heading.</summary>
    StyledHeading = 1,

    /// <summary>Không có style heading nhưng định dạng trực tiếp trông giống tiêu đề.</summary>
    HeadingCandidate = 2,

    /// <summary>Đoạn rỗng.</summary>
    Empty = 3,
}

/// <summary>
/// Một đoạn văn đã được rút gọn: chỉ giữ những thuộc tính có ích cho việc nhận diện tiêu đề.
/// </summary>
public sealed class SlimParagraph
{
    /// <summary>Chỉ số đoạn theo thứ tự tài liệu (ổn định, dùng làm khoá khi LLM trả kết quả).</summary>
    public required int Index { get; init; }

    /// <summary>Văn bản đã chuẩn hoá khoảng trắng (chưa cắt ngắn).</summary>
    public required string Text { get; init; }

    /// <summary>w:pStyle/@w:val</summary>
    public string? StyleId { get; init; }

    /// <summary>w:style/w:name/@w:val – tên hiển thị, đã resolve qua chuỗi basedOn.</summary>
    public string? StyleName { get; init; }

    /// <summary>w:outlineLvl (0..8) lấy từ đoạn hoặc kế thừa từ style.</summary>
    public int? OutlineLevel { get; init; }

    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool AllCaps { get; init; }

    /// <summary>Cỡ chữ quy về point (w:sz là half-point).</summary>
    public double? FontSizePt { get; init; }

    /// <summary>
    /// Cỡ chữ THÂN BÀI thực tế của tài liệu, dùng làm mốc so sánh tương đối.
    /// Không lấy từ docDefaults: rất nhiều tài liệu (đặc biệt luận văn tiếng Việt) đặt 14pt
    /// cho toàn bộ nội dung trong khi docDefaults vẫn là 11pt — lấy docDefaults sẽ khiến
    /// mọi đoạn đều bị chấm là "chữ to hơn thân bài". Giá trị này được gán sau khi đọc xong
    /// cả tài liệu nên là property có setter.
    /// </summary>
    public double? BodyFontSizePt { get; set; }

    /// <summary>left/center/right/both…</summary>
    public string? Alignment { get; init; }

    public int? NumberingId { get; init; }
    public int? NumberingLevel { get; init; }

    public bool KeepNext { get; init; }
    public bool PageBreakBefore { get; init; }

    /// <summary>Độ sâu bảng (0 = ngoài bảng).</summary>
    public int TableDepth { get; init; }

    /// <summary>
    /// Đoạn thuộc mục lục / danh mục hình bảng: nội dung nằm trong w:hyperlink trỏ tới
    /// neo _Toc… hoặc _heading…, hoặc dùng style TOC1..TOC9. Đây là tham chiếu tới tiêu đề,
    /// bản thân nó không phải tiêu đề.
    /// </summary>
    public bool InTableOfContents { get; init; }

    /// <summary>Chỉ số section (tăng sau mỗi w:sectPr).</summary>
    public int SectionIndex { get; init; }

    /// <summary>Kết quả phân loại bằng luật (heuristic).</summary>
    public ParagraphRole Role { get; set; } = ParagraphRole.Normal;

    /// <summary>Cấp heading đoán được từ style/numbering, 1..9. Null nếu không rõ.</summary>
    public int? GuessedLevel { get; set; }

    /// <summary>Điểm số heuristic, dùng để xếp hạng ứng viên khi cần cắt bớt.</summary>
    public double Score { get; set; }

    public bool IsCandidate => Role is ParagraphRole.StyledHeading or ParagraphRole.HeadingCandidate;

    public override string ToString() => $"[{Index}] {Role} {StyleId} :: {Text}";
}
