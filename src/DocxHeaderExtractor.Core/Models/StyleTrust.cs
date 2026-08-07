namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Style Word của MỘT tài liệu cụ thể có đáng tin không — và đáng tin cho việc gì.
/// <para>
/// Pipeline hiện trao cho style built-in <b>hai quyền vô điều kiện</b>: quyền CHỌN (đoạn này là
/// tiêu đề, <c>Score = 1.0</c>, thoát sớm khỏi <see cref="HeadingHeuristics.Classify"/>) và quyền
/// GÁN CẤP (cấp lấy từ tên style, mô hình không được ghi đè). Trên bench thì đúng — nó đưa độ chính
/// xác cấp từ 54,2% lên 100%. Trên tài liệu thật thì tuỳ tài liệu, và hai tài liệu đã đo hỏng ở hai
/// quyền khác nhau:
/// </para>
/// <list type="bullet">
/// <item><b>Khoá luận</b> (§9.2, §9.7): 68/68 đoạn mang style đều là đề mục thật ⇒ quyền CHỌN tin
/// được. Nhưng tác giả dùng Heading1 → Heading3 → Heading4, bỏ hẳn Heading2 ⇒ con số trong tên style
/// không phải độ sâu thật, đúng cấp chỉ ~28%.</item>
/// <item><b>Báo cáo thực tập</b> (§7.1, §7.4): style bị áp cho chú thích bảng, dòng bìa, khối chữ ký
/// — precision tầng OpenXML 55%, riêng chú thích mang Heading3 đã 13 mục ⇒ quyền CHỌN không tin
/// được. Và gần như mọi thứ đều là Heading2 ⇒ quyền GÁN CẤP cũng không, đúng cấp 40,7%.</item>
/// </list>
/// <para>
/// §7.1 và §9.7 đều dừng ở chỗ <i>"cần một tín hiệu đo được rằng style của tài liệu NÀY có đáng tin
/// không — chưa có"</i>. Đây là tín hiệu đó. Nó chỉ <b>hạ quyền</b>, không bao giờ xoá đoạn: đoạn
/// mang style trong tài liệu bị chấm điểm thấp vẫn đi tiếp xuống phần tính điểm và vẫn có thể là ứng
/// viên — xoá nó đi là lặp lại đúng lỗi §3.1.
/// </para>
/// <para>
/// Thêm một cơ sở đo được từ §10: trên <c>09-style-ap-sai</c>, CẢ HAI nhánh (có và không có luật R1)
/// đều KHÔNG cắt được ba đoạn nhiễu mang Heading3 — tức <i>"để mô hình lọc"</i> đã bị loại bằng số
/// đo cho chế độ hỏng này. Cần gạt còn lại là tín hiệu mức tài liệu.
/// </para>
/// </summary>
public sealed record StyleTrust(
    int StyledCount,
    int SuspectCount,
    int DistinctLevels,
    bool SkipsLevels,
    double Density,
    int NumberedSample = 0,
    int NumberedDisagree = 0)
{
    /// <summary>
    /// Tỉ lệ đoạn mang style built-in mà lại mang hình dạng của thứ KHÔNG phải đề mục.
    /// </summary>
    public double SuspectRatio => StyledCount == 0 ? 0 : (double)SuspectCount / StyledCount;

    /// <summary>
    /// Style có được áp đúng chỗ không. Hai vế: tỉ lệ đoạn "mang style nhưng trông không phải đề
    /// mục", và mật độ — nếu một phần tư tài liệu là "tiêu đề" thì từ đó không còn nghĩa gì.
    /// </summary>
    public bool SelectionTrusted =>
        StyledCount < MinimumStyledSample || (SuspectRatio <= MaxSuspectRatio && Density <= MaxDensity);

    /// <summary>
    /// Style có mang thông tin CẤP không. Một cấp duy nhất trên nhiều mục nghĩa là tác giả không
    /// phân biệt cấp bằng style; bỏ cấp giữa chừng nghĩa là con số trong tên style không phải độ sâu.
    /// </summary>
    /// <summary>
    /// Tỉ lệ đoạn vừa mang style Heading vừa có chuỗi đánh số gõ tay mà HAI NGUỒN NÓI KHÁC NHAU
    /// về độ sâu. Đây là vế duy nhất đối chiếu style với một nguồn ĐỘC LẬP; hai vế kia chỉ soi
    /// chính style (bao nhiêu cấp, có bỏ cấp không).
    /// </summary>
    public double NumberedDisagreeRatio =>
        NumberedSample == 0 ? 0 : (double)NumberedDisagree / NumberedSample;

    public bool LevelTrusted =>
        StyledCount < MinimumStyledSample
        || ((DistinctLevels > 1 && !SkipsLevels)
            && !(NumberedSample >= MinimumNumberedSample && NumberedDisagreeRatio > MaxNumberedDisagree));

    /// <summary>Dưới ngưỡng này thì không đủ mẫu để nói style có bám độ sâu đánh số hay không.</summary>
    public const int MinimumNumberedSample = 8;

    /// <summary>
    /// Trên mức này thì style không còn bám độ sâu của chuỗi đánh số. Chọn 1/3 vì lệch lác đác là
    /// chuyện thường (một mục đánh số sai, một mục cố ý nâng cấp), còn một phần ba trở lên thì đó
    /// là cách dùng style chứ không phải lỗi lẻ.
    /// ĐO ĐƯỢC trên khoá luận thật (§16.2): 40/68 đoạn có style lệch cấp so với đáp án, và trong
    /// nhóm vừa có style vừa có đánh số thì tỉ lệ bất đồng cao hơn hẳn ngưỡng này.
    /// </summary>
    public const double MaxNumberedDisagree = 1.0 / 3;

    /// <summary>
    /// Dưới ngưỡng này thì mẫu quá nhỏ để nói gì về tài liệu — giữ nguyên hành vi cũ. Chọn 8 vì
    /// bench có tài liệu chỉ 3–5 đoạn mang style, và ở quy mô đó "một cấp duy nhất" là chuyện thường
    /// chứ không phải dấu hiệu style hỏng.
    /// </summary>
    public const int MinimumStyledSample = 8;

    /// <summary>Trên báo cáo thật, riêng chú thích bảng mang Heading3 đã là 13/73 ≈ 18%.</summary>
    public const double MaxSuspectRatio = 0.15;

    public const double MaxDensity = 0.25;

    public string Describe() =>
        $"style built-in {StyledCount} đoạn ({Density:P0} tài liệu), {SuspectCount} đoạn trông không " +
        $"phải đề mục ({SuspectRatio:P0}), {DistinctLevels} cấp riêng biệt" +
        (SkipsLevels ? ", CÓ bỏ cấp giữa" : "") +
        (NumberedSample > 0
            ? $", {NumberedDisagree}/{NumberedSample} lệch so với độ sâu đánh số ({NumberedDisagreeRatio:P0})"
            : "") +
        $" ⇒ quyền chọn {(SelectionTrusted ? "GIỮ" : "HẠ")}, quyền gán cấp {(LevelTrusted ? "GIỮ" : "HẠ")}";
}
