using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Luật R1/R2/R4 của spec "lớp filter deterministic dựa trên OOXML style": đoạn mang style Heading
/// built-in, ngoài bảng/textbox, ngắn và không kết thúc bằng dấu chấm câu thì được gán thẳng
/// heading + cấp với confidence 1.0 và <b>rút hẳn khỏi luồng LLM</b>.
/// <para>
/// TỒN TẠI ĐỂ ĐO, KHÔNG PHẢI ĐỂ DÙNG. Mặc định tắt. Bằng chứng chống lại nó trước lượt đo này chỉ
/// là gián tiếp: <see cref="HeaderExtractionPipeline.SkipStyledCandidates"/> bỏ HỎI nhưng vẫn giữ
/// đoạn trong khối làm ngữ cảnh, và riêng thay đổi đó đã đủ làm precision tụt 100% → 94,1%. R1 mạnh
/// hơn hẳn — nó rút đoạn ra khỏi tập ứng viên — nên phải có số của chính nó.
/// </para>
/// <para>
/// KẾT QUẢ ĐO (handoff §10): trên bench F1 tăng 90,9% → 92,0%, nhưng truy nguyên thì bốn heading nó
/// gán thẳng đều đã đúng sẵn ở nhánh tắt — lợi ích đến từ việc rút ứng viên làm đổi thành phần khối,
/// đúng cơ chế §4.1. Trên <c>09-style-ap-sai</c> ở chế độ <c>--no-llm</c> nó làm precision TỤT
/// 57,1% → 50%, vì nó đọc thẳng <c>style_raw</c> chứ không đọc <see cref="ParagraphRole"/> nên đi
/// vòng qua luật chú thích cấu trúc của §7.4. Vì vậy: giữ để đối chứng, không bật mặc định.
/// <para>
/// Cài ở dạng ĐẦY ĐỦ NHẤT có thể để R1 không thua vì lý do phụ: heading auto-assign vẫn được nhập
/// vào kết quả TRƯỚC hậu kiểm đánh số nên chúng vẫn làm anh em cho các mục khác, và cổng precision
/// có ngoại lệ giữ chúng ở trạng thái tự nhận đúng như spec yêu cầu ("không có cơ chế review nào
/// phía sau bắt lại").
/// </para>
/// </summary>
public static class OoxmlStyleAutoAssign
{
    /// <summary>Ghi vào <see cref="HeadingRecord.ConfidenceBasis"/> để cổng precision nhận ra.</summary>
    public const string Basis = "ooxml_style_auto_assign";

    /// <summary>Ngưỡng R1 của spec. Chưa hiệu chỉnh từ dữ liệu — đúng như spec thừa nhận.</summary>
    public const int LengthThreshold = 120;

    /// <summary>
    /// "Dấu chấm câu đầy đủ" hiểu theo nghĩa hẹp (kết câu), không tính <c>:</c> hay <c>;</c>.
    /// Chọn nghĩa hẹp để R1 phủ được NHIỀU đoạn nhất — nếu nó thua thì không thua vì bị bó.
    /// </summary>
    private static readonly Regex TerminalPunctuationRx = new(@"[.!?]\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Chạy ngay sau parser. Đoạn nào được gán thẳng thì bị hạ <see cref="ParagraphRole.Normal"/>
    /// nên nó rời tập ứng viên và rời luôn phần view mà mô hình đọc như ứng viên.
    /// </summary>
    public static List<HeadingRecord> Apply(SlimDocument document, IReadOnlySet<int> quarantined)
    {
        var assigned = new List<HeadingRecord>();

        foreach (var p in document.Paragraphs)
        {
            if (quarantined.Contains(p.Index)) continue;

            // R4 đứng trước R1 — đúng thứ tự spec yêu cầu ở mục 3 và edge case #4. Đây CHÍNH LÀ chỗ
            // pipeline hiện tại làm khác: HeadingHeuristics.Classify trả sớm ở nhánh style built-in
            // nên không bao giờ chạy tới phần trừ điểm TableDepth.
            if (p.TableDepth > 0 || IsInTextBox(p)) continue;

            // R1/R2 chỉ xét style CỦA CHÍNH ĐOẠN (spec: style_raw). Cấp do danh sách đa cấp khai
            // qua w:lvl/w:pStyle không phải style_raw nên không thuộc phạm vi R1.
            if (HeadingHeuristics.BuiltInLevel(p) is not { } level) continue;

            // R2: dài quá hoặc kết thúc như một câu ⇒ trả về luồng LLM, giữ nguyên vai trò ứng viên.
            if (p.Text.Length > LengthThreshold || TerminalPunctuationRx.IsMatch(p.Text)) continue;

            assigned.Add(new HeadingRecord
            {
                Index = p.Index,
                StableId = p.StableId,
                Level = level,
                Text = p.Text,
                StyleId = p.StyleId,
                Source = HeadingSource.Style,
                Confidence = 1.0,
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                ConfidenceBasis = Basis,
            });

            p.Role = ParagraphRole.Normal;
        }

        return assigned;
    }

    /// <summary>
    /// Textbox không có cờ riêng trên <see cref="SlimParagraph"/>; đường XML do
    /// <c>ParagraphWalker</c> dựng mới là chỗ ghi lại điều đó.
    /// </summary>
    private static bool IsInTextBox(SlimParagraph p) =>
        p.StableId.Contains("txbxContent", StringComparison.Ordinal);
}
