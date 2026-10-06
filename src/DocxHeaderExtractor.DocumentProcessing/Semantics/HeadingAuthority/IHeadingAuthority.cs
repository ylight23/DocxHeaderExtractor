using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;

namespace DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;

/// <summary>
/// The one seam between source and structure. An authority reads the format-neutral occurrence
/// universe and answers which occurrences are headings and where each extent ends. Everything after
/// it - binding, placement, hierarchy, materialization - is shared and format-blind, so replacing a
/// lane's authority (for example promoting DOCX to a qualified protocol) touches only the
/// implementation behind this interface.
/// </summary>
internal interface IHeadingAuthority
{
    Task<HeadingAuthorityResult> DecideAsync(SourceOccurrenceUniverse source, CancellationToken cancellationToken);
}

/// <summary>What an authority decided, plus what the shared stages need to continue.</summary>
/// <param name="Decisions">Exact heading extents the authority selected.</param>
/// <param name="BoundHeadings">The same headings as bound semantic claims, input to placement and hierarchy.</param>
/// <param name="PlacementTransport">Transport for the bounded placement follow-up; null when none ran.</param>
/// <param name="CompleteAudit">
/// Adds the authority's own audit facts (raw responses, contracts, lane telemetry) to the route audit
/// once the shared stages have run.
/// </param>
internal sealed record HeadingAuthorityResult(
    IReadOnlyList<HeadingExtentDecision> Decisions,
    IReadOnlyList<CanonicalSemanticBoundHeading> BoundHeadings,
    IInferenceTransport? PlacementTransport,
    Func<RouteExecutionAudit, HeadingStructureAssembly, RouteExecutionAudit> CompleteAudit);
