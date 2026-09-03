using DocxHeaderExtractor.Application.Capabilities;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;
using DocxHeaderExtractor.DocumentProcessing;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.AgentHarness;

public interface IDocumentExtractionTool : IDisposable
{
    CapabilityDescriptor Descriptor { get; }

    Task<DocumentOutline> ExecuteAsync(
        AgentToolInvocation invocation,
        CancellationToken ct = default);
}

/// <summary>
/// Adapter của canonical authority pipeline thành một tool của harness. Web/CLI/MCP dùng cùng
/// orchestrator; compatibility/evaluation callers are migrated separately from normal authority.
/// </summary>
public sealed class PipelineDocumentExtractionTool : IDocumentExtractionTool
{
    private readonly DocumentProcessingService _processing;
    private readonly IHeaderClassifier? _classifier;
    private readonly bool _ownsClassifier;

    public PipelineDocumentExtractionTool(PipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _processing = new DocumentProcessingService(
            new AuthorityExtractionPipeline(options, new HeaderClassifierFactory()));
        Descriptor = Describe(options);
    }

    public PipelineDocumentExtractionTool(
        PipelineOptions options,
        IHeaderClassifier classifier,
        bool ownsClassifier = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(classifier);
        _processing = new DocumentProcessingService(new AuthorityExtractionPipeline(options, classifier));
        _classifier = classifier;
        _ownsClassifier = ownsClassifier;
        Descriptor = Describe(options);
    }

    public CapabilityDescriptor Descriptor { get; }

    /// <summary>
    /// Lượt sửa cách ly các đoạn bị validator bác rồi chạy lại pipeline từ đầu. Không lọc kết quả
    /// cũ: cây, cấp, evidence và cổng precision đều được dựng lại trên tập ứng viên đã hẹp hơn,
    /// nên một mục bị gỡ không để lại cấp mồ côi trong cây.
    /// </summary>
    public Task<DocumentOutline> ExecuteAsync(
        AgentToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var quarantine = invocation.Feedback?.QuarantineIndexes;
        return _processing.ProcessStructureOnlyAsync(
            invocation.Request.InputPath,
            quarantine is { Count: > 0 } ? quarantine.ToHashSet() : null,
            ct);
    }

    private static CapabilityDescriptor Describe(PipelineOptions options)
    {
        // LM Studio bị khóa vào loopback nên vẫn là local processing. OpenRouter (Internet) và
        // SGLang/vLLM (gateway LAN, không loopback) đều chuyển nội dung ra khỏi tiến trình này và
        // cần consent theo từng run — phải khớp với contract provenance của authority pipeline,
        // nếu không RunProvenanceValidator sẽ chặn với provenance_contradicts_descriptor.
        var remote = !options.DisableLlm &&
            options.Backend is InferenceBackend.OpenRouter or InferenceBackend.Sglang;
        // Pipeline ghi document view ra đĩa khi DumpXmlPath được đặt — đường ghi này không đi qua
        // IDocumentActionTool nên WritebackTargetGuardrail không thấy. Khai ra cả cờ lẫn đường dẫn
        // để ToolSideEffectPathGuardrail soi được, thay vì để harness hứa "chỉ đọc".
        var dump = options.DumpXmlPath;
        var writes = !string.IsNullOrWhiteSpace(dump);
        return new CapabilityDescriptor(
            "extract_document_headings",
            "Đọc cấu trúc Word, gọi classifier khi cần, dựng cây heading và áp precision gate.",
            remote ? CapabilityRisk.Medium : CapabilityRisk.Low,
            SendsDataExternally: remote,
            MutatesExternalState: writes)
        {
            SupportsRepair = true,
            SideEffectPaths = writes ? [dump!] : [],
        };
    }

    public void Dispose()
    {
        _processing.Dispose();
        if (_ownsClassifier) _classifier?.Dispose();
    }
}
