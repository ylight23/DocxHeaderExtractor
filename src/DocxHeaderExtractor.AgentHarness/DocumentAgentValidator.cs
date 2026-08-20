using DocxHeaderExtractor.Core.Models;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.AgentHarness;

/// <summary>
/// <paramref name="Index"/> là chỉ số đoạn nguồn gây lỗi, null khi lỗi thuộc về cả outline.
/// Có nó thì lượt sửa mới cách ly được đúng đoạn thay vì bỏ cả tài liệu.
/// </summary>
public sealed record AgentValidationIssue(string Code, string Message, int? Index = null);

public sealed record AgentValidationResult(IReadOnlyList<AgentValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
    public static AgentValidationResult Valid { get; } = new([]);
}

/// <summary>
/// Ngữ cảnh của lượt chạy để validator đối chiếu kết quả với ĐIỀU KIỆN đã cho phép trước khi chạy,
/// chứ không chỉ soi outline như một vật thể rời.
/// </summary>
public sealed record DocumentAgentValidationContext(
    DocumentAgentRequest Request,
    AgentToolDescriptor Tool);

public interface IDocumentAgentValidator
{
    string Name { get; }
    ValueTask<AgentValidationResult> ValidateAsync(
        DocumentOutline outline,
        DocumentAgentValidationContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Kiểm tra các bất biến có thể chứng minh bằng code. Validator không phán đoán ngữ nghĩa;
/// nó chặn index/cấp/span bịa, trùng hoặc vượt nguồn trước human-review gate.
/// </summary>
public sealed class OutlineGroundingValidator : IDocumentAgentValidator
{
    private static readonly Regex TextLayoutSectionPageRx = new(
        @"^\s*(?<marker>\d{1,3}(?:\.\d{1,3}){1,4})\s*\u2022\s*(?<title>[^\d\u2022]{2,120}?)\s+\d{1,4}\s*$",
        RegexOptions.Compiled);

    public string Name => "outline_grounding";

    public ValueTask<AgentValidationResult> ValidateAsync(
        DocumentOutline outline,
        DocumentAgentValidationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outline);
        ct.ThrowIfCancellationRequested();
        var issues = new List<AgentValidationIssue>();

        if (string.IsNullOrWhiteSpace(outline.File))
            issues.Add(new("missing_source_file", "Outline thiếu tên file nguồn."));
        if (outline.ParagraphCount < 0)
            issues.Add(new("invalid_paragraph_count", "Số paragraph không hợp lệ."));
        if (outline.CandidateCount < 0)
            issues.Add(new("invalid_candidate_count", "Số ứng viên không hợp lệ."));

        var seen = new HashSet<(int Index, string Text, int SpanStart, int SpanEnd)>();
        var previous = -1;
        foreach (var heading in outline.Headings)
        {
            var spanStart = heading.HeadingSpan?.Start ?? -1;
            var spanEnd = heading.HeadingSpan?.End ?? -1;
            if (!seen.Add((heading.Index, heading.Text.Trim(), spanStart, spanEnd)))
                issues.Add(new("duplicate_source_heading", $"Heading index {heading.Index} + text xuất hiện nhiều lần.", heading.Index));
            if (heading.Index < 0 || heading.Index >= outline.ParagraphCount)
                issues.Add(new("source_index_out_of_range", $"Index {heading.Index} không thuộc tài liệu nguồn.", heading.Index));
            if (heading.Index < previous &&
                heading.ConfidenceBasis != "pdf_financial_report")
                issues.Add(new("source_order_changed", "Heading không còn đúng thứ tự tài liệu nguồn.", heading.Index));
            previous = heading.Index;

            if (heading.Level is < 1 or > 9)
                issues.Add(new("invalid_heading_level", $"Cấp của index {heading.Index} nằm ngoài 1..9.", heading.Index));
            if (string.IsNullOrWhiteSpace(heading.Text))
                issues.Add(new("empty_heading_text", $"Heading index {heading.Index} không có văn bản nguồn.", heading.Index));
            if (!double.IsFinite(heading.Confidence) || heading.Confidence is < 0 or > 1)
                issues.Add(new("invalid_confidence", $"Confidence của index {heading.Index} nằm ngoài 0..1.", heading.Index));

            ValidateSpans(heading, issues);
        }

        return ValueTask.FromResult(issues.Count == 0
            ? AgentValidationResult.Valid
            : new AgentValidationResult(issues));
    }

    private static void ValidateSpans(HeadingRecord heading, List<AgentValidationIssue> issues)
    {
        if (heading.OriginalText is null)
        {
            if (heading.HeadingSpan is not null || heading.InlineBodySpan is not null || heading.InlineBody is not null)
                issues.Add(new("span_without_source", $"Index {heading.Index} có span nhưng thiếu originalText.", heading.Index));
            return;
        }

        if (heading.HeadingSpan is not { } headingSpan ||
            !ValidRange(headingSpan, heading.OriginalText.Length) ||
            !HeadingTextIsGrounded(heading.OriginalText[headingSpan.Start..headingSpan.End], heading.Text))
        {
            issues.Add(new("heading_span_not_grounded", $"Heading span của index {heading.Index} không khớp nguồn.", heading.Index));
        }

        if (heading.InlineBodySpan is { } bodySpan)
        {
            if (!ValidRange(bodySpan, heading.OriginalText.Length) ||
                heading.InlineBody is null ||
                heading.OriginalText[bodySpan.Start..bodySpan.End] != heading.InlineBody)
                issues.Add(new("body_span_not_grounded", $"Body span của index {heading.Index} không khớp nguồn.", heading.Index));
        }
        else if (heading.InlineBody is not null)
        {
            issues.Add(new("body_missing_span", $"Inline body của index {heading.Index} thiếu span nguồn.", heading.Index));
        }
    }

    private static bool ValidRange(TextOffsetSpan span, int length) =>
        span.Start >= 0 && span.End >= span.Start && span.End <= length;

    private static bool HeadingTextIsGrounded(string source, string heading)
    {
        if (source == heading) return true;
        if (NormalizeTextLayoutTitle(source) == NormalizeTextLayoutTitle(heading)) return true;
        if (CanonicalTitle(source) == CanonicalTitle(heading)) return true;
        if (TextLayoutSectionPageRx.Match(source) is not { Success: true } match) return false;

        var normalized = $"{match.Groups["marker"].Value.TrimEnd('.')} {match.Groups["title"].Value.Trim()}";
        return NormalizeTextLayoutTitle(normalized) == NormalizeTextLayoutTitle(heading);
    }

    private static string NormalizeTextLayoutTitle(string text)
    {
        var normalized = Regex.Replace(text.Replace('•', ' '), @"\s+", " ").Trim();
        normalized = Regex.Replace(normalized, @"^(\d+(?:\.\d+)?\s+\D.+?)\s+\d{1,4}$", "$1");
        return normalized;
    }

    private static string CanonicalTitle(string text)
    {
        var normalized = NormalizeTextLayoutTitle(text)
            .Replace('\u2019', '\'')
            .Replace('\u2018', '\'')
            .Replace('\u2010', '-')
            .Replace('\u2011', '-')
            .Replace('\u2012', '-')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-');
        normalized = Regex.Replace(normalized, @"(?<=[A-Za-z])\d{1,2}$", "");
        normalized = Regex.Replace(normalized, @"[^A-Za-z0-9]+", "");
        return normalized.ToLowerInvariant();
    }
}

public sealed class AgentOutputValidationException(
    Guid runId,
    IReadOnlyList<AgentValidationIssue> issues,
    IReadOnlyList<AgentRunEvent> trace)
    : InvalidOperationException($"Agent output không qua deterministic validator ({issues.Count} lỗi).")
{
    public Guid RunId { get; } = runId;
    public IReadOnlyList<AgentValidationIssue> Issues { get; } = issues;
    public IReadOnlyList<AgentRunEvent> Trace { get; } = trace;
}

/// <summary>
/// Đối chiếu những gì lượt chạy ĐÃ LÀM (<see cref="DocumentOutline.Provenance"/>) với những gì đã
/// được cho phép TRƯỚC khi chạy.
/// <para>
/// Guardrail chặn theo lời hứa của descriptor — một cờ tính một lần lúc dựng tool, trong khi bên
/// trong tool có tới năm lượt hỏi mô hình. Validator này là vế còn lại: nếu dữ liệu đã thực sự đi
/// ra ngoài mà run không được cấp quyền đó, run FAIL, không sửa lại. Lượt sửa chỉ có nghĩa cho lỗi
/// dựng cây; dữ liệu đã gửi đi rồi thì chạy lại không thu hồi được gì, và im lặng cho qua thì
/// guardrail chỉ còn là hình thức.
/// </para>
/// </summary>
public sealed class RunProvenanceValidator : IDocumentAgentValidator
{
    public string Name => "run_provenance";

    public ValueTask<AgentValidationResult> ValidateAsync(
        DocumentOutline outline,
        DocumentAgentValidationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (outline.Provenance is not { } provenance) return ValueTask.FromResult(AgentValidationResult.Valid);

        var issues = new List<AgentValidationIssue>();

        if (provenance.SentDataExternally && !context.Request.AllowExternalDataTransfer)
            issues.Add(new AgentValidationIssue(
                "provenance_external_without_consent",
                $"Backend {provenance.Backend} đã gửi nội dung ra ngoài ở " +
                $"{provenance.Passes.Count(x => x.SentDataExternally)} lượt, nhưng run không được cấp quyền đó."));

        if (provenance.SentDataExternally && !context.Tool.SendsDataExternally)
            issues.Add(new AgentValidationIssue(
                "provenance_contradicts_descriptor",
                $"Tool khai SendsDataExternally=false nhưng lượt chạy dùng backend {provenance.Backend}."));

        return ValueTask.FromResult(
            issues.Count == 0 ? AgentValidationResult.Valid : new AgentValidationResult(issues));
    }
}
