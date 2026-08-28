using System.Security.Cryptography;
using DocxHeaderExtractor.Core.Application.Routing;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Vision;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// The single normal extraction orchestrator. Source adapters may differ, but every accepted
/// heading crosses the same proposal, source-grounding, validation, structure, and product stages.
/// The historical <see cref="HeaderExtractionPipeline"/> is intentionally not used here.
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
            var extraction = new DocxSlimExtractor(_options.Extraction).ExtractWithSourceFacts(conversion.Path);
            var slim = extraction.Slim;
            var source = extraction.Source;
            var mode = slim.Mode ?? DocumentModeClassifier.Measure(slim.Paragraphs);
            var diagnostics = DocumentDiagnosticRunner.Analyze(slim, mode);
            var analyst = _options.DisableLlm ? null : await GetAnalystAsync(ct);
            var pdf = PdfTextbookOutline.FindSiblingPdf(inputPath);
            IReadOnlyList<HeadingRecord> rawHeadings;
            RouteExecutionAudit? audit;
            string route;
            string reason;
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
                            inputPath, slim, analyst!,
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
                    rawHeadings = result.Headings;
                    audit = ApplyQuarantine(result.Audit, rawHeadings, quarantinedIndexes, out rawHeadings);
                    route = "pdf-authority-v1";
                    reason = result.Reason;
                    break;
                }
                case AuthorityRoute.DocxAuthority:
                {
                    var result = await DocxAuthorityPipeline.RunAsync(source, slim, mode, analyst, quarantinedIndexes, ct);
                    rawHeadings = result.Headings;
                    audit = result.Audit;
                    route = "docx-authority-v1";
                    reason = "docx-source-authority";
                    break;
                }
                case AuthorityRoute.Unsupported:
                    throw new InvalidOperationException("Normal authority route requires a DOCX source.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(authorityRoute), authorityRoute, null);
            }

            var product = audit is null
                ? new PdfProductOutput(FileSha256(conversion.Path), [])
                : BuildProductOutput(conversion.Path, audit, rawHeadings);
            var headings = PdfProductOutlineAdapter.ToHeadingRecords(product);
            _options.Log?.Invoke($"Authority route {route}: validated={headings.Count}; {reason}");
            return new DocumentOutline
            {
                File = Path.GetFileName(inputPath),
                ParagraphCount = slim.Paragraphs.Count,
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

    private static PdfProductOutput BuildProductOutput(string docxPath, RouteExecutionAudit audit,
        IReadOnlyList<HeadingRecord> rawHeadings)
    {
        var finalStructure = PdfFinalStructureProjection.Project(
            FileSha256(docxPath), audit.ValidatedStructures, audit.HierarchyFacts,
            PdfCanonicalGrounding.FromGroundedHeadings(rawHeadings));
        return PdfProductOutputSerializer.Serialize(finalStructure, PdfOutputDecisionPolicy.Decide(finalStructure));
    }

    internal static RouteExecutionAudit? ApplyQuarantine(
        RouteExecutionAudit? audit,
        IReadOnlyList<HeadingRecord> headings,
        IReadOnlySet<int>? quarantinedIndexes,
        out IReadOnlyList<HeadingRecord> remainingHeadings)
    {
        if (audit is null || quarantinedIndexes is null || quarantinedIndexes.Count == 0)
        {
            remainingHeadings = headings;
            return audit;
        }

        var removedIds = headings.Where(heading => quarantinedIndexes.Contains(heading.Index))
            .Select(heading => heading.SourceId ?? heading.StableId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        remainingHeadings = headings.Where(heading => !quarantinedIndexes.Contains(heading.Index)).ToArray();
        if (removedIds.Count == 0) return audit;
        return audit with
        {
            ValidatedStructures = audit.ValidatedStructures.Where(item => !removedIds.Contains(item.SourceId)).ToArray(),
            HierarchyFacts = audit.HierarchyFacts.Where(item => !removedIds.Contains(item.Id)).ToArray(),
            GroundedBlockIds = audit.GroundedBlockIds.Where(id => !removedIds.Contains(id)).ToArray(),
            AlignedBlockIds = audit.AlignedBlockIds.Where(id => !removedIds.Contains(id)).ToArray(),
        };
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
