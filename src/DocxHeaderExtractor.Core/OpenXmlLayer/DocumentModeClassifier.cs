using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>
/// Chế độ tài liệu — tín hiệu nào thực sự mang cấu trúc trong CHÍNH tài liệu này.
/// <para>
/// Tầng 1 của <c>spec-heading-outline-v2.md</c>, spec gọi là "tầng quan trọng nhất": thiếu nó thì
/// mọi luật phía sau đều sai trên một nửa số tài liệu.
/// </para>
/// </summary>
public enum DocumentMode
{
    /// <summary>Chưa đo, hoặc tài liệu rỗng.</summary>
    Unknown,

    /// <summary><c>w:outlineLvl</c> khai thẳng cấp — thẩm quyền cao nhất trong OOXML.</summary>
    OutlineLevelDriven,

    /// <summary>Mục lục do Word sinh, khớp được phần lớn về thân bài.</summary>
    TocAnchored,

    /// <summary>Hệ đánh số hành chính Việt Nam gõ tay: <c>I.</c> <c>1.</c> <c>1.1.</c> <c>a)</c>.</summary>
    VietnameseAdministrative,

    /// <summary>Số gõ tay nhiều cấp trong text (<c>1.2.3</c>), nhất quán với style.</summary>
    TypedNumbering,

    /// <summary>Danh sách đa cấp của Word mang cấu trúc.</summary>
    NumberingDriven,

    /// <summary>Style tên tự đặt (không thuộc họ <c>Heading*</c>) nhưng dùng nhất quán.</summary>
    CustomStyle,

    /// <summary>Không còn tín hiệu cấu trúc, chỉ còn định dạng lệch khỏi thân bài.</summary>
    FormatDriven,

    /// <summary>Không còn tín hiệu nào — kỳ vọng thấp nhất, nên tăng tỉ lệ abstain.</summary>
    SemanticOnly,
}

/// <summary>Chỉ số đo được để phân loại, giữ lại nguyên vẹn để báo cáo và tranh luận sau.</summary>
public sealed record DocumentModeReport(
    DocumentMode Mode,
    int Paragraphs,
    int StyledHeadings,
    double OutlineLevelRatio,
    double VietnameseAdminRatio,
    double TypedNumberRatio,
    double NumberingRatio,
    bool FormatDiffers)
{
    public string Describe() =>
        $"Chế độ tài liệu: {Mode} " +
        $"(outlineLvl {OutlineLevelRatio:P0}, ký hiệu hành chính {VietnameseAdminRatio:P0}, " +
        $"số gõ tay {TypedNumberRatio:P0}, numPr {NumberingRatio:P0}, " +
        $"style Heading {StyledHeadings} đoạn, định dạng {(FormatDiffers ? "có lệch" : "không lệch")} thân bài)";
}

/// <summary>
/// Cây quyết định §4.2 của spec, cài dưới dạng CHẨN ĐOÁN: đo và báo cáo, KHÔNG đổi hành vi.
/// <para>
/// Lý do làm chẩn đoán trước. Corpus 95 tài liệu kèm theo spec có <b>83/95 file là PDF được trích
/// text rồi bọc vào vỏ DOCX</b> — 0 style Heading, 0 <c>numPr</c>, 0 <c>outlineLvl</c>. Còn pipeline
/// này xây và đo trên tài liệu Word gốc. Hai lớp đầu vào khác nhau, nên bước đầu là ĐO xem cây
/// quyết định của spec nói gì trên cả hai tập, rồi mới bàn chuyện đổi luật theo chế độ.
/// </para>
/// </summary>
public static class DocumentModeClassifier
{
    /// <summary>
    /// Bốn lớp ký hiệu hành chính Việt Nam (spec §4.4), kiểm từ cấp SÂU đến cấp NÔNG — nếu không,
    /// <c>3.1.</c> bị luật <c>^\d+\.</c> bắt trước và gán nhầm cấp.
    /// <para>
    /// Ở đây chỉ dùng để NHẬN DẠNG chế độ, không dùng để gán cấp. Việc gán cấp đã có luật riêng và
    /// đã đo: <c>StructuralHierarchyResolver.LocalListDepth</c> neo theo cha gần nhất (§31, đúng cấp
    /// 81,1% → 91,5%). Spec §4.4 kết luận y hệt bằng corpus khác: <i>"cấp phải suy theo ngữ cảnh cha
    /// gần nhất, không gán cứng theo loại ký hiệu"</i> — hai tập dữ liệu độc lập, cùng một luật.
    /// </para>
    /// </summary>
    private static readonly Regex[] AdministrativeMarkers =
    [
        new(@"^\s*\d{1,2}\.\d{1,2}\.?\s", RegexOptions.Compiled),   // 3.1.
        new(@"^\s*[a-zđ][\.\)]\s", RegexOptions.Compiled),          // a)  b.
        new(@"^\s*\d{1,2}\.\s*\D", RegexOptions.Compiled),          // 1.  2.
        new(@"^\s*[IVXLC]+\.\s*\S", RegexOptions.Compiled),         // I.  II.
    ];

    /// <summary>Số gõ tay nhiều cấp: <c>1.2</c>, <c>2.3.4</c>.</summary>
    private static readonly Regex TypedNumber = new(@"^\s*\d+(\.\d+)+", RegexOptions.Compiled);

    /// <summary>
    /// Ngưỡng tỉ lệ đoạn mang ký hiệu hành chính. Spec §4.2 ghi <b>0,5 trên đoạn IN ĐẬM</b> ngoài
    /// bảng; ở đây là <b>0,15 trên MỌI đoạn</b> ngoài bảng. Đổi vì đo được, không vì tiện.
    /// <para>
    /// Trên chính corpus 95 tài liệu đi kèm spec: 18 tài liệu được bản Python gán
    /// <c>vn-administrative</c> có <b>1.146/3.394 đoạn (34%) khớp ký hiệu, nhưng chỉ 4 đoạn in đậm
    /// (0%)</b>. Định nghĩa của spec hiệu chỉnh trên 3 tài liệu Word gốc ở §1.2, nơi đề mục có in
    /// đậm; còn 83/95 file trong corpus là PDF trích text bọc vỏ DOCX, nơi in đậm không sống sót.
    /// Mẫu số rỗng ⇒ luật không bao giờ kích hoạt.
    /// </para>
    /// <para>
    /// Bỏ điều kiện in đậm thì bốn nhóm tách sạch theo trung vị: <c>vn-legal</c> 60,4%,
    /// <c>vn-administrative</c> 24,3%, <c>format-driven</c> (Word gốc) 3,8%,
    /// <c>UNCLASSIFIED</c>/<c>insufficient_text</c> 0%. Ngưỡng 0,15 bắt 14/18 tài liệu hành chính.
    /// </para>
    /// </summary>
    public const double AdministrativeThreshold = 0.15;
    public const double TypedNumberThreshold = 0.6;
    public const double NumberingThreshold = 0.20;

    /// <summary>Đoạn dài hơn mức này được coi là thân bài khi tìm baseline định dạng (spec §4.1).</summary>
    private const int BodyTextMinLength = 200;

    public static DocumentModeReport Measure(IReadOnlyList<SlimParagraph> paragraphs)
    {
        var body = paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToList();
        if (body.Count == 0)
            return new DocumentModeReport(DocumentMode.Unknown, 0, 0, 0, 0, 0, 0, false);

        var styled = body.Where(p => p.HasBuiltInHeadingStyle).ToList();
        var outlineRatio = Ratio(body.Count(p => p.OutlineLevel is not null), body.Count);

        // Mẫu số là MỌI đoạn ngoài bảng, KHÔNG lọc theo in đậm — xem AdministrativeThreshold.
        var outsideTables = body.Where(p => p.TableDepth == 0).ToList();
        var adminRatio = Ratio(outsideTables.Count(p => IsAdministrativeMarker(p.Text)), outsideTables.Count);

        // Hai tỉ lệ dưới đo TRÊN tập đoạn mang style Heading, theo đúng spec.
        var typedRatio = Ratio(styled.Count(p => TypedNumber.IsMatch(p.Text)), styled.Count);
        var numberingRatio = Ratio(styled.Count(p => p.NumberingId is not null), styled.Count);

        var mode = Decide(styled.Count, outlineRatio, adminRatio, typedRatio, numberingRatio,
            FormatDiffersFromBody(body), out var formatDiffers);

        return new DocumentModeReport(mode, body.Count, styled.Count,
            outlineRatio, adminRatio, typedRatio, numberingRatio, formatDiffers);
    }

    private static DocumentMode Decide(
        int styledCount, double outlineRatio, double adminRatio, double typedRatio,
        double numberingRatio, bool formatDiffers, out bool format)
    {
        format = formatDiffers;
        if (outlineRatio > 0) return DocumentMode.OutlineLevelDriven;
        if (adminRatio >= AdministrativeThreshold) return DocumentMode.VietnameseAdministrative;
        if (styledCount > 0 && typedRatio >= TypedNumberThreshold) return DocumentMode.TypedNumbering;
        if (styledCount > 0 && numberingRatio >= NumberingThreshold) return DocumentMode.NumberingDriven;
        if (styledCount > 0) return DocumentMode.CustomStyle;
        return formatDiffers ? DocumentMode.FormatDriven : DocumentMode.SemanticOnly;
    }

    /// <summary>
    /// Có đoạn nào lệch khỏi baseline thân bài không. Baseline = mode của (đậm, cỡ chữ) trên các đoạn
    /// dài hơn <see cref="BodyTextMinLength"/> — spec §4.1.
    /// <para>
    /// Spec §4.3 cảnh báo đúng chỗ: KHÔNG được giả định "heading luôn to hơn hoặc đậm hơn". Tài liệu
    /// A có heading cùng cỡ chữ thân bài, tài liệu B có H4/H5 NHỎ HƠN thân bài. Nên luật ở đây chỉ
    /// hỏi "có LỆCH không", không hỏi "có to hơn không".
    /// </para>
    /// </summary>
    private static bool FormatDiffersFromBody(IReadOnlyList<SlimParagraph> body)
    {
        var longOnes = body.Where(p => p.Text.Length > BodyTextMinLength).ToList();
        if (longOnes.Count == 0) return false;

        var baseline = longOnes
            .GroupBy(p => (p.Bold, Size: p.FontSizePt))
            .OrderByDescending(g => g.Count())
            .First().Key;

        return body.Any(p => p.Text.Length <= BodyTextMinLength &&
                             (p.Bold != baseline.Bold ||
                              (p.FontSizePt is { } s && baseline.Size is { } b && Math.Abs(s - b) >= 1)));
    }

    public static bool IsAdministrativeMarker(string text) =>
        !string.IsNullOrWhiteSpace(text) && AdministrativeMarkers.Any(rx => rx.IsMatch(text));

    private static double Ratio(int part, int whole) => whole == 0 ? 0 : (double)part / whole;
}
