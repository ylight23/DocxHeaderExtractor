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
    Repairing,
    Skipped,
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
    bool MutatesExternalState)
{
    /// <summary>
    /// Tool biết dựng lại kết quả khi bị deterministic validator bác, thay vì trả nguyên kết quả cũ.
    /// Tool không hỗ trợ thì harness fail ngay ở lượt đầu — lặp lại y hệt chỉ tốn thời gian.
    /// </summary>
    public bool SupportsRepair { get; init; }

    /// <summary>
    /// Đường dẫn mà tool sẽ ghi TRONG khi chạy, ngoài đích writeback của request — ví dụ file dump
    /// document view khi bật cờ debug. Khai ở đây thì <see cref="ToolSideEffectPathGuardrail"/> soi
    /// được; bỏ trống thì đường ghi đó nằm ngoài mọi guardrail và harness hứa "chỉ đọc" trong khi
    /// tool có ghi file.
    /// </summary>
    public IReadOnlyList<string> SideEffectPaths { get; init; } = [];
}

/// <summary>
/// Yêu cầu cho một agent run. Cả việc gửi dữ liệu ra ngoài lẫn việc ghi ra file đều phải được
/// caller xác nhận rõ ràng cho từng run; có API key hay có quyền ghi thư mục không phải là đồng ý.
/// </summary>
public sealed record DocumentAgentRequest(
    string InputPath,
    bool AllowExternalDataTransfer = false)
{
    /// <summary>Đường dẫn .docx đích cho writeback; null nghĩa là run chỉ đọc.</summary>
    public string? WritebackTargetPath { get; init; }

    public bool AllowWritebackOverwrite { get; init; }

    /// <summary>Gán thêm style Heading N có sẵn trong tài liệu, ngoài <c>w:outlineLvl</c>.</summary>
    public bool ApplyHeadingStyles { get; init; }

    public bool WantsWriteback => !string.IsNullOrWhiteSpace(WritebackTargetPath);

    /// <summary>Thư mục ghi partial key package; null nghĩa là không tạo package review.</summary>
    public string? KeyPackageOutputDirectory { get; init; }

    public int KeyPackageLimit { get; init; } = 30;

    public int KeyPackageStart { get; init; }

    public bool KeyPackageDistributedSample { get; init; } = true;

    public bool WantsKeyPackage => !string.IsNullOrWhiteSpace(KeyPackageOutputDirectory);

    public bool WantsAction => WantsWriteback || WantsKeyPackage;
}

/// <summary>Một lượt gọi tool. <paramref name="Feedback"/> chỉ khác null ở lượt sửa.</summary>
public sealed record AgentToolInvocation(
    DocumentAgentRequest Request,
    int Attempt,
    AgentRepairFeedback? Feedback = null);

/// <summary>
/// Bằng chứng vi phạm mà validator đưa lại cho tool. Chỉ chứa mã lỗi và chỉ số nguồn — không
/// chứa gợi ý "nên trả gì", vì như vậy là để lượt sau chép lại đáp án thay vì phân tích lại.
/// </summary>
public sealed record AgentRepairFeedback(
    IReadOnlyList<AgentValidationIssue> Issues,
    IReadOnlyList<int> QuarantineIndexes);

public sealed record AgentRunEvent(
    Guid RunId,
    int Sequence,
    DateTimeOffset Timestamp,
    string Stage,
    AgentRunEventKind Kind,
    string Message);

public sealed record AgentWritebackReport(
    string OutputPath,
    int Applied,
    int Skipped);

public sealed record DocumentAgentRunResult(
    Guid RunId,
    AgentRunOutcome Outcome,
    DocumentOutline Outline,
    int Steps,
    IReadOnlyList<AgentRunEvent> Trace)
{
    public required AgentSkill Skill { get; init; }

    /// <summary>Số lượt sửa đã dùng (0 = qua validator ngay lượt đầu).</summary>
    public int RepairAttempts { get; init; }

    /// <summary>Kết quả ghi ngược; null khi run chỉ đọc hoặc khi gate chặn hành động ghi.</summary>
    public AgentWritebackReport? Writeback { get; init; }

    public int RequiresReview => Outline.Headings.Count(h =>
        h.DecisionStatus == HeadingDecisionStatus.RequiresReview || h.Disputed);
}

public sealed class AgentHarnessOptions
{
    /// <summary>
    /// Giới hạn cứng số bước có tác dụng (guardrail/tool/validator/gate/action), ngăn vòng lặp
    /// agent vô hạn. Workflow đọc mặc định cần 9 bước: skill contract, chọn tool, BỐN guardrail
    /// (input, external data, writeback target, side-effect paths), tool, HAI validator
    /// (outline grounding, run provenance) và human-review gate. Mỗi lượt sửa thêm số bước bằng
    /// số validator cộng một (ở đây là 3), mỗi hành động ghi thêm 1.
    /// <para>
    /// Mặc định 16 chứ không phải 10: một lượt sửa kèm hành động ghi cần 14 bước. Trần đặt sát quá
    /// thì thêm một validator là run hỏng ở gate — đúng cái vừa xảy ra khi thêm run_provenance.
    /// </para>
    /// </summary>
    public int MaxSteps { get; set; } = 16;

    /// <summary>
    /// Số lượt được phép dựng lại kết quả sau khi validator bác. 0 = fail-closed ngay lượt đầu.
    /// Skill là trần: cấu hình cao hơn <c>requires.maxRepairAttempts</c> sẽ bị chặn ở contract check.
    /// </summary>
    public int MaxRepairAttempts { get; set; } = 1;

    public void Validate()
    {
        if (MaxSteps is < 4 or > 64)
            throw new InvalidOperationException("AgentHarness MaxSteps phải nằm trong khoảng 4..64.");
        if (MaxRepairAttempts is < 0 or > 8)
            throw new InvalidOperationException("AgentHarness MaxRepairAttempts phải nằm trong khoảng 0..8.");
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
