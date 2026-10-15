using DocxHeaderExtractor.Core.Semantics.Validation;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The normal PDF route: the V6 selected-lines heading authority (an <see cref="IHeadingSelector"/>, the D4.12 direct agent) run under a
/// lane lease and checkpoint, then the shared binding, placement, hierarchy and materialization. The function-conditioned chain
/// (F1 → G2A → H2-C V2) it replaced was removed from production on 2026-10-14. There is intentionally no canonical-text fallback.
/// </summary>
internal static class PdfHeadingPipeline
{
    private const string AuthorityId = SelectedLinesHeadingAuthority.AuthorityId;

    public static async Task<HeadingPipelineResult> RunAsync(
        DocumentSourceSnapshot authority,
        PdfSourceDetails pdfDetails,
        string sourceName,
        IInferenceTransport? transport,
        SemanticLaneOptions? semanticLaneOptions,
        IHeadingSelector? selector,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(pdfDetails);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        if (pdfDetails.ParserLineCount == 0 || pdfDetails.Blocks.Count == 0)
            return new HeadingPipelineResult(ValidatedStructureFactory.Create([]), null, "pdf-no-text-layer") { SourceCatalog = authority.Catalog };
        if (transport is null)
            return new HeadingPipelineResult(
                ValidatedStructureFactory.Create([]), SourceOnlyAudit(authority, pdfDetails), "pdf-function-conditioned-llm-disabled")
            { SourceCatalog = authority.Catalog };
        if (transport is not IAgentRouteInferenceTransport authorized)
            throw new InvalidOperationException("AGENT_ROUTE_AUTHORIZATION_REQUIRED");
        if (selector is null)
            throw new InvalidOperationException("PDF_HEADING_SELECTOR_REQUIRED: the PDF lane has no authority without the V6 heading selector.");
        var apiKey = authorized.OpenRouterApiKey
            ?? throw new InvalidOperationException("PDF_HEADING_SELECTOR_KEY_MISSING: the authorized PDF route carries no provider key.");

        await using var scope = ProductionCheckpointScope.Create();
        await using var checkpoint = new PdfStageCheckpoint(scope.CheckpointPath, Path.GetFileNameWithoutExtension(sourceName));
        await checkpoint.RecordSelectionAsync(
            pdfDetails.Blocks.Select(block => new PdfSelectedSourceIdentity(
                block.Id, block.Page, block.Lines.Select(PdfLineIdentity.Of).ToArray(), block.DisplayText)).ToArray(), ct).ConfigureAwait(false);
        var execution = await LaneExecution.RunAsync(
            (lease, laneCt) => RunCoreAsync(authority, pdfDetails, authorized, selector, sourceName, apiKey, lease, laneCt),
            (semanticLaneOptions ?? SemanticLaneOptions.Default).LaneDeadline,
            ct).ConfigureAwait(false);
        await checkpoint.StopAcceptingWritesAndDrainAsync().ConfigureAwait(false);
        if (execution.State == LaneExecutionState.TimedOut)
            throw new TimeoutException("PDF semantic execution exceeded its lane deadline.");
        if (execution.State == LaneExecutionState.Cancelled)
            throw new OperationCanceledException(ct);
        if (execution.State == LaneExecutionState.Failed)
            throw execution.Fault ?? new InvalidOperationException("PDF semantic execution failed.");
        if (!execution.Lease.CanPublishCompletedResult || execution.Value is null)
            throw new InvalidOperationException("PDF semantic result lost its execution lease.");
        return execution.Value with { CheckpointObservations = ["source-selection:completed"] };
    }

    private static PipelineExecutionAudit SourceOnlyAudit(DocumentSourceSnapshot authority, PdfSourceDetails pdfDetails)
    {
        var sourceBlocks = pdfDetails.Blocks
            .Select(block => new SourceBlockAudit(block.Id, block.Page, block.DisplayText))
            .ToArray();
        return ExecutionAuditBoundary.Create(
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

    private static async Task<HeadingPipelineResult> RunCoreAsync(DocumentSourceSnapshot source, PdfSourceDetails pdfDetails, IAgentRouteInferenceTransport authorized,
        IHeadingSelector selector, string pdfPath, string apiKey, LaneExecutionLease lease, CancellationToken ct)
    {
        var leaseBound = new LeaseBoundInferenceTransport(authorized, lease);
        var authority = new SelectedLinesHeadingAuthority(
            selector,
            pdfPath,
            apiKey,
            pdfDetails.LayoutBlockByAtom,
            leaseBound,
            () => { if (!lease.IsActive) throw new LaneLeaseLostException(); });
        var decided = await authority.DecideAsync(source, ct).ConfigureAwait(false);
        // The selected-lines authority defines no semantic node identity, so every validated
        // source occurrence reaches the canonical structure.
        var assembly = await HeadingStructureAssembler.AssembleAsync(
            source, decided, PrimaryOccurrenceSelection.EverySourceOccurrence, ct).ConfigureAwait(false);

        var sourceBlocks = pdfDetails.Blocks.Select(block => new SourceBlockAudit(block.Id, block.Page, block.DisplayText)).ToArray();
        var pages = pdfDetails.Blocks.Select(block => block.Page).Distinct().Count();
        var audit = ExecutionAuditBoundary.Create(
            AuthorityId,
            pdfDetails.Blocks.Count,
            pdfDetails.Blocks.Count,
            pages,
            pages,
            sourceBlocks,
            sourceBlocks,
            decided.Decisions.Select(decision => new SourceBlockDecisionAudit(decision.Id, decision.SemanticFunction)).ToArray(),
            assembly.Validated.Select(item => item.SourceId).ToArray()) with
        {
            HierarchyFacts = PdfHierarchyFactsInventory.Inspect(assembly.Validated, pdfDetails.Contexts),
        };
        return new HeadingPipelineResult(
            assembly.Structure,
            decided.CompleteAudit(audit, assembly) with { PlacementExecution = assembly.PlacementObservation },
            AuthorityId,
            assembly.Structure.Elements.Select(value => value.Id).ToHashSet(StringComparer.Ordinal))
        { SourceCatalog = source.Catalog, ProjectionContext = assembly.ProjectionContext };
    }
}
