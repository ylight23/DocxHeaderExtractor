using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>
/// Luật nhận diện tiêu đề chạy trước LLM. Mục tiêu: giữ lại đúng tập ứng viên nhỏ
/// (recall cao, precision vừa phải) để LLM chỉ phải lọc và gán cấp.
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

    /// <summary>"Chương 1", "Điều 5", "Phần II", "Bài 3", "Phụ lục A", "Article 2"…</summary>
    private static readonly Regex KeywordPrefixRx = new(
        @"^\s*(chương|chuong|phần|phan|mục|muc|điều|dieu|bài|bai|tiết|tiet|phụ\s*lục|phu\s*luc|đề\s*mục|" +
        @"chapter|section|part|article|appendix|annex|unit|lesson)\s*[:\-–]?\s*([0-9]+|[ivxlcdm]+|[a-z])\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"1.", "1.2", "2.3.4)" ở đầu dòng.</summary>
    private static readonly Regex DecimalPrefixRx = new(
        @"^\s*(\d{1,2}(?:\.\d{1,2}){0,4})\s*[\.\)\-–:]?\s+\S",
        RegexOptions.Compiled);

    private static readonly Regex RomanPrefixRx = new(
        @"^\s*([IVXLCDM]{1,7})\s*[\.\)\-–:]\s+\S",
        RegexOptions.Compiled);

    /// <summary>"A. …", "Б) …" — \p{Lu} bắt mọi chữ hoa Unicode nên không phải liệt kê bảng chữ cái.</summary>
    private static readonly Regex LetterPrefixRx = new(
        @"^\s*(\p{Lu})\s*[\.\)]\s+\S",
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
    /// Gán <see cref="SlimParagraph.Role"/>, <see cref="SlimParagraph.GuessedLevel"/> và điểm số.
    /// </summary>
    public static void Classify(SlimParagraph p, ExtractionOptions options)
    {
        if (string.IsNullOrWhiteSpace(p.Text))
        {
            p.Role = ParagraphRole.Empty;
            return;
        }

        // 0) Loại thẳng hai họ nhiễu lớn nhất trong luận văn/báo cáo: dòng mục lục
        //    (tín hiệu cấu trúc: hyperlink tới neo _Toc) và chú thích hình/bảng (tín hiệu từ ngữ).
        if (p.InTableOfContents || (options.UseLexicalRules && CaptionRx.IsMatch(p.Text)))
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

        var styleLevel = looksLikeListItem ? null : LevelFromStyle(p, options);
        if (styleLevel is not null)
        {
            p.Role = ParagraphRole.StyledHeading;
            p.GuessedLevel = styleLevel;
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

        if (options.UseLexicalRules && KeywordPrefixRx.IsMatch(p.Text)) { score += 0.55; prefixLevel = 1; }

        var dec = DecimalPrefixRx.Match(p.Text);
        if (dec.Success)
        {
            var depth = dec.Groups[1].Value.Count(c => c == '.') + 1;
            score += depth >= 2 ? 0.55 : 0.35;
            prefixLevel = Math.Min(depth, 9);
        }
        else if (RomanPrefixRx.IsMatch(p.Text)) { score += 0.40; prefixLevel ??= 1; }
        else if (LetterPrefixRx.IsMatch(p.Text)) { score += 0.25; prefixLevel ??= 2; }

        if (p.Bold) score += 0.30;
        if (p.AllCaps) score += 0.25;
        if (p.KeepNext) score += 0.20;
        if (p.PageBreakBefore) score += 0.15;
        if (p.Underline) score += 0.05;

        var baseSize = p.BodyFontSizePt ?? 11.0;
        if (p.FontSizePt is { } fs)
        {
            if (fs >= baseSize + 3) score += 0.35;
            else if (fs >= baseSize + 1) score += 0.20;
            else if (fs < baseSize - 0.5) score -= 0.15;
        }

        if (string.Equals(p.Alignment, "center", StringComparison.OrdinalIgnoreCase)) score += 0.20;
        if (p.Text.Length <= 80) score += 0.10;
        if (SentenceEndRx.IsMatch(p.Text) && !p.Text.EndsWith(':')) score -= 0.25;

        // Tiêu đề có thể kết thúc bằng ':' ("CHƯƠNG 1:"), nhưng câu dẫn vào danh sách
        // ("Đề tài triển khai các nhiệm vụ như sau:") thì dài — dùng độ dài để tách hai trường hợp.
        if (p.Text.EndsWith(':') && p.Text.Length > 60) score -= 0.25;

        if (looksLikeListItem) score -= 0.35;
        if (p.Italic && !p.Bold) score -= 0.10;
        if (p.TableDepth > 0) score -= 0.35;
        if (p.NumberingId is not null && !dec.Success && !p.Bold) score -= 0.20; // bullet list thường

        p.Score = Math.Round(Math.Clamp(score, 0, 1), 3);

        if (p.Score >= options.CandidateThreshold)
        {
            p.Role = ParagraphRole.HeadingCandidate;
            p.GuessedLevel = prefixLevel;
        }
        else
        {
            p.Role = ParagraphRole.Normal;
        }
    }

    /// <summary>
    /// Suy ra cấp heading từ style. Thứ tự ưu tiên đi từ tín hiệu độc lập ngôn ngữ xuống dưới:
    /// style dựng sẵn OOXML → w:outlineLvl → (tuỳ chọn) tên style bản địa hoá.
    /// Trả null nếu không có bằng chứng nào.
    /// </summary>
    public static int? LevelFromStyle(SlimParagraph p, ExtractionOptions options)
    {
        // 1) Style dựng sẵn của OOXML — từ vựng của đặc tả, không phải của ngôn ngữ tài liệu.
        foreach (var candidate in new[] { p.StyleName, p.StyleId })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            var m = BuiltInHeadingRx.Match(candidate.Trim());
            if (!m.Success) continue;

            if (m.Groups[2].Success) return int.Parse(m.Groups[2].Value);
            return m.Value.StartsWith("subtitle", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        }

        // 2) w:outlineLvl — thuộc tính cấu trúc thuần, đúng với mọi ngôn ngữ và mọi tên style.
        if (p.OutlineLevel is >= 0 and <= 8)
            return p.OutlineLevel.Value + 1;

        // 3) Style tự đặt tên theo ngôn ngữ người dùng — chỉ khi chấp nhận luật từ ngữ.
        if (!options.UseLexicalRules) return null;

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
