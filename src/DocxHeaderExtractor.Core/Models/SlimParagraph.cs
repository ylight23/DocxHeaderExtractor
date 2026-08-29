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

/// <summary>Một span định dạng lấy trực tiếp từ các w:r, offset trên <see cref="SlimParagraph.Text"/> đã chuẩn hoá.</summary>
public sealed record SlimTextSpan(int Start, int End, bool Bold, bool Italic, bool Underline, double? FontSizePt);

/// <summary>
/// Ánh xạ ngược một đoạn liên tục của <see cref="SlimParagraph.Text"/> (đã chuẩn hoá khoảng trắng)
/// về vị trí thật trong nguồn: run thứ mấy, và lệch bao nhiêu ký tự trong text thô của run đó.
/// <para>
/// Cần vì chuẩn hoá là phép mất mát: mọi chuỗi khoảng trắng — kể cả <c>w:tab</c> và <c>w:br</c> —
/// gộp thành MỘT dấu cách, nên offset trên <c>Text</c> không còn cộng thẳng vào nguồn được. Không có
/// ánh xạ này thì mọi thứ tính bằng offset chuẩn hoá đều không ghi ngược lại DOCX được:
/// <c>OutlineWriteback</c> hiện từ chối với lý do <c>inline_body_not_splittable</c> mỗi khi heading
/// chỉ chiếm một phần paragraph.
/// </para>
/// <para>Một segment được cắt ở mỗi chỗ mạch đứt: đổi run, hoặc có ký tự nguồn bị bỏ đi.</para>
/// </summary>
public sealed record SlimSourceSegment(int Start, int End, int RunIndex, int RawStart);

/// <summary>
/// Một đoạn văn đã được rút gọn: chỉ giữ những thuộc tính có ích cho việc nhận diện tiêu đề.
/// </summary>
public sealed class SlimParagraph : DocxHeaderExtractor.Core.Application.Policy.IPolicyParagraph
{
    /// <summary>Chỉ số đoạn theo thứ tự tài liệu (ổn định, dùng làm khoá khi LLM trả kết quả).</summary>
    public required int Index { get; init; }

    /// <summary>
    /// Địa chỉ XML ổn định trong document.xml (không thay đổi khi bật/tắt lọc bảng). Dùng để
    /// gán nhãn/evaluate lâu dài; Index vẫn giữ cho grammar ngắn gọn trong từng lần suy luận.
    /// </summary>
    public string StableId { get; init; } = "";

    /// <summary>Văn bản đã chuẩn hoá khoảng trắng (chưa cắt ngắn).</summary>
    public required string Text { get; set; }

    /// <summary>Ranh giới run OOXML để phát hiện heading lẫn nội dung trong cùng paragraph.</summary>
    public IReadOnlyList<SlimTextSpan> TextSpans { get; set; } = [];

    /// <summary>
    /// Vị trí trên <see cref="Text"/> nơi nguồn có một <c>w:br</c> (Shift+Enter).
    /// <para>
    /// Trước đây <c>w:br</c> bị biến thành một dấu cách và KHÔNG để lại dấu vết nào, nên ba trường
    /// hợp xuống dòng hoàn toàn khác nhau của DOCX — Word tự ngắt theo chiều rộng, Shift+Enter, và
    /// Enter thật — chỉ còn phân biệt được hai. Một tiêu đề bị Shift+Enter cắt đôi trông y hệt một
    /// tiêu đề bình thường có dấu cách.
    /// </para>
    /// <para>
    /// CỐ Ý không đổi ký tự trong <see cref="Text"/> thành <c>\n</c>: <c>Text</c> là hợp đồng của
    /// <see cref="TextSpans"/>, <c>InlineHeadingSplitter</c>, view gửi cho mô hình và writeback —
    /// thêm một mảng offset là phép cộng, đổi ký tự là đổi mọi offset cùng lúc.
    /// </para>
    /// </summary>
    public IReadOnlyList<int> LineBreakOffsets { get; init; } = [];

    /// <summary>Ánh xạ <see cref="Text"/> đã chuẩn hoá về run + offset thô. Xem <see cref="SlimSourceSegment"/>.</summary>
    public IReadOnlyList<SlimSourceSegment> SourceSegments { get; init; } = [];

    /// <summary>Span heading/body do parser xác minh, truyền cho lượt model cross-verification.</summary>
    public int? VerifiedHeadingEnd { get; set; }
    public int? VerifiedBodyStart { get; set; }
    public string? VerifiedBoundarySource { get; set; }

    /// <summary>w:pStyle/@w:val</summary>
    public string? StyleId { get; init; }

    /// <summary>w:style/w:name/@w:val – tên hiển thị, đã resolve qua chuỗi basedOn.</summary>
    public string? StyleName { get; init; }

    /// <summary>w:outlineLvl (0..8) lấy từ đoạn hoặc kế thừa từ style.</summary>
    public int? OutlineLevel { get; init; }

    /// <summary>
    /// Chỉ true với style heading dựng sẵn của OOXML (Heading1..9/Title/Subtitle).
    /// Đây là bằng chứng mạnh hơn tên style tự đặt hay outline level bị gán nhầm.
    /// </summary>
    public bool HasBuiltInHeadingStyle { get; set; }

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

    /// <summary>
    /// Đoạn nằm bên trong một <c>w:sdt</c> (content control) — khối mà Word dựng cho mục lục tự
    /// động, form, và các vùng nội dung có cấu trúc.
    /// <para>
    /// ĐO ĐƯỢC (§36) trên khoá luận thật: 21 ứng viên nằm trong <c>w:sdt</c> và <b>KHÔNG một mục nào
    /// là đề mục thật</b>. Chúng là dòng mục lục kèm số trang (<c>MỞ ĐẦU 1</c>, <c>KẾT LUẬN 154</c>)
    /// — tức chính TÊN của đề mục khác, nên mọi luật dựa vào hình thức đều nhận nhầm.
    /// </para>
    /// <para>
    /// Đây là dấu hiệu do CHÍNH WORD đặt, không phải suy đoán về hình thức và không phụ thuộc ngôn
    /// ngữ. Ghi lại vô điều kiện; việc dùng nó để đổi hành vi thì nằm sau cờ.
    /// </para>
    /// </summary>
    public bool InContentControl { get; init; }

    /// <summary>
    /// Đoạn hỏng trong chính file nguồn — ký tự bị nhân đôi do hai luồng run xen kẽ. Spec §3.6,
    /// xem <see cref="OpenXmlLayer.CorruptParagraphDetector"/>. Ghi lại vô điều kiện; việc loại nó
    /// khỏi tập ứng viên nằm sau cờ <c>--skip-corrupt</c>.
    /// </summary>
    public bool Corrupt { get; set; }

    /// <summary>
    /// Vai trò của BẢNG chứa đoạn này — spec §5.5. Xem <see cref="OpenXmlLayer.TableRoleClassifier"/>.
    /// Ghi lại vô điều kiện; việc loại bảng dữ liệu nằm sau cờ <c>--skip-data-tables</c>.
    /// </summary>
    public OpenXmlLayer.TableRole TableRole { get; set; }
    public int? NumberingLevel { get; init; }

    /// <summary>Nhãn numbering Word đã dựng từ numbering.xml, ví dụ "2.3." hoặc "IV.".</summary>
    public string? NumberLabel { get; set; }

    /// <summary>Độ sâu list OOXML, 1-based; độc lập với cấp heading do model quyết định.</summary>
    public int? NumberingDepth { get; set; }

    /// <summary>Định dạng numbering OOXML: decimal, upperRoman, lowerLetter…</summary>
    public string? NumberingFormat { get; set; }

    /// <summary>
    /// Cấp heading do CHÍNH danh sách đa cấp khai báo, lấy từ <c>w:lvl/w:pStyle</c>: nếu cấp ilvl
    /// của danh sách trỏ tới style Heading N thì danh sách đó chính là cây đề mục của tài liệu.
    /// <para>
    /// Đây là bằng chứng mạnh nhất trong OOXML về cấu trúc — mạnh hơn cả style trên từng đoạn, vì
    /// nó do người soạn cấu hình một lần cho cả tài liệu qua hộp thoại "Define New Multilevel List"
    /// rồi mọi đoạn tự bám theo. Khác với <see cref="OutlineLevel"/> vốn hay bị gán nhầm khi copy
    /// định dạng, ánh xạ cấp→style không đi kèm thao tác định dạng lẻ nên không nhiễm lỗi đó.
    /// </para>
    /// </summary>
    public int? NumberingStyleLevel { get; set; }

    /// <summary>
    /// Đoạn đứng ngay trước các dòng mục của mục lục — tức nó là TIÊU ĐỀ của mục lục đó. Quan hệ
    /// vị trí này là bằng chứng cấu trúc thay cho danh sách từ khoá ("MỤC LỤC", "Contents",
    /// "Danh mục hình ảnh"), nên nhận được cả những cách gọi không ai liệt kê trước.
    /// </summary>
    public bool PrecedesTableOfContents { get; set; }

    /// <summary>
    /// Một bảng bắt đầu trong vài đoạn ngay sau đoạn này. Dùng để tách CHÚ THÍCH BẢNG khỏi tiêu đề
    /// bằng quan hệ vị trí thay vì bằng danh sách từ khoá ("Bảng|Hình|Table|Figure") vốn chỉ đúng
    /// với vài thứ tiếng và bị tắt cùng cờ luật từ ngữ.
    /// </summary>
    public bool PrecedesTable { get; set; }

    public bool KeepNext { get; init; }
    public bool PageBreakBefore { get; init; }

    /// <summary>Độ sâu bảng (0 = ngoài bảng).</summary>
    public int TableDepth { get; init; }

    /// <summary>
    /// Đoạn thuộc mục lục / danh mục hình bảng: nội dung nằm trong w:hyperlink trỏ tới
    /// neo _Toc… hoặc _heading…, hoặc dùng style TOC1..TOC9. Đây là tham chiếu tới tiêu đề,
    /// bản thân nó không phải tiêu đề.
    /// <para>
    /// <c>set</c> chứ không <c>init</c> vì mục lục GÕ TAY không có cả hai dấu hiệu trên; một lượt
    /// hậu xử lý theo hình dạng (<c>MarkTypedTableOfContentsRuns</c>) bổ sung chúng sau khi đã đọc
    /// xong toàn bộ đoạn — nhận diện theo dãy chứ không theo từng đoạn rời nên không thể tính lúc
    /// dựng.
    /// </para>
    /// </summary>
    public bool InTableOfContents { get; set; }

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
