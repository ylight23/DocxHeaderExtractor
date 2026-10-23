using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Provider;

namespace DocxHeaderExtractor.Contracts.Agents;

/// <summary>
/// The parent-placement agent: for each settled heading, it proposes the heading whose section contains it. It decides nothing; the
/// pipeline checks every proposal against the graph (an earlier heading, ROOT, NONE or UNRESOLVED) and keeps only what passes.
/// <para>
/// The access to the model is given per call, not held: the pipeline ties it to the lane's lease for each run, so a call after the lane
/// has ended is refused.
/// </para>
/// </summary>
public interface IHeadingPlacer
{
    /// <summary>The protocol the placer speaks, recorded in the route audit (for example its protocolVersion).</summary>
    string Contract { get; }

    Task<PlacementProposal> ProposeAsync(PlacementRequest request, ModelAccess access, CancellationToken ct);
}

/// <summary>What the placer reads.</summary>
/// <param name="Ordered">The settled headings in document order. A heading that already carries a parent relation keeps it.</param>
/// <param name="Evidence">The source facts and style measurements; <see cref="HeadingPlacementEvidence.Source"/> is required.</param>
public sealed record PlacementRequest(IReadOnlyList<CanonicalSemanticBoundHeading> Ordered, HeadingPlacementEvidence Evidence);

/// <summary>What the placer proposed, and how it got there. Nothing here is accepted yet.</summary>
/// <param name="Parents">Every proposed relation, alias to parent, in the order they were settled: relations the headings already carried first,
/// then the placer's, batch by batch.</param>
/// <param name="Basis">Per heading: UPSTREAM_PROPOSAL, MODEL_PROPOSAL or PROTOCOL_NO_PROPOSAL.</param>
/// <param name="Calls">Every model call, in order, without source text, prompt or completion.</param>
/// <param name="Failures">Units that ended without a valid proposal, by unit id.</param>
/// <param name="ModelEvidence">The raw requests and replies, by unit and round; kept out of the serialized audit.</param>
/// <param name="LogicalCalls">How many calls were sent.</param>
public sealed record PlacementProposal(
    IReadOnlyList<KeyValuePair<string, string>> Parents,
    IReadOnlyDictionary<string, string> Basis,
    IReadOnlyList<PlacementCall> Calls,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> ModelEvidence,
    int LogicalCalls);

/// <summary>Safe response-envelope diagnostics of one placement call; no source, prompt or provider completion text.
/// JSON error offsets are zero-based within the parsed content after removing an optional single JSON fence.</summary>
public sealed record PlacementCall(string UnitId, string Phase, int Round, int TargetCount,
    int RequestedMaxOutputTokens, int? MaxOutputTokens, string? FinishReason, int ResponseBytes, bool JsonFence,
    double Seconds, IReadOnlyList<string> ProtocolErrors, IReadOnlyList<string> AcceptedAliases)
{
    public long? JsonErrorLine { get; init; }
    public long? JsonErrorBytePosition { get; init; }
}
