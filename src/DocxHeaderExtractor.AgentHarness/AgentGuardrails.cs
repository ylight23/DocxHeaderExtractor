namespace DocxHeaderExtractor.AgentHarness;

public sealed record AgentGuardrailDecision(bool Allowed, string Code, string Message)
{
    public static AgentGuardrailDecision Pass(string code, string message) => new(true, code, message);
    public static AgentGuardrailDecision Block(string code, string message) => new(false, code, message);
}

public sealed record DocumentAgentGuardrailContext(
    DocumentAgentRequest Request,
    AgentToolDescriptor Tool,
    AgentToolDescriptor? ActionTool = null);

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

/// <summary>
/// Kiểm tra đích ghi TRƯỚC khi tốn một lượt suy luận, và chặn mọi cách ghi đè ngoài ý muốn.
/// Quan trọng nhất: đích không được trùng file nguồn — tài liệu gốc là source of truth, agent
/// không có quyền sửa nó.
/// </summary>
public sealed class WritebackTargetGuardrail : IDocumentAgentGuardrail
{
    private static readonly HashSet<string> WritableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm" };

    public string Name => "writeback_target";

    public ValueTask<AgentGuardrailDecision> EvaluateAsync(
        DocumentAgentGuardrailContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var request = context.Request;

        if (!request.WantsWriteback)
        {
            return ValueTask.FromResult(context.ActionTool is null
                ? AgentGuardrailDecision.Pass("read_only", "Run chỉ đọc, không có hành động ghi.")
                : AgentGuardrailDecision.Block(
                    "writeback_target_missing",
                    "Đã nạp tool ghi nhưng run không chỉ định đích; không suy đoán đường dẫn hộ caller."));
        }

        if (context.ActionTool is null)
            return ValueTask.FromResult(AgentGuardrailDecision.Block(
                "writeback_tool_not_configured",
                "Run yêu cầu ghi outline nhưng harness không được nạp tool ghi nào."));

        var target = Path.GetFullPath(request.WritebackTargetPath!);
        if (!WritableExtensions.Contains(Path.GetExtension(target)))
            return ValueTask.FromResult(AgentGuardrailDecision.Block(
                "writeback_extension_unsupported",
                $"Chỉ ghi được ra .docx/.docm, nhận: {Path.GetExtension(target)}"));

        if (File.Exists(request.InputPath) &&
            string.Equals(Path.GetFullPath(request.InputPath), target, StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(AgentGuardrailDecision.Block(
                "writeback_overwrites_source",
                "Đích ghi trùng tài liệu nguồn; agent không được sửa file gốc."));

        if (File.Exists(target) && !request.AllowWritebackOverwrite)
            return ValueTask.FromResult(AgentGuardrailDecision.Block(
                "writeback_target_exists",
                $"File đích đã tồn tại và run chưa cho phép ghi đè: {Path.GetFileName(target)}"));

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            return ValueTask.FromResult(AgentGuardrailDecision.Block(
                "writeback_directory_missing", $"Thư mục đích không tồn tại: {directory}"));

        return ValueTask.FromResult(AgentGuardrailDecision.Pass(
            "writeback_target_valid", $"Đích ghi hợp lệ: {Path.GetFileName(target)}"));
    }
}

/// <summary>
/// Soi các đường ghi phụ mà tool tự khai (<see cref="AgentToolDescriptor.SideEffectPaths"/>), áp
/// đúng hai chốt như đích writeback: không được trùng tài liệu nguồn, và thư mục đích phải có sẵn.
/// <para>
/// Lý do tồn tại: pipeline ghi document view ra <c>DumpXmlPath</c> ngay giữa lượt chạy, không đi
/// qua <c>IDocumentActionTool</c> nên <see cref="WritebackTargetGuardrail"/> không hề thấy. Chốt
/// "agent không sửa file gốc" mà thủng ở một đường ghi thì nó không còn là chốt.
/// </para>
/// </summary>
public sealed class ToolSideEffectPathGuardrail : IDocumentAgentGuardrail
{
    public string Name => "tool_side_effect_paths";

    public ValueTask<AgentGuardrailDecision> EvaluateAsync(
        DocumentAgentGuardrailContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var paths = context.Tool.SideEffectPaths
            .Concat(context.ActionTool?.SideEffectPaths ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        if (paths.Length == 0)
            return ValueTask.FromResult(AgentGuardrailDecision.Pass(
                "no_side_effect_paths", "Tool không khai đường ghi phụ nào."));

        var source = File.Exists(context.Request.InputPath)
            ? Path.GetFullPath(context.Request.InputPath)
            : null;

        foreach (var raw in paths)
        {
            var full = Path.GetFullPath(raw);
            if (source is not null && string.Equals(full, source, StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult(AgentGuardrailDecision.Block(
                    "side_effect_overwrites_source",
                    $"Tool khai sẽ ghi đè chính tài liệu nguồn: {Path.GetFileName(full)}"));

            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                return ValueTask.FromResult(AgentGuardrailDecision.Block(
                    "side_effect_directory_missing",
                    $"Thư mục cho đường ghi phụ không tồn tại: {directory}"));
        }

        return ValueTask.FromResult(AgentGuardrailDecision.Pass(
            "side_effect_paths_valid",
            $"{paths.Length} đường ghi phụ hợp lệ: {string.Join(", ", paths.Select(Path.GetFileName))}"));
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
