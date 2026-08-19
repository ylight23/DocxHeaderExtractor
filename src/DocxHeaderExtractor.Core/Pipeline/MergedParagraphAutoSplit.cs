using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Quyết định BẬT tách đoạn gộp theo TỪNG TÀI LIỆU, thay vì bật đại trà.
/// <para>
/// <b>Vì sao cần.</b> Bản chuyển PDF→DOCX của văn bản pháp quy gộp cả trang thành một
/// <c>w:p</c>: <c>010_Luat_An_ninh_mang</c> có 34 đoạn, <b>27 đoạn chứa <c>Điều N.</c></b>, nhưng
/// tầng ứng viên chấm NGUYÊN đoạn nên chỉ ra 2 ứng viên và outline cuối cùng có 2 mục trên đáp án
/// 50. Bật <c>--split-merged</c> cho đúng file đó: <b>2 → 50 mục, khớp trọn đáp án</b>.
/// </para>
/// <para>
/// <b>Vì sao không bật đại trà.</b> §113 đã đo: bật cho mọi file thì nổ rác trên 54/89 file. Và đo
/// lại ở đây trên bộ đáp án: bật đại trà làm <c>ev-human</c> Nav 28,2% → 98,8% nhưng "tuyệt đối"
/// tụt 1/5 → 0/5, vì <c>056_OpenStax</c> vốn KHÔNG gộp cũng bị tách và mất quy chiếu chỉ số.
/// </para>
/// <para>
/// <b>Vì sao không dùng độ dài đoạn.</b> Đã đo và BÁC: <c>056</c> (không được tách) dài
/// <b>2.208</b> ký tự/đoạn, còn <c>010</c> (cần tách) chỉ <b>1.865</b> — file cần tách lại ngắn
/// hơn. Trung vị corpus 2.049, tức gần như mọi file đều "gộp" theo thước đo đó.
/// </para>
/// <para>
/// <b>Dấu hiệu dùng được: tài liệu tự tố cáo mình bỏ sót.</b> So số ỨNG VIÊN tầng OpenXML tìm được
/// với số MỐC có nhãn nằm trong văn bản. Tài liệu bình thường thì hai số xấp xỉ nhau; tài liệu bị
/// gộp thì mốc nằm đầy trong thân đoạn mà ứng viên gần như bằng không.
/// </para>
/// <code>
///   010_Luat_An_ninh    2 ứng viên /  73 mốc =  3%   → tách
///   025_ND_47           1 ứng viên /  81 mốc =  1%   → tách
///   054_IBRD           22 ứng viên / 137 mốc = 16%   → không
///   056_OpenStax       60 ứng viên / 158 mốc = 38%   → không
///   036_WB_Plant      370 mục      / 488 mốc = 76%   → không
/// </code>
/// Khe trống giữa 3% và 16% là chỗ đặt <see cref="MinimumCandidateShare"/>.
/// </summary>
public static class MergedParagraphAutoSplit
{
    /// <summary>Dưới ngần này đoạn thì không đủ mẫu để nói tài liệu bị gộp.</summary>
    public const int MinimumMarkers = 10;

    /// <summary>
    /// Tỉ lệ ứng viên trên mốc, dưới ngưỡng này thì coi là tầng ứng viên đã bỏ sót cấu trúc nằm
    /// trong thân đoạn. Đặt giữa khe trống đo được 3%–16%.
    /// </summary>
    public const double MinimumCandidateShare = 0.10;

    /// <summary>
    /// Mốc CÓ NHÃN: một từ rồi tới số rồi tới dấu ngắt — <c>Điều 6.</c>, <c>Article 4.</c>,
    /// <c>Section 3)</c>. Là DẠNG, không phải danh sách từ, nên chạy y hệt trên tài liệu tiếng Anh.
    /// </summary>
    private static readonly Regex Marker = new(
        @"(?<!\w)[^\W\d_][^\W\d_]{1,14}\s+\d{1,3}\s*[.)]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Số mốc có nhãn PHÂN BIỆT nằm trong thân các đoạn.</summary>
    public static int CountMarkers(SlimDocument document) =>
        document.Paragraphs.Sum(p => p.Text is not { Length: > 0 } text
            ? 0
            : Marker.Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct().Count());

    /// <summary>
    /// Tầng ứng viên có đang bỏ sót cấu trúc nằm trong thân đoạn không? Trả về số mốc đếm được qua
    /// <paramref name="markers"/> để nơi gọi ghi log được lý do.
    /// </summary>
    public static bool ShouldSplit(SlimDocument document, int candidateCount, out int markers)
    {
        markers = CountMarkers(document);
        if (markers < MinimumMarkers) return false;

        return (double)candidateCount / markers < MinimumCandidateShare;
    }
}
