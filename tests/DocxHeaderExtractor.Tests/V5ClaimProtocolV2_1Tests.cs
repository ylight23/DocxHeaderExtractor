using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free contract tests for source-backed claim protocol v2.1: durable claim identity (C1),
/// a representable OPEN relation with an unresolved target (C2), owned/visible binding-scope
/// enforcement (C3), a closed selectionMode vocabulary (C4), and a harness-only EXHAUSTED state (C5).
/// v2's own test file (V5ClaimProtocolV2Tests.cs) is untouched and still exercises the frozen v2 baseline.
/// </summary>
public sealed class V5ClaimProtocolV2_1Tests
{
    // ---- C1: durable claim identity ----------------------------------------------------------

    [Fact]
    public void Open_to_resolved_transition_keeps_the_same_durable_claim_id()
    {
        var scope = OwnedOnlyScope();
        var open = ExactClaimBinderV2_1.Bind("request-1", [Proposal("A1", ClaimResolutionState.OPEN, evidenceNeeds: [EvidenceNeed.MORE_CONTEXT])], Atoms(), scope);
        Assert.True(open.IsComplete);
        var openId = open.Bound[0].Claim.ClaimId;

        var resolved = ExactClaimBinderV2_1.Bind("request-1",
            [Proposal("A1", ClaimResolutionState.RESOLVED) with { ExistingClaimId = openId }],
            Atoms(), ScopeWithKnownClaim(scope, openId, "A1", "DESCRIBES"));
        Assert.True(resolved.IsComplete);
        Assert.Equal(openId, resolved.Bound[0].Claim.ClaimId);
    }

    [Fact]
    public void Open_to_conflicted_transition_keeps_the_same_durable_claim_id()
    {
        var scope = OwnedOnlyScope();
        var open = ExactClaimBinderV2_1.Bind("request-1", [Proposal("A1", ClaimResolutionState.OPEN, evidenceNeeds: [EvidenceNeed.MORE_CONTEXT])], Atoms(), scope);
        var openId = open.Bound[0].Claim.ClaimId;

        var conflicted = ExactClaimBinderV2_1.Bind("request-1",
            [Proposal("A1", ClaimResolutionState.CONFLICTED, evidenceNeeds: [EvidenceNeed.MORE_CONTEXT]) with { ExistingClaimId = openId }],
            Atoms(), ScopeWithKnownClaim(scope, openId, "A1", "DESCRIBES"));
        Assert.True(conflicted.IsComplete);
        Assert.Equal(openId, conflicted.Bound[0].Claim.ClaimId);
    }

    [Fact]
    public void Value_resolution_does_not_change_the_durable_claim_id()
    {
        var scope = OwnedOnlyScope();
        var first = ExactClaimBinderV2_1.Bind("request-1", [Proposal("A1", ClaimResolutionState.OPEN, value: null, evidenceNeeds: [EvidenceNeed.MORE_CONTEXT])], Atoms(), scope);
        var id = first.Bound[0].Claim.ClaimId;

        var second = ExactClaimBinderV2_1.Bind("request-1",
            [Proposal("A1", ClaimResolutionState.RESOLVED, value: "now-known") with { ExistingClaimId = id }],
            Atoms(), ScopeWithKnownClaim(scope, id, "A1", "DESCRIBES"));
        Assert.Equal(id, second.Bound[0].Claim.ClaimId);
        Assert.Equal("now-known", second.Bound[0].Claim.Value);
    }

    [Fact]
    public void Relation_target_resolution_does_not_change_the_durable_claim_id()
    {
        var owned = new[] { "A1" };
        var visible = new[] { "A1", "A2" };
        var scope = ClaimBindingScope.Create(owned, visible);
        var open = ExactClaimBinderV2_1.Bind("request-1",
            [RelationProposal("A1", null, ClaimResolutionState.OPEN, [EvidenceNeed.GLOBAL_TARGET])], Atoms(), scope);
        Assert.True(open.IsComplete);
        var openId = open.Bound[0].Claim.ClaimId;
        Assert.Null(open.Bound[0].Claim.Object);

        var resolved = ExactClaimBinderV2_1.Bind("request-1",
            [RelationProposal("A1", "A2", ClaimResolutionState.RESOLVED, null) with { ExistingClaimId = openId }],
            Atoms(), ScopeWithKnownClaim(scope, openId, "A1", "RELATES_TO"));
        Assert.True(resolved.IsComplete);
        Assert.Equal(openId, resolved.Bound[0].Claim.ClaimId);
        Assert.NotNull(resolved.Bound[0].Claim.Object);
    }

    [Fact]
    public void Different_physical_subject_occurrence_gets_a_different_durable_id()
    {
        var atoms = Atoms().Append(new SemanticSourceAtom("A9", "S9", 9, 1, 9, 0, "Alpha")).ToArray();
        var scope = ClaimBindingScope.Create(["A1", "A9"], ["A1", "A9"]);
        var first = ExactClaimBinderV2_1.Bind("request-1", [Proposal("A1", ClaimResolutionState.RESOLVED)], atoms, scope);
        var second = ExactClaimBinderV2_1.Bind("request-1", [Proposal("A9", ClaimResolutionState.RESOLVED)], atoms, scope);
        Assert.NotEqual(first.Bound[0].Claim.ClaimId, second.Bound[0].Claim.ClaimId);
    }

    [Fact]
    public void Unknown_existing_claim_id_is_rejected()
    {
        var scope = OwnedOnlyScope();
        var result = ExactClaimBinderV2_1.Bind("request-1",
            [Proposal("A1", ClaimResolutionState.RESOLVED) with { ExistingClaimId = "v5claim21-does-not-exist" }],
            Atoms(), scope);
        Assert.False(result.IsComplete);
        Assert.Contains("unknown-existing-claim-id", result.Refusals.Values.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Mismatching_existing_claim_id_is_rejected()
    {
        var scope = ClaimBindingScope.Create(["A1", "A2"], ["A1", "A2"]);
        var open = ExactClaimBinderV2_1.Bind("request-1", [Proposal("A1", ClaimResolutionState.OPEN, evidenceNeeds: [EvidenceNeed.MORE_CONTEXT])], Atoms(), scope);
        var id = open.Bound[0].Claim.ClaimId;

        // Same known id, but the refinement now points its subject at a different atom - subject
        // compatibility must be re-checked, not assumed from the reference alone.
        var mismatched = ExactClaimBinderV2_1.Bind("request-1",
            [Proposal("A2", ClaimResolutionState.RESOLVED) with { ExistingClaimId = id }],
            Atoms(), ScopeWithKnownClaim(scope, id, "A1", "DESCRIBES"));
        Assert.False(mismatched.IsComplete);
        Assert.Contains("existing-claim-id-mismatch", mismatched.Refusals.Values.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_distinct_initial_proposals_with_the_same_subject_and_predicate_do_not_collide()
    {
        var scope = OwnedOnlyScope();
        var result = ExactClaimBinderV2_1.Bind("request-1",
            [Proposal("A1", ClaimResolutionState.RESOLVED, value: "one"), Proposal("A1", ClaimResolutionState.RESOLVED, value: "two")],
            Atoms(), scope);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Bound.Count);
        Assert.NotEqual(result.Bound[0].Claim.ClaimId, result.Bound[1].Claim.ClaimId);
    }

    [Fact]
    public void Same_subject_predicate_and_ordinal_do_not_collide_across_packs()
    {
        var scope = OwnedOnlyScope();
        var packA = ExactClaimBinderV2_1.Bind("pack-A", [Proposal("A1", ClaimResolutionState.RESOLVED)], Atoms(), scope);
        var packB = ExactClaimBinderV2_1.Bind("pack-B", [Proposal("A1", ClaimResolutionState.RESOLVED)], Atoms(), scope);
        Assert.NotEqual(packA.Bound[0].Claim.ClaimId, packB.Bound[0].Claim.ClaimId);
    }

    [Fact]
    public void Replay_of_the_same_request_produces_byte_identical_ids()
    {
        var scope = OwnedOnlyScope();
        var first = ExactClaimBinderV2_1.Bind("request-1", [Proposal("A1", ClaimResolutionState.RESOLVED)], Atoms(), scope);
        var second = ExactClaimBinderV2_1.Bind("request-1", [Proposal("A1", ClaimResolutionState.RESOLVED)], Atoms(), scope);
        Assert.Equal(first.Bound[0].Claim.ClaimId, second.Bound[0].Claim.ClaimId);
        Assert.StartsWith(HarnessClaimIdentityV2_1.Prefix, first.Bound[0].Claim.ClaimId, StringComparison.Ordinal);
    }

    // ---- C3: owned/visible binding scope ------------------------------------------------------

    [Fact]
    public void Initial_subject_must_be_owned()
    {
        var scope = ClaimBindingScope.Create(["A1"], ["A1", "A2"]);
        var result = ExactClaimBinderV2_1.Bind("request-1", [Proposal("A1", ClaimResolutionState.RESOLVED)], Atoms(), scope);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void A_halo_subject_visible_but_not_owned_is_rejected()
    {
        var scope = ClaimBindingScope.Create(["A1"], ["A1", "A2"]);
        var result = ExactClaimBinderV2_1.Bind("request-1", [Proposal("A2", ClaimResolutionState.RESOLVED)], Atoms(), scope);
        Assert.False(result.IsComplete);
        Assert.Contains("subject-alias-not-owned", result.Refusals.Values.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_visible_object_is_accepted()
    {
        var scope = ClaimBindingScope.Create(["A1"], ["A1", "A2"]);
        var result = ExactClaimBinderV2_1.Bind("request-1",
            [RelationProposal("A1", "A2", ClaimResolutionState.RESOLVED, null)], Atoms(), scope);
        Assert.True(result.IsComplete);
        Assert.NotNull(result.Bound[0].Claim.Object);
    }

    [Fact]
    public void An_invisible_object_is_rejected()
    {
        var atoms = Atoms().Append(new SemanticSourceAtom("A9", "S9", 9, 1, 9, 0, "Outside")).ToArray();
        var scope = ClaimBindingScope.Create(["A1"], ["A1", "A2"]); // A9 exists in the source but not in this pack
        var result = ExactClaimBinderV2_1.Bind("request-1",
            [RelationProposal("A1", "A9", ClaimResolutionState.RESOLVED, null)], atoms, scope);
        Assert.False(result.IsComplete);
        Assert.Contains("object-alias-not-visible", result.Refusals.Values.Single(), StringComparison.Ordinal);
    }

    // ---- C5: EXHAUSTED is harness-only ---------------------------------------------------------

    [Fact]
    public void Model_originated_exhausted_is_rejected_by_the_binder()
    {
        var scope = OwnedOnlyScope();
        var result = ExactClaimBinderV2_1.Bind("request-1", [Proposal("A1", ClaimResolutionState.EXHAUSTED)], Atoms(), scope);
        Assert.False(result.IsComplete);
        Assert.Contains("model-may-not-originate-exhausted-state", result.Refusals.Values.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Model_originated_exhausted_is_rejected_by_the_codec()
    {
        var json = """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"WHOLE_ALIAS"}]},"predicate":"DESCRIBES","state":"EXHAUSTED"}]}""";
        var ex = Assert.Throws<InvalidOperationException>(() => Parse(json));
        Assert.Contains("exhausted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exhausted_state_is_not_in_the_provider_facing_schema_enum()
    {
        var schema = JsonSerializer.Serialize(SemanticClaimContractV2_1.Schema());
        Assert.DoesNotContain("EXHAUSTED", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_originated_exhausted_is_allowed()
    {
        // The harness marks a claim EXHAUSTED directly (e.g. DocumentAgentRuntime.ExhaustOpenClaims);
        // that never goes through the codec or the binder, so it is unaffected by the model-facing gate.
        var claim = new BoundSemanticClaim(
            "v5claim21-runtime-owned", new BoundClaimEndpoint([]), "DESCRIBES", null, null,
            ClaimResolutionState.EXHAUSTED, []);
        Assert.Equal(ClaimResolutionState.EXHAUSTED, claim.State);
    }

    // ---- C2: OPEN relation with an unresolved target -------------------------------------------

    [Fact]
    public void Open_relation_without_object_is_accepted_with_global_target()
    {
        var response = Parse("""{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"WHOLE_ALIAS"}]},"predicate":"RELATES_TO","state":"OPEN","evidenceNeeds":["GLOBAL_TARGET"]}]}""");
        Assert.Null(response.Claims[0].Object);
        Assert.Equal(ClaimResolutionState.OPEN, response.Claims[0].State);
    }

    [Fact]
    public void Resolved_relation_without_object_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"WHOLE_ALIAS"}]},"predicate":"RELATES_TO","state":"RESOLVED"}]}"""));
    }

    [Fact]
    public void Open_relation_without_object_and_without_global_target_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"WHOLE_ALIAS"}]},"predicate":"RELATES_TO","state":"OPEN","evidenceNeeds":["MORE_CONTEXT"]}]}"""));
    }

    [Fact]
    public void Open_relation_without_evidence_needs_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"WHOLE_ALIAS"}]},"predicate":"DESCRIBES","state":"OPEN"}]}"""));
    }

    [Fact]
    public void Conflicted_relation_without_object_is_accepted_with_any_evidence_need()
    {
        var response = Parse("""{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"WHOLE_ALIAS"}]},"predicate":"RELATES_TO","state":"CONFLICTED","evidenceNeeds":["IDENTITY_DISAMBIGUATION"]}]}""");
        Assert.Null(response.Claims[0].Object);
    }

    // ---- C4: closed selectionMode vocabulary ----------------------------------------------------

    [Fact]
    public void Invalid_selection_mode_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"REGEX_MATCH"}]},"predicate":"DESCRIBES","state":"RESOLVED"}]}"""));
    }

    [Fact]
    public void Schema_selection_mode_enum_is_closed_to_the_two_supported_values()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(SemanticClaimContractV2_1.Schema()));
        var modes = document.RootElement.GetProperty("properties").GetProperty("claims").GetProperty("items")
            .GetProperty("properties").GetProperty("subject").GetProperty("properties").GetProperty("sourceParts")
            .GetProperty("items").GetProperty("properties").GetProperty("selectionMode").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(["WHOLE_ALIAS", "VERBATIM_TEXT"], modes);
    }

    // ---- determinism and no leakage --------------------------------------------------------------

    [Fact]
    public void Schema_hash_is_deterministic()
    {
        Assert.Equal(SemanticClaimContractV2_1.SchemaHash(), SemanticClaimContractV2_1.SchemaHash());
    }

    [Fact]
    public void Prompt_and_request_bytes_are_deterministic()
    {
        var first = V5SemanticRequestComposerV2_1.Compose(Contract(), Packet());
        var second = V5SemanticRequestComposerV2_1.Compose(Contract(), Packet());
        Assert.Equal(first.PromptHash, second.PromptHash);
        Assert.Equal(first.RequestHash, second.RequestHash);
        Assert.Equal(first.Utf8Bytes, second.Utf8Bytes);
        Assert.Equal(SemanticClaimContractV2_1.SchemaHash(), first.SchemaHash);
    }

    [Fact]
    public void V2_1_protocol_has_no_gold_or_provider_leakage()
    {
        var composed = V5SemanticRequestComposerV2_1.Compose(Contract(), Packet());
        Assert.Equal(V5SemanticRequestComposerV2_1.Version, composed.ComposerVersion);
        Assert.DoesNotContain("providerCall", composed.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("goldRead", composed.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gold", composed.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V2_1_protocol_version_identity_differs_from_v2()
    {
        Assert.NotEqual(V5Protocol.ClaimSchemaVersionV2, V5Protocol.ClaimSchemaVersionV2_1);
        Assert.NotEqual(V5SemanticRequestComposer.Version, V5SemanticRequestComposerV2_1.Version);
        Assert.NotEqual(SemanticClaimContractV2.SchemaHash(), SemanticClaimContractV2_1.SchemaHash());
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static SemanticClaimResponseV2_1 Parse(string json) =>
        SemanticClaimResponseCodecV2_1.Parse(JsonDocument.Parse(json).RootElement, Contract());

    private static ClaimBindingScope OwnedOnlyScope() => ClaimBindingScope.Create(["A1", "A2"], ["A1", "A2"]);

    private static ClaimBindingScope ScopeWithKnownClaim(ClaimBindingScope scope, string claimId, string subjectAlias, string predicate) =>
        scope with
        {
            KnownClaims = new Dictionary<string, KnownClaimReference>(scope.KnownClaims, StringComparer.Ordinal)
            {
                [claimId] = new KnownClaimReference(SubjectIdentityFor(subjectAlias), predicate),
            },
        };

    /// <summary>The real bound identity for a whole-alias subject, computed the same way the binder does - never guessed as a string.</summary>
    private static string SubjectIdentityFor(string alias)
    {
        var bound = SemanticSourcePartBinder.Bind(Atoms(), [Part(alias)]);
        return new BoundClaimEndpoint(bound.Parts).Identity;
    }

    private static SemanticClaimProposalV2_1 Proposal(
        string alias, ClaimResolutionState state, string? value = "fact", IReadOnlyList<EvidenceNeed>? evidenceNeeds = null) =>
        new(new ClaimSourceEndpoint([Part(alias)]), "DESCRIBES", state == ClaimResolutionState.RESOLVED ? value : null,
            State: state, EvidenceNeeds: evidenceNeeds);

    private static SemanticClaimProposalV2_1 RelationProposal(
        string subjectAlias, string? objectAlias, ClaimResolutionState state, IReadOnlyList<EvidenceNeed>? evidenceNeeds) =>
        new(new ClaimSourceEndpoint([Part(subjectAlias)]), "RELATES_TO", null,
            objectAlias is null ? null : new ClaimSourceEndpoint([Part(objectAlias)]), state, evidenceNeeds);

    private static SemanticSourcePart Part(string alias) => new(alias, CanonicalSemanticSelectionMode.WholeAlias);

    private static IReadOnlyList<SemanticSourceAtom> Atoms() =>
        [new("A1", "S1", 1, 1, 1, 0, "Alpha"), new("A2", "S2", 2, 1, 1, 1, "Beta")];

    private static DocumentTaskContract Contract() => new(
        V5Protocol.TaskContractVersion,
        "v5-v2-1-test",
        "Source-backed test task.",
        [new SemanticPredicateDefinition("DESCRIBES", "A unary fact.")],
        [new SemanticRelationDefinition("RELATES_TO", "A relation.")],
        [new ProjectionRequest("claims", "Claims.")],
        new EvidencePolicy([EvidenceModality.TEXT], [EvidenceNeed.MORE_CONTEXT, EvidenceNeed.GLOBAL_TARGET, EvidenceNeed.IDENTITY_DISAMBIGUATION]),
        "retain-open",
        new ExecutionBudget(MaxSemanticModelCalls: 1));

    private static V5EvidencePacket Packet() => new([], [], [], [], [], []);
}
