using DocxHeaderExtractor.DocumentProcessing.Structure;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.DocumentProcessing.Selection;
using DocxHeaderExtractor.Sources.Snapshot;
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
        ModelRoute? route,
        LaneOptions? semanticLaneOptions,
        IHeadingSelector? selector,
        IHeadingPlacer? placer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(pdfDetails);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        if (pdfDetails.ParserLineCount == 0 || pdfDetails.Blocks.Count == 0)
            return new LaneResult(StructureFactory.Create([]), null, "pdf-no-text-layer") { SourceCatalog = authority.Catalog };
        var lane = new LaneHarness.Input(
            AuthorityId,
            authority,
            pdfDetails.LayoutBlockByAtom,
            pdfDetails.Blocks.Select(block => new SourceBlockAudit(block.Id, block.Page, block.DisplayText)).ToArray(),
            pdfDetails.Blocks.Select(block => block.Page).Distinct().Count());
        if (route is null)
            return new LaneResult(StructureFactory.Create([]), LaneHarness.SourceOnlyAudit(lane), "pdf-function-conditioned-llm-disabled")
            { SourceCatalog = authority.Catalog };
        var access = LaneHarness.Authorize(route, selector, placer, Refusals);

        await using var scope = CheckpointScope.Create();
        await using var checkpoint = new PdfCheckpoint(scope.CheckpointPath, Path.GetFileNameWithoutExtension(sourceName));
        await checkpoint.RecordSelectionAsync(
            pdfDetails.Blocks.Select(block => new PdfSelectedSourceIdentity(
                block.Id, block.Page, block.Lines.Select(PdfLineIdentity.Of).ToArray(), block.DisplayText)).ToArray(), ct).ConfigureAwait(false);
        var result = await LaneHarness.CompleteAsync(
            async (lease, laneCt) =>
            {
                var run = await LaneHarness.DecideAndAssembleAsync(
                    lane, access.Selector, access.Placer, sourceName, access.ApiKey, access.Route, lease, laneCt).ConfigureAwait(false);
                return LaneHarness.Result(AuthorityId, authority, run.Assembly,
                    run.Audit with { HierarchyFacts = PdfFacts.Inspect(run.Assembly.Validated, pdfDetails.Contexts) });
            },
            (semanticLaneOptions ?? LaneOptions.Default).LaneDeadline,
            Messages,
            ct,
            afterRun: checkpoint.StopAcceptingWritesAndDrainAsync).ConfigureAwait(false);
        return result with { CheckpointObservations = ["source-selection:completed"] };
    }

    private static readonly LaneHarness.AccessMessages Refusals = new(
        "PDF_HEADING_SELECTOR_REQUIRED: the PDF lane has no authority without the V6 heading selector.",
        "PDF_HEADING_SELECTOR_KEY_MISSING: the model route carries no provider key.",
        "HEADING_PLACER_REQUIRED: the PDF route places no heading without the heading placer.");

    private static readonly LaneHarness.Messages Messages = new(
        "PDF semantic execution exceeded its lane deadline.",
        "PDF semantic execution failed.",
        "PDF semantic result lost its execution lease.");
}
