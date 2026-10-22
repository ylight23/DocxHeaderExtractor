using DocxHeaderExtractor.Sources.Snapshot;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.Sources.Detect;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Structure;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.DocumentProcessing.Lanes.Scan;
using DocxHeaderExtractor.DocumentProcessing.Harness;

namespace DocxHeaderExtractor.DocumentProcessing.Lanes.Docx;

/// <summary>
/// The single normal extraction orchestrator. Source adapters may differ, but every accepted
/// heading crosses the same proposal, source-grounding, validation, structure, and product stages.
/// Earlier direct-extraction paths are intentionally not used here.
/// </summary>
public sealed class DocxPipeline
{
    private readonly PipelineOptions _options;
    private readonly IModelProvider _provider;
    private readonly HeadingAgents _agents;

    /// <summary>
    /// The DOCX route runs with its agents (<see cref="HeadingAgents"/>): the DOCX selector says which paragraphs are headings, the scan
    /// selector reads a scanned DOCX, and the placer proposes parents. A run that needs the model and lacks one refuses
    /// (DOCX_HEADING_SELECTOR_REQUIRED, HEADING_PLACER_REQUIRED); with the model disabled the route returns the source only. The provider gives
    /// the model route of a run that calls the model: the provider key the agents use.
    /// </summary>
    public DocxPipeline(PipelineOptions options, IModelProvider provider, HeadingAgents agents)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
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
            return await ScanLane.RunAsync(inputPath, _options, _options.DisableLlm ? null : _provider.CreateRoute(), _agents, ct).ConfigureAwait(false);
        var sourceDocument = DocxHeaderExtractor.Sources.Docx.DocxSourceCache.ReadOrLoad(inputPath, _options.SourceCacheRoot, _options.Extraction);
        if (_agents.Docx is IPrimedDocumentSelector primed) primed.PrimeDocument(inputPath, _options.Extraction, sourceDocument);
        var modelRoute = _options.DisableLlm ? null : _provider.CreateRoute();
        var authority = await DocxLane.RunAsync(
            sourceDocument, modelRoute, _agents.Docx, _agents.Placer, LaneOptions.Default, ct);
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
                Model: modelRoute?.Model,
                SentDataExternally: modelRoute is not null),
            authority,
            structure,
            emittedIds,
            CatalogBuilder.FromSourceDocument(sourceDocument),
            _options,
            started);
        _options.Log?.Invoke($"Authority route {route}: validated={execution.Outline.Headings.Count}; {authority.Reason}");
        return execution;
    }
}
