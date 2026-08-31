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
    private IHeaderClassifier? _analyst;
    private readonly bool _ownsAnalyst;

    public AuthorityExtractionPipeline(PipelineOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _routePolicy = new DefaultAuthorityRoutePolicy();
        _ownsAnalyst = true;
    }

    public AuthorityExtractionPipeline(PipelineOptions options, IHeaderClassifier analyst)
        : this(options, new DefaultAuthorityRoutePolicy(), analyst)
    {
    }

    public AuthorityExtractionPipeline(PipelineOptions options, IAuthorityRoutePolicy routePolicy)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _routePolicy = routePolicy ?? throw new ArgumentNullException(nameof(routePolicy));
        _ownsAnalyst = true;
    }

    public AuthorityExtractionPipeline(
        PipelineOptions options,
        IAuthorityRoutePolicy routePolicy,
        IHeaderClassifier analyst)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _routePolicy = routePolicy ?? throw new ArgumentNullException(nameof(routePolicy));
        _analyst = analyst ?? throw new ArgumentNullException(nameof(analyst));
        _ownsAnalyst = false;
    }

    public Task<DocumentOutline> RunAsync(string inputPath, CancellationToken ct = default) =>
        RunAsync(inputPath, null, ct);

    public async Task<DocumentOutline> RunAsync(
        string inputPath,
        IReadOnlySet<int>? quarantinedIndexes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var started = Environment.TickCount64;
        var conversion = LegacyDocConverter.EnsureDocx(inputPath);
        try
        {
            var sourceDocument = new OpenXmlDocumentSource().Read(conversion.Path);
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
                    authority = result.StructuralAuthority ??
                        throw new InvalidOperationException("PDF authority producer returned no generic structural result.");
                    routeFinalStructure = result.FinalStructure;
                    routeOutputDecisions = result.OutputDecisions;
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

            var product = new PdfProductOutput(FileSha256(conversion.Path), []);
            var structural = new StructuralMaterializationResult(
                new ValidatedStructure([]), new HashSet<string>(StringComparer.Ordinal), 0, 0);
            if (audit is not null)
            {
                var isNativePdf = routeFinalStructure is not null;
                var finalStructure = isNativePdf
                    ? routeFinalStructure!
                    : BuildFinalStructure(conversion.Path, audit, authority.Structure);
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
            return new DocumentOutline
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
        }
        finally
        {
            LegacyDocConverter.Cleanup(conversion);
        }
    }

    private async Task<IHeaderClassifier> GetAnalystAsync(CancellationToken ct)
    {
        if (_analyst is not null) return _analyst;
        _options.PrepareLocalModelProfile();
        _options.Llama.ChunkTokenBudget = _options.Chunking.TokenBudget;
        _analyst = _options.Backend switch
        {
            InferenceBackend.OpenRouter => OpenRouterHeaderExtractor.CreateOwned(_options.OpenRouter),
            InferenceBackend.LmStudio => LmStudioHeaderExtractor.CreateOwned(_options.LmStudio),
            InferenceBackend.Sglang => SglangHeaderExtractor.CreateOwned(_options.Sglang),
            _ => await LlamaHeaderExtractor.LoadAsync(_options.Llama, ct),
        };
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
            .Select(element => element.ParentId is not null && removedElementIds.Contains(element.ParentId)
                ? element with { ParentId = null }
                : element)
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

        return authority with
        {
            Structure = ValidatedStructure.FromElements(remaining),
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
