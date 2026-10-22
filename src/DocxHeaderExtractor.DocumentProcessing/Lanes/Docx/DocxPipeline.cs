using DocxHeaderExtractor.Sources.Snapshot;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.Sources.Detect;
using DocxHeaderExtractor.Contracts.Inference;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Structure;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.DocumentProcessing.Lanes.Scan;
using DocxHeaderExtractor.DocumentProcessing.Harness;

namespace DocxHeaderExtractor.DocumentProcessing.Lanes.Docx;

/// <summary>
/// The single normal extraction orchestrator. Source adapters may differ, but every accepted
/// heading crosses the same proposal, source-grounding, validation, structure, and product stages.
/// Earlier direct-extraction paths are intentionally not used here.
/// </summary>
public sealed class DocxPipeline : IDisposable
{
    private readonly PipelineOptions _options;
    private readonly IInferenceTransportFactory? _transportFactory;
    private readonly IHeadingSelector? _selector;
    private readonly IScanHeadingSelector? _scanSelector;
    private readonly bool _transportSendsDataExternally;
    private IInferenceTransport? _transport;
    private readonly bool _ownsTransport;

    /// <summary>
    /// The DOCX route's authority is the V6 heading selector: without one, a run that needs the model refuses (DOCX_HEADING_SELECTOR_REQUIRED);
    /// with the model disabled the route returns the source only. The transport (or the factory that makes it) must be the authorized agent route:
    /// it carries the provider key the selector uses and serves the shared placement stage.
    /// </summary>
    public DocxPipeline(PipelineOptions options, IInferenceTransportFactory transportFactory, IHeadingSelector? selector = null, IScanHeadingSelector? scanSelector = null)
        : this(options, null, transportFactory, selector, scanSelector: scanSelector) { }

    public DocxPipeline(PipelineOptions options, IInferenceTransport transport, IHeadingSelector? selector = null, IScanHeadingSelector? scanSelector = null)
        : this(options, transport, null, selector, false, scanSelector)
    {
    }

    public DocxPipeline(
        PipelineOptions options,
        IInferenceTransport transport,
        bool sendsDataExternally,
        IHeadingSelector? selector = null)
        : this(options, transport, null, selector, sendsDataExternally) { }

    private DocxPipeline(
        PipelineOptions options,
        IInferenceTransport? transport,
        IInferenceTransportFactory? transportFactory,
        IHeadingSelector? selector,
        bool transportSendsDataExternally = false, IScanHeadingSelector? scanSelector = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transport = transport;
        _transportFactory = transportFactory;
        _selector = selector;
        _scanSelector = scanSelector ?? transportFactory?.CreateScanHeadingSelector();
        _transportSendsDataExternally = transportSendsDataExternally || transportFactory?.SendsDataExternally == true;
        _ownsTransport = transportFactory is not null || transport is null;
    }

    public Task<RunResult> RunDocumentExecutionAsync(
        string inputPath,
        CancellationToken ct = default) =>
        ExecuteDocumentAsync(inputPath, ct);

    private async Task<RunResult> ExecuteDocumentAsync(
        string inputPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        // The uploaded file decides everything that follows, and it is identified by its bytes.
        // Naming a PDF ".docx" must fail here rather than inside an OOXML reader.
        var uploadedType = SourceDetector.Detect(inputPath);
        if (uploadedType != SourceType.Docx)
            throw new NotSupportedException(
                $"DocxPipeline nhận đầu vào OOXML đã chuẩn hoá (.docx/.docm); " +
                $"tệp được tải lên được nhận dạng là {uploadedType}. " +
                "source adapter phải chuyển đổi định dạng không phải OOXML trước khi gọi pipeline.");

        var started = Environment.TickCount64;
        ct.ThrowIfCancellationRequested();
        if (DocxHeaderExtractor.Sources.Docx.DocxSourceIntegrity.Read(inputPath).Kind == DocxHeaderExtractor.Sources.Docx.DocxSourceKind.ScannedImageOnly)
            return await ScanLane.RunAsync(inputPath, _options, _options.DisableLlm ? null : await GetTransportAsync(ct),
                _scanSelector, _transportSendsDataExternally, ct).ConfigureAwait(false);
        var sourceDocument = DocxHeaderExtractor.Sources.Docx.DocxSourceCache.ReadOrLoad(inputPath, _options.SourceCacheRoot, _options.Extraction);
        if (_selector is DocxHeaderExtractor.Contracts.Routing.IPrimedDocumentSelector primed) primed.PrimeDocument(inputPath, _options.Extraction, sourceDocument);
        var transport = _options.DisableLlm ? null : await GetTransportAsync(ct);
        var authority = await DocxLane.RunAsync(
            sourceDocument, transport, _selector, LaneOptions.Default, ct);
        var audit = authority.Audit;
        const string route = DocxLane.RouteId;

        // Without an audit (an empty source, or the model disabled) the route returns the source only.
        var structure = audit is null ? StructureFactory.Create([]) : authority.Structure;
        IReadOnlySet<string> emittedIds = audit is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : authority.EmittedElementIds ?? structure.Elements.Select(element => element.Id).ToHashSet(StringComparer.Ordinal);
        var execution = ExtractionOutput.Build(
            new ExtractionOutput.Lane(
                inputPath,
                new DocumentIdentity(sourceDocument.DocumentId, Path.GetFileName(inputPath), sourceDocument.SourceKind, inputPath),
                new DocumentExtractionProvenance(
                    route,
                    "docx-source-document",
                    _options.DisableLlm ? 0 : audit?.HeadingProviderCalls ?? audit?.RawModelResponses.Count ?? 0),
                ProductSha256: null,
                ParagraphCount: sourceDocument.Paragraphs.Count,
                SourceCount: audit?.SourceBlocksSelected ?? 0,
                Route: route,
                Model: transport?.ModelName,
                SentDataExternally: !_options.DisableLlm && (_transportFactory?.SendsDataExternally ?? _transportSendsDataExternally)),
            authority,
            structure,
            emittedIds,
            CatalogBuilder.FromSourceDocument(sourceDocument),
            _options,
            started);
        _options.Log?.Invoke($"Authority route {route}: validated={execution.Outline.Headings.Count}; {authority.Reason}");
        return execution;
    }

    private async Task<IInferenceTransport> GetTransportAsync(CancellationToken ct)
    {
        if (_transport is not null) return _transport;
        if (_transportFactory is null)
            throw new InvalidOperationException(
                "Inference provider factory chưa được đăng ký ở composition root.");
        _transport = await _transportFactory.CreateAgentRouteAsync(ct);
        return _transport;
    }

    public void Dispose()
    {
        if (_ownsTransport) _transport?.Dispose();
        _transport = null;
    }
}
