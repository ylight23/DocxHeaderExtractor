using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Explicit boundary from canonical validated structure to product-facing projections.
/// <para>
/// The boundary delegates to the existing projection implementation. It deliberately accepts
/// already-materialized facts and never resolves semantic identity, hierarchy, or provider work.
/// </para>
/// </summary>
internal static class CanonicalProjectionBoundary
{
    internal static PdfFinalStructure ProjectPdfFinalStructure(
        string sourceDocumentSha256,
        RouteExecutionAudit audit,
        ValidatedStructure structure) =>
        PdfFinalStructureProjection.Project(
            sourceDocumentSha256,
            audit.ValidatedStructures,
            audit.HierarchyFacts,
            PdfCanonicalGrounding.FromValidatedStructure(structure));
}
