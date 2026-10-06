using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// One claim after exact binding: the harness's own durable identity plus harness-resolved
/// coordinates. This is the runtime's only currency - the graph, transition policy, projections and
/// provenance all operate on this record and never on a model-facing proposal/response type, which is
/// exactly why the wire protocol could evolve to v2.1 without touching any of them.
/// </summary>
public sealed record BoundSemanticClaim(
    string ClaimId,
    BoundClaimEndpoint Subject,
    string Predicate,
    string? Value,
    BoundClaimEndpoint? Object,
    ClaimResolutionState State,
    IReadOnlyList<EvidenceNeed> EvidenceNeeds)
{
    public string Identity => $"{Subject.Identity}|{Predicate}|{Object?.Identity ?? Value ?? string.Empty}";
}
