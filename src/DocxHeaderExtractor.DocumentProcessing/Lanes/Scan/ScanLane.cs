using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Inference;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Sources.Scan;
using DocxHeaderExtractor.Sources.Detect;
using DocxHeaderExtractor.DocumentProcessing.Harness;

namespace DocxHeaderExtractor.DocumentProcessing.Lanes.Scan;

/// <summary>Scanned DOCX files use OCR/VLM source lines and the same binding, placement, hierarchy and projections as the other lanes.</summary>
internal static class ScanLane
{
    internal const string RouteId = "scan-v6-selected-lines";

    internal static async Task<RunResult> RunAsync(string path, PipelineOptions options,
        IInferenceTransport? transport, HeadingAgents agents, bool sendsDataExternally, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var selector = agents.Scan ?? throw new InvalidOperationException("SCAN_HEADING_SELECTOR_REQUIRED");
        if (!options.DisableLlm && transport is not IAgentRouteInferenceTransport)
            throw new InvalidOperationException("AGENT_ROUTE_AUTHORIZATION_REQUIRED");
        var key = options.DisableLlm ? null : ((IAgentRouteInferenceTransport)transport!).OpenRouterApiKey
            ?? throw new InvalidOperationException("SCAN_HEADING_SELECTOR_KEY_MISSING");
        if (!options.DisableLlm && agents.Placer is null) throw new InvalidOperationException("HEADING_PLACER_REQUIRED");
        var started = Environment.TickCount64;
        var sourceSha = SourceHash.Compute(path);
        var result = await LaneHarness.CompleteAsync(async (lease, laneCt) =>
        {
            options.Log?.Invoke("Scan: OCR/VLM preflight and heading selection");
            var prepared = await selector.PrepareAsync(path, key, laneCt).ConfigureAwait(false);
            laneCt.ThrowIfCancellationRequested();
            if (prepared.SourceSha256 != sourceSha || sourceSha != SourceHash.Compute(path))
                throw new InvalidOperationException("SCAN_SOURCE_HASH_MISMATCH");
            var source = ScanSourceAdapter.Build(prepared);
            if (source.Atoms.Count == 0) throw new InvalidOperationException("SCAN_EMPTY_UNIVERSE");
            var lane = new LaneHarness.Input(
                RouteId,
                source,
                new Dictionary<string, string>(),
                source.Atoms.Select(a => new SourceBlockAudit(a.SourceId, a.Page, a.Text)).ToArray(),
                source.Atoms.Select(a => a.Page).Distinct().Count());
            options.Log?.Invoke("Scan: bind, place and materialize headings");
            var run = await LaneHarness.DecideAndAssembleAsync(
                lane, new PreparedSelector(selector.SelectorId, prepared.Selection), agents.Placer, path, key ?? "", transport, lease, laneCt).ConfigureAwait(false);
            var (decided, assembly) = (run.Decided, run.Assembly);
            var stageSeconds = new Dictionary<string, double>(prepared.Diagnostics.StageSeconds)
            { ["structure"] = Math.Round(run.AssembleSeconds, 1) };
            var audit = run.Audit with
            {
                PipelineId = RouteId,
                Scan = prepared.Diagnostics with
                { PlacementCalls = assembly.PlacementObservation?.LogicalCalls ?? 0, StageSeconds = stageSeconds },
            };
            if (options.DisableLlm)
                audit = audit with { SemanticLane = new LaneExecutionAudit("not-run", 0, 0, 0, 0, "llm-disabled") };
            else if (prepared.Diagnostics.TranscriptionFailures.Count > 0 || prepared.Diagnostics.VisionFailures.Count > 0 || prepared.Diagnostics.OcrGaps.Count > 0)
                audit = audit with { SemanticLane = audit.SemanticLane! with { Status = "partial", FailureClass = "SCAN_PREFLIGHT_INCOMPLETE:" + audit.SemanticLane!.FailureClass } };
            if (!options.DisableLlm && (audit.SemanticLane!.FailureClass is not null || assembly.Validated.Count != decided.Decisions.Count))
                audit = audit with { SemanticLane = audit.SemanticLane! with { Status = "partial",
                    FailureClass = audit.SemanticLane!.FailureClass ?? "SCAN_BINDING_INCOMPLETE" } };
            return LaneHarness.Result(options.DisableLlm ? "scan-llm-disabled" : RouteId, source, assembly, audit);
        }, LaneOptions.Default.LaneDeadline, Messages, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (sourceSha != SourceHash.Compute(path)) throw new InvalidOperationException("SCAN_SOURCE_CHANGED_DURING_RUN");
        var catalog = result.SourceCatalog!;
        var diagnostics = result.Audit!.Scan!;
        return ExtractionOutput.Build(
            new ExtractionOutput.Lane(
                path,
                new DocumentIdentity(Path.GetFileNameWithoutExtension(path), Path.GetFileName(path), "scan", path),
                new DocumentExtractionProvenance(RouteId, "scan-source-document", diagnostics.PreflightCalls + diagnostics.ConversationCalls + diagnostics.PlacementCalls),
                ProductSha256: null,
                ParagraphCount: catalog.Units.Count,
                SourceCount: catalog.Units.Count,
                Route: RouteId,
                Model: transport?.ModelName,
                SentDataExternally: !options.DisableLlm && sendsDataExternally),
            result, result.Structure, result.EmittedElementIds!, catalog, options, started);
    }

    private static readonly LaneHarness.Messages Messages = new(
        "Scan execution exceeded its lane deadline.", "SCAN_EXECUTION_FAILED", "SCAN_EXECUTION_LEASE_LOST");

    sealed class PreparedSelector(string id, HeadingSelection selection) : IHeadingSelector
    {
        public string SelectorId => id;
        public Task<HeadingSelection> SelectAsync(string path, string apiKey, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(selection); }
    }
}
