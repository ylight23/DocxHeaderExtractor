using System.Text;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.AgentHarness;

/// <summary>
/// Chuyển kết quả run thành một câu trả lời cho người đọc. Mọi con số ở đây đều lấy từ outline
/// và trace đã qua validator — narrator không suy diễn thêm, không làm tròn "cần duyệt" thành
/// "đã xong", và luôn nói rõ vì sao một hành động không xảy ra.
/// </summary>
public static class AgentRunNarrator
{
    public static string Describe(DocumentAgentRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var outline = result.Outline;
        var sb = new StringBuilder();

        var headings = outline.Headings.Count;
        sb.Append(headings == 0
            ? $"Không tìm thấy tiêu đề nào trong {outline.File} ({outline.ParagraphCount} đoạn)."
            : $"Tìm được {headings} tiêu đề trong {outline.File} ({outline.ParagraphCount} đoạn), " +
              $"sâu nhất cấp {outline.Headings.Max(h => h.Level)}.");

        if (headings > 0)
        {
            var bySource = outline.Headings
                .GroupBy(h => h.Source)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()} {SourceLabel(g.Key)}");
            sb.Append(' ').Append("Nguồn: ").Append(string.Join(", ", bySource)).Append('.');
        }

        var review = result.RequiresReview;
        sb.Append(' ').Append(review == 0
            ? "Tất cả đều qua cổng precision nên không còn mục nào chờ duyệt."
            : $"Còn {review} mục chưa đủ bằng chứng để tự nhận, đang chờ người duyệt.");

        if (result.RepairAttempts > 0)
            sb.Append($" Đã phải dựng lại {result.RepairAttempts} lượt sau khi validator bác kết quả đầu tiên.");

        sb.Append(' ').Append(WritebackSentence(result));

        if (outline.ElapsedMs > 0)
            sb.Append($" Mất {outline.ElapsedMs / 1000.0:0.0}s")
              .Append(outline.Model is { Length: > 0 } model ? $" với {model}." : ".");

        return sb.ToString();
    }

    private static string WritebackSentence(DocumentAgentRunResult result)
    {
        if (result.Writeback is { } writeback)
        {
            var skipped = writeback.Skipped > 0
                ? $", bỏ qua {writeback.Skipped} mục chưa đủ điều kiện ghi"
                : "";
            return $"Đã ghi {writeback.Applied} cấp heading{skipped} vào bản sao " +
                   $"{Path.GetFileName(writeback.OutputPath)}; file gốc không bị sửa.";
        }

        var skippedAction = result.Trace.FirstOrDefault(e =>
            e.Kind == AgentRunEventKind.Skipped && e.Stage.StartsWith("action.", StringComparison.Ordinal));
        if (skippedAction is not null)
            return $"Chưa ghi ra file: {Lowercase(skippedAction.Message)}";

        return "Run này chỉ đọc, không tác động ra ngoài.";
    }

    public static string DescribeBlocked(AgentRunBlockedException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return $"Đã dừng trước khi chạy: guardrail {error.Guardrail} chặn với mã {error.Code}. {error.Message}";
    }

    public static string DescribeInvalid(AgentOutputValidationException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var codes = error.Issues.Select(i => i.Code).Distinct().Take(3);
        return $"Kết quả bị chặn vì {error.Issues.Count} vi phạm bất biến nguồn " +
               $"({string.Join(", ", codes)}). Không trả outline nào ra ngoài — " +
               "một cây heading không truy được về văn bản gốc thì tệ hơn là không có.";
    }

    public static string DescribeContract(AgentSkillContractException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return $"Cấu hình harness không thoả policy {error.Skill.Name}@{error.Skill.Version}. {error.Message}";
    }

    /// <summary>Mô tả bất kỳ lỗi nào của harness bằng đúng giọng của từng loại.</summary>
    public static string DescribeError(Exception error) => error switch
    {
        AgentRunBlockedException blocked => DescribeBlocked(blocked),
        AgentOutputValidationException invalid => DescribeInvalid(invalid),
        AgentSkillContractException contract => DescribeContract(contract),
        _ => error.Message,
    };

    private static string SourceLabel(HeadingSource source) => source switch
    {
        HeadingSource.Style => "theo style Word",
        HeadingSource.Model => "do model xác nhận",
        HeadingSource.Heuristic => "theo luật OpenXML",
        HeadingSource.Structure => "cứu theo đánh số",
        HeadingSource.HumanCorrection => "người dùng đã sửa",
        _ => source.ToString(),
    };

    private static string Lowercase(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
