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
        var response = new SemanticClaimResponse([
            new("c1", new ClaimSourceEndpoint([
                new("A1", CanonicalSemanticSelectionMode.VerbatimText, "not present")]), "RELATES_TO"),
            new("c2", new ClaimSourceEndpoint([
                new("UNKNOWN", CanonicalSemanticSelectionMode.WholeAlias)]), "RELATES_TO"),
        ]);

        var result = ExactClaimBinder.Bind(response.Claims, atoms);
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
        var whole = (string alias) => new ClaimSourceEndpoint([
            new(alias, CanonicalSemanticSelectionMode.WholeAlias)]);
        var claims = ExactClaimBinder.Bind([
            new("c1", whole("A1"), "RELATES_TO", Object: whole("A2")),
            new("c2", whole("A2"), "RELATES_TO", Object: whole("A1")),
        ], atoms).Bound;
        var issues = KnowledgeGraphValidator.Validate(new DocumentKnowledgeState(graph, claims), Contract());
        Assert.Contains(issues, issue => issue.Code == "STRUCTURAL_CYCLE");
    }

    [Fact]
    public void Planner_uses_open_claim_need_not_model_confidence()
    {
        var graph = EvidenceGraphBuilder.Build([Observation("E1", "A1", "Alpha")]);
        var claims = ExactClaimBinder.Bind([
            new("c1", new ClaimSourceEndpoint([
                new("A1", CanonicalSemanticSelectionMode.WholeAlias)]),
                "RELATES_TO", State: ClaimResolutionState.OPEN,
                EvidenceNeeds: [EvidenceNeed.GLOBAL_TARGET]),
        ], Atoms()).Bound;
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
            "source", "universe", contract.Hash(), SemanticClaimContract.SchemaHash(), "prompt",
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
        var files = Directory.EnumerateFiles(root, "V5*.cs", SearchOption.TopDirectoryOnly);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var symbol in forbidden)
                Assert.DoesNotContain(symbol, source, StringComparison.Ordinal);
        }
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
        Assert.Throws<InvalidOperationException>(() => SemanticClaimResponseCodec.Parse(document.RootElement, Contract()));
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
}
