using System.Diagnostics;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

public enum AgentStage
{
    INGEST,
    OBSERVE,
    INITIAL_REASONING,
    VALIDATE,
    PLAN_EVIDENCE,
    RETRIEVE,
    LAYOUT_ESCALATE,
    VISUAL_ESCALATE,
    REVALIDATE,
    PROJECT,
    COMPLETE,
}

public sealed record SemanticReasoningContext(
    DocumentTaskContract TaskContract,
    UniversalEvidenceGraph EvidenceGraph,
    IReadOnlyList<EvidenceCandidate> RetrievedEvidence,
    int CallOrdinal);

public interface ISemanticReasoner
{
    ValueTask<SemanticClaimResponse> ReasonAsync(SemanticReasoningContext context, CancellationToken cancellationToken);
    string Identity { get; }
}

public sealed record VisualEvidenceRequest(
    string ClaimId,
    string Question,
    IReadOnlyList<string> EvidenceIds,
    int? Page,
    EvidenceGeometry? Region,
    int Budget);

public interface IVisualEvidenceReasoner
{
    ValueTask<IReadOnlyList<SourceObservation>> InspectAsync(VisualEvidenceRequest request, CancellationToken cancellationToken);
    string Identity { get; }
}

public sealed record AgentExecutionTrace(AgentStage Stage, DateTimeOffset At, string Detail);

public sealed record DocumentAgentExecutionResult(
    DocumentKnowledgeState State,
    AgentStage TerminalStage,
    IReadOnlyList<AgentExecutionTrace> Trace,
    bool BudgetExhausted,
    int SemanticModelCalls,
    int RetrievalRounds,
    int VisualCalls);

/// <summary>Provider-neutral deterministic workflow. It owns transitions, not semantic judgement.</summary>
public sealed class DocumentAgentRuntime
{
    private readonly ISemanticReasoner _reasoner;
    private readonly IEvidenceRetriever _retriever;
    private readonly EvidencePlanner _planner;
    private readonly IVisualEvidenceReasoner? _visual;

    public DocumentAgentRuntime(
        ISemanticReasoner reasoner,
        IEvidenceRetriever retriever,
        EvidencePlanner? planner = null,
        IVisualEvidenceReasoner? visual = null)
    {
        _reasoner = reasoner ?? throw new ArgumentNullException(nameof(reasoner));
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        _planner = planner ?? new EvidencePlanner();
        _visual = visual;
    }

    public async Task<DocumentAgentExecutionResult> RunAsync(
        DocumentTaskContract contract,
        UniversalEvidenceGraph evidenceGraph,
        IReadOnlyList<SemanticSourceAtom> atoms,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(evidenceGraph);
        ArgumentNullException.ThrowIfNull(atoms);
        contract.Validate();
        var budget = contract.ExecutionBudget;
        var started = Stopwatch.GetTimestamp();
        var trace = new List<AgentExecutionTrace>();
        void Mark(AgentStage stage, string detail) => trace.Add(new(stage, DateTimeOffset.UtcNow, detail));
        Mark(AgentStage.INGEST, "task-contract-validated");
        Mark(AgentStage.OBSERVE, $"evidence-nodes={evidenceGraph.Nodes.Count}");

        var allClaims = new List<BoundSemanticClaim>();
        var allConflicts = new List<KnowledgeValidationIssue>();
        var retrieved = new List<EvidenceCandidate>();
        var semanticCalls = 0;
        var retrievalRounds = 0;
        var visualCalls = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ElapsedSeconds(started) > budget.MaxWallClockSeconds)
                return Finish(allClaims, evidenceGraph, allConflicts, trace, true, semanticCalls, retrievalRounds, visualCalls);

            if (semanticCalls >= budget.MaxSemanticModelCalls)
                break;
            Mark(semanticCalls == 0 ? AgentStage.INITIAL_REASONING : AgentStage.REVALIDATE, $"semantic-call={semanticCalls + 1}");
            var response = await _reasoner.ReasonAsync(
                new SemanticReasoningContext(contract, evidenceGraph, retrieved, semanticCalls), cancellationToken);
            var contractIssues = SemanticClaimContract.Validate(response, contract);
            if (contractIssues.Count > 0)
            {
                allConflicts.AddRange(contractIssues.Select(issue => new KnowledgeValidationIssue("CLAIM_CONTRACT", null, issue)));
                break;
            }
            var binding = ExactClaimBinder.Bind(response.Claims, atoms);
            allClaims.AddRange(binding.Bound);
            allConflicts.AddRange(binding.Refusals.Select(item => new KnowledgeValidationIssue("CLAIM_BINDING", item.Key, item.Value)));
            semanticCalls++;

            var state = new DocumentKnowledgeState(evidenceGraph, allClaims, allConflicts);
            Mark(AgentStage.VALIDATE, $"claims={state.Claims.Count};open={state.OpenClaims.Count}");
            var graphIssues = KnowledgeGraphValidator.Validate(state, contract);
            allConflicts.AddRange(graphIssues);
            if (state.OpenClaims.Count == 0 && graphIssues.Count == 0) break;

            Mark(AgentStage.PLAN_EVIDENCE, $"round={retrievalRounds + 1}");
            var plan = _planner.Plan(state, contract, retrievalRounds);
            if (plan.BudgetExhausted || plan.Actions.Count == 0) break;
            Mark(AgentStage.RETRIEVE, $"actions={plan.Actions.Count}");
            foreach (var action in plan.Actions)
            {
                var source = state.Claims.FirstOrDefault(item => item.ClaimId == action.ClaimId)?.Subject.Identity;
                var candidate = _retriever.Retrieve(
                    new EvidenceRetrievalRequest(action.ClaimId, state.Claims.First(item => item.ClaimId == action.ClaimId).Predicate,
                        action.Need, evidenceGraph.Nodes.FirstOrDefault(node => node.SourceAlias == source)?.EvidenceId,
                        "task-contract", action.MaxResults), evidenceGraph);
                retrieved.AddRange(candidate.Take(Math.Max(0, budget.MaxRetrievedEvidenceNodes - retrieved.Count)));
            }
            retrievalRounds++;
            if (retrievalRounds >= budget.MaxRetrievalRounds || semanticCalls >= budget.MaxSemanticModelCalls) break;
        }

        var finalState = new DocumentKnowledgeState(evidenceGraph, allClaims, allConflicts);
        var finalIssues = KnowledgeGraphValidator.Validate(finalState, contract);
        allConflicts.AddRange(finalIssues);
        Mark(AgentStage.PROJECT, "projection-input-frozen");
        Mark(AgentStage.COMPLETE, allConflicts.Count == 0 ? "resolved" : "completed-with-open-or-conflicted-claims");
        return Finish(allClaims, evidenceGraph, allConflicts, trace, semanticCalls >= budget.MaxSemanticModelCalls && finalState.OpenClaims.Count > 0,
            semanticCalls, retrievalRounds, visualCalls);
    }

    private static DocumentAgentExecutionResult Finish(
        IReadOnlyList<BoundSemanticClaim> claims,
        UniversalEvidenceGraph graph,
        IReadOnlyList<KnowledgeValidationIssue> conflicts,
        IReadOnlyList<AgentExecutionTrace> trace,
        bool exhausted,
        int semanticCalls,
        int retrievalRounds,
        int visualCalls) => new(
            new DocumentKnowledgeState(graph, claims, conflicts),
            AgentStage.COMPLETE,
            trace,
            exhausted,
            semanticCalls,
            retrievalRounds,
            visualCalls);

    private static double ElapsedSeconds(long started) => (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
}
