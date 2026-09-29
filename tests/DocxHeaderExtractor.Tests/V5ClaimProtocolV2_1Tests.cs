using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free contract tests for the currently-evolving v2.1 claim protocol: durable claim
/// identity, a representable OPEN relation with an unresolved target, owned/visible binding-scope
/// enforcement, mandatory evidenceNeeds, a harness-only EXHAUSTED state, and - hardened after a real
/// 3-pack canary at commit 72bb954 - a provider wire with no selectionMode field at all.
/// </summary>
public sealed class V5ClaimProtocolV2_1Tests
{
    // ---- normalization: no selectionMode on the wire ------------------------------------------

    [Fact]
    public void SourceAlias_alone_normalizes_to_whole_alias()
    {
        var canonical = ProviderSourcePartNormalization.ToCanonical(new ProviderSourcePartV2_1("A1"));
        Assert.Equal(CanonicalSemanticSelectionMode.WholeAlias, canonical.SelectionMode);
        Assert.Null(canonical.VerbatimText);
    }

    [Fact]
    public void SourceAlias_with_verbatim_text_normalizes_to_verbatim_text()
    {
        var canonical = ProviderSourcePartNormalization.ToCanonical(new ProviderSourcePartV2_1("A1", "Alpha"));
        Assert.Equal(CanonicalSemanticSelectionMode.VerbatimText, canonical.SelectionMode);
        Assert.Equal("Alpha", canonical.VerbatimText);
    }

    [Fact]
    public void A_provider_selection_mode_field_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"WHOLE_ALIAS"}]},"predicate":"DESCRIBES","state":"RESOLVED","evidenceNeeds":[]}]}"""));
        Assert.Contains("selectionMode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_verbatim_text_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","verbatimText":""}]},"predicate":"DESCRIBES","state":"RESOLVED","evidenceNeeds":[]}]}"""));
    }

    [Fact]
    public void Exact_verbatim_binding_remains_strict_after_normalization()
    {
        var scope = OwnedOnlyScope();
        var proposal = new SemanticClaimProposalV2_1(
            Endpoint(new ProviderSourcePartV2_1("A1", "not-in-atom")), "DESCRIBES", EvidenceNeeds: []);
        var result = ExactClaimBinderV2_1.Bind("request-1", [proposal], Atoms(), scope);
        Assert.False(result.IsComplete);
        Assert.Contains("does not contain that text", result.Refusals.Values.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_wire_can_no_longer_express_whole_alias_with_a_verbatim_quote()
    {
        // The canary's PACK_004 failure mode - selectionMode:WHOLE_ALIAS plus a verbatimText quote -
        // has no representation any more: verbatimText presence alone decides the mode.
        Assert.DoesNotContain("selectionMode", JsonSerializer.Serialize(new ProviderSourcePartV2_1("A1", "Alpha")), StringComparison.Ordinal);
    }

    // ---- evidenceNeeds is mandatory on every claim ---------------------------------------------

    [Fact]
    public void Resolved_with_empty_evidence_needs_is_accepted()
    {
        var response = Parse(ValidUnaryJson());
        Assert.Empty(response.Claims[0].EvidenceNeeds!);
    }

    [Fact]
    public void Resolved_with_a_non_empty_evidence_need_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"DESCRIBES","value":"fact","state":"RESOLVED","evidenceNeeds":["MORE_CONTEXT"]}]}"""));
    }

    [Fact]
    public void Open_with_global_target_is_accepted()
    {
        var response = Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"RELATES_TO","state":"OPEN","evidenceNeeds":["GLOBAL_TARGET"]}]}""");
        Assert.Null(response.Claims[0].Object);
    }

    [Fact]
    public void Open_with_empty_evidence_needs_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"DESCRIBES","state":"OPEN","evidenceNeeds":[]}]}"""));
    }

    [Fact]
    public void Missing_evidence_needs_field_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"DESCRIBES","value":"fact","state":"RESOLVED"}]}"""));
    }

    [Fact]
    public void Conflicted_requires_a_non_empty_evidence_need()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"DESCRIBES","state":"CONFLICTED","evidenceNeeds":[]}]}"""));
        var accepted = Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"DESCRIBES","state":"CONFLICTED","evidenceNeeds":["IDENTITY_DISAMBIGUATION"]}]}""");
        Assert.Equal(ClaimResolutionState.CONFLICTED, accepted.Claims[0].State);
    }

    [Fact]
    public void Schema_requires_evidence_needs_on_every_claim()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(SemanticClaimContractV2_1.Schema()));
        var required = document.RootElement.GetProperty("properties").GetProperty("claims").GetProperty("items")
            .GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("evidenceNeeds", required);
    }

    // ---- EXHAUSTED is harness-only, never provider-owned ---------------------------------------

    [Fact]
    public void Provider_originated_exhausted_is_rejected_by_the_codec()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"DESCRIBES","state":"EXHAUSTED","evidenceNeeds":[]}]}"""));
        Assert.Contains("exhausted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Provider_originated_exhausted_is_rejected_by_the_binder()
    {
        var scope = OwnedOnlyScope();
        var proposal = new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A1")), "DESCRIBES",
            State: ClaimResolutionState.EXHAUSTED, EvidenceNeeds: []);
        var result = ExactClaimBinderV2_1.Bind("request-1", [proposal], Atoms(), scope);
        Assert.False(result.IsComplete);
        Assert.Contains("model-may-not-originate-exhausted-state", result.Refusals.Values.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Harness_originated_exhausted_is_allowed()
    {
        // The harness marks a claim EXHAUSTED directly (e.g. DocumentAgentRuntime.ExhaustOpenClaims);
        // that never goes through the codec or the binder, so it is unaffected by the provider-facing gate.
        var claim = new BoundSemanticClaim(
            "v5claim21-runtime-owned", new BoundClaimEndpoint([]), "DESCRIBES", null, null,
            ClaimResolutionState.EXHAUSTED, []);
        Assert.Equal(ClaimResolutionState.EXHAUSTED, claim.State);
    }

    [Fact]
    public void Exhausted_state_is_not_in_the_provider_facing_schema_enum()
    {
        var schema = JsonSerializer.Serialize(SemanticClaimContractV2_1.Schema());
        Assert.DoesNotContain("EXHAUSTED", schema, StringComparison.Ordinal);
    }

    // ---- relation object: resolved requires it, OPEN/CONFLICTED may explain its absence --------

    [Fact]
    public void Open_relation_missing_object_is_accepted_with_global_target()
    {
        var response = Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"RELATES_TO","state":"OPEN","evidenceNeeds":["GLOBAL_TARGET"]}]}""");
        Assert.Null(response.Claims[0].Object);
    }

    [Fact]
    public void Resolved_relation_missing_object_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(
            """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"RELATES_TO","state":"RESOLVED","evidenceNeeds":[]}]}"""));
    }

    // ---- C3: owned/visible binding scope (unchanged by the wire hardening) ---------------------

    [Fact]
    public void Initial_subject_must_be_owned()
    {
        var scope = ClaimBindingScope.Create(["A1"], ["A1", "A2"]);
        var proposal = new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A1")), "DESCRIBES", EvidenceNeeds: []);
        var result = ExactClaimBinderV2_1.Bind("request-1", [proposal], Atoms(), scope);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void A_halo_subject_visible_but_not_owned_is_rejected()
    {
        var scope = ClaimBindingScope.Create(["A1"], ["A1", "A2"]);
        var proposal = new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A2")), "DESCRIBES", EvidenceNeeds: []);
        var result = ExactClaimBinderV2_1.Bind("request-1", [proposal], Atoms(), scope);
        Assert.False(result.IsComplete);
        Assert.Contains("subject-alias-not-owned", result.Refusals.Values.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_visible_object_is_accepted()
    {
        var scope = ClaimBindingScope.Create(["A1"], ["A1", "A2"]);
        var proposal = new SemanticClaimProposalV2_1(
            Endpoint(new ProviderSourcePartV2_1("A1")), "RELATES_TO",
            Object: Endpoint(new ProviderSourcePartV2_1("A2")), EvidenceNeeds: []);
        var result = ExactClaimBinderV2_1.Bind("request-1", [proposal], Atoms(), scope);
        Assert.True(result.IsComplete);
        Assert.NotNull(result.Bound[0].Claim.Object);
    }

    [Fact]
    public void An_invisible_object_is_rejected()
    {
        var atoms = Atoms().Append(new SemanticSourceAtom("A9", "S9", 9, 1, 9, 0, "Outside")).ToArray();
        var scope = ClaimBindingScope.Create(["A1"], ["A1", "A2"]);
        var proposal = new SemanticClaimProposalV2_1(
            Endpoint(new ProviderSourcePartV2_1("A1")), "RELATES_TO",
            Object: Endpoint(new ProviderSourcePartV2_1("A9")), EvidenceNeeds: []);
        var result = ExactClaimBinderV2_1.Bind("request-1", [proposal], atoms, scope);
        Assert.False(result.IsComplete);
        Assert.Contains("object-alias-not-visible", result.Refusals.Values.Single(), StringComparison.Ordinal);
    }

    // ---- durable claim identity (unchanged by the wire hardening) ------------------------------

    [Fact]
    public void Open_to_resolved_transition_keeps_the_same_durable_claim_id()
    {
        var scope = OwnedOnlyScope();
        var open = ExactClaimBinderV2_1.Bind("request-1",
            [new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A1")), "DESCRIBES",
                State: ClaimResolutionState.OPEN, EvidenceNeeds: [EvidenceNeed.MORE_CONTEXT])],
            Atoms(), scope);
        Assert.True(open.IsComplete);
        var openId = open.Bound[0].Claim.ClaimId;

        var resolved = ExactClaimBinderV2_1.Bind("request-1",
            [new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A1")), "DESCRIBES", "now-known",
                State: ClaimResolutionState.RESOLVED, EvidenceNeeds: [], ExistingClaimId: openId)],
            Atoms(), ScopeWithKnownClaim(scope, openId, "A1", "DESCRIBES"));
        Assert.True(resolved.IsComplete);
        Assert.Equal(openId, resolved.Bound[0].Claim.ClaimId);
        Assert.Equal("now-known", resolved.Bound[0].Claim.Value);
    }

    [Fact]
    public void Relation_target_resolution_does_not_change_the_durable_claim_id()
    {
        var scope = ClaimBindingScope.Create(["A1"], ["A1", "A2"]);
        var open = ExactClaimBinderV2_1.Bind("request-1",
            [new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A1")), "RELATES_TO",
                State: ClaimResolutionState.OPEN, EvidenceNeeds: [EvidenceNeed.GLOBAL_TARGET])],
            Atoms(), scope);
        var openId = open.Bound[0].Claim.ClaimId;

        var resolved = ExactClaimBinderV2_1.Bind("request-1",
            [new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A1")), "RELATES_TO",
                Object: Endpoint(new ProviderSourcePartV2_1("A2")), State: ClaimResolutionState.RESOLVED,
                EvidenceNeeds: [], ExistingClaimId: openId)],
            Atoms(), ScopeWithKnownClaim(scope, openId, "A1", "RELATES_TO"));
        Assert.True(resolved.IsComplete);
        Assert.Equal(openId, resolved.Bound[0].Claim.ClaimId);
        Assert.NotNull(resolved.Bound[0].Claim.Object);
    }

    [Fact]
    public void Unknown_existing_claim_id_is_rejected()
    {
        var scope = OwnedOnlyScope();
        var proposal = new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A1")), "DESCRIBES",
            EvidenceNeeds: [], ExistingClaimId: "v5claim21-does-not-exist");
        var result = ExactClaimBinderV2_1.Bind("request-1", [proposal], Atoms(), scope);
        Assert.False(result.IsComplete);
        Assert.Contains("unknown-existing-claim-id", result.Refusals.Values.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Mismatching_existing_claim_id_is_rejected()
    {
        var scope = ClaimBindingScope.Create(["A1", "A2"], ["A1", "A2"]);
        var open = ExactClaimBinderV2_1.Bind("request-1",
            [new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A1")), "DESCRIBES",
                State: ClaimResolutionState.OPEN, EvidenceNeeds: [EvidenceNeed.MORE_CONTEXT])],
            Atoms(), scope);
        var id = open.Bound[0].Claim.ClaimId;

        var mismatched = ExactClaimBinderV2_1.Bind("request-1",
            [new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A2")), "DESCRIBES",
                State: ClaimResolutionState.RESOLVED, EvidenceNeeds: [], ExistingClaimId: id)],
            Atoms(), ScopeWithKnownClaim(scope, id, "A1", "DESCRIBES"));
        Assert.False(mismatched.IsComplete);
        Assert.Contains("existing-claim-id-mismatch", mismatched.Refusals.Values.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_distinct_initial_proposals_with_the_same_subject_and_predicate_do_not_collide()
    {
        var scope = OwnedOnlyScope();
        var result = ExactClaimBinderV2_1.Bind("request-1",
            [new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A1")), "DESCRIBES", "one", EvidenceNeeds: []),
             new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A1")), "DESCRIBES", "two", EvidenceNeeds: [])],
            Atoms(), scope);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Bound.Count);
        Assert.NotEqual(result.Bound[0].Claim.ClaimId, result.Bound[1].Claim.ClaimId);
    }

    [Fact]
    public void Replay_of_the_same_request_produces_byte_identical_ids()
    {
        var scope = OwnedOnlyScope();
        var proposal = new SemanticClaimProposalV2_1(Endpoint(new ProviderSourcePartV2_1("A1")), "DESCRIBES", EvidenceNeeds: []);
        var first = ExactClaimBinderV2_1.Bind("request-1", [proposal], Atoms(), scope);
        var second = ExactClaimBinderV2_1.Bind("request-1", [proposal], Atoms(), scope);
        Assert.Equal(first.Bound[0].Claim.ClaimId, second.Bound[0].Claim.ClaimId);
        Assert.StartsWith(HarnessClaimIdentityV2_1.Prefix, first.Bound[0].Claim.ClaimId, StringComparison.Ordinal);
    }

    // ---- determinism, no Gold/provider leakage --------------------------------------------------

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
        var bound = SemanticSourcePartBinder.Bind(Atoms(), [new SemanticSourcePart(alias, CanonicalSemanticSelectionMode.WholeAlias)]);
        return new BoundClaimEndpoint(bound.Parts).Identity;
    }

    private static ClaimSourceEndpointV2_1 Endpoint(params ProviderSourcePartV2_1[] parts) => new(parts);

    private static string ValidUnaryJson(string predicate = "DESCRIBES", string alias = "A1") =>
        $"{{\"claims\":[{{\"subject\":{{\"sourceParts\":[{{\"sourceAlias\":\"{alias}\"}}]}},\"predicate\":\"{predicate}\",\"value\":\"fact\",\"state\":\"RESOLVED\",\"evidenceNeeds\":[]}}]}}";

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
