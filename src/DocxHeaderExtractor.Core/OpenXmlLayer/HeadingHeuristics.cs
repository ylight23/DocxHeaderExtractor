using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>
/// Luật nhận diện tiêu đề chạy trước LLM. Mục tiêu: giữ lại đúng tập ứng viên nhỏ
/// (recall cao, precision vừa phải) để LLM chỉ phải lọc và gán cấp.
/// <para>
/// Các mẫu tiền tố đánh số ở đây CỐ Ý rộng hơn <see cref="Pipeline.NumberingAudit"/>: bỏ sót một
/// ứng viên ở tầng này là mất hẳn vì mô hình không bao giờ nhìn thấy nó, còn nhận rộng thì mô hình
/// và hậu kiểm vẫn còn cơ hội bác. Danh sách đầy đủ các chỗ lệch và lý do nằm ở đầu
/// <c>NumberingAudit</c>; sửa một bên thì đọc bên kia trước.
/// </para>
/// </summary>
public static class HeadingHeuristics
{
    /// <summary>
    /// Tên style dựng sẵn của OOXML. ĐÂY KHÔNG PHẢI từ vựng tiếng Anh mà là định danh do
    /// chính đặc tả ECMA-376 quy định: dù Word chạy giao diện tiếng gì, w:styleId và w:name
    /// của style dựng sẵn vẫn là "Heading1"/"heading 1", "Title", "Subtitle".
    /// Vì vậy luật này không phụ thuộc ngôn ngữ tài liệu và luôn được bật.
    /// </summary>
    private static readonly Regex BuiltInHeadingRx = new(
        @"^(heading\s*([1-9])|title|subtitle|toc\s*heading)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
    /// Dạng đánh số có từ nhãn đứng trước: "PHẦN I. …", "Chương 2. …", "Điều 5. …", "Section 3: …".
    /// <para>
    /// Thay cho danh sách từ khoá cứng (chương|phần|mục|điều|chapter|section|…) vốn chỉ đúng với
    /// tiếng Việt và tiếng Anh, và chỉ đúng với những từ ai đó nghĩ ra sẵn. Ở đây KHÔNG quan tâm
    /// từ nhãn là gì — chỉ cần một từ viết hoa đứng trước một số (Ả Rập hoặc La Mã), có dấu ngắt,
    /// rồi tới phần tên mục. Đó là một document number format, nhận diện bằng hình dạng chứ không
    /// bằng vốn từ, nên áp được cho mọi ngôn ngữ.
    /// </para>
    /// <para>
    /// Hai dạng được nhận: có dấu ngắt rồi tới phần tên ("PHẦN I. CƠ SỞ…"), hoặc không dấu ngắt
    /// nhưng phần tên bắt đầu bằng chữ HOA ("Chương 1 Tổng quan"). Ràng buộc chữ hoa ở nhánh thứ
    /// hai là thứ tách nó khỏi câu văn có số: "Ngày 14 tháng 01 năm 2026" không khớp vì sau số là
    /// chữ thường. "Ngày 14/01/2026 báo cáo…" không khớp vì sau số là dấu gạch chéo, và "Trang 5"
    /// không khớp vì không có phần tên mục.
    /// </para>
    /// </summary>
    private static readonly Regex LabelledNumberPrefixRx = new(
        @"^\s*\p{Lu}[\p{L}]{1,11}\s+(\d{1,3}|[IVXLCDM]{1,7})(?:\s*[\.\):\-–]\s+\p{L}|\s+\p{Lu})",
        RegexOptions.Compiled);

    /// <summary>
    /// "1.", "1.2", "2.3.4)" ở đầu dòng — kể cả khi thiếu dấu cách sau dấu chấm.
    /// <para>
    /// Bản gõ tay rất hay quên dấu cách, ví dụ: "1.MUC (chỉ số tổng hợp…)"
    /// mất trọn 0.35 điểm thưởng đánh số nên chỉ còn 0.40, dưới ngưỡng 0.45 và bị loại — trong
    /// khi hai mục anh em "2. MB…" và "3. MB…" được 0.75. Mô hình không cứu được vì đoạn bị loại
    /// từ trước khi nó nhìn thấy.
    /// </para>
    /// <para>
    /// Vẫn CỐ Ý không nhận "1MUC" (mất luôn dấu chấm): không phân biệt được với "3G", "4K", "2B".
    /// Sau số phải có dấu ngắt hoặc khoảng trắng — không chấp nhận nối thẳng chữ.
    /// <c>(?!\d)</c> chặn nuốt nhầm số dài: "2024 Báo cáo" không được thành mục "20".
    /// </para>
    /// </summary>
    private static readonly Regex DecimalPrefixRx = new(
        @"^\s*(\d{1,2}(?:\.\d{1,2}){0,4})(?!\d)\s*(?:[\.\)\-–:]\s*|\s+)\S",
        RegexOptions.Compiled);

    private static readonly Regex RomanPrefixRx = new(
        @"^\s*([IVXLCDM]{1,7})\s*[\.\)\-–:]\s+\S",
        RegexOptions.Compiled);

    /// <summary>"A. …", "Б) …" — \p{Lu} bắt mọi chữ hoa Unicode nên không phải liệt kê bảng chữ cái.</summary>
    private static readonly Regex LetterPrefixRx = new(
        @"^\s*(\p{Lu})\s*[\.\)]\s+\S",
        RegexOptions.Compiled);

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
    /// <see cref="SlimParagraph.PrecedesTable"/>.
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
    private static readonly Regex CaptionRx = new(
        @"^\s*(hình(\s*ảnh|\s*vẽ)?|ảnh|bảng|biểu\s*đồ|sơ\s*đồ|đồ\s*thị|phụ\s*biểu|" +
        @"figure|fig|table|chart|picture|image|diagram)\s*\d+([.\-–]\d+)*\s*[.:\-–)]?\s+\S",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Nhãn đặt tên đối tượng đứng ngay trước chính bảng nó đặt tên, số gõ tay chứ không do Word
    /// sinh. Công khai để <see cref="StyleTrustAudit"/> dùng lại đúng luật này thay vì dựng bộ thứ
    /// hai — hai bộ luật cho cùng một khái niệm thì sớm muộn đi lệch nhau.
    /// </summary>
    public static bool IsObjectCaption(SlimParagraph p) =>
        p.PrecedesTable && p.NumberingId is null && ObjectLabelPrefixRx.IsMatch(p.Text);

    /// <summary>Mở đầu bằng ký hiệu gạch đầu dòng — quy ước ký hiệu, không gắn với ngôn ngữ nào.</summary>
    public static bool LooksLikeListItem(string text) => BulletPrefixRx.IsMatch(text);

    /// <summary>Kết thúc bằng dấu câu của câu văn thường.</summary>
    public static bool EndsLikeSentence(string text) => SentenceEndRx.IsMatch(text);

    /// <summary>
    /// Gán <see cref="SlimParagraph.Role"/>, <see cref="SlimParagraph.GuessedLevel"/> và điểm số.
    /// </summary>
    /// <param name="trustStyleSelection">
    /// Cho phép style built-in thoát sớm với <c>Score = 1.0</c>. Đặt false khi
    /// <see cref="StyleTrustAudit"/> chấm rằng style của TÀI LIỆU NÀY bị áp bừa: khi đó đoạn mang
    /// style vẫn đi tiếp xuống phần tính điểm — nó KHÔNG bị xoá, chỉ mất quyền phủ quyết mọi luật
    /// hình dạng phía dưới (bảng, chú thích, gạch đầu dòng, dấu câu cuối).
    /// </param>
    public static void Classify(SlimParagraph p, ExtractionOptions options, bool trustStyleSelection = true)
    {
        if (string.IsNullOrWhiteSpace(p.Text))
        {
            p.Role = ParagraphRole.Empty;
            return;
        }

        // 0) Loại thẳng hai họ nhiễu lớn nhất trong luận văn/báo cáo: dòng mục lục
        //    (tín hiệu cấu trúc: hyperlink tới neo _Toc) và chú thích hình/bảng (tín hiệu từ ngữ).
        // Chú thích bảng nhận diện bằng CẤU TRÚC, không bằng từ vựng: nhãn "từ + số nhiều phần"
        // đứng ngay trước chính bảng nó đặt tên, và con số là gõ tay (NumberingId null) chứ không
        // do danh sách numbering của Word sinh ra. Mọi heading đánh số thật trong tài liệu Word đều
        // mang NumberingId — đó là vế tách hai nhóm sạch nhất.
        // ĐO ĐƯỢC: trên một báo cáo thật 1183 đoạn, 13 chú thích bị tác giả gán style Heading3 nên
        // nhánh style cho điểm 1.0 và thoát sớm. Ở chế độ --structural-only (mặc định của giao
        // diện) thì CaptionRx bị tắt cùng cờ luật từ ngữ, tức không còn bộ lọc chú thích nào.
        var objectCaption = p.PrecedesTable && p.NumberingId is null && ObjectLabelPrefixRx.IsMatch(p.Text);

        if (p.InTableOfContents || objectCaption ||
            (options.UseLexicalRules && CaptionRx.IsMatch(p.Text)))
        {
            p.Role = ParagraphRole.Normal;
            p.Score = 0;
            return;
        }

        // 1) Style khẳng định — TRỪ khi dòng mở đầu bằng ký tự gạch đầu dòng.
        //    Đo được trên tài liệu thật: đoạn thân bài "- Kích thước dữ liệu: Khoảng 200 GB…"
        //    bị gán nhầm w:outlineLvl=3, và nhánh style thoát sớm nên mọi luật về hình thức
        //    (gạch đầu dòng, dấu chấm cuối câu) không bao giờ được chạy. Ký tự gạch đầu dòng
        //    là quy ước ký hiệu, không gắn với ngôn ngữ nào, nên phủ quyết ở đây là an toàn.
        //    Không loại thẳng: vẫn cho xuống phần tính điểm để đoạn nào thực sự nổi bật về
        //    định dạng còn cơ hội trở lại làm ứng viên.
        var looksLikeListItem = BulletPrefixRx.IsMatch(p.Text);

        // 0) Danh sách đa cấp tự khai cấp này gắn với style Heading N. Đây là tuyên bố cấu trúc
        //    mạnh nhất trong OOXML: người soạn cấu hình MỘT LẦN cho cả tài liệu qua hộp thoại
        //    multilevel list, nên nó không nhiễm lỗi copy định dạng như w:outlineLvl. Đặt trước
        //    cả nhánh style built-in vì nó khai báo cả cấp lẫn quan hệ cha–con của cả cây.
        if (trustStyleSelection && !looksLikeListItem && p.NumberingStyleLevel is { } listHeadingLevel)
        {
            p.Role = ParagraphRole.StyledHeading;
            p.HasBuiltInHeadingStyle = true;
            p.GuessedLevel = listHeadingLevel;
            p.Score = 1.0;
            return;
        }

        // Chỉ style built-in mới đủ mạnh để được khôi phục vô điều kiện. outlineLvl và tên
        // style tự đặt là evidence tốt nhưng đều có thể bị người soạn gán nhầm, nhất là trong
        // bảng biểu mẫu; chúng phải còn cơ hội để mô hình bác bỏ.
        var builtInLevel = looksLikeListItem ? null : BuiltInLevel(p);
        if (builtInLevel is not null && trustStyleSelection)
        {
            p.Role = ParagraphRole.StyledHeading;
            p.HasBuiltInHeadingStyle = true;
            p.GuessedLevel = builtInLevel;
            p.Score = 1.0;
            return;
        }

        // 2) Định dạng trực tiếp + mẫu đánh số.
        if (p.Text.Length > options.MaxCandidateTextLength)
        {
            p.Role = ParagraphRole.Normal;
            return;
        }

        double score = 0;
        int? prefixLevel = null;

        // Style built-in trong tài liệu bị StyleTrustAudit chấm là áp bừa: KHÔNG mất bằng chứng,
        // chỉ mất quyền thoát sớm. Điểm đủ cao để một mình nó vẫn vượt ngưỡng ứng viên, nhưng giờ
        // các luật hình dạng bên dưới (ô bảng, gạch đầu dòng, dấu câu cuối) trừ được vào nó.
        if (builtInLevel is not null)
        {
            // CỐ Ý không đặt HasBuiltInHeadingStyle: đó là cờ miễn trừ — nó chặn mô hình xoá đoạn và
            // cho critic bỏ qua. Đặt lại ở đây là vừa tuyên bố "không tin style của tài liệu này"
            // vừa trả cho nó nguyên quyền phủ quyết, tức luật thành vô hiệu. Cấp vẫn giữ qua
            // prefixLevel nên KHÔNG mất bằng chứng — chỉ chuyển quyền phán quyết sang mô hình.
            score += 0.80;
            prefixLevel = builtInLevel;
        }

        if (!looksLikeListItem && p.OutlineLevel is >= 0 and <= 8)
        {
            // Outline level là tín hiệu mạnh, nhưng không return sớm: một câu hướng dẫn trong
            // bảng hoặc bullet có outline level sai vẫn phải bị ngữ cảnh/model phản biện.
            score += p.TableDepth > 0 ? 0.25 : 0.65;
            prefixLevel = p.OutlineLevel.Value + 1;
        }
        else if (options.UseLexicalRules && LocalizedStyleLevel(p) is { } localizedLevel)
        {
            // Tên style bản địa hoá là metadata cấu trúc, không phải font; giữ đủ điểm
            // để không đánh rơi heading không đánh số trước khi LLM thấy chúng.
            score += 0.75;
            prefixLevel = localizedLevel;
        }

        // Ưu tiên numbering metadata do OOXML/NumberingResolver cung cấp. Đây là nguồn
        // đáng tin hơn việc đoán từ font hay từ nội dung hiển thị.
        if (p.NumberingId is not null)
        {
            var listLevel = p.NumberingDepth ?? ((p.NumberingLevel ?? 0) + 1);
            var isBullet = string.Equals(p.NumberingFormat, "bullet", StringComparison.OrdinalIgnoreCase);
            score += isBullet ? 0.10 : 0.60;
            prefixLevel ??= Math.Clamp(listLevel, 1, 9);
        }

        // Dạng "từ nhãn + số" là bằng chứng CẤU TRÚC (document number format), không phải bằng
        // chứng từ vựng — nên không nằm sau cờ UseLexicalRules. Trước đây nó là danh sách từ khoá
        // và bị tắt cùng luật từ ngữ, khiến "PHẦN I. CƠ SỞ LÝ LUẬN" mất sạch điểm đánh số ở đúng
        // cấu hình mà giao diện chạy mặc định.
        if (!looksLikeListItem && !CaptionRx.IsMatch(p.Text) && LabelledNumberPrefixRx.IsMatch(p.Text))
        {
            score += 0.55;
            prefixLevel ??= 1;
        }

        var dec = DecimalPrefixRx.Match(p.Text);
        if (dec.Success)
        {
            var depth = dec.Groups[1].Value.Count(c => c == '.') + 1;
            // Numbering là tín hiệu chính; không phụ thuộc bold/cỡ chữ/căn lề.
            score += depth >= 2 ? 0.55 : 0.35;
            // Số mục nhiều cấp trong table cell thường bị trừ điểm vì nằm trong bảng,
            // dù chính numbering là bằng chứng sibling mạnh (ví dụ 3.1/3.2). Giữ một
            // phần điểm cấu trúc để các mục này vẫn được đưa cho LLM hậu kiểm.
            if (p.TableDepth > 0 && depth >= 2) score += 0.25;
            prefixLevel = Math.Min(depth, 9);
        }
        else if (RomanPrefixRx.IsMatch(p.Text)) { score += 0.40; prefixLevel ??= 1; }
        else if (LetterPrefixRx.IsMatch(p.Text)) { score += 0.35; prefixLevel ??= 2; }

        // Formatting chỉ là fallback recall rất nhỏ cho tiêu đề không đánh số; không được
        // tự quyết định cấp và luôn phải qua quan hệ/LLM hậu kiểm.
        if (p.AllCaps) score += 0.25;
        if (p.KeepNext) score += 0.20;
        if (p.PageBreakBefore) score += 0.15;
        if (p.Underline) score += 0.05;

        // Chỉ dùng cỡ chữ như fallback recall cho heading không có numbering/style;
        // không dùng nó để suy ra level và không thể tự chấp nhận heading.
        var hasNumberingOrListStructure = p.NumberingId is not null || p.OutlineLevel is not null
            || dec.Success || RomanPrefixRx.IsMatch(p.Text) || LetterPrefixRx.IsMatch(p.Text);
        var baseSize = p.BodyFontSizePt ?? 11.0;
        if (!hasNumberingOrListStructure && p.FontSizePt is { } fs)
        {
            if (fs >= baseSize + 3) score += 0.35;
            else if (fs >= baseSize + 1) score += 0.20;
            else if (fs < baseSize - 0.5) score -= 0.15;
        }

        if (string.Equals(p.Alignment, "center", StringComparison.OrdinalIgnoreCase)) score += 0.20;
        if (p.Text.Length <= 80) score += 0.10;
        if (SentenceEndRx.IsMatch(p.Text) && !p.Text.EndsWith(':')) score -= 0.25;
        if (p.Text.EndsWith(':') && p.Text.Length > 60) score -= 0.25;
        if (looksLikeListItem) score -= 0.35;
        // Ô bảng thường là nhiễu; mục nhiều cấp đã được cộng evidence ở nhánh decimal.
        if (p.TableDepth > 0) score -= 0.35;
        if (p.NumberingId is not null && !dec.Success &&
            string.Equals(p.NumberingFormat, "bullet", StringComparison.OrdinalIgnoreCase))
            score -= 0.20; // bullet list thường

        p.Score = Math.Round(Math.Clamp(score, 0, 1), 3);

        if (p.Score >= options.CandidateThreshold)
        {
            p.Role = ParagraphRole.HeadingCandidate;
            p.GuessedLevel = prefixLevel;
            return;
        }

        p.Role = ParagraphRole.Normal;
        PromoteStandaloneLine(p, options);
    }

    /// <summary>
    /// Vớt heading KHÔNG đánh số và KHÔNG khác định dạng thân bài — "Danh mục hình ảnh",
    /// "Danh mục bảng biểu", "Tài liệu tham khảo".
    /// <para>
    /// Với những dòng này mọi tín hiệu hình thức đều bằng 0: không số nên không có điểm numbering,
    /// cùng font cùng cỡ nên không có điểm định dạng. Tính ra điểm 0,10 và bị loại ngay ở tầng lọc,
    /// tức mô hình KHÔNG BAO GIỜ được hỏi — không phải mô hình sai, mà là nó không được trao cơ hội.
    /// </para>
    /// <para>
    /// Không dùng tiêu chí "đoạn kế tiếp dài hơn": chính "Danh mục hình ảnh" lại đứng trước một
    /// loạt dòng ngắn ("Hình 1. Sơ đồ… 5"), nên tiêu chí đó trượt đúng ca cần vớt. Chỉ dựa vào đặc
    /// điểm của bản thân dòng: ngắn, mở đầu bằng chữ hoa, không kết thúc bằng dấu câu của câu văn,
    /// không phải bullet/caption/ô bảng.
    /// </para>
    /// <para>
    /// Cho điểm đúng bằng ngưỡng: đây là lớp ứng viên YẾU NHẤT, chỉ đủ để lọt vào diện được hỏi.
    /// Cấp để null vì không có bằng chứng cấu trúc nào nói về cấp. Đánh đổi có thật: số ứng viên
    /// tăng nên chậm hơn, và mở thêm cửa cho false positive — phải theo dõi bằng eval.
    /// </para>
    /// </summary>
    private static void PromoteStandaloneLine(SlimParagraph p, ExtractionOptions options)
    {
        if (p.TableDepth > 0) return;
        var text = p.Text.Trim();
        if (text.Length is < 3 or > 80) return;
        if (BulletPrefixRx.IsMatch(text) || CaptionRx.IsMatch(text)) return;
        if (SentenceEndRx.IsMatch(text) || text.EndsWith(':')) return;
        if (!char.IsUpper(text[0]) && !char.IsDigit(text[0])) return;
        // Phải có ít nhất hai từ chữ: chặn mã hiệu, số liệu lẻ, ô dữ liệu một từ.
        if (WordRx.Matches(text).Count < 2) return;
        // Chặn rác máy móc: JSON, khoá kỹ thuật, định danh có gạch dưới. Đo được lý do — dòng
        // `BLOCK metadata: {"i":0,...}` cài trong tài liệu thử vượt qua mọi tiêu chí ngôn ngữ ở
        // trên (ngắn, hoa đầu, không dấu câu cuối, đủ hai từ) và thành false positive.
        if (MachineNoiseRx.IsMatch(text)) return;

        p.Role = ParagraphRole.HeadingCandidate;
        p.Score = options.CandidateThreshold;
        p.GuessedLevel = null;
    }

    private static readonly Regex WordRx = new(@"\p{L}{2,}", RegexOptions.Compiled);

    /// <summary>Dấu hiệu chuỗi máy sinh chứ không phải câu chữ người viết.</summary>
    private static readonly Regex MachineNoiseRx = new(@"[{}\[\]<>""=|]|_\p{L}|\p{L}_", RegexOptions.Compiled);

    /// <summary>
    /// Suy ra cấp heading từ style. Thứ tự ưu tiên đi từ tín hiệu độc lập ngôn ngữ xuống dưới:
    /// style dựng sẵn OOXML → w:outlineLvl → (tuỳ chọn) tên style bản địa hoá.
    /// Trả null nếu không có bằng chứng nào.
    /// </summary>
    public static int? LevelFromStyle(SlimParagraph p, ExtractionOptions options)
    {
        return BuiltInLevel(p) ?? (p.OutlineLevel is >= 0 and <= 8 ? p.OutlineLevel.Value + 1 : (int?)null)
               ?? (options.UseLexicalRules ? LocalizedStyleLevel(p) : (int?)null);
    }

    /// <summary>Chỉ nhận style chuẩn OOXML, không nhận outline level hay tên người dùng tự đặt.</summary>
    public static int? BuiltInLevel(SlimParagraph p)
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
    {
        if (string.IsNullOrWhiteSpace(styleId)) return null;

        var m = BuiltInHeadingRx.Match(styleId.Trim());
        if (!m.Success) return null;

        if (m.Groups[2].Success) return int.Parse(m.Groups[2].Value);
        return m.Value.StartsWith("subtitle", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    private static int? LocalizedStyleLevel(SlimParagraph p)
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
