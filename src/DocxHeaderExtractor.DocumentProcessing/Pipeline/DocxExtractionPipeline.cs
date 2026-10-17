using DocxHeaderExtractor.DocumentProcessing.Source;
using System.Security.Cryptography;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Semantics.Validation;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The single normal extraction orchestrator. Source adapters may differ, but every accepted
/// heading crosses the same proposal, source-grounding, validation, structure, and product stages.
/// Earlier direct-extraction paths are intentionally not used here.
/// </summary>
public sealed class DocxExtractionPipeline : IDisposable
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
    public DocxExtractionPipeline(PipelineOptions options, IInferenceTransportFactory transportFactory, IHeadingSelector? selector = null, IScanHeadingSelector? scanSelector = null)
        : this(options, null, transportFactory, selector, scanSelector: scanSelector) { }

    public DocxExtractionPipeline(PipelineOptions options, IInferenceTransport transport, IHeadingSelector? selector = null, IScanHeadingSelector? scanSelector = null)
        : this(options, transport, null, selector, false, scanSelector)
    {
    }

    public DocxExtractionPipeline(
        PipelineOptions options,
        IInferenceTransport transport,
        bool sendsDataExternally,
        IHeadingSelector? selector = null)
        : this(options, transport, null, selector, sendsDataExternally) { }

    private DocxExtractionPipeline(
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

    public Task<DocumentExtractionExecutionResult> RunDocumentExecutionAsync(
        string inputPath,
        CancellationToken ct = default) =>
        ExecuteDocumentAsync(inputPath, ct);

    private async Task<DocumentExtractionExecutionResult> ExecuteDocumentAsync(
        string inputPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        // The uploaded file decides everything that follows, and it is identified by its bytes.
        // Naming a PDF ".docx" must fail here rather than inside an OOXML reader.
        var uploadedType = UploadedSourceDetector.Detect(inputPath);
        if (uploadedType != SourceType.Docx)
            throw new NotSupportedException(
                $"DocxExtractionPipeline nhận đầu vào OOXML đã chuẩn hoá (.docx/.docm); " +
                $"tệp được tải lên được nhận dạng là {uploadedType}. " +
                "source adapter phải chuyển đổi định dạng không phải OOXML trước khi gọi pipeline.");

        var started = Environment.TickCount64;
        ct.ThrowIfCancellationRequested();
        if (DocxHeaderExtractor.DocumentProcessing.Source.Docx.DocxSourceIntegrity.Read(inputPath).Kind == DocxHeaderExtractor.DocumentProcessing.Source.Docx.DocxSourceKind.ScannedImageOnly)
            return await ScanExtractionPipeline.RunAsync(inputPath, _options, _options.DisableLlm ? null : await GetTransportAsync(ct),
                _scanSelector, _transportSendsDataExternally, ct).ConfigureAwait(false);
        var sourceDocument = new OpenXmlDocumentSource(_options.Extraction).Read(inputPath);
            var transport = _options.DisableLlm ? null : await GetTransportAsync(ct);
            var authority = await DocxHeadingPipeline.RunAsync(
                sourceDocument, transport, _selector, SemanticLaneOptions.Default, ct);
            var audit = authority.Audit;
            const string route = DocxHeadingPipeline.RouteId;
            var reason = authority.Reason;

            var product = new DocumentProductOutput(FileSha256(inputPath), []);
            var structure = ValidatedStructureFactory.Create([]);
            IReadOnlySet<string> emittedIds = new HashSet<string>(StringComparer.Ordinal);
            if (audit is not null)
            {
                var finalStructure = BuildFinalStructure(inputPath, audit, authority.Structure, authority.ProjectionContext);
                var decisions = OutputDecisionPolicy.Decide(finalStructure);
                product = DocumentProductOutputProjector.Serialize(finalStructure, decisions);
                structure = authority.Structure;
                emittedIds = authority.EmittedElementIds ?? structure.Elements
                    .Select(element => element.Id).ToHashSet(StringComparer.Ordinal);
            }

            var sourceCatalog = DocumentSourceCatalogBuilder.FromSourceDocument(sourceDocument);
            var headings = HeadingOutlineProjection.Project(
                structure, emittedIds, authority.ProjectionContext, sourceCatalog);
            _options.Log?.Invoke($"Authority route {route}: validated={headings.Count}; {reason}");
            var sections = StructuralSectionProjection.Project(structure, sourceCatalog);
            var chunks = SectionChunkProjection.Project(
                sections, sourceCatalog, structure,
                new DocumentChunkingPolicy(Math.Max(1, _options.Chunking.TokenBudget)));
            var extractionResult = new DocumentExtractionResult(
                new DocumentIdentity(
                    sourceDocument.DocumentId,
                    Path.GetFileName(inputPath),
                    sourceDocument.SourceKind,
                    inputPath),
                sourceCatalog,
                structure,
                sections,
                chunks,
                new DocumentExtractionProvenance(
                    route,
                    "docx-source-document",
                    _options.DisableLlm ? 0 : audit?.HeadingProviderCalls ?? audit?.RawModelResponses.Count ?? 0));
            var outline = new DocumentOutline
            {
                File = Path.GetFileName(inputPath),
                ParagraphCount = sourceDocument.Paragraphs.Count,
                SourceCount = audit?.SourceBlocksSelected ?? 0,
                Headings = headings,
                ProductOutput = product,
                ElapsedMs = Environment.TickCount64 - started,
                Model = transport?.ModelName,
                DeterministicRoute = route,
                RouteAudit = audit,
                Provenance = BuildProvenance(audit,
                    !_options.DisableLlm && (_transportFactory?.SendsDataExternally ?? _transportSendsDataExternally)),
            };
            return new DocumentExtractionExecutionResult(extractionResult, outline) { HeadingPipeline = authority };
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

    internal static CanonicalFinalStructure BuildFinalStructure(string docxPath, PipelineExecutionAudit audit,
        ValidatedStructure structure, HeadingProjectionContext? projectionContext = null)
    {
        // Materializes only facts already validated upstream; resolves no identity, hierarchy or
        // provider work.
        return CanonicalFinalStructureProjection.Project(
            FileSha256(docxPath),
            audit.ValidatedStructures,
            audit.HierarchyFacts,
            CanonicalGroundingProjection.Project(structure, projectionContext));
    }

    internal static OutlineRunProvenance BuildProvenance(PipelineExecutionAudit? audit,
        bool sentDataExternally)
    {
        if (audit is null)
            return new OutlineRunProvenance("deterministic-ooxml", false, []);

        var passes = new List<OutlinePass>();
        if (audit.SemanticLane is { Scheduled: > 0, Completed: > 0 })
            passes.Add(new("semantic-role", audit.SemanticLane.Completed, audit.SemanticLane.Scheduled, sentDataExternally));
        if (audit.SpanLane is { Scheduled: > 0, Completed: > 0 })
            passes.Add(new("heading-span", audit.SpanLane.Completed, audit.SpanLane.Scheduled, sentDataExternally));
        passes.Add(new("source-facts", 1, audit.SourceBlocksAvailable, false));
        passes.Add(new("proposal-validation", 1, audit.BlockDecisions.Count, false));
        passes.Add(new("deterministic-hierarchy", 1, audit.ValidatedStructures.Count, false));
        passes.Add(new("output-policy", 1, audit.ValidatedStructures.Count, false));
        return new OutlineRunProvenance(
            audit.SemanticLane is not null || audit.SpanLane is not null ? "configured-route" : "deterministic-ooxml",
            sentDataExternally,
            passes);
    }

    private static string FileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public void Dispose()
    {
        if (_ownsTransport) _transport?.Dispose();
        _transport = null;
    }
}
