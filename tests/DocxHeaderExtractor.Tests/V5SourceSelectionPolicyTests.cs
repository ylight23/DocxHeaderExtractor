using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free tests for the wire remediation that follows the exact-text-binding audit
/// (<see cref="V5ExactTextBindingAuditTests"/>): the 31-pack cohort's 169 part-level exact-text
/// failures were 502/534 successful verbatimText uses re-typing a whole atom the model could have
/// named by alias alone, plus 74 spacing + 37 Unicode + 4 punctuation failures that were the
/// model's own re-typed copy differing from the source, versus only 20 genuine multi-atom spans the
/// wire already supports via multiple sourceParts. <see cref="V5SourceSelectionPolicy"/> makes the
/// cheapest correct wire shape explicit and machine-readable instead of leaving it to prose; nothing
/// here touches the exact binder, the response schema, ownership rules or claim shapes - this proves
/// exactly that, alongside the policy's own shape and wording.
/// </summary>
public sealed class V5SourceSelectionPolicyTests
{
    // ---- 1: appears deterministically -----------------------------------------------------------

    [Fact]
    public void SourceSelectionPolicy_appears_deterministically_in_the_composed_request()
    {
        var first = V5SemanticRequestComposerV2_1.Compose(Contract(), Packet());
        var second = V5SemanticRequestComposerV2_1.Compose(Contract(), Packet());
        Assert.Equal(first.Prompt, second.Prompt);
        Assert.Equal(first.RequestHash, second.RequestHash);
        Assert.Contains("\"sourceSelectionPolicy\"", first.Prompt, StringComparison.Ordinal);
        Assert.Contains(V5SourceSelectionPolicy.Version, first.Prompt, StringComparison.Ordinal);

        var policyFirst = JsonSerializer.Serialize(V5SourceSelectionPolicy.Generate());
        var policySecond = JsonSerializer.Serialize(V5SourceSelectionPolicy.Generate());
        Assert.Equal(policyFirst, policySecond);
    }

    // ---- 2: wholeAtom says verbatimText omitted -------------------------------------------------

    [Fact]
    public void WholeAtom_case_says_verbatimText_must_be_omitted()
    {
        var policy = PolicyJson();
        var wholeAtom = policy.GetProperty("wholeAtom");
        Assert.Equal("MUST_BE_OMITTED", wholeAtom.GetProperty("verbatimText").GetString());
        var shape = wholeAtom.GetProperty("shape");
        Assert.True(shape.TryGetProperty("sourceAlias", out _));
        Assert.False(shape.TryGetProperty("verbatimText", out _));
        Assert.Equal(V5SourceSelectionPolicy.WholeAtomMode, policy.GetProperty("default").GetString());
    }

    // ---- 3: substring requires exact copy -------------------------------------------------------

    [Fact]
    public void Substring_case_requires_an_exact_byte_for_byte_copy()
    {
        var substring = PolicyJson().GetProperty("strictSubstring");
        Assert.True(substring.GetProperty("shape").TryGetProperty("verbatimText", out _));
        var rule = substring.GetProperty("rule").GetString()!;
        Assert.Contains("exactly", rule, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never rewrite", rule, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 4: multipart requires multiple source parts --------------------------------------------

    [Fact]
    public void MultiAtomRegion_case_requires_multiple_source_parts_in_source_order()
    {
        var multiAtom = PolicyJson().GetProperty("multiAtomRegion");
        var shape = multiAtom.GetProperty("shape");
        Assert.Equal(JsonValueKind.Array, shape.ValueKind);
        Assert.True(shape.GetArrayLength() >= 2);
        Assert.Contains("one sourcePart per contributing alias", multiAtom.GetProperty("rule").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 5: no heading vocabulary -----------------------------------------------------------------

    [Fact]
    public void Policy_and_the_composed_prompt_never_mention_heading_vocabulary()
    {
        var composed = V5SemanticRequestComposerV2_1.Compose(Contract(), Packet());
        Assert.DoesNotContain("heading", JsonSerializer.Serialize(V5SourceSelectionPolicy.Generate()), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("heading", composed.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 6: does not alter TaskContract authority -------------------------------------------------

    [Fact]
    public void Policy_does_not_alter_or_reference_the_task_contract_vocabulary()
    {
        var contract = Contract();
        var expectedHash = contract.Hash();
        var composed = V5SemanticRequestComposerV2_1.Compose(contract, Packet());
        Assert.Equal(expectedHash, contract.Hash());

        var policyJson = JsonSerializer.Serialize(V5SourceSelectionPolicy.Generate());
        foreach (var predicate in contract.Predicates.Select(p => p.Name).Concat(contract.Relations.Select(r => r.Name)))
            Assert.DoesNotContain(predicate, policyJson, StringComparison.Ordinal);

        // The contract subtree embedded in the composed prompt is exactly the contract's own
        // canonical serialization - the policy sits alongside it, not inside it.
        Assert.Contains(JsonSerializer.Serialize(contract, CanonicalJson.Options), composed.Prompt, StringComparison.Ordinal);
    }

    // ---- 7: provider schema still permits legitimate substring selection -------------------------

    [Fact]
    public void Response_schema_still_permits_a_legitimate_strict_substring_selection()
    {
        var response = Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","verbatimText":"Al"}]},"predicate":"DESCRIBES","value":"fact","state":"RESOLVED","evidenceNeeds":[]}]}""");
        Assert.Equal("Al", response.Claims[0].Subject.SourceParts[0].VerbatimText);

        var bound = ExactClaimBinderV2_1.Bind("request-1", response.Claims, Atoms(), OwnedOnlyScope());
        Assert.True(bound.IsComplete);
    }

    // ---- 8: binder remains byte-for-byte unchanged -------------------------------------------------

    [Fact]
    public void Exact_binder_behavior_for_whole_alias_and_substring_selection_is_unchanged()
    {
        var scope = OwnedOnlyScope();
        var wholeAlias = ExactClaimBinderV2_1.Bind("request-1",
            [new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A1")), "DESCRIBES", EvidenceNeeds: [])],
            Atoms(), scope);
        Assert.True(wholeAlias.IsComplete);
        Assert.Equal("S1:0-5", wholeAlias.Bound[0].Claim.Subject.Identity);

        var substring = ExactClaimBinderV2_1.Bind("request-1",
            [new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A1", "Al")), "DESCRIBES", EvidenceNeeds: [])],
            Atoms(), scope);
        Assert.True(substring.IsComplete);
        Assert.Equal("S1:0-2", substring.Bound[0].Claim.Subject.Identity);
    }

    // ---- 9: provider cannot emit selectionMode -----------------------------------------------------

    [Fact]
    public void Provider_still_cannot_emit_a_selectionMode_field()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"WHOLE_ALIAS"}]},"predicate":"DESCRIBES","state":"RESOLVED","evidenceNeeds":[]}]}"""));
        Assert.Contains("selectionMode", ex.Message, StringComparison.Ordinal);
    }

    // ---- 10: ownership rules unchanged --------------------------------------------------------------

    [Fact]
    public void Ownership_rules_are_unchanged_a_halo_subject_is_still_rejected()
    {
        var scope = ClaimBindingScope.Create(["A1"], ["A1", "A2"]);
        var proposal = new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A2")), "DESCRIBES", EvidenceNeeds: []);
        var result = ExactClaimBinderV2_1.Bind("request-1", [proposal], Atoms(), scope);
        Assert.False(result.IsComplete);
        Assert.Contains("subject-alias-not-owned", result.Refusals.Values.Single(), StringComparison.Ordinal);
    }

    // ---- 11: claimShapes unchanged ------------------------------------------------------------------

    [Fact]
    public void ClaimShapes_arity_generation_is_unchanged()
    {
        var shapes = V5ClaimShapesV2_1.Generate(Contract());
        var unary = shapes.Single(s => s.Name == "DESCRIBES");
        Assert.Equal(V5ClaimShapesV2_1.Unary, unary.Kind);
        Assert.False(unary.ObjectAllowed);
        var relation = shapes.Single(s => s.Name == "RELATES_TO");
        Assert.Equal(V5ClaimShapesV2_1.Relation, relation.Kind);
        Assert.True(relation.ResolvedObjectRequired);
    }

    // ---- 12: evidenceNeeds rules unchanged ----------------------------------------------------------

    [Fact]
    public void EvidenceNeeds_rules_are_unchanged_resolved_must_not_carry_a_need()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"DESCRIBES","value":"fact","state":"RESOLVED","evidenceNeeds":["MORE_CONTEXT"]}]}"""));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static JsonElement PolicyJson() => JsonSerializer.SerializeToElement(V5SourceSelectionPolicy.Generate());

    private static SemanticClaimResponseV2_1 Parse(string json) =>
        SemanticClaimResponseCodecV2_1.Parse(JsonDocument.Parse(json).RootElement, Contract());

    private static ClaimBindingScope OwnedOnlyScope() => ClaimBindingScope.Create(["A1", "A2"], ["A1", "A2"]);

    private static ClaimSourceEndpointV2_1 Endpoint(params ProviderSourcePartV2_1[] parts) => new(parts);

    private static IReadOnlyList<SemanticSourceAtom> Atoms() =>
        [new("A1", "S1", 1, 1, 1, 0, "Alpha"), new("A2", "S2", 2, 1, 1, 1, "Beta")];

    private static DocumentTaskContract Contract() => new(
        V5Protocol.TaskContractVersion,
        "v5-source-selection-policy-test",
        "Source-backed test task.",
        [new SemanticPredicateDefinition("DESCRIBES", "A unary fact.")],
        [new SemanticRelationDefinition("RELATES_TO", "A relation.")],
        [new ProjectionRequest("claims", "Claims.")],
        new EvidencePolicy([EvidenceModality.TEXT], [EvidenceNeed.MORE_CONTEXT, EvidenceNeed.GLOBAL_TARGET, EvidenceNeed.IDENTITY_DISAMBIGUATION]),
        "retain-open",
        new ExecutionBudget(MaxSemanticModelCalls: 1));

    private static V5EvidencePacketV2_1 Packet() => new([], [], [], [], [], []);
}
