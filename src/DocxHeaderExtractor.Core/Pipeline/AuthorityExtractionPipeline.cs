using System.Security.Cryptography;
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
    private IHeaderClassifier? _analyst;
    private readonly bool _ownsAnalyst;

    public AuthorityExtractionPipeline(PipelineOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownsAnalyst = true;
    }

    public AuthorityExtractionPipeline(PipelineOptions options, IHeaderClassifier analyst)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
            var slim = new DocxSlimExtractor(_options.Extraction).Extract(conversion.Path);
            var mode = slim.Mode ?? DocumentModeClassifier.Measure(slim.Paragraphs);
            var diagnostics = DocumentDiagnosticRunner.Analyze(slim, mode);
            if (_options.DisableLlm)
                return Empty(inputPath, slim, mode, diagnostics, started);

            var analyst = await GetAnalystAsync(ct);
            var pdf = PdfTextbookOutline.FindSiblingPdf(inputPath);
            IReadOnlyList<HeadingRecord> rawHeadings;
            RouteExecutionAudit? audit;
            string route;
            string reason;
            if (!string.IsNullOrWhiteSpace(pdf))
            {
                using var visual = CreateVisualAnalyst();
                var result = await PdfLayoutEvidenceOutline.TryBuildBroadAuditWithAnalystAsync(
                    inputPath, slim, analyst,
                    maximumAnalystBlocks: _options.PdfFirstAnalystBlocks,
                    includeAllVisualStyles: true,
                    includeSupplementCandidates: true,
                    maximumVisualRegions: _options.PdfFirstVisualRegions,
                    visualAnalyst: visual,
                    ct: ct);
                rawHeadings = result.Headings;
                audit = result.Audit;
                route = "pdf-authority-v1";
                reason = result.Reason;
            }
            else
            {
                var result = await DocxAuthorityPipeline.RunAsync(slim, mode, analyst, ct);
                rawHeadings = result.Headings;
                audit = result.Audit;
                route = "docx-authority-v1";
                reason = "docx-source-authority";
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
                Model = analyst.ModelName,
                DocumentMode = mode,
                DeterministicRoute = route,
                RouteAudit = audit,
                Diagnostics = diagnostics,
                DecisionAudit = null,
                Provenance = new OutlineRunProvenance(_options.Backend.ToString(),
                    !_options.DisableLlm && _options.Backend is InferenceBackend.OpenRouter or InferenceBackend.Sglang, []),
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

    private IPdfVisualQuestion? CreateVisualAnalyst() =>
        _options.Backend == InferenceBackend.Sglang &&
        string.Equals(_options.Sglang.Endpoint.Host, "integrate.api.nvidia.com", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(_options.Sglang.ApiKey)
            ? new NvidiaNimVisualQuestion(_options.Sglang.Endpoint, _options.Sglang.ApiKey, _options.Sglang.Model,
                _options.Sglang.RequestTimeoutSeconds, _options.Sglang.TransientRequestRetries)
            : null;

    private static PdfProductOutput BuildProductOutput(string docxPath, RouteExecutionAudit audit,
        IReadOnlyList<HeadingRecord> rawHeadings)
    {
        var finalStructure = PdfFinalStructureProjection.Project(
            FileSha256(docxPath), audit.ValidatedStructures, audit.HierarchyFacts,
            PdfCanonicalGrounding.FromGroundedHeadings(rawHeadings));
        return PdfProductOutputSerializer.Serialize(finalStructure, PdfOutputDecisionPolicy.Decide(finalStructure));
    }

    private static DocumentOutline Empty(string path, SlimDocument slim, DocumentModeReport mode,
        DocumentDiagnosticReport diagnostics, long started) => new()
    {
        File = Path.GetFileName(path),
        ParagraphCount = slim.Paragraphs.Count,
        CandidateCount = 0,
        Headings = [],
        ElapsedMs = Environment.TickCount64 - started,
        DocumentMode = mode,
        DeterministicRoute = "authority-v1-no-model",
        Diagnostics = diagnostics,
    };

    private static string FileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public void Dispose()
    {
        if (_ownsAnalyst) _analyst?.Dispose();
        _analyst = null;
    }
}
