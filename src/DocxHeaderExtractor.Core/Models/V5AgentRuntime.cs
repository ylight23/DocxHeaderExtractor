using System.Diagnostics;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

public enum AgentStage
{
    INGEST, OBSERVE, INITIAL_REASONING, VALIDATE, PLAN_EVIDENCE, RETRIEVE,
    LAYOUT_ESCALATE, VISUAL_ESCALATE, REVALIDATE, PROJECT, COMPLETE,
}

public sealed record SemanticReasoningContext(
    DocumentTaskContract TaskContract,
    UniversalEvidenceGraph EvidenceGraph,
    IReadOnlyList<EvidenceCandidate> RetrievedEvidence,
    int CallOrdinal,
    IReadOnlyList<BoundSemanticClaim> OpenOrConflictedClaims,
    V5SemanticDecisionRequestPacketV3 RequestPacket,
    V5ComposedSemanticDecisionRequestV3 Request);

public sealed record SemanticReasoningUsage(
    int PromptTokens = 0,
    int CompletionTokens = 0,
    int ReasoningTokens = 0)
{
    public int TotalTokens => checked(PromptTokens + CompletionTokens);
}

/// <summary>Live V3.1 sparse response. The compatibility constructor is test/replay-only: it maps
/// a historical V3.0 dense response by its harness-owned positional index before runtime binding.</summary>
public sealed record SemanticReasoningResult(V5SemanticSparseDecisionResponseV3_1 Response, SemanticReasoningUsage Usage)
{
    public SemanticReasoningResult(V5SemanticDecisionResponseV3 exhaustiveResponse, SemanticReasoningUsage usage)
        : this(V5SemanticSparseDecisionResponseV3_1.FromExhaustive(exhaustiveResponse), usage) { }
}

public interface ISemanticReasoner
{
    ValueTask<SemanticReasoningResult> ReasonAsync(SemanticReasoningContext context, CancellationToken cancellationToken);
    string Identity { get; }
}

public sealed record LayoutEvidenceRequest(string ClaimId, string Question, IReadOnlyList<string> EvidenceIds, int Budget);

public interface ILayoutEvidenceProvider
{
    ValueTask<IReadOnlyList<SourceObservation>> ObserveAsync(LayoutEvidenceRequest request, CancellationToken cancellationToken);
    string Identity { get; }
}

public sealed record VisualEvidenceRequest(
    string ClaimId, string Question, IReadOnlyList<string> EvidenceIds, int? Page, EvidenceGeometry? Region, int Budget);

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
    int VisualCalls,
    int LayoutCalls = 0,
    int TotalTokens = 0)
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
    private readonly ILayoutEvidenceProvider? _layout;
    private readonly ProjectionEngine? _projectionEngine;

    public DocumentAgentRuntime(
        ISemanticReasoner reasoner,
        IEvidenceRetriever retriever,
        EvidencePlanner? planner = null,
        IVisualEvidenceReasoner? visual = null,
        ILayoutEvidenceProvider? layout = null,
        ProjectionEngine? projectionEngine = null)
    {
        _reasoner = reasoner ?? throw new ArgumentNullException(nameof(reasoner));
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        _planner = planner ?? new EvidencePlanner();
        _visual = visual;
        _layout = layout;
        _projectionEngine = projectionEngine;
    }

    public async Task<DocumentAgentExecutionResult> RunAsync(
        DocumentTaskContract contract,
        UniversalEvidenceGraph evidenceGraph,
        IReadOnlyList<SemanticSourceAtom> atoms,
        IReadOnlySet<string>? ownedAliases = null,
        IReadOnlySet<string>? visibleAliases = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(evidenceGraph);
        ArgumentNullException.ThrowIfNull(atoms);
        contract.Validate();
        var budget = contract.ExecutionBudget;
        // Ownership is an explicit runtime input, never inferred from semantic content. A caller
        // that has not packed its evidence (the whole-document callers this runtime has always
        // supported) gets every atom owned and visible, which reproduces the exact behavior the
        // binder had before it enforced ownership at all. A caller that packs (a real PDF pack) is
        // expected to pass its own owned/visible alias sets.
        var owned = ownedAliases ?? atoms.Select(atom => atom.Alias).ToHashSet(StringComparer.Ordinal);
        var visible = visibleAliases ?? owned;
        var started = Stopwatch.GetTimestamp();
        var trace = new List<AgentExecutionTrace>();
        void Mark(AgentStage stage, string detail) => trace.Add(new(stage, DateTimeOffset.UtcNow, detail));
        Mark(AgentStage.INGEST, "task-contract-validated");
        Mark(AgentStage.OBSERVE, $"evidence-nodes={evidenceGraph.Nodes.Count}");

        var allClaims = new Dictionary<string, BoundSemanticClaim>(StringComparer.Ordinal);
        var allConflicts = new List<KnowledgeValidationIssue>();
        var retrieved = new List<EvidenceCandidate>();
        var retrievalByClaim = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var layoutByClaim = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var visualByClaim = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var refinementCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var workingGraph = evidenceGraph;
        var hashesByClaim = new Dictionary<string, (string Request, string Response)>(StringComparer.Ordinal);
        var semanticCalls = 0;
        var retrievalRounds = 0;
        var visualCalls = 0;
        var layoutCalls = 0;
        var totalTokens = 0;
        var exhausted = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ElapsedSeconds(started) > budget.MaxWallClockSeconds)
            {
                ExhaustOpenClaims(allClaims); exhausted = true; break;
            }
            if (semanticCalls >= budget.MaxSemanticModelCalls) break;
            if (budget.MaxTokens > 0 && totalTokens >= budget.MaxTokens)
            {
                ExhaustOpenClaims(allClaims); exhausted = true; break;
            }
            Mark(semanticCalls == 0 ? AgentStage.INITIAL_REASONING : AgentStage.REVALIDATE, $"semantic-call={semanticCalls + 1}");
            var openOrConflicted = allClaims.Values
                .Where(claim => claim.State is ClaimResolutionState.OPEN or ClaimResolutionState.CONFLICTED)
                .ToArray();
            var requestPacket = BuildDecisionPacket(contract, workingGraph, atoms, owned, visible, retrieved, openOrConflicted);
            var composedRequest = V5SemanticSparseDecisionComposerV3_1.Compose(contract, requestPacket);
            var context = new SemanticReasoningContext(contract, workingGraph, retrieved.ToArray(), semanticCalls, openOrConflicted, requestPacket, composedRequest);
            var requestHash = composedRequest.RequestHash;
            using var turnDeadline = CreateDeadlineToken(started, budget.MaxWallClockSeconds, cancellationToken);
            SemanticReasoningResult reasoning;
            try
            {
                reasoning = await _reasoner.ReasonAsync(context, turnDeadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                ExhaustOpenClaims(allClaims);
                exhausted = true;
                break;
            }
            var response = reasoning.Response;
            totalTokens = checked(totalTokens + reasoning.Usage.TotalTokens);
            var responseHash = Hashing.Sha256(JsonSerializer.Serialize(response, CanonicalJson.Options));
            // KnownClaims reflects the durable state as of THIS turn, so a refinement proposal's
            // existingClaimId is checked against what the runtime actually holds right now, not a
            // stale snapshot from an earlier turn.
            var knownClaims = allClaims.ToDictionary(
                item => item.Key,
                item => new KnownClaimReference(item.Value.Subject.Identity, item.Value.Predicate),
                StringComparer.Ordinal);
            var scope = ClaimBindingScope.Create(owned, visible, knownClaims);
            var decisionBinding = V5SemanticSparseDecisionContractV3_1.Bind(requestHash, response, contract,
                requestPacket.SubjectEvidence, requestPacket.ContextOnlyEvidence, atoms, scope);
            foreach (var refusal in decisionBinding.Refusals)
                allConflicts.Add(new KnowledgeValidationIssue("CLAIM_BINDING", refusal.Key, refusal.Value));
            foreach (var boundClaim in decisionBinding.Bound)
            {
                var claim = boundClaim.Claim;
                if (allClaims.TryGetValue(claim.ClaimId, out var previous) && !ClaimTransitionPolicy.IsAllowed(previous, claim))
                {
                    allConflicts.Add(new KnowledgeValidationIssue("ILLEGAL_CLAIM_TRANSITION", claim.ClaimId,
                        $"transition {previous.State}->{claim.State} or identity change is not allowed"));
                    continue;
                }
                allClaims[claim.ClaimId] = claim;
                hashesByClaim[claim.ClaimId] = (requestHash, responseHash);
            }
            semanticCalls++;

            if (budget.MaxTokens > 0 && totalTokens >= budget.MaxTokens)
            {
                ExhaustOpenClaims(allClaims); exhausted = true;
            }
            var state = new DocumentKnowledgeState(workingGraph, allClaims.Values, allConflicts);
            Mark(AgentStage.VALIDATE, $"claims={state.Claims.Count};open={state.OpenClaims.Count}");
            var graphIssues = KnowledgeGraphValidator.Validate(state, contract);
            allConflicts.AddRange(graphIssues);
            if (state.OpenClaims.Count == 0 && graphIssues.Count == 0) break;
            if (exhausted) break;

            Mark(AgentStage.PLAN_EVIDENCE, $"round={retrievalRounds + 1}");
            var plan = _planner.Plan(state, contract, retrievalRounds);
            if (plan.BudgetExhausted || plan.Actions.Count == 0)
            {
                ExhaustOpenClaims(allClaims);
                exhausted = true;
                break;
            }
            foreach (var action in plan.Actions)
            {
                var claim = state.Claims.FirstOrDefault(item => item.ClaimId == action.ClaimId);
                if (claim is null) continue;
                refinementCounts[action.ClaimId] = refinementCounts.GetValueOrDefault(action.ClaimId) + 1;
                if (refinementCounts[action.ClaimId] > budget.MaxUnresolvedRefinements)
                {
                    allClaims[action.ClaimId] = claim with { State = ClaimResolutionState.EXHAUSTED };
                    continue;
                }
                var aliases = claim.Subject.Parts.Select(part => part.Alias).ToHashSet(StringComparer.Ordinal);
                var sourceIds = workingGraph.Nodes.Where(node => aliases.Contains(node.SourceAlias)).Select(node => node.EvidenceId).ToArray();
                var sourceEvidenceId = sourceIds.FirstOrDefault();
                if (action.Modality == EvidenceModality.LAYOUT && _layout is null)
                {
                    allConflicts.Add(new KnowledgeValidationIssue("LAYOUT_PROVIDER_MISSING", claim.ClaimId,
                        "layout evidence was requested but no typed layout provider was registered"));
                    continue;
                }
                if (action.Modality == EvidenceModality.LAYOUT && layoutCalls >= budget.MaxLayoutCalls)
                {
                    allClaims[action.ClaimId] = claim with { State = ClaimResolutionState.EXHAUSTED };
                    continue;
                }
                if (action.Modality == EvidenceModality.LAYOUT && _layout is not null && layoutCalls < budget.MaxLayoutCalls)
                {
                    Mark(AgentStage.LAYOUT_ESCALATE, $"claim={action.ClaimId}");
                    var observations = await _layout.ObserveAsync(new LayoutEvidenceRequest(action.ClaimId,
                        $"Resolve evidence need {action.Need} for predicate {claim.Predicate}.", sourceIds,
                        budget.MaxLayoutCalls - layoutCalls), cancellationToken);
                    MergeObservations(ref workingGraph, observations, layoutByClaim, action.ClaimId);
                    layoutCalls++;
                    continue;
                }
                if (action.Modality == EvidenceModality.VISUAL && _visual is null)
                {
                    allConflicts.Add(new KnowledgeValidationIssue("VISUAL_PROVIDER_MISSING", claim.ClaimId,
                        "visual evidence was requested but no visual provider was registered"));
                    continue;
                }
                if (action.Modality == EvidenceModality.VISUAL && visualCalls >= budget.MaxVisualCalls)
                {
                    allClaims[action.ClaimId] = claim with { State = ClaimResolutionState.EXHAUSTED };
                    continue;
                }
                if (action.Modality == EvidenceModality.VISUAL && _visual is not null && visualCalls < budget.MaxVisualCalls)
                {
                    Mark(AgentStage.VISUAL_ESCALATE, $"claim={action.ClaimId}");
                    var region = workingGraph.Nodes.Where(node => aliases.Contains(node.SourceAlias)).Select(node => node.Anchor.Geometry).FirstOrDefault(value => value is not null);
                    var page = workingGraph.Nodes.Where(node => aliases.Contains(node.SourceAlias)).Select(node => node.Anchor.Geometry?.Page).FirstOrDefault(value => value is not null);
                    var observations = await _visual.InspectAsync(new VisualEvidenceRequest(action.ClaimId,
                        $"Resolve evidence need {action.Need} for predicate {claim.Predicate}.", sourceIds, page, region,
                        budget.MaxVisualCalls - visualCalls), cancellationToken);
                    MergeObservations(ref workingGraph, observations, visualByClaim, action.ClaimId);
                    visualCalls++;
                    continue;
                }
                var candidates = _retriever.Retrieve(new EvidenceRetrievalRequest(action.ClaimId, claim.Predicate,
                    action.Need, sourceEvidenceId, "task-contract", action.MaxResults), workingGraph);
                var remaining = Math.Max(0, budget.MaxRetrievedEvidenceNodes - retrieved.Count);
                var accepted = candidates.Take(remaining).ToArray();
                retrieved.AddRange(accepted);
                if (!retrievalByClaim.TryGetValue(action.ClaimId, out var ids)) retrievalByClaim[action.ClaimId] = ids = new();
                foreach (var item in accepted) ids.Add(item.EvidenceId);
            }
            retrievalRounds++;
            if (semanticCalls >= budget.MaxSemanticModelCalls)
            {
                ExhaustOpenClaims(allClaims);
                exhausted = true;
                break;
            }
        }

        var finalState = new DocumentKnowledgeState(workingGraph, allClaims.Values, allConflicts);
        var finalIssues = KnowledgeGraphValidator.Validate(finalState, contract);
        allConflicts.AddRange(finalIssues);
        IReadOnlyDictionary<string, ProjectionResult> projections = new Dictionary<string, ProjectionResult>(StringComparer.Ordinal);
        if (_projectionEngine is null && contract.Projections.Any(item => item.Required))
            throw new InvalidOperationException("projection-engine-missing");
        if (_projectionEngine is not null)
            projections = _projectionEngine.Project(finalState, contract).ToDictionary(item => item.ProjectionName, StringComparer.Ordinal);
        Mark(AgentStage.PROJECT, $"projections={projections.Count}");
        Mark(AgentStage.COMPLETE, allConflicts.Count == 0 ? "resolved" : "completed-with-open-or-conflicted-claims");
        return Finish(allClaims.Values.ToArray(), workingGraph, allConflicts, trace, exhausted,
            semanticCalls, retrievalRounds, visualCalls, layoutCalls, totalTokens, contract, retrieved,
            hashesByClaim, retrievalByClaim, layoutByClaim, visualByClaim, _reasoner.Identity, projections);
    }

    private static V5SemanticDecisionRequestPacketV3 BuildDecisionPacket(
        DocumentTaskContract contract,
        UniversalEvidenceGraph graph,
        IReadOnlyList<SemanticSourceAtom> atoms,
        IReadOnlySet<string> ownedAliases,
        IReadOnlySet<string> visibleAliases,
        IReadOnlyList<EvidenceCandidate> retrieved,
        IReadOnlyList<BoundSemanticClaim> openOrConflicted)
    {
        var atomsByAlias = atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        if (ownedAliases.Any(alias => !atomsByAlias.ContainsKey(alias)) || visibleAliases.Any(alias => !atomsByAlias.ContainsKey(alias)))
            throw new InvalidOperationException("runtime-ownership-alias-not-in-source-atoms");
        var nodesByAlias = graph.Nodes.GroupBy(node => node.SourceAlias, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(node => node.Modality == EvidenceModality.TEXT ? 0 : 1)
                    .ThenBy(node => node.SourceOrdinal).ThenBy(node => node.EvidenceId, StringComparer.Ordinal).First(),
                StringComparer.Ordinal);
        EvidenceNode Resolve(string alias)
        {
            if (nodesByAlias.TryGetValue(alias, out var node)) return node;
            var atom = atomsByAlias[alias];
            var span = new StructuralSpan(0, atom.Text.Length);
            return new EvidenceNode($"V5:{atom.SourceId}", atom.SourceId, atom.Alias, atom.Ordinal,
                EvidenceModality.TEXT, atom.Text, new EvidenceAnchor(atom.SourceId, atom.Ordinal, span,
                    atom.Page > 0 ? new EvidenceGeometry(atom.Page) : null), new Dictionary<string, string?>(StringComparer.Ordinal));
        }
        var orderedOwned = atoms.Where(atom => ownedAliases.Contains(atom.Alias)).OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal)
            .Select(atom => Resolve(atom.Alias)).ToArray();
        var orderedContext = atoms.Where(atom => visibleAliases.Contains(atom.Alias) && !ownedAliases.Contains(atom.Alias))
            .OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).Select(atom => Resolve(atom.Alias)).ToArray();
        var layout = graph.Nodes.Where(node => node.Modality == EvidenceModality.LAYOUT).ToArray();
        var visual = graph.Nodes.Where(node => node.Modality == EvidenceModality.VISUAL).ToArray();
        return new V5SemanticDecisionRequestPacketV3(orderedOwned, orderedContext, openOrConflicted, retrieved, layout, visual);
    }

    private static void MergeObservations(ref UniversalEvidenceGraph graph, IReadOnlyList<SourceObservation> observations,
        IDictionary<string, HashSet<string>> byClaim, string claimId)
    {
        if (observations.Count == 0) return;
        var added = EvidenceGraphBuilder.Build(observations);
        graph = new UniversalEvidenceGraph(graph.Nodes.Concat(added.Nodes), graph.Relations.Concat(added.Relations));
        if (!byClaim.TryGetValue(claimId, out var ids)) byClaim[claimId] = ids = new();
        foreach (var item in added.Nodes) ids.Add(item.EvidenceId);
    }

    private static void ExhaustOpenClaims(IDictionary<string, BoundSemanticClaim> claims)
    {
        foreach (var item in claims.Where(item => item.Value.State is ClaimResolutionState.OPEN or ClaimResolutionState.CONFLICTED).ToArray())
            claims[item.Key] = item.Value with { State = ClaimResolutionState.EXHAUSTED };
    }

    private static DocumentAgentExecutionResult Finish(
        IReadOnlyList<BoundSemanticClaim> claims, UniversalEvidenceGraph graph, IReadOnlyList<KnowledgeValidationIssue> conflicts,
        IReadOnlyList<AgentExecutionTrace> trace, bool exhausted, int semanticCalls, int retrievalRounds, int visualCalls,
        int layoutCalls, int totalTokens, DocumentTaskContract contract, IReadOnlyList<EvidenceCandidate> retrieved,
        IReadOnlyDictionary<string, (string Request, string Response)> hashesByClaim,
        IReadOnlyDictionary<string, HashSet<string>> retrievalByClaim, IReadOnlyDictionary<string, HashSet<string>> layoutByClaim,
        IReadOnlyDictionary<string, HashSet<string>> visualByClaim, string modelIdentity,
        IReadOnlyDictionary<string, ProjectionResult> projections)
    {
        var contractHash = contract.Hash();
        var graphHash = graph.Hash();
        var provenance = claims.Select(claim =>
        {
            var evidenceIds = graph.Nodes.Where(node => claim.Subject.Parts.Any(part => part.Alias == node.SourceAlias))
                .Select(node => node.EvidenceId).Distinct(StringComparer.Ordinal).ToArray();
            var hashes = hashesByClaim.GetValueOrDefault(claim.ClaimId);
            return new ClaimProvenance(claim.ClaimId, contractHash, graphHash, evidenceIds, hashes.Request, hashes.Response,
                null, modelIdentity, "v5-exact-source-parts-1", "v5-knowledge-validator-1",
                retrievalByClaim.GetValueOrDefault(claim.ClaimId)?.OrderBy(item => item, StringComparer.Ordinal).ToArray() ?? [],
                visualByClaim.GetValueOrDefault(claim.ClaimId)?.OrderBy(item => item, StringComparer.Ordinal).ToArray() ?? [],
                claim.State, projections.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray()) with
            {
                LayoutEvidenceIds = layoutByClaim.GetValueOrDefault(claim.ClaimId)?.OrderBy(item => item, StringComparer.Ordinal).ToArray() ?? [],
            };
        }).ToArray();
        var state = new DocumentKnowledgeState(graph, claims, conflicts, provenance, projections);
        return new DocumentAgentExecutionResult(state, AgentStage.COMPLETE, trace, exhausted,
            semanticCalls, retrievalRounds, visualCalls, layoutCalls, totalTokens) { Provenance = provenance };
    }

    private static double ElapsedSeconds(long started) => (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;

    private static CancellationTokenSource CreateDeadlineToken(long started, int maxSeconds, CancellationToken caller)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(caller);
        var remaining = Math.Max(1, (int)Math.Ceiling(maxSeconds - ElapsedSeconds(started)));
        source.CancelAfter(TimeSpan.FromSeconds(remaining));
        return source;
    }
}
