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
    /// Nhận làm ứng viên cả những dòng CHỈ vì chúng đứng riêng một dòng, không dấu câu cuối, viết
    /// hoa đầu — tức không có bằng chứng cấu trúc nào, kể cả điểm heuristic.
    /// <para>
    /// ĐO ĐƯỢC trên khoá luận thật (§33): đường này sinh <b>75 ứng viên và KHÔNG một đề mục thật
    /// nào</b> — độ chính xác 0%. Nó nhận `Nguồn: Facebook` mười hai lần, dòng bìa, phương án trắc
    /// nghiệm, dòng mục lục gõ tay kèm số trang. Đó là 58% tập ứng viên, và 6 trong 21 dương tính
    /// giả cuối cùng đi ra từ đây.
    /// </para>
    /// <para>
    /// Vẫn để MẶC ĐỊNH BẬT: hợp đồng của tầng ứng viên là chọn RỘNG, và một tài liệu không đủ để
    /// kết luận đường này vô dụng ở mọi tài liệu. Cờ tồn tại để đo được cái giá của nó.
    /// </para>
    /// </summary>
    public bool PromoteStandaloneLines { get; set; } = true;

    /// <summary>
    /// Bỏ đoạn nằm trong <c>w:sdt</c> (content control) ra khỏi tập ứng viên.
    /// <para>
    /// ĐO ĐƯỢC (§36): 21/129 ứng viên trên khoá luận nằm trong khối này và KHÔNG mục nào là đề mục
    /// thật — chúng là dòng mục lục tự động kèm số trang. Vì văn bản của chúng chính là TÊN của đề
    /// mục khác nên không luật hình thức nào tách được; chỉ dấu hiệu cấu trúc của Word mới tách.
    /// </para>
    /// <para>
    /// MẶC ĐỊNH TẮT: hợp đồng của tầng ứng viên là chọn rộng, và một tài liệu không đủ để kết luận
    /// mọi content control đều là mục lục — Word còn dùng <c>w:sdt</c> cho form và vùng nội dung
    /// có cấu trúc, nơi đề mục thật hoàn toàn có thể nằm trong.
    /// </para>
    /// </summary>
    public bool SkipContentControls { get; set; }

    /// <summary>
    /// Cho phép hậu kiểm đọc chuỗi đánh số dạng <c>NHÃN + SỐ</c> khi KHÔNG còn chữ nào phía sau
    /// (<c>PHỤ LỤC 1</c>), thay vì đòi phần đuôi như hiện tại.
    /// <para>
    /// Lý do ràng buộc cũ tồn tại: bỏ nó đi thì <c>Bảng 1.2 Đối chiếu…</c> bị tách thành nhãn
    /// "Bảng" + số 1 rồi hậu kiểm báo thiếu những mục không tồn tại. Nhưng chú thích LUÔN có phần
    /// đuôi; <c>NHÃN + SỐ + HẾT</c> là hình dạng khác hẳn.
    /// </para>
    /// <para>
    /// ĐO ĐƯỢC (§36): trên khoá luận, mẫu này khớp 13 đoạn — 8 trong <c>w:sdt</c> (dòng mục lục kèm
    /// số trang, không phải đề mục) và 5 ngoài <c>w:sdt</c> (<c>PHỤ LỤC 1</c>, <c>PHỤ LỤC 2</c>,
    /// <c>Tiểu kết chương 1/2/3</c>) — <b>5/5 là đề mục thật</b>. Vì vậy luật này chỉ áp cho đoạn
    /// NGOÀI content control; hai vế tách nhau sạch 8/8 và 5/5.
    /// </para>
    /// </summary>
    public bool AllowBareLabelledNumbers { get; set; }

    /// <summary>
    /// Đánh dấu nhãn lặp (<c>Nguồn: Facebook</c>, <c>Nhận xét:</c>) là ô cấu trúc, không phải đề mục
    /// điều hướng — spec §6.3c. Xem <see cref="Pipeline.RepeatedLabelAudit"/>.
    /// <para>MẶC ĐỊNH TẮT: spec nói rõ đây là quyết định cấu hình MỘT LẦN cho cả tập, tuỳ mục đích
    /// outline (điều hướng hay tái dựng cấu trúc đầy đủ), không phải phán đoán từng ca.</para>
    /// </summary>
    public bool FlagRepeatedLabels { get; set; }

    /// <summary>Lệnh `xml --mode-only`: chỉ in chế độ tài liệu đo được, không dựng gì thêm.</summary>
    public bool ReportModeOnly { get; set; }

    /// <summary>
    /// Bật các luật dựa trên TỪ NGỮ: danh sách từ khoá mở đầu ("Chương", "Điều", "Phụ lục",
    /// "Chapter"…) và mẫu chú thích ("Hình 2.4.", "Bảng 1.2", "Figure 3:").
    /// Tắt (<c>--structural-only</c>) để chỉ dùng tín hiệu thuần cấu trúc OOXML — không phụ
    /// thuộc ngôn ngữ tài liệu — và nhường toàn bộ phán đoán ngữ nghĩa cho mô hình.
    /// Các luật về đánh số (1.2.3, I., A.), gạch đầu dòng và dấu câu vẫn giữ vì chúng
    /// là quy ước ký hiệu chung, không gắn với một ngôn ngữ cụ thể.
    /// </summary>
    public bool UseLexicalRules { get; set; } = true;

    /// <summary>
    /// Chấm độ tin cậy của style Word ở mức TÀI LIỆU và hạ quyền của nó khi tài liệu áp style bừa.
    /// Xem <see cref="StyleTrustAudit"/>.
    /// <para>MẶC ĐỊNH TẮT cho tới khi có số đo; kỷ luật §9.3 là một biến mỗi vòng.</para>
    /// </summary>
    public bool UseStyleTrust { get; set; }

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
