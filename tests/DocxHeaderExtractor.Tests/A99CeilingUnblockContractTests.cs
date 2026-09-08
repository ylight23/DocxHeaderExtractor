using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class A99CeilingUnblockContractTests
{
    [Fact]
    public async Task One_occurrence_can_emit_four_headings_and_zero_is_valid()
    {
        var state = NativePolicyStateFactory.Create([(0, "Chapter I; Article 1; Article 2; Article 3", null, (int?)null)]);
        var model = new FixedModel(_ => new ReasoningModelResponse(
            [H(0, 9, "CHAPTER"), H(11, 20, "ARTICLE"), H(22, 31, "ARTICLE"), H(33, 42, "ARTICLE")], []));

        var result = await new ReasoningPreservingHeadingHarness(model)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling);

        Assert.Equal(4, result.Validated.Count(x => x.Accepted));
        Assert.Equal(4, result.Proposed.Count);
    }

    [Fact]
    public async Task Visible_local_span_maps_to_global_and_can_finish_in_halo()
    {
        var state = NativePolicyStateFactory.Create([(0, "0123456789", null, (int?)null)]);
        var model = new FixedModel(request => request.OwnedOutputScope.OwnedStart == 4
            ? new ReasoningModelResponse([H(3, 7, "SECTION")], [])
            : new ReasoningModelResponse([], []));

        var result = await new ReasoningPreservingHeadingHarness(model, 1, 6)
            .RunAsync(state.Source, state, ReasoningRoute.ModelCapabilityCeiling);

        var accepted = Assert.Single(result.Validated.Where(x => x.Accepted));
        Assert.Equal(new StructuralSpan(5, 9), accepted.Proposal.HeadingSpan);
    }

    [Fact]
    public void Global_hierarchy_uses_proposal_ids_and_derives_unbounded_depth()
    {
        var state = NativePolicyStateFactory.Create([(0, "A", null, (int?)null), (1, "B", null, (int?)null)]);
        var proposals = new[]
        {
            Proposal("p[0]", 0, 1, "CHAPTER"),
            Proposal("p[1]", 0, 1, "ARTICLE"),
        };
        var inventory = ReasoningGlobalHierarchyPass.BuildInventory("DOC", state.Source, proposals);
        var edges = new[] { new ReasoningHierarchyEdge(inventory[1].ProposalId, inventory[0].ProposalId) };
        var valid = ReasoningGlobalHierarchyPass.Validate(inventory, edges);
        var levels = ReasoningGlobalHierarchyPass.DeriveLevels(inventory, valid.AcceptedEdges);

        Assert.Single(valid.AcceptedEdges);
        Assert.Equal(1, levels[inventory[0].ProposalId]);
        Assert.Equal(2, levels[inventory[1].ProposalId]);
    }

    [Fact]
    public void Parent_conflict_keeps_one_canonical_occurrence()
    {
        var state = NativePolicyStateFactory.Create([(0, "A", null, (int?)null), (1, "B", null, (int?)null), (2, "C", null, (int?)null)]);
        var p = Proposal("p[1]", 0, 1, "SECTION") with { Text = "B" };
        var result = ReasoningProposalMaterializer.Materialize(state.Source, state,
        [p with { ProposedParent = "reasoning:p[0]:0:1" }, p with { ProposedParent = "reasoning:p[2]:0:1" }]);

        Assert.Single(result.Structure.Elements);
        Assert.Equal("PARENT_CONFLICT", Assert.Single(result.Validated).ConflictStatus);
        Assert.Null(result.Structure.Elements[0].ParentId);
    }

    private static ReasoningHeadingProposal Proposal(string sourceId, int start, int end, string role) => new()
    {
        SourceId = sourceId, HeadingSpan = new StructuralSpan(start, end), Text = "A", SemanticRole = role, Confidence = .8,
    };

    private static ReasoningModelHeadingProposal H(int start, int end, string role) => new()
    {
        Start = start, End = end, SemanticRole = role, ProposedLevel = null, ProposedParentLocalId = null,
        Confidence = .9, DecisionEvidence = [],
    };

    private sealed class FixedModel(Func<ReasoningModelRequest, ReasoningModelResponse> handler) : IReasoningSemanticModel
    {
        public string ModelName => "fixed";
        public string ProviderName => "test";
        public int ContextSize => 80_000;
        public int ProviderCalls { get; private set; }
        public Task<ReasoningModelResponse> CompleteAsync(ReasoningModelRequest request, CancellationToken ct = default)
        {
            ProviderCalls++;
            return Task.FromResult(handler(request));
        }
    }
}
