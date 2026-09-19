using DocxHeaderExtractor.Application.Capabilities;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;
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
    private readonly AuthorityExtractionPipeline _docxLane;
    private readonly PdfCanonicalSourceExtractor _pdfLane;
    private readonly CanonicalExtractionDispatcher _dispatcher;
    private readonly IHeaderClassifier? _classifier;
    private readonly bool _ownsClassifier;

    public PipelineDocumentExtractionTool(PipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var factory = new HeaderClassifierFactory();
        _docxLane = new AuthorityExtractionPipeline(options, factory);
        _pdfLane = new PdfCanonicalSourceExtractor(options, factory);
        _dispatcher = Dispatch(_docxLane, _pdfLane);
        Descriptor = Describe(options, factory.SendsDataExternally);
    }

    public PipelineDocumentExtractionTool(PipelineOptions options, IHeaderClassifierFactory factory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        _docxLane = new AuthorityExtractionPipeline(options, factory);
        _pdfLane = new PdfCanonicalSourceExtractor(options, factory);
        _dispatcher = Dispatch(_docxLane, _pdfLane);
        Descriptor = Describe(options, factory.SendsDataExternally);
    }

    public PipelineDocumentExtractionTool(
        PipelineOptions options,
        IHeaderClassifier classifier,
        bool ownsClassifier = false,
        bool sendsDataExternally = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(classifier);
        _docxLane = new AuthorityExtractionPipeline(options, classifier, sendsDataExternally);
        // The same classifier instance both lanes use, owned by whoever handed it in. Creating a
        // second one here would open a second provider connection for one document.
        _pdfLane = new PdfCanonicalSourceExtractor(options, classifier, sendsDataExternally);
        _dispatcher = Dispatch(_docxLane, _pdfLane);
        _classifier = classifier;
        _ownsClassifier = ownsClassifier;
        Descriptor = Describe(options, sendsDataExternally);
    }

    private static CanonicalExtractionDispatcher Dispatch(
        AuthorityExtractionPipeline pipeline, PdfCanonicalSourceExtractor pdfLane) =>
        new(new DocxCanonicalSourceExtractor(pipeline), pdfLane);

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
        return ExecuteNormalizedAsync(invocation.Request.InputPath,
            quarantine is { Count: > 0 } ? quarantine.ToHashSet() : null, ct);
    }

    /// <summary>
    /// Routes the uploaded file to the lane that owns its format, and nowhere else.
    /// <para>
    /// The format is read from the bytes, not the extension, and the legacy conversion step runs
    /// only for a format that needs it. Before this, every upload was pushed through
    /// <c>EnsureDocx</c> first, which refuses a PDF on its extension - so the PDF lane existed,
    /// was tested, and could not be reached by the Web, CLI or MCP host at all. A lane no host can
    /// call is not a supported format.
    /// </para>
    /// </summary>
    private async Task<DocumentOutline> ExecuteNormalizedAsync(
        string inputPath,
        IReadOnlySet<int>? quarantine,
        CancellationToken ct)
    {
        // Already a format a lane owns, whatever it is called. Sending it through the converter
        // first would refuse it on its extension: a PDF, and equally a DOCX that someone named
        // ".pdf", both of which the lanes can read perfectly well from their bytes.
        if (UploadedSourceDetector.Detect(inputPath) != SourceType.Unknown)
            return await ExtractAsync(inputPath, quarantine, ct).ConfigureAwait(false);

        // Nothing a lane owns: .doc, .rtf and .odt become OOXML first, and the converted file is
        // what gets routed - so the lane still decides on bytes it can actually parse.
        var conversion = LegacyDocConverter.EnsureDocx(inputPath);
        try
        {
            return await ExtractAsync(conversion.Path, quarantine, ct).ConfigureAwait(false);
        }
        finally
        {
            LegacyDocConverter.Cleanup(conversion);
        }
    }

    private async Task<DocumentOutline> ExtractAsync(
        string path, IReadOnlySet<int>? quarantine, CancellationToken ct)
    {
        var execution = await _dispatcher.ExtractAsync(
                new AuthorityExtractionRequest(UploadedFile.FromLocalPath(path)), quarantine, ct)
            .ConfigureAwait(false);
        return execution.CompatibilityOutline;
    }

    private static CapabilityDescriptor Describe(PipelineOptions options, bool sendsDataExternally)
    {
        // LM Studio bị khóa vào loopback nên vẫn là local processing. OpenRouter (Internet) và
        // SGLang/vLLM (gateway LAN, không loopback) đều chuyển nội dung ra khỏi tiến trình này và
        // cần consent theo từng run — phải khớp với contract provenance của authority pipeline,
        // nếu không RunProvenanceValidator sẽ chặn với provenance_contradicts_descriptor.
        var remote = !options.DisableLlm && sendsDataExternally;
        // Pipeline ghi document view ra đĩa khi DumpXmlPath được đặt — đường ghi này không đi qua
        // IDocumentActionTool nên WritebackTargetGuardrail không thấy. Khai ra cả cờ lẫn đường dẫn
        // để ToolSideEffectPathGuardrail soi được, thay vì để harness hứa "chỉ đọc".
        var dump = options.DumpXmlPath;
        var writes = !string.IsNullOrWhiteSpace(dump);
        return new CapabilityDescriptor(
            "extract_document_headings",
            "Đọc cấu trúc tài liệu đã tải lên (DOCX hoặc PDF), gọi classifier khi cần, dựng cây heading và áp precision gate.",
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
        _docxLane.Dispose();
        _pdfLane.Dispose();
        if (_ownsClassifier) _classifier?.Dispose();
    }
}
