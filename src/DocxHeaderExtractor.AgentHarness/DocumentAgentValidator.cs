using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.AgentHarness;

public sealed record AgentValidationIssue(string Code, string Message);

public sealed record AgentValidationResult(IReadOnlyList<AgentValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
    public static AgentValidationResult Valid { get; } = new([]);
}

public interface IDocumentAgentValidator
{
    string Name { get; }
    ValueTask<AgentValidationResult> ValidateAsync(
        DocumentOutline outline,
        CancellationToken ct = default);
}

/// <summary>
/// Kiểm tra các bất biến có thể chứng minh bằng code. Validator không phán đoán ngữ nghĩa;
/// nó chặn index/cấp/span bịa, trùng hoặc vượt nguồn trước human-review gate.
/// </summary>
public sealed class OutlineGroundingValidator : IDocumentAgentValidator
{
    public string Name => "outline_grounding";

    public ValueTask<AgentValidationResult> ValidateAsync(
        DocumentOutline outline,
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

        var seen = new HashSet<int>();
        var previous = -1;
        foreach (var heading in outline.Headings)
        {
            if (!seen.Add(heading.Index))
                issues.Add(new("duplicate_source_index", $"Index {heading.Index} xuất hiện nhiều lần."));
            if (heading.Index < 0 || heading.Index >= outline.ParagraphCount)
                issues.Add(new("source_index_out_of_range", $"Index {heading.Index} không thuộc tài liệu nguồn."));
            if (heading.Index < previous)
                issues.Add(new("source_order_changed", "Heading không còn đúng thứ tự tài liệu nguồn."));
            previous = heading.Index;

            if (heading.Level is < 1 or > 9)
                issues.Add(new("invalid_heading_level", $"Cấp của index {heading.Index} nằm ngoài 1..9."));
            if (string.IsNullOrWhiteSpace(heading.Text))
                issues.Add(new("empty_heading_text", $"Heading index {heading.Index} không có văn bản nguồn."));
            if (!double.IsFinite(heading.Confidence) || heading.Confidence is < 0 or > 1)
                issues.Add(new("invalid_confidence", $"Confidence của index {heading.Index} nằm ngoài 0..1."));

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
                issues.Add(new("span_without_source", $"Index {heading.Index} có span nhưng thiếu originalText."));
            return;
        }

        if (heading.HeadingSpan is not { } headingSpan ||
            !ValidRange(headingSpan, heading.OriginalText.Length) ||
            heading.OriginalText[headingSpan.Start..headingSpan.End] != heading.Text)
        {
            issues.Add(new("heading_span_not_grounded", $"Heading span của index {heading.Index} không khớp nguồn."));
        }

        if (heading.InlineBodySpan is { } bodySpan)
        {
            if (!ValidRange(bodySpan, heading.OriginalText.Length) ||
                heading.InlineBody is null ||
                heading.OriginalText[bodySpan.Start..bodySpan.End] != heading.InlineBody)
                issues.Add(new("body_span_not_grounded", $"Body span của index {heading.Index} không khớp nguồn."));
        }
        else if (heading.InlineBody is not null)
        {
            issues.Add(new("body_missing_span", $"Inline body của index {heading.Index} thiếu span nguồn."));
        }
    }

    private static bool ValidRange(TextOffsetSpan span, int length) =>
        span.Start >= 0 && span.End >= span.Start && span.End <= length;
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
