using DocxHeaderExtractor.Infrastructure.AI.QualifiedInference;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

public sealed class V5ArchitectureTests
{
    [Fact]
    public void Task_contract_is_generic_serializable_and_hashable()
    {
        var contract = Contract();
        contract.Validate();
        Assert.Equal(64, contract.Hash().Length);
        Assert.Equal(contract.Hash(), (contract with { }).Hash());
        Assert.DoesNotContain("Heading", contract.Hash(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evidence_graph_rejects_semantic_candidate_facts()
    {
        var observation = Observation("E1", "A1", "Source text", new Dictionary<string, string?>
        {
            ["candidateScore"] = "10",
        });
        Assert.Throws<InvalidOperationException>(() => EvidenceGraphBuilder.Build([observation]));
    }

    [Fact]
    public void Exact_claim_binding_refuses_unquoted_or_unknown_source()
    {
        var atoms = Atoms();
        var scope = AllOwnedScope(atoms);
        var proposals = new SemanticClaimProposalV2_1[]
        {
            new(new ClaimSourceEndpointV2_1([
                new ProviderSourcePartV2_1("A1", "not present")]), "RELATES_TO"),
            new(new ClaimSourceEndpointV2_1([
                new ProviderSourcePartV2_1("UNKNOWN")]), "RELATES_TO"),
        };

        var result = ExactClaimBinderV2_1.Bind("test-request", proposals, atoms, scope);
        Assert.Empty(result.Bound);
        Assert.Equal(2, result.Refusals.Count);
    }

    [Fact]
    public void Knowledge_graph_rejects_structural_cycles()
    {
        var graph = EvidenceGraphBuilder.Build([
            Observation("E1", "A1", "Alpha"),
            Observation("E2", "A2", "Beta"),
        ]);
        var atoms = Atoms();
        var scope = AllOwnedScope(atoms);
        var whole = (string alias) => new ClaimSourceEndpointV2_1([new ProviderSourcePartV2_1(alias)]);
        var claims = ExactClaimBinderV2_1.Bind("test-request", [
            new SemanticClaimProposalV2_1(whole("A1"), "RELATES_TO", Object: whole("A2")),
            new SemanticClaimProposalV2_1(whole("A2"), "RELATES_TO", Object: whole("A1")),
        ], atoms, scope).Bound.Select(item => item.Claim).ToArray();
        var issues = KnowledgeGraphValidator.Validate(new DocumentKnowledgeState(graph, claims), Contract());
        Assert.Contains(issues, issue => issue.Code == "STRUCTURAL_CYCLE");
    }

    [Fact]
    public void Unresolved_structural_cycle_is_not_authoritative()
    {
        var graph = EvidenceGraphBuilder.Build([
            Observation("E1", "A1", "Alpha"), Observation("E2", "A2", "Beta")]);
        var atoms = Atoms();
        var scope = AllOwnedScope(atoms);
        var whole = (string alias) => new ClaimSourceEndpointV2_1([new ProviderSourcePartV2_1(alias)]);
        var claims = ExactClaimBinderV2_1.Bind("test-request", [
            new SemanticClaimProposalV2_1(whole("A1"), "RELATES_TO", Object: whole("A2"), State: ClaimResolutionState.OPEN,
                EvidenceNeeds: [EvidenceNeed.GLOBAL_TARGET]),
            new SemanticClaimProposalV2_1(whole("A2"), "RELATES_TO", Object: whole("A1"), State: ClaimResolutionState.CONFLICTED,
                EvidenceNeeds: [EvidenceNeed.GLOBAL_TARGET]),
        ], atoms, scope).Bound.Select(item => item.Claim).ToArray();
        var issues = KnowledgeGraphValidator.Validate(new DocumentKnowledgeState(graph, claims), Contract());
        Assert.DoesNotContain(issues, issue => issue.Code == "STRUCTURAL_CYCLE");
    }

    [Fact]
    public void Claim_transition_policy_is_fail_closed()
    {
        var atoms = Atoms();
        var scope = AllOwnedScope(atoms);
        var bound = ExactClaimBinderV2_1.Bind("test-request", [
            new SemanticClaimProposalV2_1(new ClaimSourceEndpointV2_1([new ProviderSourcePartV2_1("A1")]),
                "DESCRIBES", "one", State: ClaimResolutionState.RESOLVED, EvidenceNeeds: []),
        ], atoms, scope).Bound.Single().Claim;
        var changed = bound with { Value = "two" };
        var exhausted = bound with { State = ClaimResolutionState.EXHAUSTED };
        Assert.False(ClaimTransitionPolicy.IsAllowed(bound, changed));
        Assert.False(ClaimTransitionPolicy.IsAllowed(exhausted, bound));
        Assert.True(ClaimTransitionPolicy.IsAllowed(bound, bound));
    }

    [Fact]
    public void Planner_uses_open_claim_need_not_model_confidence()
    {
        var graph = EvidenceGraphBuilder.Build([Observation("E1", "A1", "Alpha")]);
        var atoms = Atoms();
        var scope = AllOwnedScope(atoms);
        var claims = ExactClaimBinderV2_1.Bind("test-request", [
            new SemanticClaimProposalV2_1(new ClaimSourceEndpointV2_1([
                new ProviderSourcePartV2_1("A1")]),
                "RELATES_TO", State: ClaimResolutionState.OPEN,
                EvidenceNeeds: [EvidenceNeed.GLOBAL_TARGET]),
        ], atoms, scope).Bound.Select(item => item.Claim).ToArray();
        var plan = new EvidencePlanner().Plan(new DocumentKnowledgeState(graph, claims), Contract(), 0);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(EvidenceNeed.GLOBAL_TARGET, action.Need);
        Assert.Equal(EvidenceModality.TEXT, action.Modality);
    }

    [Fact]
    public void Provider_preflight_is_explicitly_provider_free()
    {
        var contract = Contract();
        var preflight = new V5ProviderPreflight(
            "source", "universe", contract.Hash(), SemanticClaimContractV2_1.SchemaHash(), "prompt",
            "RESOURCE_BOUNDED", 0, [], [], new("model", "provider", "none", true, "json_object", 300),
            PlannedProviderCalls: 0, GoldRead: false, ProviderCalls: 0);
        preflight.Validate();
        Assert.Equal(64, preflight.Hash().Length);
    }

    [Fact]
    public void V5_core_does_not_depend_on_heading_or_domain_authority_symbols()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "DocxHeaderExtractor.Core", "Models"));
        var forbidden = new[] { "IsHeading", "IsMember", "PdfSemanticRole", "PdfBlockRole", "LegalArticle", "TOCEntry" };
        var files = Directory.EnumerateFiles(root, "V5*.cs", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var symbol in forbidden)
                Assert.DoesNotContain(symbol, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Production_core_has_no_reverse_reference_to_qualification_assembly()
    {
        var project = File.ReadAllText(TestRepository.Path("src/DocxHeaderExtractor.Core/DocxHeaderExtractor.Core.csproj"));
        Assert.DoesNotContain("DocxHeaderExtractor.V5Qualification.csproj", project, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", project, StringComparison.Ordinal);

        var models = TestRepository.Path("src/DocxHeaderExtractor.Core/Models");
        var topLevelProtocols = Directory.EnumerateFiles(models, "V5*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(topLevelProtocols);
        var retiredFolder = Path.Combine(models, "QualifiedInference");
        Assert.True(!Directory.Exists(retiredFolder) || !Directory.EnumerateFiles(retiredFolder, "*", SearchOption.AllDirectories).Any());
        var inference = Directory.EnumerateFiles(Path.Combine(models, "Inference"), "*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["CanonicalSemanticTextInferenceContracts.cs", "OccurrenceFunctionProtocolV1.cs", "QualifiedPromptText.cs",
            "SemanticContextPacket.cs", "V5CanonicalProtocolPrimitives.cs", "V5OccurrenceAuthorityDtos.cs"], inference);

        var core = typeof(V5IssuedOccurrenceV1).Assembly;
        Assert.NotEqual(core, typeof(V5ProviderEnvelope).Assembly);
        Assert.Equal(typeof(V5ProviderEnvelope).Assembly, typeof(OpenRouterQwen37JsonObjectCarrierV2_1).Assembly);
        foreach (var qualificationOnly in new[] { typeof(V5ComposedSemanticRequest), typeof(BoundClaimEndpoint), typeof(V5SystemPromptV2_1), typeof(V5ToolCallDeltaFragment),
                     typeof(SemanticSourcePartsV2), typeof(SemanticSourcePartCanonicalizer), typeof(SemanticSourcePartCanonicalization) })
            Assert.NotEqual(core, qualificationOnly.Assembly);
        Assert.DoesNotContain(typeof(OpenRouterQwen37JsonObjectCarrierV2_1).GetMethods(),
            method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(V5ComposedSemanticRequest)));
    }

    [Fact]
    public void Projection_engine_keeps_task_output_after_validation()
    {
        var graph = EvidenceGraphBuilder.Build([Observation("E1", "A1", "Alpha")]);
        var state = new DocumentKnowledgeState(graph, []);
        var projection = new ProjectionEngine([
            new TestProjection("knowledge-state", stateToProject => stateToProject.ResolvedClaims.Count),
        ]);
        var result = Assert.Single(projection.Project(state, Contract()).Where(item => item.ProjectionName == "knowledge-state"));
        Assert.Equal(0, Assert.IsType<int>(result.Payload));
    }

    [Fact]
    public void Task_compiler_selects_declared_projections_without_inventing_vocabulary()
    {
        var compiled = new DeterministicTaskCompiler().Compile(
            new TaskGoal("outline-task", "Project a validated outline", ["knowledge-state"]), Contract());
        Assert.Equal("outline-task", compiled.TaskId);
        Assert.Single(compiled.Projections);
        Assert.Throws<InvalidOperationException>(() => new DeterministicTaskCompiler().Compile(
            new TaskGoal("bad", "bad", ["invented-projection"]), Contract()));
    }

    [Fact]
    public void Claim_codec_rejects_coordinates_and_unknown_fields()
    {
        using var document = JsonDocument.Parse("""
            {"claims":[{"claimId":"c1","subject":{"sourceParts":[{"sourceAlias":"A1","start":0}]},"predicate":"DESCRIBES","state":"RESOLVED"}]}
            """);
        Assert.Throws<InvalidOperationException>(() => SemanticClaimResponseCodecV2_1.Parse(document.RootElement, Contract()));
    }

    [Fact]
    public void Preflight_builder_freezes_hashes_without_provider_calls()
    {
        var graph = EvidenceGraphBuilder.Build([Observation("E1", "A1", "Alpha")]);
        var preflight = V5ProviderPreflightBuilder.Build(
            "production", graph, Contract(), "stable prompt", "RESOURCE_BOUNDED", ["request-1"],
            new V5ProviderEnvelope("model", "provider", "none", true, "json_object", 300));
        Assert.Equal(1, preflight.PlannedProviderCalls);
        Assert.Equal(0, preflight.ProviderCalls);
        Assert.False(preflight.GoldRead);
        Assert.Equal(64, preflight.Hash().Length);
    }

    [Fact]
    public async Task Runtime_replaces_open_claim_after_bounded_evidence_round()
    {
        var graph = EvidenceGraphBuilder.Build([
            Observation("E1", "A1", "Alpha"),
            Observation("E2", "A2", "Alpha target"),
        ]);
        var reasoner = new SequenceReasoner();
        var contract = Contract() with
        {
            ExecutionBudget = new ExecutionBudget(MaxSemanticModelCalls: 2, MaxRetrievalRounds: 1),
        };
        var result = await new DocumentAgentRuntime(reasoner, new InMemoryEvidenceRetriever())
            .RunAsync(contract, graph, Atoms());
        var claim = Assert.Single(result.State.Claims);
        Assert.Equal(ClaimResolutionState.RESOLVED, claim.State);
        Assert.Equal(2, result.SemanticModelCalls);
        var provenance = Assert.Single(result.Provenance);
        Assert.Equal(claim.ClaimId, provenance.ClaimId);
        Assert.NotNull(provenance.ModelRequestHash);
        Assert.NotNull(provenance.ModelResponseHash);
        Assert.Equal("v5-exact-source-parts-1", provenance.BinderVersion);
    }

    [Fact]
    public async Task Runtime_executes_required_projection_and_records_layout_budget()
    {
        var contract = Contract() with
        {
            Projections = [new ProjectionRequest("knowledge-state", "required", Required: true)],
            ExecutionBudget = new ExecutionBudget(MaxSemanticModelCalls: 1, MaxLayoutCalls: 1),
        };
        var graph = EvidenceGraphBuilder.Build([Observation("E1", "A1", "Alpha")]);
        var projection = new ProjectionEngine([new TestProjection("knowledge-state", state => state.Claims.Count)]);
        var result = await new DocumentAgentRuntime(new NoopReasoner(), new InMemoryEvidenceRetriever(),
            projectionEngine: projection).RunAsync(contract, graph, Atoms());
        Assert.True(result.State.ProjectionState.ContainsKey("knowledge-state"));
        Assert.Equal(0, Assert.IsType<int>(result.State.ProjectionState["knowledge-state"].Payload));
    }

    [Fact]
    public async Task Retrieval_evidence_is_passed_to_next_reasoner_and_claim_scoped()
    {
        var recorder = new RecordingReasoner();
        var result = await new DocumentAgentRuntime(recorder, new FixedRetriever("E2"))
            .RunAsync(Contract() with { ExecutionBudget = new ExecutionBudget(MaxSemanticModelCalls: 2, MaxRetrievalRounds: 1) },
                EvidenceGraphBuilder.Build([Observation("E1", "A1", "Alpha"), Observation("E2", "A2", "Beta")]), Atoms());
        Assert.Equal(2, recorder.Contexts.Count);
        Assert.Contains(recorder.Contexts[1].RetrievedEvidence, item => item.EvidenceId == "E2");
        Assert.Contains("E2", Assert.Single(result.Provenance).RetrievalEvidenceIds);
    }

    [Fact]
    public async Task Visual_evidence_is_merged_before_subsequent_reasoning()
    {
        var recorder = new RecordingReasoner(EvidenceNeed.VISUAL_EVIDENCE);
        var visual = new FixedVisualProvider();
        var contract = Contract() with
        {
            EvidencePolicy = new EvidencePolicy([EvidenceModality.VISUAL], [EvidenceNeed.VISUAL_EVIDENCE]),
            ExecutionBudget = new ExecutionBudget(MaxSemanticModelCalls: 2, MaxVisualCalls: 1),
        };
        var result = await new DocumentAgentRuntime(recorder, new InMemoryEvidenceRetriever(), visual: visual)
            .RunAsync(contract, EvidenceGraphBuilder.Build([Observation("E1", "A1", "Alpha")]), Atoms());
        Assert.Contains(recorder.Contexts[1].EvidenceGraph.Nodes, item => item.EvidenceId == "VISUAL-1");
        Assert.Contains("VISUAL-1", Assert.Single(result.Provenance).VisualEvidenceIds);
    }

    [Fact]
    public void Request_composer_is_deterministic_and_contract_driven()
    {
        var graph = EvidenceGraphBuilder.Build([Observation("E1", "A1", "Alpha")]);
        var packet = new V5SemanticDecisionRequestPacketV3(graph.Nodes, [], [], [], [], []);
        var first = V5SemanticDecisionComposerV3.Compose(Contract(), packet);
        var second = V5SemanticDecisionComposerV3.Compose(Contract(), packet);
        Assert.Equal(first.RequestHash, second.RequestHash);
        Assert.Equal(first.PromptHash, second.PromptHash);
        Assert.DoesNotContain("heading", first.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Real_pdf_preflight_composes_requests_without_provider_or_gold()
    {
        foreach (var (id, relativePath) in new[]
        {
            ("SRC-089", SourcePdfCorpus.Src089),
            ("SRC-095", SourcePdfCorpus.Src095),
        })
        {
            var built = DocxHeaderExtractor.DocumentProcessing.Pipeline.V5PdfPreflightBuilder.BuildV2_1(
                TestRepository.Path(relativePath),
                id,
                DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create(),
                "RESOURCE_BOUNDED_SOURCE_PACKING_V1",
                new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300));
            built.Preflight.Validate();
            Assert.NotEmpty(built.Requests);
            Assert.Equal(built.Requests.Count, built.Preflight.PlannedProviderCalls);
            Assert.Equal(0, built.Preflight.ProviderCalls);
            Assert.False(built.Preflight.GoldRead);
            Assert.All(built.Requests, item => Assert.NotEmpty(item.Request.RequestHash));
        }
    }

    private static DocumentTaskContract Contract() => new(
        V5Protocol.TaskContractVersion,
        "generic-document-task",
        "A task-defined source-backed claim extraction contract.",
        [new SemanticPredicateDefinition("DESCRIBES", "A task-defined unary fact.")],
        [new SemanticRelationDefinition("RELATES_TO", "A task-defined relation.", StructuralParent: true)],
        [new ProjectionRequest("knowledge-state", "Validated claims and relations.")],
        new EvidencePolicy([EvidenceModality.TEXT, EvidenceModality.LAYOUT], [EvidenceNeed.GLOBAL_TARGET, EvidenceNeed.MORE_CONTEXT]),
        "retain-open",
        new ExecutionBudget(MaxSemanticModelCalls: 1, MaxRetrievalRounds: 2));

    private static IReadOnlyList<SemanticSourceAtom> Atoms() => [
        new("A1", "S1", 1, 1, 1, 0, "Alpha"),
        new("A2", "S2", 2, 1, 2, 0, "Beta"),
    ];

    /// <summary>Everything owned, nothing halo - the scope a whole-document (unpacked) caller gets by default.</summary>
    private static ClaimBindingScope AllOwnedScope(IReadOnlyList<SemanticSourceAtom> atoms)
    {
        var aliases = atoms.Select(atom => atom.Alias).ToArray();
        return ClaimBindingScope.Create(aliases, aliases);
    }

    private static SourceObservation Observation(
        string id,
        string alias,
        string text,
        IReadOnlyDictionary<string, string?>? facts = null) =>
        new(id, id, alias, int.Parse(id[1..]), EvidenceModality.TEXT, text,
            new StructuralSpan(0, text.Length), Facts: facts);

    private sealed class TestProjection(string name, Func<DocumentKnowledgeState, object> projector) : IKnowledgeProjection
    {
        public string Name => name;

        public ProjectionResult Project(DocumentKnowledgeState state, DocumentTaskContract contract) =>
            new(Name, projector(state), state.Claims.Select(claim => claim.ClaimId).ToArray(), []);
    }

    private sealed class NoopReasoner : ISemanticReasoner
    {
        public string Identity => "test-noop";

        public ValueTask<SemanticReasoningResult> ReasonAsync(SemanticReasoningContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SemanticReasoningResult(new V5SemanticDecisionResponseV3(
                Enumerable.Range(0, context.RequestPacket.SubjectEvidence.Count).Select(_ => new V5SemanticSubjectDecisionV3([])).ToArray()), new SemanticReasoningUsage()));
    }

    private sealed class SequenceReasoner : ISemanticReasoner
    {
        public string Identity => "test-sequence";

        public ValueTask<SemanticReasoningResult> ReasonAsync(SemanticReasoningContext context, CancellationToken cancellationToken)
        {
            // Turn 0 proposes A1 as a new OPEN claim. Turn 1 refines it: the runtime told this turn
            // about its own open claim via context.OpenOrConflictedClaims, so it echoes that durable
            // id back as existingClaimId rather than letting the runtime mint a second, unrelated one.
            var existingClaimId = context.OpenOrConflictedClaims.SingleOrDefault()?.ClaimId;
            var state = context.CallOrdinal == 0 ? ClaimResolutionState.OPEN : ClaimResolutionState.RESOLVED;
            var claim = new V5SemanticDecisionClaimV3("DESCRIBES",
                Value: state == ClaimResolutionState.RESOLVED ? "target" : null,
                State: state,
                EvidenceNeeds: state == ClaimResolutionState.OPEN ? [EvidenceNeed.GLOBAL_TARGET] : [],
                ExistingClaimId: existingClaimId);
            return ValueTask.FromResult(new SemanticReasoningResult(new V5SemanticDecisionResponseV3([
                new([claim]), new([]),
            ]), new SemanticReasoningUsage()));
        }
    }

    private sealed class RecordingReasoner(EvidenceNeed need = EvidenceNeed.GLOBAL_TARGET) : ISemanticReasoner
    {
        public string Identity => "test-recording";
        public List<SemanticReasoningContext> Contexts { get; } = [];

        public ValueTask<SemanticReasoningResult> ReasonAsync(SemanticReasoningContext context, CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            var existingClaimId = context.OpenOrConflictedClaims.SingleOrDefault()?.ClaimId;
            var state = context.CallOrdinal == 0 ? ClaimResolutionState.OPEN : ClaimResolutionState.RESOLVED;
            var response = new V5SemanticDecisionResponseV3([
                new([new V5SemanticDecisionClaimV3("DESCRIBES", Value: state == ClaimResolutionState.RESOLVED ? "target" : null,
                    State: state, EvidenceNeeds: state == ClaimResolutionState.OPEN ? [need] : [], ExistingClaimId: existingClaimId)]), new([]),
            ]);
            return ValueTask.FromResult(new SemanticReasoningResult(response, new SemanticReasoningUsage()));
        }
    }

    private sealed class FixedRetriever(string evidenceId) : IEvidenceRetriever
    {
        public IReadOnlyList<EvidenceCandidate> Retrieve(EvidenceRetrievalRequest request, UniversalEvidenceGraph graph) =>
            [new EvidenceCandidate(evidenceId, 1, "TEST", "fixed")];
    }

    private sealed class FixedVisualProvider : IVisualEvidenceReasoner
    {
        public string Identity => "test-visual";
        public ValueTask<IReadOnlyList<SourceObservation>> InspectAsync(VisualEvidenceRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceObservation>>([
                new SourceObservation("VISUAL-1", "visual-source", "A1", 3, EvidenceModality.VISUAL, "visual observation",
                    new StructuralSpan(0, 18)),
            ]);
    }
}
