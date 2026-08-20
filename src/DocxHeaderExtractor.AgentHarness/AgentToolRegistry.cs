namespace DocxHeaderExtractor.AgentHarness;

/// <summary>Bộ tool đã chọn cho đúng một run, kèm lý do chọn để ghi vào trace.</summary>
public sealed record AgentToolSelection(
    IDocumentExtractionTool Extraction,
    IDocumentActionTool? Action,
    string Rationale);

public interface IAgentToolRegistry
{
    /// <summary>Toàn bộ tool đã đăng ký — để host và test soi được bề mặt quyền của một harness.</summary>
    IReadOnlyList<AgentToolDescriptor> Descriptors { get; }

    AgentToolSelection Select(DocumentAgentRequest request);
}

/// <summary>
/// Chọn tool bằng luật của code, không phải bằng model.
/// <para>
/// Đây là điểm khác biệt cố ý so với agent tự định tuyến: model không được nhìn danh sách tool và
/// tự quyết gọi cái nào. Chọn tool ở đây quyết định dữ liệu có rời khỏi máy hay không và file có
/// bị ghi hay không — hai câu hỏi không nên phụ thuộc vào một chuỗi sinh ra từ phân phối xác suất.
/// Bù lại, lựa chọn được ghi vào trace kèm lý do nên vẫn kiểm tra lại được sau sự việc.
/// </para>
/// </summary>
public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private readonly IReadOnlyList<IDocumentExtractionTool> _extraction;
    private readonly IReadOnlyList<IDocumentActionTool> _actions;

    public AgentToolRegistry(IDocumentExtractionTool extraction, IDocumentActionTool? action = null)
        : this([extraction], action) { }

    public AgentToolRegistry(
        IEnumerable<IDocumentExtractionTool> extraction,
        IDocumentActionTool? action = null)
        : this(extraction, action is null ? [] : [action]) { }

    public AgentToolRegistry(
        IEnumerable<IDocumentExtractionTool> extraction,
        IEnumerable<IDocumentActionTool> actions)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(actions);
        _extraction = extraction.ToArray();
        if (_extraction.Count == 0)
            throw new ArgumentException("Cần ít nhất một tool phân tích.", nameof(extraction));
        _actions = actions.ToArray();
    }

    public IReadOnlyList<AgentToolDescriptor> Descriptors =>
        [.. _extraction.Select(t => t.Descriptor), .. _actions.Select(t => t.Descriptor)];

    public AgentToolSelection Select(DocumentAgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Ưu tiên tool chạy được với consent hiện có, rủi ro thấp trước; OrderBy của LINQ ổn định
        // nên các tool cùng mức rủi ro giữ nguyên thứ tự đăng ký của host.
        var permitted = _extraction
            .Where(t => !t.Descriptor.SendsDataExternally || request.AllowExternalDataTransfer)
            .OrderBy(t => t.Descriptor.Risk)
            .ToList();

        var extraction = permitted.FirstOrDefault() ?? _extraction[0];
        var action = request.WantsAction
            ? _actions.FirstOrDefault(a => a.CanExecute(request))
            : null;

        return new AgentToolSelection(extraction, action, Explain(request, extraction, permitted.Count));
    }

    private string Explain(DocumentAgentRequest request, IDocumentExtractionTool chosen, int permittedCount)
    {
        var reason = permittedCount == 0
            // Không tự hạ cấp sang tool khác cho "chạy được": im lặng đổi tool là cách nhanh nhất
            // để một run tưởng là cục bộ hoá ra đã gửi dữ liệu đi, hoặc ngược lại.
            ? "không tool nào hợp lệ với consent hiện tại nên giữ nguyên lựa chọn đầu để guardrail chặn đúng lý do"
            : _extraction.Count == 1
                ? "chỉ một tool phân tích được đăng ký"
                : $"{permittedCount}/{_extraction.Count} tool hợp lệ, lấy mức rủi ro thấp nhất";

        var wantsAction = request.WantsWriteback
            ? "ghi outline"
            : request.WantsKeyPackage
                ? "tạo key package"
                : null;
        var action = wantsAction is not null
            ? _actions.FirstOrDefault(a => a.CanExecute(request)) is { } chosenAction
                ? $" + {chosenAction.Descriptor.Name}"
                : $"; run yêu cầu {wantsAction} nhưng không có tool action phù hợp"
            : "; không hành động ghi";

        return $"{chosen.Descriptor.Name} " +
               $"({(chosen.Descriptor.SendsDataExternally ? "gửi dữ liệu ra ngoài" : "cục bộ")}, " +
               $"rủi ro {chosen.Descriptor.Risk}) — {reason}{action}.";
    }
}
