using DocxHeaderExtractor.DocumentProcessing.Source;
using DocxHeaderExtractor.DocumentProcessing.Provenance;
using System.Security.Cryptography;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.Core.Models;
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
    private readonly IInferenceTransportFactory? _analystFactory;
    private readonly bool _transportSendsDataExternally;
    private IInferenceTransport? _analyst;
    private readonly bool _ownsAnalyst;

    public DocxExtractionPipeline(PipelineOptions options)
        : this(options, null, null) { }

    public DocxExtractionPipeline(PipelineOptions options, IInferenceTransportFactory analystFactory)
        : this(options, null, analystFactory) { }

    public DocxExtractionPipeline(PipelineOptions options, IInferenceTransport analyst)
        : this(options, analyst, null, false)
    {
    }

    public DocxExtractionPipeline(
        PipelineOptions options,
        IInferenceTransport analyst,
        bool sendsDataExternally)
        : this(options, analyst, null, sendsDataExternally) { }

    private DocxExtractionPipeline(
        PipelineOptions options,
        IInferenceTransport? analyst,
        IInferenceTransportFactory? analystFactory,
        bool transportSendsDataExternally = false)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _analyst = analyst;
        _analystFactory = analystFactory;
        _transportSendsDataExternally = transportSendsDataExternally || analystFactory?.SendsDataExternally == true;
        _ownsAnalyst = analystFactory is not null || analyst is null;
    }

    public Task<DocumentOutline> RunAsync(string inputPath, CancellationToken ct = default) =>
        RunAsync(inputPath, null, ct);

    public async Task<DocumentOutline> RunAsync(
        string inputPath,
        IReadOnlySet<int>? quarantinedIndexes,
        CancellationToken ct = default)
    {
        var execution = await ExecuteDocumentAsync(inputPath, quarantinedIndexes, ct);
        return execution.Outline;
    }

    public Task<AuthorityPipelineExecutionResult> RunDocumentExecutionAsync(
        string inputPath,
        IReadOnlySet<int>? quarantinedIndexes = null,
        CancellationToken ct = default) =>
        ExecuteDocumentAsync(inputPath, quarantinedIndexes, ct);

    private async Task<AuthorityPipelineExecutionResult> ExecuteDocumentAsync(
        string inputPath,
        IReadOnlySet<int>? quarantinedIndexes,
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
        var sourceDocument = new OpenXmlDocumentSource().Read(inputPath);
            var analyst = _options.DisableLlm ? null : await GetAnalystAsync(ct);
            var authority = await DocxHeadingPipeline.RunAsync(
                sourceDocument, analyst, ct);
            authority = ApplyStructuralQuarantine(authority, quarantinedIndexes);
            var audit = authority.Audit;
            const string route = "docx-canonical-vnext";
            var reason = authority.Reason;

            var product = new DocumentProductOutput(FileSha256(inputPath), []);
            var structural = new StructuralMaterializationResult(
                new ValidatedStructure([]), new HashSet<string>(StringComparer.Ordinal), 0, 0);
            if (audit is not null)
            {
                var finalStructure = BuildFinalStructure(inputPath, audit, authority.Structure);
                var decisions = OutputDecisionPolicy.Decide(finalStructure);
                product = DocumentProductOutputProjector.Serialize(finalStructure, decisions);
                structural = new StructuralMaterializationResult(
                    authority.Structure,
                    authority.EmittedElementIds ?? authority.Structure.Elements
                        .Select(element => element.Id).ToHashSet(StringComparer.Ordinal),
                    0,
                    0);
            }

            var headings = HeadingOutlineProjection.Project(
                structural.Structure, structural.EmittedElementIds);
            _options.Log?.Invoke($"Authority route {route}: validated={headings.Count}; {reason}");
            var sourceCatalog = DocumentSourceCatalogBuilder.FromSourceDocument(sourceDocument);
            var sections = StructuralSectionProjection.Project(structural.Structure, sourceCatalog);
            var chunks = SectionChunkProjection.Project(
                sections, sourceCatalog, structural.Structure,
                new DocumentChunkingPolicy(Math.Max(1, _options.Chunking.TokenBudget)));
            var extractionResult = new DocumentExtractionResult(
                new DocumentIdentity(
                    sourceDocument.DocumentId,
                    Path.GetFileName(inputPath),
                    sourceDocument.SourceKind,
                    inputPath),
                sourceCatalog,
                structural.Structure,
                sections,
                chunks,
                new DocumentExtractionProvenance(
                    route,
                    "docx-source-document",
                    _options.DisableLlm ? 0 : audit?.RawAnalystResponses.Count ?? 0));
            var outline = new DocumentOutline
            {
                File = Path.GetFileName(inputPath),
                ParagraphCount = sourceDocument.Paragraphs.Count,
                SourceCount = audit?.SourceBlocksSelected ?? 0,
                Headings = headings,
                ProductOutput = product,
                ElapsedMs = Environment.TickCount64 - started,
                Model = analyst?.ModelName,
                DeterministicRoute = route,
                RouteAudit = audit,
                Provenance = BuildProvenance(audit,
                    !_options.DisableLlm && (_analystFactory?.SendsDataExternally ?? _transportSendsDataExternally)),
            };
            return new AuthorityPipelineExecutionResult(extractionResult, outline);
    }

    private async Task<IInferenceTransport> GetAnalystAsync(CancellationToken ct)
    {
        if (_analyst is not null) return _analyst;
        if (_analystFactory is null)
            throw new InvalidOperationException(
                "Inference provider factory chưa được đăng ký ở composition root.");
        _analyst = await _analystFactory.CreateAsync(_options, ct);
        return _analyst;
    }

    internal static CanonicalFinalStructure BuildFinalStructure(string docxPath, PipelineExecutionAudit audit,
        ValidatedStructure structure)
    {
        // Materializes only facts already validated upstream; resolves no identity, hierarchy or
        // provider work.
        return CanonicalFinalStructureProjection.Project(
            FileSha256(docxPath),
            audit.ValidatedStructures,
            audit.HierarchyFacts,
            CanonicalGrounding.FromValidatedStructure(structure));
    }

    internal static StructuralAuthorityResult ApplyStructuralQuarantine(
        StructuralAuthorityResult authority,
        IReadOnlySet<int>? quarantinedIndexes)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (quarantinedIndexes is null || quarantinedIndexes.Count == 0) return authority;

        var removedElements = authority.Structure.Elements
            .Where(element => element.Sources.Any(source => quarantinedIndexes.Contains(source.SourceOrdinal)))
            .ToArray();
        if (removedElements.Length == 0) return authority;

        var removedElementIds = removedElements.Select(element => element.Id).ToHashSet(StringComparer.Ordinal);
        var removedSourceIds = removedElements.SelectMany(element => element.Sources)
            .SelectMany(source => new[] { source.SourceId, source.StableId })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var remaining = authority.Structure.Elements
            .Where(element => !removedElementIds.Contains(element.Id))
            .ToArray();
        var emitted = (authority.EmittedElementIds ?? authority.Structure.Elements
                .Select(element => element.Id).ToHashSet(StringComparer.Ordinal))
            .Where(id => !removedElementIds.Contains(id))
            .ToHashSet(StringComparer.Ordinal);
        var audit = authority.Audit;
        if (audit is not null)
        {
            audit = audit with
            {
                ValidatedStructures = audit.ValidatedStructures.Where(item => !removedSourceIds.Contains(item.SourceId)).ToArray(),
                HierarchyFacts = audit.HierarchyFacts.Where(item => !removedSourceIds.Contains(item.Id)).ToArray(),
                GroundedBlockIds = audit.GroundedBlockIds.Where(id => !removedSourceIds.Contains(id)).ToArray(),
            };
        }

        var survivingRelations = authority.Structure.Relations
            .Where(relation => !removedElementIds.Contains(relation.FromId) &&
                !removedElementIds.Contains(relation.ToId))
            .Select(relation => new StructuralRelationProposal(
                relation.FromId, relation.ToId, relation.Type));
        return authority with
        {
            Structure = ValidatedStructure.FromElements(remaining, survivingRelations),
            Audit = audit,
            EmittedElementIds = emitted,
        };
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
        if (audit.HierarchyProposals.Count > 0)
            passes.Add(new("semantic-hierarchy", audit.HierarchyProposals.Count, audit.HierarchyProposals.Count, sentDataExternally));
        passes.Add(new("source-facts", 1, audit.SourceBlocksAvailable, false));
        passes.Add(new("proposal-validation", 1, audit.BlockDecisions.Count, false));
        passes.Add(new("deterministic-hierarchy", 1, audit.ValidatedStructures.Count, false));
        passes.Add(new("output-policy", 1, audit.ValidatedStructures.Count, false));
        return new OutlineRunProvenance(
            audit.SemanticLane is not null || audit.SpanLane is not null ? "configured-analyst" : "deterministic-ooxml",
            sentDataExternally,
            passes);
    }

    private static string FileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public void Dispose()
    {
        if (_ownsAnalyst) _analyst?.Dispose();
        _analyst = null;
    }
}
