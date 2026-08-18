using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>
/// Đầu trang chảy vào thân bài. Bản chuyển PDF→DOCX không có <c>headerN.xml</c>; dòng đầu trang bị
/// dán thẳng vào đầu đoạn, kèm số trang:
/// <code>
/// "44 CÔNG BÁO/Số 281 + 282/Ngày 28-02-2015 Đồng thời chuyển giá trị hao mòn, ghi"
///  ▲ số trang  ▲ đầu trang lặp lại           ▲ nội dung thật
/// </code>
/// Hậu quả đo được ở §106: bộ dựng đọc SỐ TRANG làm mốc đánh số và sinh ra 13/14 đề mục rác trên
/// <c>019_TT_200-2014</c>, đồng thời che mất mốc <c>Điều N.</c> thật nằm ngay sau đó.
/// <para>
/// <b>Vì sao dùng dạng chứ không dùng danh sách từ.</b> Che mọi chuỗi chữ số thành <c>#</c> rồi tìm
/// tiền tố dùng chung — đầu trang khác nhau đúng ở số trang và số hiệu, phần còn lại lặp nguyên
/// văn. Không có một từ tiếng Việt nào trong luật này, nên nó chạy y hệt trên tài liệu tiếng Anh
/// (<c>"Management’s Discussion and Analysis Section #"</c>).
/// </para>
/// <para>
/// <b>Vì sao ngưỡng đặt ở đây.</b> Đo trên 82 file corpus có ≥10 đoạn dài, cửa sổ 32 ký tự đã che
/// số: trung vị tỉ lệ lặp là <b>5,8%</b>, còn nhóm dính đầu trang nằm ở <b>30–96%</b> — không file
/// nào rơi vào khoảng 20–30%. Ngưỡng <see cref="MinimumShare"/> nằm giữa khe trống đó, không phải
/// số chọn cho vừa một file.
/// </para>
/// <para>
/// <b>Vì sao không cắt cứng 32 ký tự.</b> 32 chỉ dùng để GOM cụm; độ dài cắt thật là tiền tố chung
/// dài nhất của cả cụm, nên nó tự co giãn theo từng tài liệu và không xén vào nội dung thật.
/// </para>
/// <para>
/// §34.3 đã ghi cái bẫy của luật "lặp nhiều lần" thuần: nó loại 2 dương tính giả nhưng làm mất 4 đề
/// mục THẬT, vì cấu trúc song song lặp đề mục có chủ ý. Luật này khác ở chỗ chỉ xét đoạn DÀI
/// (&gt; <see cref="MinimumBodyLength"/> ký tự) — đề mục song song là đoạn ngắn nên không lọt vào
/// mẫu — và chỉ BÓC tiền tố chứ không bao giờ bỏ đoạn.
/// </para>
/// </summary>
internal static class RunningHeaderAudit
{
    /// <summary>Chỉ xét đoạn dài hơn ngần này; đề mục thật ngắn hơn nhiều nên không lọt vào mẫu.</summary>
    internal const int MinimumBodyLength = 50;

    /// <summary>Dưới ngần này đoạn dài thì mẫu quá nhỏ để nói "lặp".</summary>
    internal const int MinimumSample = 10;

    /// <summary>Số đoạn tối thiểu cùng một tiền tố.</summary>
    internal const int MinimumMembers = 5;

    /// <summary>
    /// Cửa sổ gom cụm THÔ, tính trên văn bản ĐÃ CHE. Cố tình ngắn: cửa sổ đo trên văn bản gốc sẽ
    /// TRƯỢT khi số trang đổi từ một sang hai chữ số — đo được, <c>"10 CÔNG BÁO/…"</c> rơi khác cụm
    /// với <c>"0 CÔNG BÁO/…"</c> và cả nhóm bị bỏ sót. Gom thô rồi để
    /// <see cref="MinimumCommonPrefix"/> làm cổng thật; cụm gom nhầm có tiền tố chung ngắn nên bị
    /// loại ở đó.
    /// </summary>
    internal const int ClusterWindow = 12;

    /// <summary>Tỉ lệ đoạn dài cùng tiền tố. Nằm giữa khe trống 20–30% đo được trên corpus.</summary>
    internal const double MinimumShare = 0.30;

    /// <summary>
    /// Tiền tố chung tối thiểu (tính trên bản đã che) để coi là đầu trang. Ngắn hơn ngần này thì
    /// cái dùng chung nhiều khả năng là một mốc đánh số thật, không phải dòng đầu trang.
    /// </summary>
    internal const int MinimumCommonPrefix = 20;

    /// <summary>
    /// Số lượt bóc tối đa. Một tài liệu có thể mang NHIỀU biến thể đầu trang cùng lúc — đo trên
    /// <c>019_TT_200-2014</c>: đoạn mở bằng <c>"CÔNG BÁO/…"</c> và đoạn mở bằng <c>"2 CÔNG BÁO/…"</c>
    /// (có số trang dẫn đầu) rơi vào hai cụm khác nhau, và bản chỉ bóc cụm lớn nhất bỏ sót nguyên
    /// nhóm thứ hai. Chặn trên để không lặp vô hạn nếu một lượt bóc lại sinh ra cụm mới.
    /// </summary>
    internal const int MaximumPasses = 3;

    /// <summary>Bóc đầu trang lặp khỏi đầu đoạn. Trả về tổng số đoạn đã bóc qua mọi lượt.</summary>
    internal static int Strip(List<SlimParagraph> paragraphs)
    {
        var total = 0;
        for (var pass = 0; pass < MaximumPasses; pass++)
        {
            var n = StripOnce(paragraphs);
            if (n == 0) break;
            total += n;
        }

        return total;
    }

    private static int StripOnce(List<SlimParagraph> paragraphs)
    {
        var body = paragraphs.Where(p => (p.Text?.Length ?? 0) > MinimumBodyLength).ToList();
        if (body.Count < MinimumSample) return 0;

        var masked = body.ToDictionary(p => p, p => Mask(p.Text));

        var cluster = masked
            .GroupBy(kv => kv.Value[..Math.Min(ClusterWindow, kv.Value.Length)])
            .OrderByDescending(g => g.Count())
            .First()
            .ToList();

        if (cluster.Count < MinimumMembers) return 0;
        if ((double)cluster.Count / body.Count < MinimumShare) return 0;

        var common = LungTrachMoc(LongestCommonPrefix(cluster.Select(kv => kv.Value)), cluster[0].Value);
        if (common < MinimumCommonPrefix) return 0;

        var stripped = 0;
        foreach (var (paragraph, _) in cluster)
        {
            var cut = MapBack(paragraph.Text, common);
            // Không bao giờ bóc hết đoạn: còn lại phải đủ dài để còn là nội dung.
            if (cut <= 0 || paragraph.Text.Length - cut <= MinimumBodyLength / 2) continue;
            paragraph.Text = paragraph.Text[cut..].TrimStart(' ', '\t', '.', '-', '–');
            stripped++;
        }

        return stripped;
    }

    /// <summary>
    /// Đuôi tiền tố chung có thể là MỐC THẬT chứ không phải đầu trang: khi mọi trang đều mở bằng
    /// <c>Section 1.</c>, <c>Section 2.</c>… thì phần dùng chung kéo dài qua cả mốc, và bóc theo nó
    /// sẽ xoá đúng cái cần giữ. Đo được trên <c>092_RFC9111</c>.
    /// <para>
    /// Phân biệt bằng DẤU NGẮT: mốc là <i>chữ + số + dấu ngắt</i> (<c>Điều #.</c>, <c>Section #)</c>),
    /// còn ngày tháng và số trang của đầu trang không có dấu ngắt đóng (<c>Ngày #-#-#</c>). Không có
    /// từ khoá nào trong luật này.
    /// </para>
    /// </summary>
    private static readonly Regex MocODuoi =
        new(@"\p{L}+\s*#\s*[.)]\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static int LungTrachMoc(int common, string masked)
    {
        if (common <= 0 || common > masked.Length) return common;

        var m = MocODuoi.Match(masked[..common]);
        return m.Success ? common - m.Length : common;
    }

    /// <summary>Mọi chuỗi chữ số thành một <c>#</c> — số trang và số hiệu là phần DUY NHẤT đổi.</summary>
    private static string Mask(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inDigits = false;
        foreach (var c in text)
        {
            if (char.IsDigit(c))
            {
                if (!inDigits) sb.Append('#');
                inDigits = true;
                continue;
            }

            inDigits = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static int LongestCommonPrefix(IEnumerable<string> values)
    {
        string? first = null;
        var length = int.MaxValue;
        foreach (var v in values)
        {
            if (first is null) { first = v; length = v.Length; continue; }

            var i = 0;
            var max = Math.Min(length, v.Length);
            while (i < max && first[i] == v[i]) i++;
            length = i;
            if (length == 0) break;
        }

        return first is null ? 0 : length;
    }

    /// <summary>Đổi độ dài trên bản ĐÃ CHE thành vị trí cắt trên bản gốc.</summary>
    private static int MapBack(string text, int maskedLength)
    {
        var seen = 0;
        var i = 0;
        while (i < text.Length && seen < maskedLength)
        {
            if (char.IsDigit(text[i]))
            {
                while (i < text.Length && char.IsDigit(text[i])) i++;
                seen++;
                continue;
            }

            i++;
            seen++;
        }

        return i;
    }
}
