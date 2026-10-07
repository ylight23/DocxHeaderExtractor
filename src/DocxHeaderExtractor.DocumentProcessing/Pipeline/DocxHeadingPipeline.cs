using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Semantics.Validation;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.DocumentProcessing.Source.Docx;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The normal DOCX route: DOCX source adapter, then the canonical-text heading authority, then the
/// shared binding, placement, hierarchy and materialization. The inference transport is only a
/// provider seam; semantic output crosses into the Core vNext contract before binding and never
/// supplies coordinates or hierarchy truth.
/// </summary>
internal static class DocxHeadingPipeline
{
    internal const string RouteId = "docx-canonical-vnext";

    public static async Task<StructuralAuthorityResult> RunAsync(
        SourceDocument sourceDocument,
        IInferenceTransport? transport,
        CancellationToken cancellationToken,
        CanonicalSemanticExperiment? experiment = null)
    {
        ArgumentNullException.ThrowIfNull(sourceDocument);
        var source = DocxSourceAdapter.Build(sourceDocument).Snapshot;
        if (source.Occurrences.Count == 0)
            return new StructuralAuthorityResult(ValidatedStructureFactory.Create([]), null, "empty-docx-source");

        IHeadingAuthority authority = new TextSemanticHeadingAuthority(transport, experiment);
        var decided = await authority.DecideAsync(source, cancellationToken).ConfigureAwait(false);
        // The canonical-text authority defines semantic node identity, so a repeated heading keeps
        // one primary occurrence in the outline.
        var assembly = await HeadingStructureAssembler.AssembleAsync(
            source, decided, PrimaryOccurrenceSelection.FirstOfSemanticNode, cancellationToken).ConfigureAwait(false);

        var blocks = source.Occurrences.Select(item => new SourceBlockAudit(item.Id, 0, item.Text)).ToArray();
        var audit = ExecutionAuditBoundary.Create(
            RouteId,
            blocks.Length,
            blocks.Length,
            0,
            0,
            blocks,
            blocks,
            decided.Decisions.Select(decision => new SourceBlockDecisionAudit(decision.Id, decision.SemanticFunction)).ToArray(),
            assembly.Validated.Select(item => item.SourceId).ToArray());
        return new StructuralAuthorityResult(
            assembly.Structure,
            decided.CompleteAudit(audit, assembly),
            "docx-canonical-vnext-semantic-authority") { ProjectionContext = assembly.ProjectionContext };
    }
}
