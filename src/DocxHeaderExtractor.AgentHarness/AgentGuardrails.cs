namespace DocxHeaderExtractor.AgentHarness;

public sealed record AgentGuardrailDecision(bool Allowed, string Code, string Message)
{
    public static AgentGuardrailDecision Pass(string code, string message) => new(true, code, message);
    public static AgentGuardrailDecision Block(string code, string message) => new(false, code, message);
}

public sealed record DocumentAgentGuardrailContext(
    DocumentAgentRequest Request,
    AgentToolDescriptor Tool);

public interface IDocumentAgentGuardrail
{
    string Name { get; }

    ValueTask<AgentGuardrailDecision> EvaluateAsync(
        DocumentAgentGuardrailContext context,
        CancellationToken ct = default);
}

/// <summary>Chặn sớm đường dẫn không tồn tại hoặc định dạng mà pipeline không hỗ trợ.</summary>
public sealed class InputDocumentGuardrail : IDocumentAgentGuardrail
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm", ".doc", ".rtf", ".odt" };

    public string Name => "input_document";

    public ValueTask<AgentGuardrailDecision> EvaluateAsync(
        DocumentAgentGuardrailContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = context.Request.InputPath;
        if (string.IsNullOrWhiteSpace(path))
            return ValueTask.FromResult(AgentGuardrailDecision.Block("input_missing", "Chưa có file đầu vào."));
        if (!File.Exists(path))
            return ValueTask.FromResult(AgentGuardrailDecision.Block(
                "input_not_found", $"Không tìm thấy file: {Path.GetFileName(path)}"));
        if (!SupportedExtensions.Contains(Path.GetExtension(path)))
            return ValueTask.FromResult(AgentGuardrailDecision.Block(
                "input_unsupported", $"Định dạng không được hỗ trợ: {Path.GetExtension(path)}"));

        return ValueTask.FromResult(AgentGuardrailDecision.Pass(
            "input_valid", $"Đầu vào hợp lệ: {Path.GetFileName(path)}"));
    }
}

/// <summary>
/// Không cho phép tool gửi dữ liệu ra dịch vụ bên ngoài chỉ vì server đã có credential.
/// Caller phải bật consent ở đúng run đang thực thi.
/// </summary>
public sealed class ExternalDataTransferGuardrail : IDocumentAgentGuardrail
{
    public string Name => "external_data_transfer";

    public ValueTask<AgentGuardrailDecision> EvaluateAsync(
        DocumentAgentGuardrailContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (context.Tool.SendsDataExternally && !context.Request.AllowExternalDataTransfer)
            return ValueTask.FromResult(AgentGuardrailDecision.Block(
                "external_data_not_approved",
                "Run dùng suy luận từ xa nhưng chưa được phép gửi nội dung tài liệu ra ngoài."));

        return ValueTask.FromResult(AgentGuardrailDecision.Pass(
            context.Tool.SendsDataExternally ? "external_data_approved" : "local_only",
            context.Tool.SendsDataExternally
                ? "Đã xác nhận cho phép suy luận từ xa cho run này."
                : "Run chỉ xử lý cục bộ."));
    }
}

public sealed class AgentRunBlockedException : InvalidOperationException
{
    public Guid RunId { get; }
    public string Guardrail { get; }
    public string Code { get; }
    public IReadOnlyList<AgentRunEvent> Trace { get; }

    public AgentRunBlockedException(
        Guid runId,
        string guardrail,
        AgentGuardrailDecision decision,
        IReadOnlyList<AgentRunEvent> trace)
        : base(decision.Message)
    {
        RunId = runId;
        Guardrail = guardrail;
        Code = decision.Code;
        Trace = trace;
    }
}
