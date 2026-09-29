using System.Collections.ObjectModel;

namespace DocxHeaderExtractor.Core.V5;

public sealed record EvidenceRetrievalRequest(
    string ClaimId,
    string Predicate,
    EvidenceNeed Need,
    string? OriginatingEvidenceId,
    string AllowedScope,
    int MaxResults);

/// <summary>Retrieval output is a candidate only; it is never a semantic decision.</summary>
public sealed record EvidenceCandidate(
    string EvidenceId,
    int Rank,
    string Method,
    string MatchReason);

public interface IEvidenceRetriever
{
    IReadOnlyList<EvidenceCandidate> Retrieve(EvidenceRetrievalRequest request, UniversalEvidenceGraph graph);
}

/// <summary>Deterministic exact/lexical retriever for tests and small deployments.</summary>
public sealed class InMemoryEvidenceRetriever : IEvidenceRetriever
{
    public IReadOnlyList<EvidenceCandidate> Retrieve(EvidenceRetrievalRequest request, UniversalEvidenceGraph graph)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(graph);
        var query = graph.Nodes.FirstOrDefault(node => node.EvidenceId == request.OriginatingEvidenceId)?.Text ?? string.Empty;
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        return graph.Nodes
            .Where(node => node.EvidenceId != request.OriginatingEvidenceId)
            .Select(node =>
            {
                var overlap = node.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(item => item.ToUpperInvariant()).Count(terms.Contains);
                var exact = terms.Count > 0 && node.Text.Contains(query, StringComparison.OrdinalIgnoreCase);
                return new { node, overlap, exact };
            })
            .Where(item => item.exact || item.overlap > 0)
            .OrderByDescending(item => item.exact)
            .ThenByDescending(item => item.overlap)
            .ThenBy(item => item.node.SourceOrdinal)
            .ThenBy(item => item.node.EvidenceId, StringComparer.Ordinal)
            .Take(Math.Max(0, request.MaxResults))
            .Select((item, index) => new EvidenceCandidate(
                item.node.EvidenceId,
                index + 1,
                item.exact ? "EXACT_NORMALIZED" : "LEXICAL",
                item.exact ? "text-exact" : "token-overlap"))
            .ToArray();
    }
}

public sealed record EvidencePlanAction(
    string ClaimId,
    EvidenceNeed Need,
    EvidenceModality Modality,
    int Round,
    int MaxResults);

public sealed record EvidencePlan(
    IReadOnlyList<EvidencePlanAction> Actions,
    bool BudgetExhausted);

/// <summary>
/// Escalation policy is driven by explicit unresolved state and evidence need, never raw confidence.
/// </summary>
public sealed class EvidencePlanner
{
    public EvidencePlan Plan(DocumentKnowledgeState state, DocumentTaskContract contract, int round)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(contract);
        contract.Validate();
        if (round >= contract.ExecutionBudget.MaxRetrievalRounds)
            return new([], true);

        var allowed = contract.EvidencePolicy.Modalities.ToHashSet();
        var actions = new List<EvidencePlanAction>();
        foreach (var claim in state.OpenClaims.Concat(state.Claims.Where(item => item.State == ClaimResolutionState.CONFLICTED)))
        {
            foreach (var need in claim.EvidenceNeeds)
            {
                var modality = ModalityFor(need);
                if (!allowed.Contains(modality)) continue;
                actions.Add(new(claim.ClaimId, need, modality, round, Math.Max(1, contract.ExecutionBudget.MaxRetrievedEvidenceNodes)));
            }
        }
        return new(actions, false);
    }

    private static EvidenceModality ModalityFor(EvidenceNeed need) => need switch
    {
        EvidenceNeed.LAYOUT_EVIDENCE => EvidenceModality.LAYOUT,
        EvidenceNeed.VISUAL_EVIDENCE => EvidenceModality.VISUAL,
        _ => EvidenceModality.TEXT,
    };
}
