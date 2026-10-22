using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Sources.Semantics.Validation;
using DocxHeaderExtractor.Sources.Docx;
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
        // A DOCX has no parser layout blocks and no pages; the selector gives the division into headings itself.
        var lane = new LaneHarness.Input(
            RouteId,
            source,
            new Dictionary<string, string>(StringComparer.Ordinal),
            source.Occurrences.Select(item => new SourceBlockAudit(item.Id, 0, item.Text)).ToArray(),
            0);
        if (transport is null)
            return new LaneResult(ValidatedStructureFactory.Create([]), LaneHarness.SourceOnlyAudit(lane), "docx-llm-disabled") { SourceCatalog = source.Catalog };
        var access = LaneHarness.Authorize(transport, selector, Refusals);

        return await LaneHarness.CompleteAsync(
            async (lease, laneCt) =>
            {
                var run = await LaneHarness.DecideAndAssembleAsync(
                    lane, access.Selector, sourceDocument.SourcePath, access.ApiKey, access.Transport, lease, laneCt).ConfigureAwait(false);
                return LaneHarness.Result(RouteId, source, run.Assembly, run.Audit);
            },
            (semanticLaneOptions ?? LaneOptions.Default).LaneDeadline,
            Messages,
            ct).ConfigureAwait(false);
    }

    private static readonly LaneHarness.AccessMessages Refusals = new(
        "AGENT_ROUTE_AUTHORIZATION_REQUIRED: the DOCX route runs only on the authorized OpenRouter route.",
        "DOCX_HEADING_SELECTOR_REQUIRED: the DOCX route has no authority without the V6 heading selector.",
        "DOCX_HEADING_SELECTOR_KEY_MISSING: the authorized route carries no provider key.");

    private static readonly LaneHarness.Messages Messages = new(
        "DOCX semantic execution exceeded its lane deadline.",
        "DOCX semantic execution failed.",
        "DOCX semantic result lost its execution lease.");
}
