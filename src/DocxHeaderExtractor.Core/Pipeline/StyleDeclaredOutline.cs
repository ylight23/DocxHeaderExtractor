using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Outline = ĐÚNG những gì tác giả đã khai bằng style Heading của Word, cấp suy từ ký hiệu đánh số.
/// Không gọi mô hình, hoàn toàn tất định.
/// <para>
/// Đây là định nghĩa outline do NGƯỜI DÙNG xác nhận (2026-08-10, §41). Nó khác định nghĩa cũ ở một
/// điểm bản lề: mục không mang style Heading thì <b>không</b> thuộc outline, dù nó có đánh số, in
/// đậm hay đứng riêng một dòng.
/// </para>
/// <para>
/// ĐO ĐƯỢC trên khoá luận, chấm với đáp án người dùng xác nhận (68 mục):
/// tập 68 mục mang style trùng KHÍT tập đáp án — <b>68 có style / 0 mục thừa nào có style</b>;
/// 59 mục pipeline trả thêm thì 46 chỉ có <c>numPr</c> và 13 không có bằng chứng nào.
/// Luật cấp tái tạo <b>68/68</b>.
/// </para>
/// </summary>
public static class StyleDeclaredOutline
{
    /// <summary>Số gõ tay nhiều cấp ở đầu dòng: <c>1.1</c>, <c>2.3.4</c>.</summary>
    private static readonly Regex TypedNumber = new(@"^\s*(\d+(?:\.\d+)+)", RegexOptions.Compiled);

    /// <summary>
    /// Cấp theo bằng chứng, đúng ba nhánh mà người dùng ghi trong cột <c>evidence</c>:
    /// <list type="bullet">
    /// <item>số gõ tay độ sâu <c>d</c> → cấp <c>d + 1</c> (<c>1.1</c> sâu 2 ⇒ cấp 3);</item>
    /// <item>danh sách Word (<c>numPr</c>) không có số trong text → cấp 2;</item>
    /// <item>còn lại (style, không đánh số) → cấp 1.</item>
    /// </list>
    /// <para>
    /// Vì sao <c>d + 1</c> chứ không phải <c>d</c>: mục không đánh số (<c>CHƯƠNG 1</c>, <c>MỞ ĐẦU</c>)
    /// chiếm cấp 1, nên mọi thứ có số phải lùi xuống một bậc. Hệ quả nhìn có vẻ lạ — <c>1.1</c> là
    /// cấp 3 nên dưới <c>CHƯƠNG 1</c> không có mục cấp 2 nào — nhưng đó đúng là hình dạng của tài
    /// liệu, không phải lỗi.
    /// </para>
    /// </summary>
    public static int LevelOf(SlimParagraph paragraph)
    {
        if (TypedNumber.Match(paragraph.Text ?? "") is { Success: true } m)
            return Math.Clamp(m.Groups[1].Value.Count(c => c == '.') + 2, 1, 9);
        return paragraph.NumberingId is not null ? 2 : 1;
    }

    /// <summary>Chú thích hình/bảng — luật X2 của spec §5.1, loại bất kể style.</summary>
    private static readonly Regex Caption = new(
        @"^\s*(hình(\s*ảnh|\s*vẽ)?|ảnh|bảng|biểu\s*đồ|sơ\s*đồ|đồ\s*thị|figure|fig|table|chart)\s*\d",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Phần đầu tài liệu coi là bìa + danh mục — spec §5.1b dùng 15%.</summary>
    private const double FrontMatterFraction = 0.25;

    /// <summary>
    /// Dựng outline từ các đoạn mang style Heading built-in, SAU KHI áp các luật loại trừ của spec.
    /// <para>
    /// Bản đầu đọc thẳng <c>document.Paragraphs</c> nên đi tắt qua mọi luật loại trừ. Đo trên báo cáo
    /// thực tập (§42): 63 mục trả về thì <b>10 nằm trong phần bìa/danh mục</b> — <c>BÁO CÁO THỰC TẬP</c>
    /// (style <c>Title</c>, lặp 2 lần vì bìa nhân đôi), <c>Đà Nẵng, tháng 03 năm 2025</c> (style
    /// <c>Heading3</c>), <c>Sinh viên thực hiện</c> (style <c>Heading2</c>) — và <c>Bảng 1.1:</c> mang
    /// style <c>Heading3</c> lọt vào như một đề mục.
    /// </para>
    /// <para>
    /// Ba luật loại trừ áp ở đây, đúng spec §5.1: X1 đoạn hỏng, X2 chú thích, X6 khối bìa lặp.
    /// Chúng KHÔNG đụng tới khoá luận (vẫn 68/68) vì tài liệu đó không có ca nào thuộc ba lớp này.
    /// </para>
    /// </summary>
    /// <summary>
    /// Từ khoá mở đầu phần front/back matter và chương — spec §5.3. Nhóm này KHÔNG đánh số nên mọi
    /// luật số học đều bỏ sót; đây là cách duy nhất bắt được chúng.
    /// </summary>
    private static readonly Regex StructuralKeyword = new(
        @"^\s*(MỤC\s*LỤC|DANH\s*MỤC|LỜI\s*(CAM\s*ĐOAN|CẢM\s*ƠN|MỞ\s*ĐẦU|NÓI\s*ĐẦU)|MỞ\s*ĐẦU" +
        @"|ĐẶT\s*VẤN\s*ĐỀ|TỔNG\s*QUAN|KẾT\s*LUẬN|KIẾN\s*NGHỊ|TÀI\s*LIỆU\s*THAM\s*KHẢO" +
        @"|PHỤ\s*LỤC|TÓM\s*TẮT|ABSTRACT|CHƯƠNG\s|PHẦN\s)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TableLetterHeading = new(@"^\s*[A-Z]\.\s+\p{L}", RegexOptions.Compiled);

    private static readonly Regex TableSectionReference = new(
        @"^\s*(?:[•\u2022]\s*)?Section\s+[IVXLCDM]+\s*[-–]\s+\p{L}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Outline theo DANH SÁCH ĐA CẤP của Word — chế độ <c>numpr-driven</c> của spec §4.3.
    /// <para>
    /// Định nghĩa do người dùng xác nhận trên báo cáo thực tập (§42): chọn mục theo <c>numPr</c>
    /// (KHÔNG theo style, vì style ở tài liệu này sai 51% — gán cho dòng bìa, khối chữ ký, chú
    /// thích bảng), cấp = <c>ilvl + 1</c>. Phần front/back matter và tên chương không đánh số nên
    /// bắt bằng từ khoá cấu trúc, cấp 1.
    /// </para>
    /// <para>
    /// Vì sao KHÔNG dùng chung một luật với <see cref="Build"/>: hai tài liệu thật cho kết quả trái
    /// ngược với cùng một luật — đúng nguyên tắc N1 của spec. Trên khoá luận, style đúng 100%; trên
    /// báo cáo này, style đưa vào 10 mục bìa/danh mục và làm vỡ cây ở 6 chỗ.
    /// </para>
    /// </summary>
    public static List<HeadingRecord> BuildFromNumbering(SlimDocument document)
    {
        var frontMatter = (int)(document.Paragraphs.Count * FrontMatterFraction);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<HeadingRecord>();

        var headingLists = HeadingNumberingIds(document);

        foreach (var p in document.Paragraphs.OrderBy(p => p.Index))
        {
            if (string.IsNullOrWhiteSpace(p.Text) || p.Corrupt) continue;   // X1
            if (Caption.IsMatch(p.Text)) continue;                          // X2
            if (p.TableDepth > 0) continue;
            // Dòng mục lục do Word sinh mang style TOC1–TOC9 — chúng LẶP LẠI tên đề mục khác nên mọi
            // luật nội dung đều nhận nhầm; chỉ style mới tách được.
            if (p.StyleId?.StartsWith("TOC", StringComparison.OrdinalIgnoreCase) == true) continue;

            int level;
            if (p.NumberingLevel is { } ilvl && ilvl >= 1 &&
                p.NumberingId is { } id && headingLists.Contains((id, ilvl)))
            {
                level = Math.Clamp(ilvl + 1, 1, 9);
            }
            else if (StructuralKeyword.IsMatch(p.Text) && IsStandaloneKeyword(p))
            {
                level = 1;
            }
            else continue;

            // X6: khối bìa lặp — cùng văn bản lần hai trong phần đầu tài liệu.
            if (p.Index < frontMatter && !seen.Add(p.Text.Trim())) continue;

            result.Add(new HeadingRecord
            {
                Index = p.Index,
                Level = level,
                Text = p.Text,
                Source = p.NumberingLevel is not null ? HeadingSource.Structure : HeadingSource.Style,
                Confidence = 1.0,
                ConfidenceBasis = "numbering_declared",
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
            });
        }
        return result;
    }

    /// <summary>
    /// Những <c>numId</c> thực sự dùng cho đề mục — spec §4.3: <i>"cần lọc thêm theo numId, xác
    /// định tập numId nào thực sự dùng cho heading"</i>.
    /// <para>
    /// ĐO ĐƯỢC trên báo cáo thực tập: đề mục thật dùng <c>numId=3</c> (văn bản ngắn, trung bình dưới
    /// 60 ký tự), còn <c>numId=4</c> là danh sách NỘI DUNG trong thân bài (<c>ListParagraph</c>, mỗi
    /// mục là một câu dài). Không lọc thì 5 danh sách nội dung lọt vào outline.
    /// </para>
    /// </summary>
    private static HashSet<(int Id, int Level)> HeadingNumberingIds(SlimDocument document) =>
    [
        .. document.Paragraphs
            .Where(p => p.NumberingId is not null && p.NumberingLevel >= 1 && !string.IsNullOrWhiteSpace(p.Text))
            .GroupBy(p => (Id: p.NumberingId!.Value, Level: p.NumberingLevel!.Value))
            .Where(g => g.Count() >= MinimumListItems &&
                        g.Count(p => p.HasBuiltInHeadingStyle) >= g.Count() * HeadingStyleShare)
            .Select(g => g.Key),
    ];

    /// <summary>Danh sách phải có bấy nhiêu mục thì tỉ lệ mới có nghĩa.</summary>
    private const int MinimumListItems = 3;

    /// <summary>
    /// Tỉ lệ mục trong một danh sách phải mang style Heading để coi danh sách đó là danh sách ĐỀ MỤC.
    /// <para>
    /// Spec §4.3 nói đúng cách làm: <i>"xác định numId nào thực sự dùng cho heading bằng cách xem
    /// numId nào xuất hiện cùng block có style Heading với tỷ lệ cao"</i>. Style ở tài liệu này sai
    /// 51% khi dùng để CHỌN từng đoạn, nhưng dùng để nhận diện danh sách nào là danh sách đề mục thì
    /// vẫn tin được — sai lẻ tẻ không kéo được tỉ lệ của cả một numId.
    /// </para>
    /// <para>
    /// ĐO ĐƯỢC: khoá theo <c>numId</c> ĐƠN LẺ là SAI. Trên báo cáo thực tập, <c>numId=4</c> có 21
    /// mục — 10 là đề mục thật (ilvl 1–2), 11 là danh sách nội dung (ilvl 3). Khoá theo numId thì
    /// hoặc mất trắng 10 đề mục của chương 2, hoặc nhận cả 11 mục nội dung.
    /// Khoá theo CẶP <c>(numId, ilvl)</c> tách sạch: giữ {(3,1),(3,2),(4,1),(4,2)}, bỏ (4,3).
    /// Lọc theo độ dài trung bình cũng KHÔNG tách được vì numId=4 có nhiều mục ngắn.
    /// </para>
    /// </summary>
    private const double HeadingStyleShare = 0.8;

    /// <summary>Trung bình độ dài để một danh sách được coi là danh sách ĐỀ MỤC, không phải nội dung.</summary>
    private const int HeadingTextMaxLength = 90;

    /// <summary>
    /// Từ khoá cấu trúc chỉ tính khi đoạn ĐỨNG RIÊNG làm đề mục, không phải khi nó xuất hiện giữa
    /// thân bài. Đo được: <c>Chương 1: Giới thiệu tổng quát…</c> nằm trong đoạn liệt kê của phần mở
    /// đầu (<c>BodyText</c>) và <c>Phụ lục 1: Các tài khoản…</c> là mục con, cả hai đều không thuộc
    /// outline theo đáp án người dùng.
    /// </summary>
    private static bool IsStandaloneKeyword(SlimParagraph p) =>
        !string.Equals(p.StyleId, "BodyText", StringComparison.OrdinalIgnoreCase) &&
        p.Text.Length <= HeadingTextMaxLength &&
        !p.Text.Contains(':');

    public static List<HeadingRecord> Build(SlimDocument document)
    {
        var frontMatter = (int)(document.Paragraphs.Count * FrontMatterFraction);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var kept = new List<SlimParagraph>();
        foreach (var p in document.Paragraphs.OrderBy(p => p.Index))
        {
            if (!p.HasBuiltInHeadingStyle || string.IsNullOrWhiteSpace(p.Text)) continue;
            if (p.Corrupt) continue;                                   // X1
            if (Caption.IsMatch(p.Text)) continue;                     // X2
            // X6: khối bìa lặp — cùng văn bản xuất hiện lần hai trong phần đầu tài liệu.
            if (p.Index < frontMatter && !seen.Add(p.Text.Trim())) continue;
            kept.Add(p);
        }

        var result = kept.Select(p => new HeadingRecord
        {
            Index = p.Index,
            Level = LevelOf(p),
            Text = p.Text,
            Source = HeadingSource.Style,
            Confidence = 1.0,
            ConfidenceBasis = "style_declared",
            DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
        }).ToList();

        // KHÔNG gọi RepairInvertedTree ở đây: đo được nó kéo khoá luận từ đúng cấp 100% xuống
        // 89,7% và đúng cha 100% xuống 82,4%. Cây "lộn ngược" là hình dạng THẬT của tài liệu đó,
        // không phải lỗi cần sửa. Luật này chỉ đúng cho tài liệu numpr-driven, nên nó thuộc về
        // BuildFromNumbering nếu cần, không thuộc về đường style.
        return result;
    }

    public static List<HeadingRecord> Build(IReadOnlyList<IPolicyParagraph> paragraphs)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        var frontMatter = (int)(paragraphs.Count * FrontMatterFraction);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<HeadingRecord>();
        foreach (var p in paragraphs.OrderBy(p => p.Index))
        {
            if (!p.HasBuiltInHeadingStyle || string.IsNullOrWhiteSpace(p.Text) || p.Corrupt ||
                Caption.IsMatch(p.Text)) continue;
            if (p.Index < frontMatter && !seen.Add(p.Text.Trim())) continue;
            result.Add(new HeadingRecord
            {
                Index = p.Index, StableId = p.StableId, SourceId = p.StableId,
                Level = LevelOf(p), Text = p.Text, StyleId = p.StyleId,
                Source = HeadingSource.Style, Confidence = 1.0,
                ConfidenceBasis = "style_declared",
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
            });
        }
        return result;
    }

    private static int LevelOf(IPolicyParagraph paragraph)
    {
        if (TypedNumber.Match(paragraph.Text) is { Success: true } match)
            return Math.Clamp(match.Groups[1].Value.Count(c => c == '.') + 2, 1, 9);
        return paragraph.NumberingId is not null ? 2 : 1;
    }

    /// <summary>
    /// Outline do chính <c>w:outlineLvl</c> khai trên paragraph/style. Khác với
    /// <see cref="Build"/>: style Heading built-in chỉ là một cách Word UI đặt outline level, còn
    /// nhiều template thật khai <c>w:outlineLvl</c> qua style riêng hoặc trực tiếp trên paragraph.
    /// Nhánh <c>auto:outline-level</c> phải đọc tín hiệu này, không được thu hẹp về
    /// <c>HasBuiltInHeadingStyle</c>.
    /// </summary>
    public static List<HeadingRecord> BuildFromOutlineLevel(SlimDocument document)
    {
        var frontMatter = (int)(document.Paragraphs.Count * FrontMatterFraction);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIndexes = new HashSet<int>();
        var customStylesUnderOutlineAnchor = OutlineAnchorCustomStyles.Find(document.Paragraphs);
        var tableStylesUnderOutlineAnchor = OutlineAnchorCustomStyles.FindTableStyles(document.Paragraphs);
        var result = new List<HeadingRecord>();
        int? currentAnchorLevel = null;

        foreach (var p in document.Paragraphs.OrderBy(p => p.Index))
        {
            if (string.IsNullOrWhiteSpace(p.Text)) continue;
            if (p.Corrupt) continue;                                   // X1
            if (p.InTableOfContents) continue;                         // TOC lặp lại heading thân bài.
            if (p.StyleId?.StartsWith("TOC", StringComparison.OrdinalIgnoreCase) == true) continue;
            if (Caption.IsMatch(p.Text)) continue;                     // X2

            if ((OutlineAnchorCustomStyles.IsAnchoredTableCustomStyle(p, tableStylesUnderOutlineAnchor) ||
                 IsAnchoredTableHeadingShape(p)) &&
                seenIndexes.Add(p.Index))
            {
                result.Add(new HeadingRecord
                {
                    Index = p.Index,
                    StableId = p.StableId,
                    Level = currentAnchorLevel is { } tableAnchorLevel
                        ? Math.Clamp(tableAnchorLevel + 2, 1, 9)
                        : 1,
                    Text = p.Text,
                    StyleId = p.StyleId,
                    Source = HeadingSource.Structure,
                    Confidence = 0.95,
                    ConfidenceBasis = "outline_anchor_table_custom_style",
                    DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                });
                continue;
            }

            if (p.Index < frontMatter && !seen.Add(p.Text.Trim())) continue;

            if (p.OutlineLevel is >= 0 and <= 8)
            {
                currentAnchorLevel = p.OutlineLevel.Value;
                result.Add(new HeadingRecord
                {
                    Index = p.Index,
                    StableId = p.StableId,
                    Level = Math.Clamp(p.OutlineLevel.Value + 1, 1, 9),
                    Text = p.Text,
                    StyleId = p.StyleId,
                    Source = HeadingSource.Structure,
                    Confidence = 1.0,
                    ConfidenceBasis = "outline_level_declared",
                    DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                });
                seenIndexes.Add(p.Index);
                continue;
            }

            // §63: tài liệu form-based có cụm heading liên tiếp không mở ngay ra prose dài.
            // OutlineLvl vẫn là nguồn neo; chỉ ghép thêm HeadingCandidate style tự đặt đã sống
            // sót dưới anchor đó, với cấp = anchor gần nhất + 1.
            var anchoredCustomStyle = OutlineAnchorCustomStyles.IsAnchoredCustomStyle(p, customStylesUnderOutlineAnchor);
            if (!p.IsCandidate && !anchoredCustomStyle) continue;
            if (!anchoredCustomStyle && !IsAnchoredNumberedCandidateShape(p)) continue;
            if (currentAnchorLevel is not { } anchorLevel) continue;
            if (!seenIndexes.Add(p.Index)) continue;

            result.Add(new HeadingRecord
            {
                Index = p.Index,
                StableId = p.StableId,
                Level = Math.Clamp(anchorLevel + 2, 1, 9),
                Text = p.Text,
                StyleId = p.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0.95,
                ConfidenceBasis = "outline_anchor_custom_style",
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
            });
        }

        return result;
    }

    public static List<HeadingRecord> BuildFromOutlineLevel(IReadOnlyList<IPolicyParagraph> paragraphs)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        var frontMatter = (int)(paragraphs.Count * FrontMatterFraction);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<HeadingRecord>();
        foreach (var p in paragraphs.OrderBy(p => p.Index))
        {
            if (string.IsNullOrWhiteSpace(p.Text) || p.Corrupt || p.InTableOfContents ||
                p.StyleId?.StartsWith("TOC", StringComparison.OrdinalIgnoreCase) == true ||
                Caption.IsMatch(p.Text)) continue;
            if (p.Index < frontMatter && !seen.Add(p.Text.Trim())) continue;
            if (p.OutlineLevel is null or < 0 or > 8) continue;
            result.Add(new HeadingRecord
            {
                Index = p.Index, StableId = p.StableId, SourceId = p.StableId,
                Level = Math.Clamp(p.OutlineLevel.Value + 1, 1, 9), Text = p.Text,
                StyleId = p.StyleId, Source = HeadingSource.Structure, Confidence = 1.0,
                ConfidenceBasis = "outline_level_declared",
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
            });
        }
        return result;
    }

    public static List<HeadingRecord> BuildFromNumbering(IReadOnlyList<IPolicyParagraph> paragraphs)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        var frontMatter = (int)(paragraphs.Count * FrontMatterFraction);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headingLists = paragraphs
            .Where(p => p.NumberingId is not null && p.NumberingLevel >= 1 &&
                        !string.IsNullOrWhiteSpace(p.Text))
            .GroupBy(p => (Id: p.NumberingId!.Value, Level: p.NumberingLevel!.Value))
            .Where(g => g.Count() >= MinimumListItems &&
                        g.Count(p => p.HasBuiltInHeadingStyle) >= g.Count() * HeadingStyleShare)
            .Select(g => g.Key)
            .ToHashSet();
        var result = new List<HeadingRecord>();
        foreach (var p in paragraphs.OrderBy(p => p.Index))
        {
            if (string.IsNullOrWhiteSpace(p.Text) || p.Corrupt || Caption.IsMatch(p.Text) || p.TableDepth > 0 ||
                p.StyleId?.StartsWith("TOC", StringComparison.OrdinalIgnoreCase) == true) continue;
            int level;
            if (p.NumberingLevel is { } ilvl && ilvl >= 1 && p.NumberingId is { } id &&
                headingLists.Contains((id, ilvl)))
                level = Math.Clamp(ilvl + 1, 1, 9);
            else if (StructuralKeyword.IsMatch(p.Text) && IsStandaloneKeyword(p))
                level = 1;
            else continue;
            if (p.Index < frontMatter && !seen.Add(p.Text.Trim())) continue;
            result.Add(new HeadingRecord
            {
                Index = p.Index, StableId = p.StableId, SourceId = p.StableId,
                Level = level, Text = p.Text,
                Source = p.NumberingLevel is not null ? HeadingSource.Structure : HeadingSource.Style,
                Confidence = 1.0, ConfidenceBasis = "numbering_declared",
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
            });
        }
        return result;
    }

    private static bool IsStandaloneKeyword(IPolicyParagraph p) =>
        !string.Equals(p.StyleId, "BodyText", StringComparison.OrdinalIgnoreCase) &&
        p.Text.Length <= HeadingTextMaxLength && !p.Text.Contains(':');

    private static bool IsAnchoredNumberedCandidateShape(SlimParagraph p)
    {
        if (!p.IsCandidate || p.TableDepth > 0 || p.OutlineLevel is not null || p.NumberingId is null)
            return false;

        var text = p.Text.Trim();
        if (text.Length is < 4 or > 90) return false;
        if (text.EndsWith('.') || text.EndsWith(';') || text.EndsWith(','))
            return false;
        return NumberingAudit.ParseParagraph(p, p.Text) is not null;
    }

    private static bool IsAnchoredTableHeadingShape(SlimParagraph p)
    {
        if (p.TableDepth <= 0 || p.OutlineLevel is not null || p.HasBuiltInHeadingStyle)
            return false;

        var text = p.Text.Trim();
        if (text.Length is < 4 or > 90) return false;
        if (text.EndsWith('.') || text.EndsWith(';') || text.EndsWith(','))
            return false;

        return (p.Bold && string.Equals(p.Alignment, "center", StringComparison.OrdinalIgnoreCase) &&
                TableLetterHeading.IsMatch(text)) ||
               (p.Bold && NumberingAudit.Parse(text) is { Kind: NumberKind.Arabic }) ||
               TableSectionReference.IsMatch(text);
    }

    /// <summary>
    /// Con không được NÔNG hơn cha. Luật <c>numPr → 2</c> / <c>không số → 1</c> đúng trên khoá luận
    /// nhưng trên báo cáo thực tập tạo ra cây lộn ngược: <c>Quá trình thành lập</c> (có numPr) là cấp
    /// 2, còn <c>Giai đoạn 1994 – 2004</c> ngay dưới nó không đánh số nên thành cấp 1 — đo được 6 ca.
    /// <para>
    /// Sửa tối thiểu: mục không đánh số đứng ngay sau một mục sâu hơn thì nhận cấp của mục đó. Không
    /// đụng mục CÓ đánh số, vì với chúng chuỗi số là tuyên bố tường minh và phải thắng vị trí.
    /// </para>
    /// </summary>
    private static void RepairInvertedTree(List<HeadingRecord> headings)
    {
        for (var i = 1; i < headings.Count; i++)
        {
            var current = headings[i];
            if (current.Level >= headings[i - 1].Level) continue;
            if (TypedNumber.IsMatch(current.Text ?? "")) continue;
            if (current.Level != 1) continue;
            current.Level = headings[i - 1].Level;
        }
    }
}
