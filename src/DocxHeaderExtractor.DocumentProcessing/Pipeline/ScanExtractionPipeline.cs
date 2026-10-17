using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;
using DocxHeaderExtractor.DocumentProcessing.Provenance;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Scanned DOCX files use OCR/VLM source lines and the same binding, placement, hierarchy and projections as the other lanes.</summary>
internal static class ScanExtractionPipeline
{
    internal const string RouteId = "scan-v6-selected-lines";

    internal static async Task<DocumentExtractionExecutionResult> RunAsync(string path, PipelineOptions options,
        IInferenceTransport? transport, IScanHeadingSelector? selector, bool sendsDataExternally, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (selector is null) throw new InvalidOperationException("SCAN_HEADING_SELECTOR_REQUIRED");
        if (!options.DisableLlm && transport is not IAgentRouteInferenceTransport)
            throw new InvalidOperationException("AGENT_ROUTE_AUTHORIZATION_REQUIRED");
        var key = options.DisableLlm ? null : ((IAgentRouteInferenceTransport)transport!).OpenRouterApiKey
            ?? throw new InvalidOperationException("SCAN_HEADING_SELECTOR_KEY_MISSING");
        var started = Environment.TickCount64;
        var sourceSha = CanonicalSemanticSourceHash.Compute(path);
        var execution = await LaneExecution.RunAsync(async (lease, laneCt) =>
        {
            options.Log?.Invoke("Scan: OCR/VLM preflight and heading selection");
            var prepared = await selector.PrepareAsync(path, key, laneCt).ConfigureAwait(false);
            laneCt.ThrowIfCancellationRequested();
            if (prepared.SourceSha256 != sourceSha || sourceSha != CanonicalSemanticSourceHash.Compute(path))
                throw new InvalidOperationException("SCAN_SOURCE_HASH_MISMATCH");
            var source = ScanSourceAdapter.Build(prepared);
            if (source.Atoms.Count == 0) throw new InvalidOperationException("SCAN_EMPTY_UNIVERSE");
            var fixedSelector = new PreparedSelector(selector.SelectorId, prepared.Selection);
            var authority = new SelectedLinesHeadingAuthority(fixedSelector, path, key ?? "", new Dictionary<string, string>(),
                transport is null ? null : new LeaseBoundInferenceTransport(transport, lease),
                () => { if (!lease.IsActive) throw new LaneLeaseLostException(); });
            var decided = await authority.DecideAsync(source, laneCt).ConfigureAwait(false);
            options.Log?.Invoke("Scan: bind, place and materialize headings");
            var structureClock = System.Diagnostics.Stopwatch.StartNew();
            var assembly = await HeadingStructureAssembler.AssembleAsync(source, decided, PrimaryOccurrenceSelection.EverySourceOccurrence, laneCt).ConfigureAwait(false);
            var stageSeconds = new Dictionary<string, double>(prepared.Diagnostics.StageSeconds)
            { ["structure"] = Math.Round(structureClock.Elapsed.TotalSeconds, 1) };
            var blocks = source.Atoms.Select(a => new SourceBlockAudit(a.SourceId, a.Page, a.Text)).ToArray();
            var pages = source.Atoms.Select(a => a.Page).Distinct().Count();
            var audit = decided.CompleteAudit(ExecutionAuditBoundary.Create(RouteId, blocks.Length, blocks.Length, pages, pages,
                blocks, blocks, decided.Decisions.Select(d => new SourceBlockDecisionAudit(d.Id, d.SemanticFunction)).ToArray(),
                assembly.Validated.Select(v => v.SourceId).ToArray()), assembly) with
            {
                PipelineId = RouteId,
                Scan = prepared.Diagnostics with
                { PlacementCalls = assembly.PlacementObservation?.LogicalCalls ?? 0, StageSeconds = stageSeconds },
                PlacementExecution = assembly.PlacementObservation,
            };
            if (options.DisableLlm)
                audit = audit with { SemanticLane = new LaneExecutionAudit("not-run", 0, 0, 0, 0, "llm-disabled") };
            else if (prepared.Diagnostics.TranscriptionFailures.Count > 0 || prepared.Diagnostics.VisionFailures.Count > 0 || prepared.Diagnostics.OcrGaps.Count > 0)
                audit = audit with { SemanticLane = audit.SemanticLane! with { Status = "partial", FailureClass = "SCAN_PREFLIGHT_INCOMPLETE:" + audit.SemanticLane!.FailureClass } };
            if (!options.DisableLlm && (audit.SemanticLane!.FailureClass is not null || assembly.Validated.Count != decided.Decisions.Count))
                audit = audit with { SemanticLane = audit.SemanticLane! with { Status = "partial",
                    FailureClass = audit.SemanticLane!.FailureClass ?? "SCAN_BINDING_INCOMPLETE" } };
            return new HeadingPipelineResult(assembly.Structure, audit, options.DisableLlm ? "scan-llm-disabled" : RouteId,
                assembly.Structure.Elements.Select(e => e.Id).ToHashSet(StringComparer.Ordinal))
            { SourceCatalog = source.Catalog, ProjectionContext = assembly.ProjectionContext };
        }, SemanticLaneOptions.Default.LaneDeadline, ct).ConfigureAwait(false);
        if (execution.State == LaneExecutionState.Cancelled) throw new OperationCanceledException(ct);
        if (execution.State == LaneExecutionState.TimedOut) throw new TimeoutException("Scan execution exceeded its lane deadline.");
        if (execution.State == LaneExecutionState.Failed) throw execution.Fault ?? new InvalidOperationException("SCAN_EXECUTION_FAILED");
        if (!execution.Lease.CanPublishCompletedResult || execution.Value is null) throw new InvalidOperationException("SCAN_EXECUTION_LEASE_LOST");
        ct.ThrowIfCancellationRequested();
        if (sourceSha != CanonicalSemanticSourceHash.Compute(path)) throw new InvalidOperationException("SCAN_SOURCE_CHANGED_DURING_RUN");
        var result = execution.Value;
        var catalog = result.SourceCatalog!;
        var sections = StructuralSectionProjection.Project(result.Structure, catalog);
        var chunks = SectionChunkProjection.Project(sections, catalog, result.Structure, new DocumentChunkingPolicy(Math.Max(1, options.Chunking.TokenBudget)));
        var diagnostics = result.Audit!.Scan!;
        var document = new DocumentExtractionResult(new DocumentIdentity(Path.GetFileNameWithoutExtension(path), Path.GetFileName(path), "scan", path),
            catalog, result.Structure, sections, chunks,
            new DocumentExtractionProvenance(RouteId, "scan-source-document", diagnostics.PreflightCalls + diagnostics.ConversationCalls + diagnostics.PlacementCalls));
        var final = DocxExtractionPipeline.BuildFinalStructure(path, result.Audit, result.Structure, result.ProjectionContext);
        var outline = new DocumentOutline
        {
            File = Path.GetFileName(path), ParagraphCount = catalog.Units.Count, SourceCount = catalog.Units.Count,
            Headings = HeadingOutlineProjection.Project(result.Structure, result.EmittedElementIds, result.ProjectionContext, catalog),
            ProductOutput = DocumentProductOutputProjector.Serialize(final, OutputDecisionPolicy.Decide(final)),
            ElapsedMs = Environment.TickCount64 - started, Model = transport?.ModelName, DeterministicRoute = RouteId, RouteAudit = result.Audit,
            Provenance = DocxExtractionPipeline.BuildProvenance(result.Audit, !options.DisableLlm && sendsDataExternally),
        };
        return new DocumentExtractionExecutionResult(document, outline) { HeadingPipeline = result };
    }

    sealed class PreparedSelector(string id, HeadingSelection selection) : IHeadingSelector
    {
        public string SelectorId => id;
        public Task<HeadingSelection> SelectAsync(string path, string apiKey, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(selection); }
    }
}
