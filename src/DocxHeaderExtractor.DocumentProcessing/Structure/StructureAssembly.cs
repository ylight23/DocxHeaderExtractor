using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Hierarchy;
using DocxHeaderExtractor.DocumentProcessing.Output.Outline;
using DocxHeaderExtractor.DocumentProcessing.Selection;

namespace DocxHeaderExtractor.DocumentProcessing.Structure;

/// <summary>Validated headings, their resolved placement and the canonical structure built from them.</summary>
internal sealed record StructureAssembly(
    IReadOnlyList<ValidatedHeading> Validated,
    IReadOnlyDictionary<string, ResolvedHierarchy> Hierarchies,
    ValidatedStructure Structure,
    ProjectionContext ProjectionContext)
{
    public PlacementAudit? PlacementObservation { get; init; }
}
