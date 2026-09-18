using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

/// <summary>
/// Phân loại đoạn trước LLM. SEMANTIC_RECALL_CEILING = SOURCE_OCCURRENCE_UNIVERSE, không phải
/// một tập ứng viên do điểm số quyết định: mọi đoạn còn lại sau các loại trừ cấu trúc dứt khoát
/// (rỗng, hỏng, bảng dữ liệu, content control, dòng mục lục/chú thích) đều trở thành
/// HeadingCandidate và được đưa cho LLM tự quyết định isHeading. Điểm số chi tiết theo
/// numbering/prefix/table-depth/formatting đã bị bỏ - nó từng là một cổng chặn ẩn (loại đoạn
/// trước khi mô hình kịp thấy), điều mà kiến trúc hiện tại coi là quyết định NGHĨA
/// (candidateHint chỉ là gợi ý attention, không phải tập heading được phép) và vì vậy thuộc về
/// LLM, không phải harness.
/// </summary>
public static class HeadingHeuristics
{
    /// <summary>
    /// Style do người dùng TỰ ĐẶT TÊN theo ngôn ngữ của họ. Đây mới thực sự là mapping cứng:
    /// nó chỉ đúng với vài thứ tiếng và phải bổ sung tay khi gặp tiếng khác.
    /// Chỉ dùng khi <see cref="ExtractionOptions.UseLexicalRules"/> bật, và chỉ như phương án
    /// cuối cùng sau khi w:outlineLvl đã không cho kết luận.
    /// </summary>
    private static readonly string[] LocalizedHeadingTokens =
    [
        "tiêu đề", "tieu de", "đề mục", "de muc", "chương", "chuong",
        "überschrift", "titre", "заголовок", "título", "intestazione",
    ];

    private static readonly Regex StyleLevelRx = new(@"(\d+)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Nhãn đặt tên cho một đối tượng, dạng "TỪ + SỐ NHIỀU PHẦN": "Bảng 1.2:", "Hình 2.4", "Table 3.1".
    /// <para>
    /// CỐ Ý đòi số nhiều phần (<c>1.2</c>, không phải <c>1</c>). Đó là thứ tách nó khỏi đề mục thật
    /// dạng "Chương 1.", "Điều 5.", "Phụ lục 1:" — cũng là "từ + số" nhưng số một phần. Không có
    /// ràng buộc này thì luật ăn nhầm đúng họ đề mục phổ biến nhất của văn bản hành chính.
    /// </para>
    /// <para>
    /// Không liệt kê từ nào: "Bảng"/"Hình"/"Table"/"Figure" đều chỉ là "một từ 2–12 chữ cái".
    /// Bản thân mẫu này KHÔNG đủ để kết luận — nó phải đi cùng bằng chứng vị trí
    /// source policy paragraph's table-boundary fact.
    /// </para>
    /// </summary>
    private static readonly Regex ObjectLabelPrefixRx = new(
        @"^\s*\p{L}{2,12}\s+\d{1,3}(?:[.\-–]\d{1,3})+\s*[:.\-–)]?\s+\S",
        RegexOptions.Compiled);

    /// <summary>Kết thúc bằng dấu câu của câu văn thường ⇒ ít khả năng là tiêu đề.</summary>
    private static readonly Regex SentenceEndRx = new(@"[\.;,:]\s*$", RegexOptions.Compiled);

    /// <summary>Gạch đầu dòng liệt kê gõ tay: "- Fanpage…", "• Kênh…", "+ Mục…".</summary>
    private static readonly Regex BulletPrefixRx = new(@"^\s*[-–—•*▪+o]\s+\S", RegexOptions.Compiled);

    /// <summary>
    /// Chú thích hình/bảng/biểu đồ: "Hình ảnh 2.4. …", "Bảng 1.2 …", "Figure 3: …".
    /// Bắt buộc có chữ số ngay sau từ khoá nên "Bảng phân công nhiệm vụ" không bị dính.
    /// </summary>
    /// <remarks>internal (không private): <see cref="Eval.TocAnswerKeyGenerator"/> dùng lại để loại
    /// mục "Danh mục hình ảnh"/"Danh mục bảng biểu" khỏi mục lục coi là đề mục — Word đánh dấu
    /// chúng bằng đúng cơ chế TOC field/hyperlink _Toc như mục lục chương, dù chúng là chú thích.</remarks>
    internal static readonly Regex CaptionRx = new(
        @"^\s*(hình(\s*ảnh|\s*vẽ)?|ảnh|bảng|biểu\s*đồ|sơ\s*đồ|đồ\s*thị|phụ\s*biểu|" +
        @"figure|fig|table|chart|picture|image|diagram)\s*\d+([.\-–]\d+)*\s*[.:\-–)]?\s+\S",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Nhãn đặt tên đối tượng đứng ngay trước chính bảng nó đặt tên, số gõ tay chứ không do Word
    /// sinh. Công khai để <see cref="StyleTrustAudit"/> dùng lại đúng luật này thay vì dựng bộ thứ
    /// hai — hai bộ luật cho cùng một khái niệm thì sớm muộn đi lệch nhau.
    /// </summary>
    public static bool IsObjectCaption(IPolicyParagraph p) =>
        p.PrecedesTable && p.NumberingId is null && ObjectLabelPrefixRx.IsMatch(p.Text);

    /// <summary>Mở đầu bằng ký hiệu gạch đầu dòng — quy ước ký hiệu, không gắn với ngôn ngữ nào.</summary>
    public static bool LooksLikeListItem(string text) => BulletPrefixRx.IsMatch(text);

    /// <summary>Kết thúc bằng dấu câu của câu văn thường.</summary>
    public static bool EndsLikeSentence(string text) => SentenceEndRx.IsMatch(text);

    /// <summary>
    /// Gán role và (khi có bằng chứng style chắc chắn) guessed level cho policy paragraph. Không
    /// còn chấm điểm/ngưỡng: mọi đoạn còn sống sót qua các loại trừ cấu trúc dứt khoát bên dưới
    /// đều thành HeadingCandidate.
    /// </summary>
    /// <param name="trustStyleSelection">
    /// Cho phép style built-in thoát sớm với role StyledHeading. Đặt false khi
    /// <see cref="StyleTrustAudit"/> chấm rằng style của TÀI LIỆU NÀY bị áp bừa: khi đó đoạn mang
    /// style vẫn tiếp tục xuống thành HeadingCandidate như mọi đoạn khác — nó không mất quyền được
    /// LLM xét, chỉ mất nhãn "chắc chắn theo style".
    /// </param>
    public static void Classify(IPolicyParagraph p, ExtractionOptions options, bool trustStyleSelection = true)
    {
        if (string.IsNullOrWhiteSpace(p.Text))
        {
            p.Role = ParagraphRole.Empty;
            return;
        }

        // Khối w:sdt của Word: mục lục tự động, form, vùng nội dung có cấu trúc. Xem
        // ExtractionOptions.SkipContentControls — 21/129 ứng viên nằm ở đây và không mục nào là
        // đề mục thật. Chỉ HẠ vai trò, không xoá đoạn: nó vẫn là ngữ cảnh cho các đoạn khác.
        // X1 cua spec §5.1: doan hong loai TRUOC moi luat thu nhan.
        if (options.SkipCorruptParagraphs && p.Corrupt)
        {
            p.Role = ParagraphRole.Normal;
            p.Score = 0;
            return;
        }

        // Bang DU LIEU khong chua de muc — spec §5.5. Bang LAYOUT va CONTENT thi van xet binh thuong.
        if (options.SkipDataTables && p.TableRole == TableRole.Data)
        {
            p.Role = ParagraphRole.Normal;
            p.Score = 0;
            return;
        }

        if (options.SkipContentControls && p.InContentControl)
        {
            p.Role = ParagraphRole.Normal;
            p.Score = 0;
            return;
        }

        // Loại thẳng hai họ nhiễu lớn nhất trong luận văn/báo cáo: dòng mục lục (tín hiệu cấu
        // trúc: hyperlink tới neo _Toc) và chú thích hình/bảng. Đây vẫn là loại trừ cấu trúc dứt
        // khoát (không phải chấm điểm mềm): một dòng TOC hay một chú thích hình/bảng không phải
        // là một "ứng viên yếu" cần LLM cân nhắc, nó không phải một occurrence heading khác trong
        // tài liệu — nó LÀ điều nó là, xác định bằng cấu trúc (anchor _Toc) hoặc hình dạng
        // (nhãn "từ + số nhiều phần" đứng trước bảng).
        var objectCaption = p.PrecedesTable && p.NumberingId is null && ObjectLabelPrefixRx.IsMatch(p.Text);

        if (p.InTableOfContents || objectCaption ||
            (options.UseLexicalRules && CaptionRx.IsMatch(p.Text)))
        {
            p.Role = ParagraphRole.Normal;
            p.Score = 0;
            return;
        }

        var looksLikeListItem = BulletPrefixRx.IsMatch(p.Text);

        // Danh sách đa cấp tự khai cấp này gắn với style Heading N. Đây là tuyên bố cấu trúc
        // mạnh nhất trong OOXML: người soạn cấu hình MỘT LẦN cho cả tài liệu qua hộp thoại
        // multilevel list, nên nó không nhiễm lỗi copy định dạng như w:outlineLvl.
        if (trustStyleSelection && !looksLikeListItem && p.NumberingStyleLevel is { } listHeadingLevel)
        {
            p.Role = ParagraphRole.StyledHeading;
            p.HasBuiltInHeadingStyle = true;
            p.GuessedLevel = listHeadingLevel;
            p.Score = 1.0;
            return;
        }

        // Style built-in OOXML — evidence cấp mạnh nhất về CẤP (level), giữ lại như một gợi ý cho
        // LLM; role vẫn StyledHeading để hạ nguồn biết đây là style-declared, không phải suy đoán.
        var builtInLevel = looksLikeListItem ? null : BuiltInLevel(p);
        if (builtInLevel is not null && trustStyleSelection)
        {
            p.Role = ParagraphRole.StyledHeading;
            p.HasBuiltInHeadingStyle = true;
            p.GuessedLevel = builtInLevel;
            p.Score = 1.0;
            return;
        }

        // SEMANTIC_RECALL_CEILING = SOURCE_OCCURRENCE_UNIVERSE, not HEURISTIC_CANDIDATE_SET.
        // Mọi đoạn sống sót tới đây (không rỗng, không hỏng, không phải bảng dữ liệu/content
        // control bị chặn, không phải dòng TOC/chú thích, không có style/numbering built-in đã tự
        // xử lý ở trên) trở thành HeadingCandidate vô điều kiện — không chấm điểm, không ngưỡng.
        // candidateHints là gợi ý attention, không phải tập heading được phép; mô hình quyết định
        // nghĩa (isHeading), harness quyết định toạ độ.
        p.Role = ParagraphRole.HeadingCandidate;
    }

    /// <summary>
    /// Suy ra cấp heading từ style. Thứ tự ưu tiên đi từ tín hiệu độc lập ngôn ngữ xuống dưới:
    /// style dựng sẵn OOXML → w:outlineLvl → (tuỳ chọn) tên style bản địa hoá.
    /// Trả null nếu không có bằng chứng nào.
    /// </summary>
    public static int? LevelFromStyle(IPolicyParagraph p, ExtractionOptions options)
    {
        return BuiltInLevel(p) ?? (p.OutlineLevel is >= 0 and <= 8 ? p.OutlineLevel.Value + 1 : (int?)null)
               ?? (options.UseLexicalRules ? LocalizedStyleLevel(p) : (int?)null);
    }

    /// <summary>Chỉ nhận style chuẩn OOXML, không nhận outline level hay tên người dùng tự đặt.</summary>
    public static int? BuiltInLevel(IPolicyParagraph p)
    {
        foreach (var candidate in new[] { p.StyleName, p.StyleId })
        {
            if (BuiltInLevelFromStyleId(candidate) is { } level) return level;
        }
        return null;
    }

    /// <summary>
    /// Cùng luật với <see cref="BuiltInLevel"/> nhưng nhận thẳng một styleId — dùng cho
    /// <c>w:lvl/w:pStyle</c> của danh sách đa cấp, nơi chỉ có tên style chứ không có paragraph.
    /// </summary>
    public static int? BuiltInLevelFromStyleId(string? styleId)
        => BuiltInHeadingStyleIdentity.LevelFromStyleIdentity(styleId);

    private static int? LocalizedStyleLevel(IPolicyParagraph p)
    {
        var name = p.StyleName ?? p.StyleId;
        if (string.IsNullOrEmpty(name)) return null;

        var lower = name.ToLowerInvariant();
        if (!LocalizedHeadingTokens.Any(t => lower.Contains(t, StringComparison.Ordinal))) return null;

        var digit = StyleLevelRx.Match(name);
        return digit.Success && int.TryParse(digit.Groups[1].Value, out var lvl) && lvl is >= 1 and <= 9
            ? lvl
            : 1;
    }
}
