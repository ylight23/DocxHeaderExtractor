using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Inference;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Structure;

namespace DocxHeaderExtractor.DocumentProcessing.Selection;

/// <summary>What an authority decided, plus what the shared stages need to continue.</summary>
/// <param name="Decisions">Exact heading extents the authority selected.</param>
/// <param name="BoundHeadings">The same headings as bound semantic claims, input to placement and hierarchy.</param>
/// <param name="PlacementTransport">Transport for the bounded placement follow-up; null when none ran.</param>
/// <param name="PlacementEvidence">Source context, upstream states and optional style measurements for placement.</param>
/// <param name="Placer">The agent that proposes parents; placement runs only with it and a transport.</param>
/// <param name="CompleteAudit">
/// Adds the authority's own audit facts (raw responses, contracts, lane telemetry) to the route audit
/// once the shared stages have run.
/// </param>
internal sealed record SelectionResult(
    IReadOnlyList<HeadingExtentDecision> Decisions,
    IReadOnlyList<CanonicalSemanticBoundHeading> BoundHeadings,
    IInferenceTransport? PlacementTransport,
    Func<RunAudit, StructureAssembly, RunAudit> CompleteAudit,
    HeadingPlacementEvidence? PlacementEvidence = null,
    IHeadingPlacer? Placer = null);
