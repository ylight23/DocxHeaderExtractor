using System.Diagnostics;
using System.Text.Json;
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
    int VisualCalls)
{
    public IReadOnlyList<ClaimProvenance> Provenance { get; init; } = [];
}

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

        var allClaims = new Dictionary<string, BoundSemanticClaim>(StringComparer.Ordinal);
        var allConflicts = new List<KnowledgeValidationIssue>();
        var retrieved = new List<EvidenceCandidate>();
        var workingGraph = evidenceGraph;
        var hashesByClaim = new Dictionary<string, (string Request, string Response)>(StringComparer.Ordinal);
        var semanticCalls = 0;
        var retrievalRounds = 0;
        var visualCalls = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ElapsedSeconds(started) > budget.MaxWallClockSeconds)
                return Finish(allClaims.Values.ToArray(), workingGraph, allConflicts, trace, true, semanticCalls, retrievalRounds,
                    visualCalls, contract, retrieved, hashesByClaim, _reasoner.Identity);

            if (semanticCalls >= budget.MaxSemanticModelCalls)
                break;
            Mark(semanticCalls == 0 ? AgentStage.INITIAL_REASONING : AgentStage.REVALIDATE, $"semantic-call={semanticCalls + 1}");
            var context = new SemanticReasoningContext(contract, workingGraph, retrieved, semanticCalls);
            var requestHash = Hashing.Sha256(JsonSerializer.Serialize(new
            {
                contract = contract.Hash(),
                graph = workingGraph.Hash(),
                call = semanticCalls,
                retrieved,
            }, CanonicalJson.Options));
            var response = await _reasoner.ReasonAsync(context, cancellationToken);
            var responseHash = Hashing.Sha256(JsonSerializer.Serialize(response, CanonicalJson.Options));
            var contractIssues = SemanticClaimContract.Validate(response, contract);
            if (contractIssues.Count > 0)
            {
                allConflicts.AddRange(contractIssues.Select(issue => new KnowledgeValidationIssue("CLAIM_CONTRACT", null, issue)));
                break;
            }
            var binding = ExactClaimBinder.Bind(response.Claims, atoms);
            foreach (var claim in binding.Bound)
            {
                allClaims[claim.ClaimId] = claim;
                hashesByClaim[claim.ClaimId] = (requestHash, responseHash);
            }
            allConflicts.AddRange(binding.Refusals.Select(item => new KnowledgeValidationIssue("CLAIM_BINDING", item.Key, item.Value)));
            semanticCalls++;

            var state = new DocumentKnowledgeState(workingGraph, allClaims.Values, allConflicts);
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
                var claim = state.Claims.FirstOrDefault(item => item.ClaimId == action.ClaimId);
                if (claim is null) continue;
                var sourceAlias = claim.Subject.Parts.FirstOrDefault()?.Alias;
                var sourceEvidenceId = workingGraph.Nodes.FirstOrDefault(node => node.SourceAlias == sourceAlias)?.EvidenceId;
                if (action.Modality == EvidenceModality.VISUAL && _visual is not null && visualCalls < budget.MaxVisualCalls)
                {
                    Mark(AgentStage.VISUAL_ESCALATE, $"claim={action.ClaimId}");
                    var aliases = claim.Subject.Parts.Select(part => part.Alias).ToHashSet(StringComparer.Ordinal);
                    var visualRequest = new VisualEvidenceRequest(
                        claim.ClaimId,
                        $"Resolve evidence need {action.Need} for predicate {claim.Predicate}.",
                        workingGraph.Nodes.Where(node => aliases.Contains(node.SourceAlias)).Select(node => node.EvidenceId).ToArray(),
                        workingGraph.Nodes.Where(node => aliases.Contains(node.SourceAlias)).Select(node => node.Anchor.Geometry?.Page).FirstOrDefault(value => value is not null),
                        workingGraph.Nodes.Where(node => aliases.Contains(node.SourceAlias)).Select(node => node.Anchor.Geometry).FirstOrDefault(value => value is not null),
                        budget.MaxVisualCalls - visualCalls);
                    var visualObservations = await _visual.InspectAsync(visualRequest, cancellationToken);
                    if (visualObservations.Count > 0)
                    {
                        var visualGraph = EvidenceGraphBuilder.Build(visualObservations);
                        workingGraph = new UniversalEvidenceGraph(
                            workingGraph.Nodes.Concat(visualGraph.Nodes),
                            workingGraph.Relations.Concat(visualGraph.Relations));
                    }
                    visualCalls++;
                    continue;
                }
                var candidate = _retriever.Retrieve(
                    new EvidenceRetrievalRequest(action.ClaimId, claim.Predicate, action.Need, sourceEvidenceId,
                        "task-contract", action.MaxResults), workingGraph);
                retrieved.AddRange(candidate.Take(Math.Max(0, budget.MaxRetrievedEvidenceNodes - retrieved.Count)));
            }
            retrievalRounds++;
            if (semanticCalls >= budget.MaxSemanticModelCalls) break;
        }

        var finalState = new DocumentKnowledgeState(workingGraph, allClaims.Values, allConflicts);
        var finalIssues = KnowledgeGraphValidator.Validate(finalState, contract);
        allConflicts.AddRange(finalIssues);
        Mark(AgentStage.PROJECT, "projection-input-frozen");
        Mark(AgentStage.COMPLETE, allConflicts.Count == 0 ? "resolved" : "completed-with-open-or-conflicted-claims");
        return Finish(allClaims.Values.ToArray(), workingGraph, allConflicts, trace,
            semanticCalls >= budget.MaxSemanticModelCalls && finalState.OpenClaims.Count > 0,
            semanticCalls, retrievalRounds, visualCalls, contract, retrieved, hashesByClaim, _reasoner.Identity);
    }

    private static DocumentAgentExecutionResult Finish(
        IReadOnlyList<BoundSemanticClaim> claims,
        UniversalEvidenceGraph graph,
        IReadOnlyList<KnowledgeValidationIssue> conflicts,
        IReadOnlyList<AgentExecutionTrace> trace,
        bool exhausted,
        int semanticCalls,
        int retrievalRounds,
        int visualCalls,
        DocumentTaskContract contract,
        IReadOnlyList<EvidenceCandidate> retrieved,
        IReadOnlyDictionary<string, (string Request, string Response)> hashesByClaim,
        string modelIdentity)
    {
        var contractHash = contract.Hash();
        var graphHash = graph.Hash();
        var provenance = claims.Select(claim =>
        {
            var evidenceIds = graph.Nodes
                .Where(node => claim.Subject.Parts.Any(part => part.Alias == node.SourceAlias))
                .Select(node => node.EvidenceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var hashes = hashesByClaim.GetValueOrDefault(claim.ClaimId);
            return new ClaimProvenance(
                claim.ClaimId,
                contractHash,
                graphHash,
                evidenceIds,
                hashes.Request,
                hashes.Response,
                null,
                modelIdentity,
                "v5-exact-source-parts-1",
                "v5-knowledge-validator-1",
                retrieved.Select(item => item.EvidenceId).Distinct(StringComparer.Ordinal).ToArray(),
                [],
                claim.State,
                contract.Projections.Select(item => item.Name).ToArray());
        }).ToArray();
        var state = new DocumentKnowledgeState(graph, claims, conflicts, provenance);
        return new DocumentAgentExecutionResult(state, AgentStage.COMPLETE, trace, exhausted,
            semanticCalls, retrievalRounds, visualCalls)
        {
            Provenance = provenance,
        };
    }

    private static double ElapsedSeconds(long started) => (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
}
