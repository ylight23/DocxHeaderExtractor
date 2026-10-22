using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Structure;

namespace DocxHeaderExtractor.DocumentProcessing.Selection;

/// <summary>What an authority decided, plus what the shared stages need to continue.</summary>
/// <param name="Decisions">Exact heading extents the authority selected.</param>
/// <param name="BoundHeadings">The same headings as bound semantic claims, input to placement and hierarchy.</param>
/// <param name="PlacementAccess">The placer's access to the model for this run, tied to the lane lease; null when the model is disabled.</param>
/// <param name="PlacementEvidence">Source context, upstream states and optional style measurements for placement.</param>
/// <param name="Placer">The agent that proposes parents; placement runs only with it and an access.</param>
/// <param name="CompleteAudit">
/// Adds the authority's own audit facts (raw responses, contracts, lane telemetry) to the route audit
/// once the shared stages have run.
/// </param>
internal sealed record SelectionResult(
    IReadOnlyList<HeadingExtentDecision> Decisions,
    IReadOnlyList<CanonicalSemanticBoundHeading> BoundHeadings,
    ModelAccess? PlacementAccess,
    Func<RunAudit, StructureAssembly, RunAudit> CompleteAudit,
    HeadingPlacementEvidence? PlacementEvidence = null,
    IHeadingPlacer? Placer = null);
