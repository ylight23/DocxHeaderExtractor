using System.Text;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The normal PDF route: the qualified function-conditioned heading authority (F1 → G2A → H2-C V2)
/// run under a lane lease and checkpoint, then the shared binding, placement, hierarchy and
/// materialization. There is intentionally no canonical-text fallback.
/// </summary>
internal static class PdfHeadingPipeline
{
    private const string AuthorityId = FunctionAnchorExtentHeadingAuthority.AuthorityId;

    public static async Task<StructuralAuthorityResult> RunAsync(
        DocumentSourceSnapshot authority,
        PdfSourceDetails pdfDetails,
        string sourceName,
        IInferenceTransport? transport,
        SemanticLaneOptions? semanticLaneOptions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(pdfDetails);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        if (pdfDetails.ParserLineCount == 0 || pdfDetails.Blocks.Count == 0)
            return new StructuralAuthorityResult(new ValidatedStructure([]), null, "pdf-no-text-layer") { SourceCatalog = authority.Catalog };
        if (transport is null)
            return new StructuralAuthorityResult(
                new ValidatedStructure([]), SourceOnlyAudit(authority, pdfDetails), "pdf-function-conditioned-llm-disabled")
            { SourceCatalog = authority.Catalog };
        if (transport is not IFrozenInferenceTransport frozen)
            throw new InvalidOperationException("PDF_H2C_PRODUCTION_ROUTE_REQUIRES_FROZEN_REQUEST_TRANSPORT");

        await using var scope = ProductionCheckpointScope.Create();
        await using var checkpoint = new PdfStageCheckpoint(scope.CheckpointPath, Path.GetFileNameWithoutExtension(sourceName));
        await checkpoint.RecordSelectionAsync(
            pdfDetails.Blocks.Select(block => new PdfSelectedSourceIdentity(
                block.Id, block.Page, block.Lines.Select(PdfLineIdentity.Of).ToArray(), block.DisplayText)).ToArray(), ct).ConfigureAwait(false);
        var execution = await PdfLaneExecution.RunAsync(
            (lease, laneCt) => RunCoreAsync(authority, pdfDetails, frozen, lease, laneCt),
            (semanticLaneOptions ?? SemanticLaneOptions.Default).LaneDeadline,
            ct).ConfigureAwait(false);
        await checkpoint.StopAcceptingWritesAndDrainAsync().ConfigureAwait(false);
        if (execution.State == PdfLaneExecutionState.TimedOut)
            throw new TimeoutException("PDF semantic execution exceeded its lane deadline.");
        if (execution.State == PdfLaneExecutionState.Cancelled)
            throw new OperationCanceledException(ct);
        if (execution.State == PdfLaneExecutionState.Failed)
            throw execution.Fault ?? new InvalidOperationException("PDF semantic execution failed.");
        if (!execution.Lease.CanPublishCompletedResult || execution.Value is null)
            throw new InvalidOperationException("PDF semantic result lost its execution lease.");
        return execution.Value;
    }

    private static RouteExecutionAudit SourceOnlyAudit(DocumentSourceSnapshot authority, PdfSourceDetails pdfDetails)
    {
        var sourceBlocks = pdfDetails.Blocks
            .Select(block => new RouteBlockAudit(block.Id, block.Page, block.DisplayText))
            .ToArray();
        return CanonicalRouteAuditBoundary.Create(
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
            SemanticLane = new RouteLaneExecutionAudit(
                "not-run", authority.Atoms.Count, 0, 0, authority.Atoms.Count, "llm-disabled"),
            SpanLane = new RouteLaneExecutionAudit(
                "not-run", 0, 0, 0, 0, "llm-disabled"),
        };
    }

    private static async Task<StructuralAuthorityResult> RunCoreAsync(DocumentSourceSnapshot source, PdfSourceDetails pdfDetails, IFrozenInferenceTransport frozen, PdfLaneExecutionLease lease, CancellationToken ct)
    {
        var leaseBound = new LeaseBoundFrozenInferenceTransport(frozen, lease);
        IHeadingAuthority authority = new FunctionAnchorExtentHeadingAuthority(
            leaseBound,
            pdfDetails.LayoutBlockByAtom,
            () => { if (!lease.IsActive) throw new PdfExecutionLeaseLostException(); });
        var decided = await authority.DecideAsync(source, ct).ConfigureAwait(false);
        // The function-conditioned chain defines no semantic node identity, so every validated
        // source occurrence reaches the canonical structure.
        var assembly = await HeadingStructureAssembler.AssembleAsync(
            source, decided, PrimaryOccurrenceSelection.EverySourceOccurrence, ct).ConfigureAwait(false);

        var sourceBlocks = pdfDetails.Blocks.Select(block => new RouteBlockAudit(block.Id, block.Page, block.DisplayText)).ToArray();
        var pages = pdfDetails.Blocks.Select(block => block.Page).Distinct().Count();
        var audit = CanonicalRouteAuditBoundary.Create(
            AuthorityId,
            pdfDetails.Blocks.Count,
            pdfDetails.Blocks.Count,
            pages,
            pages,
            sourceBlocks,
            sourceBlocks,
            decided.Decisions.Select(decision => new RouteBlockDecisionAudit(decision.Id, decision.SemanticFunction)).ToArray(),
            assembly.Validated.Select(item => item.SourceId).ToArray()) with
        {
            HierarchyFacts = PdfHierarchyFactsInventory.Inspect(assembly.Validated, pdfDetails.Contexts),
        };
        return new StructuralAuthorityResult(
            assembly.Structure,
            decided.CompleteAudit(audit, assembly),
            AuthorityId,
            assembly.Structure.Elements.Select(value => value.Id).ToHashSet(StringComparer.Ordinal))
        { SourceCatalog = source.Catalog };
    }

    private sealed class LeaseBoundFrozenInferenceTransport : IFrozenInferenceTransport
    {
        private readonly IFrozenInferenceTransport _inner;
        private readonly PdfLaneExecutionLease _lease;

        public LeaseBoundFrozenInferenceTransport(IFrozenInferenceTransport inner, PdfLaneExecutionLease lease)
        {
            _inner = inner;
            _lease = lease;
        }

        public string ModelName => _inner.ModelName;
        public int ContextSize => _inner.ContextSize;
        public string RuntimeDescription => _inner.RuntimeDescription;
        public int SharedPrefixTokens => _inner.SharedPrefixTokens;
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            StartAndObserve(() => _inner.BoundaryCutAsync(systemPrompt, userMessage, ct, expectedItemCount));
        public Task<FrozenInferenceResult> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens, string systemPrompt, string userMessage, CancellationToken cancellationToken = default) =>
            StartAndObserve(() => _inner.ExecuteFrozenRequestAsync(providerBody, maxTokens, systemPrompt, userMessage, cancellationToken));
        public void Dispose() { }

        private Task<T> StartAndObserve<T>(Func<Task<T>> start)
        {
            Task<T>? task = null;
            if (!_lease.TryStartDownstream(() => task = start())) throw new PdfExecutionLeaseLostException();
            return ObserveAsync(task!, _lease);
        }

        private static async Task<T> ObserveAsync<T>(Task<T> task, PdfLaneExecutionLease lease)
        {
            var result = await task.ConfigureAwait(false);
            if (!lease.IsActive) throw new PdfExecutionLeaseLostException();
            return result;
        }
    }
}
