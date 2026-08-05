using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.AgentHarness;

public interface IDocumentExtractionTool : IDisposable
{
    AgentToolDescriptor Descriptor { get; }

    Task<DocumentOutline> ExecuteAsync(
        AgentToolInvocation invocation,
        CancellationToken ct = default);
}

/// <summary>
/// Adapter biến pipeline hiện tại thành một tool của harness. Pipeline vẫn là implementation
/// chi tiết; Web/CLI không gọi thẳng pipeline nữa.
/// </summary>
public sealed class PipelineDocumentExtractionTool : IDocumentExtractionTool
{
    private readonly HeaderExtractionPipeline _pipeline;
    private readonly IHeaderClassifier? _classifier;
    private readonly bool _ownsClassifier;

    public PipelineDocumentExtractionTool(PipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pipeline = new HeaderExtractionPipeline(options);
        Descriptor = Describe(options);
    }

    public PipelineDocumentExtractionTool(
        PipelineOptions options,
        IHeaderClassifier classifier,
        bool ownsClassifier = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(classifier);
        _pipeline = new HeaderExtractionPipeline(options, classifier);
        _classifier = classifier;
        _ownsClassifier = ownsClassifier;
        Descriptor = Describe(options);
    }

    public AgentToolDescriptor Descriptor { get; }

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
        return _pipeline.RunAsync(
            invocation.Request.InputPath,
            quarantine is { Count: > 0 } ? quarantine.ToHashSet() : null,
            ct);
    }

    private static AgentToolDescriptor Describe(PipelineOptions options)
    {
        // LM Studio bị khóa vào loopback nên vẫn là local processing. Chỉ OpenRouter chuyển
        // nội dung ra dịch vụ bên ngoài và cần consent theo từng run.
        var remote = !options.DisableLlm && options.Backend == InferenceBackend.OpenRouter;
        return new AgentToolDescriptor(
            "extract_document_headings",
            "Đọc cấu trúc Word, gọi classifier khi cần, dựng cây heading và áp precision gate.",
            remote ? AgentToolRisk.Medium : AgentToolRisk.Low,
            SendsDataExternally: remote,
            MutatesExternalState: false)
        {
            SupportsRepair = true,
        };
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        if (_ownsClassifier) _classifier?.Dispose();
    }
}
