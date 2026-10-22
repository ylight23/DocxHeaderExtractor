using DocxHeaderExtractor.Sources.Semantics.Validation;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Inference;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.DocumentProcessing.Selection;
using DocxHeaderExtractor.Sources.Common;
using DocxHeaderExtractor.Sources.Pdf;
using DocxHeaderExtractor.DocumentProcessing.Harness;

namespace DocxHeaderExtractor.DocumentProcessing.Lanes.Pdf;

/// <summary>
/// The normal PDF route: the V6 selected-lines heading authority (an <see cref="IHeadingSelector"/>, the D4.12 direct agent) run under a
/// lane lease and checkpoint, then the shared binding, placement, hierarchy and materialization. The function-conditioned chain
/// (F1 → G2A → H2-C V2) it replaced was removed from production on 2026-10-14. There is intentionally no canonical-text fallback.
/// </summary>
internal static class PdfLane
{
    private const string AuthorityId = SelectionAuthority.AuthorityId;

    public static async Task<LaneResult> RunAsync(
        DocumentSourceSnapshot authority,
        PdfSourceDetails pdfDetails,
        string sourceName,
        IInferenceTransport? transport,
        LaneOptions? semanticLaneOptions,
        IHeadingSelector? selector,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(pdfDetails);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        if (pdfDetails.ParserLineCount == 0 || pdfDetails.Blocks.Count == 0)
            return new LaneResult(ValidatedStructureFactory.Create([]), null, "pdf-no-text-layer") { SourceCatalog = authority.Catalog };
        if (transport is null)
            return new LaneResult(
                ValidatedStructureFactory.Create([]), SourceOnlyAudit(authority, pdfDetails), "pdf-function-conditioned-llm-disabled")
            { SourceCatalog = authority.Catalog };
        if (transport is not IAgentRouteInferenceTransport authorized)
            throw new InvalidOperationException("AGENT_ROUTE_AUTHORIZATION_REQUIRED");
        if (selector is null)
            throw new InvalidOperationException("PDF_HEADING_SELECTOR_REQUIRED: the PDF lane has no authority without the V6 heading selector.");
        var apiKey = authorized.OpenRouterApiKey
            ?? throw new InvalidOperationException("PDF_HEADING_SELECTOR_KEY_MISSING: the authorized PDF route carries no provider key.");

        await using var scope = CheckpointScope.Create();
        await using var checkpoint = new PdfCheckpoint(scope.CheckpointPath, Path.GetFileNameWithoutExtension(sourceName));
        await checkpoint.RecordSelectionAsync(
            pdfDetails.Blocks.Select(block => new PdfSelectedSourceIdentity(
                block.Id, block.Page, block.Lines.Select(PdfLineIdentity.Of).ToArray(), block.DisplayText)).ToArray(), ct).ConfigureAwait(false);
        var result = await LaneHarness.CompleteAsync(
            (lease, laneCt) => RunCoreAsync(authority, pdfDetails, authorized, selector, sourceName, apiKey, lease, laneCt),
            (semanticLaneOptions ?? LaneOptions.Default).LaneDeadline,
            Messages,
            ct,
            afterRun: checkpoint.StopAcceptingWritesAndDrainAsync).ConfigureAwait(false);
        return result with { CheckpointObservations = ["source-selection:completed"] };
    }

    private static readonly LaneHarness.Messages Messages = new(
        "PDF semantic execution exceeded its lane deadline.",
        "PDF semantic execution failed.",
        "PDF semantic result lost its execution lease.");

    private static RunAudit SourceOnlyAudit(DocumentSourceSnapshot authority, PdfSourceDetails pdfDetails)
    {
        var sourceBlocks = pdfDetails.Blocks
            .Select(block => new SourceBlockAudit(block.Id, block.Page, block.DisplayText))
            .ToArray();
        return AuditBoundary.Create(
            AuthorityId,
            pdfDetails.Blocks.Count,
            pdfDetails.Blocks.Count,
            pdfDetails.Blocks.Select(block => block.Page).Distinct().Count(),
            pdfDetails.Blocks.Select(block => block.Page).Distinct().Count(),
            sourceBlocks,
            sourceBlocks,
            [],
            []) with
        {
            ModelInputContracts = [],
            SemanticLane = new LaneExecutionAudit(
                "not-run", authority.Atoms.Count, 0, 0, authority.Atoms.Count, "llm-disabled"),
            SpanLane = new LaneExecutionAudit(
                "not-run", 0, 0, 0, 0, "llm-disabled"),
        };
    }

    private static async Task<LaneResult> RunCoreAsync(DocumentSourceSnapshot source, PdfSourceDetails pdfDetails, IAgentRouteInferenceTransport authorized,
        IHeadingSelector selector, string pdfPath, string apiKey, LaneExecutionLease lease, CancellationToken ct)
    {
        var decided = await LaneHarness.DecideAsync(source, selector, pdfPath, apiKey, pdfDetails.LayoutBlockByAtom, authorized, lease, ct).ConfigureAwait(false);
        var assembly = await LaneHarness.AssembleAsync(source, decided, ct).ConfigureAwait(false);
        var sourceBlocks = pdfDetails.Blocks.Select(block => new SourceBlockAudit(block.Id, block.Page, block.DisplayText)).ToArray();
        var pages = pdfDetails.Blocks.Select(block => block.Page).Distinct().Count();
        var audit = LaneHarness.Audit(AuthorityId, sourceBlocks, pages, decided, assembly) with
        {
            HierarchyFacts = PdfFacts.Inspect(assembly.Validated, pdfDetails.Contexts),
        };
        return LaneHarness.Result(AuthorityId, source, assembly, LaneHarness.Completed(decided, audit, assembly));
    }
}
