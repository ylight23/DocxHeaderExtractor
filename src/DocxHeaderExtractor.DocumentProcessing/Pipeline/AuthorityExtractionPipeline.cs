using System.Security.Cryptography;
using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Application.Routing;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Vision;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// The single normal extraction orchestrator. Source adapters may differ, but every accepted
/// heading crosses the same proposal, source-grounding, validation, structure, and product stages.
/// Legacy extraction is intentionally not used here.
/// </summary>
public sealed class AuthorityExtractionPipeline : IDisposable
{
    private readonly PipelineOptions _options;
    private readonly IAuthorityRoutePolicy _routePolicy;
    private readonly IHeaderClassifierFactory? _analystFactory;
    private IHeaderClassifier? _analyst;
    private readonly bool _ownsAnalyst;

    public AuthorityExtractionPipeline(PipelineOptions options)
        : this(options, new DefaultAuthorityRoutePolicy(), null, null) { }

    public AuthorityExtractionPipeline(PipelineOptions options, IHeaderClassifierFactory analystFactory)
        : this(options, new DefaultAuthorityRoutePolicy(), null, analystFactory) { }

    public AuthorityExtractionPipeline(PipelineOptions options, IHeaderClassifier analyst)
        : this(options, new DefaultAuthorityRoutePolicy(), analyst, null)
    {
    }

    public AuthorityExtractionPipeline(PipelineOptions options, IAuthorityRoutePolicy routePolicy)
        : this(options, routePolicy, null, null) { }

    public AuthorityExtractionPipeline(
        PipelineOptions options,
        IAuthorityRoutePolicy routePolicy,
        IHeaderClassifier analyst)
        : this(options, routePolicy, analyst, null) { }

    private AuthorityExtractionPipeline(
        PipelineOptions options,
        IAuthorityRoutePolicy routePolicy,
        IHeaderClassifier? analyst,
        IHeaderClassifierFactory? analystFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _routePolicy = routePolicy ?? throw new ArgumentNullException(nameof(routePolicy));
        _analyst = analyst;
        _analystFactory = analystFactory;
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
        var extension = Path.GetExtension(inputPath);
        if (!string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".docm", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "AuthorityExtractionPipeline nhận đầu vào OOXML đã chuẩn hoá (.docx/.docm); " +
                "compatibility adapter phải chuyển đổi định dạng đời cũ trước khi gọi pipeline.");

        var started = Environment.TickCount64;
        var sourceDocument = new OpenXmlDocumentSource().Read(inputPath);
            var structuralFeatures = NumberingStyleFeatures.FromSourceDocument(sourceDocument);
            var derivedFeatures = new DocumentFeatureDeriver().Derive(sourceDocument);
            var policyState = DocxPolicyStateBuilder.Build(
                sourceDocument, structuralFeatures, derivedFeatures, _options.Extraction);
            var mode = DocumentModeClassifier.Measure(policyState.Paragraphs.Cast<IPolicyParagraph>().ToArray());
            var diagnostics = DocumentDiagnosticRunner.Analyze(policyState, mode);
            var analyst = _options.DisableLlm ? null : await GetAnalystAsync(ct);
            var pdf = PdfTextbookOutline.FindSiblingPdf(inputPath);
            StructuralAuthorityResult authority;
            RouteExecutionAudit? audit;
            string route;
            string reason;
            DocumentSourceCatalog? routeSourceCatalog = null;
            PdfFinalStructure? routeFinalStructure = null;
            IReadOnlyList<PdfOutputDecision>? routeOutputDecisions = null;
            var authorityRoute = _routePolicy.Decide(new SourceCapabilities(
                HasDocx: true,
                HasPdf: !string.IsNullOrWhiteSpace(pdf),
                AnalystAvailable: analyst is not null));
            switch (authorityRoute)
            {
                case AuthorityRoute.PdfAuthority:
                {
                    await using var productionCheckpoint = ProductionCheckpointScope.Create();
                    await using var checkpoint = new PdfStageCheckpoint(
                        productionCheckpoint.CheckpointPath, resume: false, Path.GetFileName(pdf));
                    PdfTextbookOutlineResult result;
                    try
                    {
                        result = await PdfLayoutEvidenceOutline.TryBuildBroadAuditWithAnalystCoreAsync(
                            inputPath, policyState, analyst!,
                            maximumAnalystBlocks: _options.PdfFirstAnalystBlocks,
                            includeAllVisualStyles: true,
                            includeSupplementCandidates: true,
                            maximumVisualRegions: _options.PdfFirstVisualRegions,
                            visualAnalyst: null,
                            ct: ct,
                            resume: false,
                            checkpointInstance: checkpoint);
                    }
                    finally
                    {
                        await checkpoint.StopAcceptingWritesAndDrainAsync();
                    }
                    authority = result.Authority;
                    routeFinalStructure = result.FinalStructure;
                    routeOutputDecisions = result.OutputDecisions;
                    routeSourceCatalog = result.SourceCatalog;
                    authority = ApplyStructuralQuarantine(authority, quarantinedIndexes);
                    route = "pdf-authority-v1";
                    reason = result.Reason;
                    audit = authority.Audit;
                    break;
                }
                case AuthorityRoute.DocxAuthority:
                {
                    authority = await DocxAuthorityPipeline.RunAsync(policyState, mode, analyst, ct);
                    authority = ApplyStructuralQuarantine(authority, quarantinedIndexes);
                    audit = authority.Audit;
                    route = "docx-authority-v1";
                    reason = authority.Reason;
                    break;
                }
                case AuthorityRoute.Unsupported:
                    throw new InvalidOperationException("Normal authority route requires a DOCX source.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(authorityRoute), authorityRoute, null);
            }

            var product = new PdfProductOutput(FileSha256(inputPath), []);
            var structural = new StructuralMaterializationResult(
                new ValidatedStructure([]), new HashSet<string>(StringComparer.Ordinal), 0, 0);
            if (audit is not null)
            {
                var isNativePdf = routeFinalStructure is not null;
                var finalStructure = isNativePdf
                    ? routeFinalStructure!
                    : BuildFinalStructure(inputPath, audit, authority.Structure);
                var decisions = isNativePdf && routeOutputDecisions is not null
                    ? routeOutputDecisions!
                    : PdfOutputDecisionPolicy.Decide(finalStructure);
                if (isNativePdf && quarantinedIndexes is { Count: > 0 })
                    decisions = FilterPdfOutputDecisions(decisions, authority.Structure);
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
            var sourceCatalog = authorityRoute == AuthorityRoute.PdfAuthority
                ? routeSourceCatalog ?? throw new InvalidOperationException("pdf-source-catalog-missing")
                : DocumentSourceCatalogBuilder.FromSourceDocument(sourceDocument);
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
                    route == "pdf-authority-v1" ? "pdf-parser-facts-plus-canonical-grounding" : "docx-source-document",
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
                    !_options.DisableLlm && _options.Backend is InferenceBackend.OpenRouter or InferenceBackend.Sglang),
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

    private static PdfFinalStructure BuildFinalStructure(string docxPath, RouteExecutionAudit audit,
        ValidatedStructure structure)
    {
        return PdfFinalStructureProjection.Project(
            FileSha256(docxPath), audit.ValidatedStructures, audit.HierarchyFacts,
            PdfCanonicalGrounding.FromValidatedStructure(structure));
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

    internal static IReadOnlyList<PdfOutputDecision> FilterPdfOutputDecisions(
        IReadOnlyList<PdfOutputDecision> decisions,
        ValidatedStructure survivingStructure)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(survivingStructure);
        var survivingCompatibilityIds = survivingStructure.Elements
            .Select(element => element.ProjectionMetadata?.CompatibilitySourceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        return decisions.Where(decision => survivingCompatibilityIds.Contains(decision.HeadingId)).ToArray();
    }

    private static OutlineRunProvenance BuildProvenance(RouteExecutionAudit? audit,
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
