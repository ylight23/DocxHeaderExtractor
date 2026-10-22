using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Sources.Semantics.Validation;
using DocxHeaderExtractor.Sources.Docx;
using DocxHeaderExtractor.Sources.Common;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Inference;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.DocumentProcessing.Harness;

namespace DocxHeaderExtractor.DocumentProcessing.Lanes.Docx;

/// <summary>
/// The normal DOCX route, the counterpart of the PDF route: the DOCX source adapter, then the selected-lines heading authority (an
/// <see cref="IHeadingSelector"/>, the V6 agent over the paragraphs) run under a lane lease, then the shared binding, placement, hierarchy and
/// materialization. The selector says which paragraphs are headings and how they group; the shared stages never take a heading, a coordinate or a
/// hierarchy from anywhere else. There is intentionally no fallback to a text-model authority.
/// </summary>
internal static class DocxLane
{
    internal const string RouteId = "docx-v6-selected-lines";

    public static async Task<LaneResult> RunAsync(
        SourceDocument sourceDocument,
        IInferenceTransport? transport,
        IHeadingSelector? selector,
        LaneOptions? semanticLaneOptions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourceDocument);
        var source = DocxSourceAdapter.Build(sourceDocument).Snapshot;
        if (source.Occurrences.Count == 0)
            return new LaneResult(ValidatedStructureFactory.Create([]), null, "empty-docx-source");
        if (transport is null)
            return new LaneResult(ValidatedStructureFactory.Create([]), SourceOnlyAudit(source), "docx-llm-disabled") { SourceCatalog = source.Catalog };
        if (transport is not IAgentRouteInferenceTransport authorized)
            throw new InvalidOperationException("AGENT_ROUTE_AUTHORIZATION_REQUIRED: the DOCX route runs only on the authorized OpenRouter route.");
        if (selector is null)
            throw new InvalidOperationException("DOCX_HEADING_SELECTOR_REQUIRED: the DOCX route has no authority without the V6 heading selector.");
        var apiKey = authorized.OpenRouterApiKey
            ?? throw new InvalidOperationException("DOCX_HEADING_SELECTOR_KEY_MISSING: the authorized route carries no provider key.");

        return await LaneHarness.CompleteAsync(
            (lease, laneCt) => RunCoreAsync(source, sourceDocument.SourcePath, authorized, selector, apiKey, lease, laneCt),
            (semanticLaneOptions ?? LaneOptions.Default).LaneDeadline,
            Messages,
            ct).ConfigureAwait(false);
    }

    private static readonly LaneHarness.Messages Messages = new(
        "DOCX semantic execution exceeded its lane deadline.",
        "DOCX semantic execution failed.",
        "DOCX semantic result lost its execution lease.");

    private static RunAudit SourceOnlyAudit(DocumentSourceSnapshot source)
    {
        var blocks = source.Occurrences.Select(item => new SourceBlockAudit(item.Id, 0, item.Text)).ToArray();
        return AuditBoundary.Create(RouteId, blocks.Length, blocks.Length, 0, 0, blocks, blocks, [], []) with
        {
            ModelInputContracts = [],
            SemanticLane = new LaneExecutionAudit("not-run", source.Atoms.Count, 0, 0, source.Atoms.Count, "llm-disabled"),
            SpanLane = new LaneExecutionAudit("not-run", 0, 0, 0, 0, "llm-disabled"),
        };
    }

    private static async Task<LaneResult> RunCoreAsync(
        DocumentSourceSnapshot source, string docxPath, IAgentRouteInferenceTransport authorized, IHeadingSelector selector, string apiKey,
        LaneExecutionLease lease, CancellationToken ct)
    {
        // A DOCX has no parser layout blocks; the selector gives the division into headings itself.
        var decided = await LaneHarness.DecideAsync(
            source, selector, docxPath, apiKey, new Dictionary<string, string>(StringComparer.Ordinal), authorized, lease, ct).ConfigureAwait(false);
        var assembly = await LaneHarness.AssembleAsync(source, decided, ct).ConfigureAwait(false);
        var blocks = source.Occurrences.Select(item => new SourceBlockAudit(item.Id, 0, item.Text)).ToArray();
        var audit = LaneHarness.Audit(RouteId, blocks, 0, decided, assembly);
        return LaneHarness.Result(RouteId, source, assembly, LaneHarness.Completed(decided, audit, assembly));
    }
}
