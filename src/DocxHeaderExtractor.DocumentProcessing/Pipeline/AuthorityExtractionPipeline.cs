using System.Security.Cryptography;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The single normal extraction orchestrator. Source adapters may differ, but every accepted
/// heading crosses the same proposal, source-grounding, validation, structure, and product stages.
/// Legacy extraction is intentionally not used here.
/// </summary>
public sealed class AuthorityExtractionPipeline : IDisposable
{
    private readonly PipelineOptions _options;
    private readonly IHeaderClassifierFactory? _analystFactory;
    private readonly bool _classifierSendsDataExternally;
    private IHeaderClassifier? _analyst;
    private readonly bool _ownsAnalyst;

    public AuthorityExtractionPipeline(PipelineOptions options)
        : this(options, null, null) { }

    public AuthorityExtractionPipeline(PipelineOptions options, IHeaderClassifierFactory analystFactory)
        : this(options, null, analystFactory) { }

    public AuthorityExtractionPipeline(PipelineOptions options, IHeaderClassifier analyst)
        : this(options, analyst, null, false)
    {
    }

    public AuthorityExtractionPipeline(
        PipelineOptions options,
        IHeaderClassifier analyst,
        bool sendsDataExternally)
        : this(options, analyst, null, sendsDataExternally) { }

    private AuthorityExtractionPipeline(
        PipelineOptions options,
        IHeaderClassifier? analyst,
        IHeaderClassifierFactory? analystFactory,
        bool classifierSendsDataExternally = false)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _analyst = analyst;
        _analystFactory = analystFactory;
        _classifierSendsDataExternally = classifierSendsDataExternally || analystFactory?.SendsDataExternally == true;
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
        return execution.CompatibilityOutline;
    }

    public async Task<DocumentExtractionResult> RunDocumentAsync(
        string inputPath,
        CancellationToken ct = default) =>
        (await RunDocumentExecutionAsync(inputPath, null, ct)).Result;

    public async Task<DocumentExtractionResult> RunDocumentAsync(
        string inputPath,
        IReadOnlySet<int>? quarantinedIndexes,
        CancellationToken ct = default) =>
        (await RunDocumentExecutionAsync(inputPath, quarantinedIndexes, ct)).Result;

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
                $"AuthorityExtractionPipeline nhận đầu vào OOXML đã chuẩn hoá (.docx/.docm); " +
                $"tệp được tải lên được nhận dạng là {uploadedType}. " +
                "compatibility adapter phải chuyển đổi định dạng đời cũ trước khi gọi pipeline.");

        var started = Environment.TickCount64;
        var sourceDocument = new OpenXmlDocumentSource().Read(inputPath);
            var structuralFeatures = NumberingStyleFeatures.FromSourceDocument(sourceDocument);
            var derivedFeatures = new DocumentFeatureDeriver().Derive(sourceDocument);
            var policyState = DocxPolicyStateBuilder.Build(
                sourceDocument, structuralFeatures, derivedFeatures, _options.Extraction);
            var mode = DocumentModeClassifier.Measure(policyState.Paragraphs.Cast<IPolicyParagraph>().ToArray());
            var diagnostics = _options.EnableDocumentDiagnostics
                ? (_options.DocumentDiagnosticsAnalyzer ?? DocumentDiagnosticRunner.Analyze)(policyState, mode)
                : null;
            var analyst = _options.DisableLlm ? null : await GetAnalystAsync(ct);
            var authority = await CanonicalSemanticDocxAuthorityAdapter.RunAsync(
                policyState, mode, analyst, ct, replayCapture: _options.ReplayCapture);
            authority = ApplyStructuralQuarantine(authority, quarantinedIndexes);
            var audit = authority.Audit;
            const string route = "docx-canonical-vnext";
            var reason = authority.Reason;

            var product = new PdfProductOutput(FileSha256(inputPath), []);
            var structural = new StructuralMaterializationResult(
                new ValidatedStructure([]), new HashSet<string>(StringComparer.Ordinal), 0, 0);
            if (audit is not null)
            {
                var finalStructure = BuildFinalStructure(inputPath, audit, authority.Structure);
                var decisions = PdfOutputDecisionPolicy.Decide(finalStructure);
                product = PdfProductOutputSerializer.Serialize(finalStructure, decisions);
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
            if (audit is not null)
            {
                var sourceRepresentations = BuildSourceRepresentations(sourceCatalog, audit);
                var traceAudit = audit with { SourceRepresentations = sourceRepresentations };
                var traces = RouteOccurrenceTraceBuilder.Build(
                    sourceDocument.DocumentId,
                    FileSha256(inputPath),
                    sourceCatalog,
                    structural.Structure,
                    structural.EmittedElementIds,
                    traceAudit,
                    routeOwner: "DOCX_AUTHORITY_ROUTE");
                audit = traceAudit with { OccurrenceTraces = traces };
                authority = authority with { Audit = audit };
            }
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
            var compatibilityOutline = new DocumentOutline
            {
                File = Path.GetFileName(inputPath),
                ParagraphCount = sourceDocument.Paragraphs.Count,
                CandidateCount = audit?.CandidatesSelected ?? 0,
                Headings = headings,
                ProductOutput = product,
                ElapsedMs = Environment.TickCount64 - started,
                Model = analyst?.ModelName,
                DocumentMode = mode,
                DeterministicRoute = route,
                RouteAudit = audit,
                Diagnostics = diagnostics,
                DecisionAudit = null,
                Provenance = BuildProvenance(audit,
                    !_options.DisableLlm && (_analystFactory?.SendsDataExternally ?? _classifierSendsDataExternally)),
            };
            return new AuthorityPipelineExecutionResult(extractionResult, compatibilityOutline);
    }

    private async Task<IHeaderClassifier> GetAnalystAsync(CancellationToken ct)
    {
        if (_analyst is not null) return _analyst;
        if (_analystFactory is null)
            throw new InvalidOperationException(
                "Inference provider factory chưa được đăng ký ở composition root.");
        _analyst = await _analystFactory.CreateAsync(_options, ct);
        return _analyst;
    }

    internal static PdfFinalStructure BuildFinalStructure(string docxPath, RouteExecutionAudit audit,
        ValidatedStructure structure)
    {
        return CanonicalProjectionBoundary.ProjectPdfFinalStructure(
            FileSha256(docxPath), audit, structure);
    }

    private static IReadOnlyList<RouteSourceRepresentation> BuildSourceRepresentations(
        DocumentSourceCatalog sourceCatalog,
        RouteExecutionAudit audit)
    {
        var candidateIds = audit.CandidateBlocks
            .Select(block => block.Id)
            .ToHashSet(StringComparer.Ordinal);
        return sourceCatalog.Units
            .Select(unit => new RouteSourceRepresentation(
                unit.SourceId,
                unit.SourceId,
                "DOCX_SOURCE_PARAGRAPH",
                candidateIds.Contains(unit.SourceId) ? unit.SourceId : null,
                "PARSER_OWNED_LINEAGE"))
            .ToArray();
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
                AlignedBlockIds = audit.AlignedBlockIds.Where(id => !removedSourceIds.Contains(id)).ToArray(),
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

    internal static OutlineRunProvenance BuildProvenance(RouteExecutionAudit? audit,
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
        passes.Add(new("source-facts", 1, audit.CandidatesAvailable, false));
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
