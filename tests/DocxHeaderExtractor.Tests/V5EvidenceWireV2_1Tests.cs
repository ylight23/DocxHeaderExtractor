using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free verification of the two wire-ergonomics fixes a real 3-pack canary at commit
/// 8e5debb motivated: subjectEvidence/contextOnlyEvidence are structurally disjoint (no more owned
/// item looking the same as a halo one), and claimShapes make unary-vs-relation arity explicit
/// instead of something the model has to infer from two separate lists. Neither the binder nor the
/// validator changed - both already rejected what they should have; only request representation did.
/// </summary>
public sealed class V5EvidenceWireV2_1Tests
{
    // ---- ownership wire: subjectEvidence / contextOnlyEvidence are disjoint --------------------

    [Fact]
    public void Subject_and_context_only_evidence_are_disjoint_for_a_real_pdf_pack()
    {
        var built = BuildSrc089();
        var pack1 = built.Requests[0];
        var subjectAliases = pack1.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var contextOnlyAliases = pack1.VisibleAliases.Where(alias => !subjectAliases.Contains(alias)).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(subjectAliases.Intersect(contextOnlyAliases, StringComparer.Ordinal));
    }

    [Fact]
    public void Visible_alias_set_is_conserved_across_subject_and_context_only()
    {
        var built = BuildSrc089();
        var pack1 = built.Requests[0];
        var subjectAliases = pack1.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var contextOnlyAliases = pack1.VisibleAliases.Where(alias => !subjectAliases.Contains(alias)).ToArray();

        var reunited = subjectAliases.Concat(contextOnlyAliases).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(pack1.VisibleAliases.ToHashSet(StringComparer.Ordinal), reunited);
    }

    [Fact]
    public void An_owned_alias_never_appears_as_context_only()
    {
        var built = BuildSrc089();
        var pack1 = built.Requests[0];
        var subjectAliases = pack1.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var contextOnlyAliases = pack1.VisibleAliases.Where(alias => !subjectAliases.Contains(alias));
        Assert.DoesNotContain(contextOnlyAliases, alias => subjectAliases.Contains(alias));
    }

    [Fact]
    public void A_halo_alias_never_appears_in_owned()
    {
        var built = BuildSrc089();
        var pack1 = built.Requests[0];
        Assert.True(pack1.VisibleAliases.Count > pack1.OwnedAliases.Count, "this pack must actually have halo to test");
        var subjectAliases = pack1.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var halo = pack1.VisibleAliases.First(alias => !subjectAliases.Contains(alias));
        Assert.DoesNotContain(halo, pack1.OwnedAliases);
    }

    [Fact]
    public void L0094_S0_for_src089_pack_001_is_the_first_context_only_alias_after_the_owned_tail()
    {
        // The exact fact behind the canary's HALO_AS_SUBJECT failure: L0094:S0 is visible but not
        // owned for SRC-089's first pack - real data, not a constructed fixture.
        var built = BuildSrc089();
        var pack1 = built.Requests[0];
        Assert.Equal("L0093:S0", pack1.OwnedAliases[^1]);
        Assert.Contains("L0094:S0", pack1.VisibleAliases);
        Assert.DoesNotContain("L0094:S0", pack1.OwnedAliases);
    }

    [Fact]
    public void The_wire_payload_carries_subject_and_context_only_fields_not_owned_and_visible()
    {
        var built = BuildSrc089();
        var prompt = built.Requests[0].Request.Prompt;
        Assert.Contains("\"subjectEvidence\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"contextOnlyEvidence\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ownedEvidence\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"visibleEvidence\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_binder_still_refuses_a_halo_alias_as_subject_after_the_wire_change()
    {
        // The binder itself is untouched by this task - it was already correct. This just
        // reconfirms it still holds with the new wire shape upstream of it.
        var scope = ClaimBindingScope.Create(["A1"], ["A1", "A2"]);
        var atoms = new[]
        {
            new DocxHeaderExtractor.Core.Models.SemanticSourceAtom("A1", "S1", 1, 1, 1, 0, "Alpha"),
            new DocxHeaderExtractor.Core.Models.SemanticSourceAtom("A2", "S2", 2, 1, 1, 1, "Beta"),
        };
        var proposal = new SemanticClaimProposalV2_1(
            new ClaimSourceEndpointV2_1([new ProviderSourcePartV2_1("A2")]), "DESCRIBES", EvidenceNeeds: []);
        var result = ExactClaimBinderV2_1.Bind("request-1", [proposal], atoms, scope);
        Assert.False(result.IsComplete);
        Assert.Contains("subject-alias-not-owned", result.Refusals.Values.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_context_only_alias_may_still_bind_as_a_relation_object()
    {
        var scope = ClaimBindingScope.Create(["A1"], ["A1", "A2"]);
        var atoms = new[]
        {
            new DocxHeaderExtractor.Core.Models.SemanticSourceAtom("A1", "S1", 1, 1, 1, 0, "Alpha"),
            new DocxHeaderExtractor.Core.Models.SemanticSourceAtom("A2", "S2", 2, 1, 1, 1, "Beta"),
        };
        var proposal = new SemanticClaimProposalV2_1(
            new ClaimSourceEndpointV2_1([new ProviderSourcePartV2_1("A1")]), "RELATES_TO",
            Object: new ClaimSourceEndpointV2_1([new ProviderSourcePartV2_1("A2")]), EvidenceNeeds: []);
        var result = ExactClaimBinderV2_1.Bind("request-1", [proposal], atoms, scope);
        Assert.True(result.IsComplete);
        Assert.NotNull(result.Bound[0].Claim.Object);
    }

    // ---- claim arity: claimShapes are explicit and deterministic --------------------------------

    [Fact]
    public void Every_declared_predicate_gets_exactly_one_unary_claim_shape()
    {
        var contract = Contract();
        var shapes = V5ClaimShapesV2_1.Generate(contract);
        foreach (var predicate in contract.Predicates)
        {
            var matches = shapes.Where(shape => shape.Name == predicate.Name).ToArray();
            Assert.Single(matches);
            Assert.Equal(V5ClaimShapesV2_1.Unary, matches[0].Kind);
        }
    }

    [Fact]
    public void Every_declared_relation_gets_exactly_one_relation_claim_shape()
    {
        var contract = Contract();
        var shapes = V5ClaimShapesV2_1.Generate(contract);
        foreach (var relation in contract.Relations)
        {
            var matches = shapes.Where(shape => shape.Name == relation.Name).ToArray();
            Assert.Single(matches);
            Assert.Equal(V5ClaimShapesV2_1.Relation, matches[0].Kind);
        }
    }

    [Fact]
    public void A_unary_shape_forbids_an_object()
    {
        var shapes = V5ClaimShapesV2_1.Generate(Contract());
        Assert.All(shapes.Where(shape => shape.Kind == V5ClaimShapesV2_1.Unary), shape => Assert.False(shape.ObjectAllowed));
    }

    [Fact]
    public void A_relation_shape_forbids_a_value_and_requires_object_on_resolved()
    {
        var shapes = V5ClaimShapesV2_1.Generate(Contract());
        Assert.All(shapes.Where(shape => shape.Kind == V5ClaimShapesV2_1.Relation), shape =>
        {
            Assert.False(shape.ValueAllowed);
            Assert.True(shape.ResolvedObjectRequired);
        });
    }

    [Fact]
    public void No_name_appears_in_the_generated_vocabulary_that_the_contract_did_not_declare()
    {
        var contract = Contract();
        var declared = contract.Predicates.Select(p => p.Name).Concat(contract.Relations.Select(r => r.Name)).ToHashSet(StringComparer.Ordinal);
        var shapes = V5ClaimShapesV2_1.Generate(contract);
        Assert.Equal(declared.Count, shapes.Count);
        Assert.All(shapes, shape => Assert.Contains(shape.Name, declared));
    }

    [Fact]
    public void Generated_claim_shapes_are_deterministic()
    {
        var contract = Contract();
        Assert.Equal(V5ClaimShapesV2_1.Hash(contract), V5ClaimShapesV2_1.Hash(contract));
    }

    [Fact]
    public void The_composed_prompt_embeds_claim_shapes_and_no_gold_reference()
    {
        var composed = V5SemanticRequestComposerV2_1.Compose(Contract(), new V5EvidencePacketV2_1([], [], [], [], [], []));
        Assert.Contains("\"claimShapes\"", composed.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("gold", composed.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    // ---- prior fixes still hold ------------------------------------------------------------------

    [Fact]
    public void The_previous_selection_mode_removal_is_still_in_effect()
    {
        var schema = System.Text.Json.JsonSerializer.Serialize(SemanticClaimContractV2_1.Schema());
        Assert.DoesNotContain("selectionMode", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void Src095_pack_001_completion_budget_has_not_regressed_to_the_legacy_formula()
    {
        var built = BuildSrc095();
        var pack1 = built.Requests[0];
        var legacy = DocxHeaderExtractor.Infrastructure.AI.OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(
            pack1.Request.Prompt, pack1.OwnedAliases.Count, 32768);
        Assert.True(pack1.MaxCompletionTokens > legacy,
            $"expected the V5-owned budget ({pack1.MaxCompletionTokens}) to exceed the legacy formula ({legacy})");
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static (V5ProviderPreflight Preflight, IReadOnlyList<V5PackedSourceRequest> Requests) BuildSrc089() =>
        V5PdfPreflightBuilder.BuildV2_1(
            TestRepository.Path(SourcePdfCorpus.Src089), "SRC-089", Contract(),
            V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope());

    private static (V5ProviderPreflight Preflight, IReadOnlyList<V5PackedSourceRequest> Requests) BuildSrc095() =>
        V5PdfPreflightBuilder.BuildV2_1(
            TestRepository.Path(SourcePdfCorpus.Src095), "SRC-095", Contract(),
            V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope());

    private static V5ProviderEnvelope Envelope() => new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
    {
        UsageInclude = true,
    };

    private static DocumentTaskContract Contract() =>
        DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
}
