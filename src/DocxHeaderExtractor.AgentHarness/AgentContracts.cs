using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.AgentHarness;

public enum AgentRunOutcome
{
    Completed,
    NeedsHumanReview,
}

public enum AgentRunEventKind
{
    Started,
    Passed,
    Completed,
    Blocked,
    Failed,
    Cancelled,
}

public enum AgentToolRisk
{
    Low,
    Medium,
    High,
}

/// <summary>
/// Mô tả khả năng và tác động của tool để harness áp guardrail trước khi thực thi.
/// Không dùng mô tả do model tự khai báo.
/// </summary>
public sealed record AgentToolDescriptor(
    string Name,
    string Description,
    AgentToolRisk Risk,
    bool SendsDataExternally,
    bool MutatesExternalState);

/// <summary>
/// Yêu cầu cho một agent run. Truyền dữ liệu ra ngoài phải được caller xác nhận rõ ràng cho
/// từng run; việc có API key không đồng nghĩa với đồng ý gửi tài liệu.
/// </summary>
public sealed record DocumentAgentRequest(
    string InputPath,
    bool AllowExternalDataTransfer = false);

public sealed record AgentRunEvent(
    Guid RunId,
    int Sequence,
    DateTimeOffset Timestamp,
    string Stage,
    AgentRunEventKind Kind,
    string Message);

public sealed record DocumentAgentRunResult(
    Guid RunId,
    AgentRunOutcome Outcome,
    DocumentOutline Outline,
    int Steps,
    IReadOnlyList<AgentRunEvent> Trace)
{
    public int RequiresReview => Outline.Headings.Count(h =>
        h.DecisionStatus == HeadingDecisionStatus.RequiresReview || h.Disputed);
}

public sealed class AgentHarnessOptions
{
    /// <summary>
    /// Giới hạn cứng số bước có tác dụng (guardrail/tool/gate), ngăn vòng lặp agent vô hạn.
    /// Workflow mặc định cần 5 bước: hai guardrail, tool, validator và human-review gate.
    /// </summary>
    public int MaxSteps { get; set; } = 8;

    public void Validate()
    {
        if (MaxSteps is < 4 or > 64)
            throw new InvalidOperationException("AgentHarness MaxSteps phải nằm trong khoảng 4..64.");
    }
}

public interface IAgentRunSink
{
    ValueTask WriteAsync(AgentRunEvent evt, CancellationToken ct = default);
}

public sealed class DelegateAgentRunSink(
    Func<AgentRunEvent, CancellationToken, ValueTask> write) : IAgentRunSink
{
    public ValueTask WriteAsync(AgentRunEvent evt, CancellationToken ct = default) => write(evt, ct);
}

internal sealed class NullAgentRunSink : IAgentRunSink
{
    public static NullAgentRunSink Instance { get; } = new();

    public ValueTask WriteAsync(AgentRunEvent evt, CancellationToken ct = default) => ValueTask.CompletedTask;
}
