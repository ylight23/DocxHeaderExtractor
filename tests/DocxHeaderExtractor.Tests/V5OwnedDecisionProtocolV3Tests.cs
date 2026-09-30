using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

public sealed class V5OwnedDecisionProtocolV3Tests
{
    [Fact]
    public void Schema_hard_closes_subject_identity_and_requires_one_decision_per_owned_subject()
    {
        var packet = Packet();
        var contract = Contract();
        var canonical = V5OwnedDecisionRequestComposerV3.BuildCanonical(contract, packet);
        using var schema = JsonDocument.Parse(JsonSerializer.Serialize(canonical.ResponseSchema, CanonicalJson.Options));
        var decisions = schema.RootElement.GetProperty("properties").GetProperty("decisions");

        Assert.Equal(2, decisions.GetProperty("minItems").GetInt32());
        Assert.Equal(2, decisions.GetProperty("maxItems").GetInt32());

        var raw = schema.RootElement.GetRawText();
        Assert.DoesNotContain("sourceAlias", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"subject\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"CONTEXT\"", raw, StringComparison.Ordinal);
        Assert.Contains("Return exactly 2 decisions", canonical.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Codec_rejects_legacy_subject_alias_and_missing_owned_decision()
    {
        var packet = Packet();
        var contract = Contract();

        using var legacy = JsonDocument.Parse("""
            {
              "decisions": [
                {
                  "kind": "CLAIMS",
                  "claims": [
                    {
                      "subject": {"sourceAlias": "HALO"},
                      "predicate": "DESCRIBES",
                      "value": "x",
                      "state": "RESOLVED",
                      "evidenceNeeds": []
                    }
                  ]
                },
                {"kind": "NONE", "claims": []}
              ]
            }
            """);
        var legacyError = Assert.Throws<InvalidOperationException>(() =>
            V5OwnedDecisionResponseCodecV3.Parse(legacy.RootElement, contract, packet));
        Assert.Contains("claim-unknown-field:subject", legacyError.Message, StringComparison.Ordinal);

        using var sparse = JsonDocument.Parse("""
            {"decisions":[{"kind":"NONE","claims":[]}]}
            """);
        var sparseError = Assert.Throws<InvalidOperationException>(() =>
            V5OwnedDecisionResponseCodecV3.Parse(sparse.RootElement, contract, packet));
        Assert.Contains("decision-count-mismatch:1:2", sparseError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Codec_rejects_missing_required_positional_index()
    {
        var packet = Packet();
        var contract = Contract();
        using var payload = JsonDocument.Parse("""
            {
              "decisions": [
                {
                  "kind": "CLAIMS",
                  "claims": [
                    {
                      "predicate": "RELATES_TO",
                      "objectParts": [{"scope": "CONTEXT"}],
                      "state": "RESOLVED",
                      "evidenceNeeds": []
                    }
                  ]
                },
                {"kind": "NONE", "claims": []}
              ]
            }
            """);

        var error = Assert.Throws<InvalidOperationException>(() =>
            V5OwnedDecisionResponseCodecV3.Parse(payload.RootElement, contract, packet));
        Assert.Contains("object-part-index-missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_owned_anchor_can_use_different_source_selection_per_claim()
    {
        var packet = Packet();
        var contract = Contract();
        var response = new V5OwnedDecisionResponseV3([
            new V5OwnedSubjectDecisionV3(
                V5OwnedDecisionKindsV3.Claims,
                [
                    new V5OwnedDecisionClaimV3(
                        "DESCRIBES",
                        Value: "prefix",
                        VerbatimText: "Al",
                        EvidenceNeeds: []),
                    new V5OwnedDecisionClaimV3(
                        "RELATES_TO",
                        AdditionalSubjectParts: [new V5OwnedAdditionalSubjectPartV3(1)],
                        ObjectParts: [new V5VisibleSourcePartRefV3(V5EvidenceReferenceScopesV3.Context, 0)],
                        EvidenceNeeds: []),
                ]),
            new V5OwnedSubjectDecisionV3(V5OwnedDecisionKindsV3.None, []),
        ]);

        Assert.Empty(V5OwnedDecisionContractV3.Validate(response, contract, packet));
        var proposals = V5OwnedDecisionAdapterV3.ToV2_1(response, contract, packet);

        var unary = Assert.Single(proposals.Where(item => item.Predicate == "DESCRIBES"));
        Assert.Equal("Al", Assert.Single(unary.Subject.SourceParts).VerbatimText);

        var relation = Assert.Single(proposals.Where(item => item.Predicate == "RELATES_TO"));
        Assert.Equal(new[] { "OWNED-A", "OWNED-B" }, relation.Subject.SourceParts.Select(part => part.SourceAlias));
        Assert.Equal("HALO-C", Assert.Single(relation.Object!.SourceParts).SourceAlias);
    }

    [Fact]
    public void Multi_atom_owned_subject_and_context_relation_target_bind_through_existing_exact_binder()
    {
        var packet = Packet();
        var contract = Contract();
        var response = new V5OwnedDecisionResponseV3([
            new V5OwnedSubjectDecisionV3(
                V5OwnedDecisionKindsV3.Claims,
                [
                    new V5OwnedDecisionClaimV3(
                        "DESCRIBES",
                        Value: "section",
                        AdditionalSubjectParts: [new V5OwnedAdditionalSubjectPartV3(1)],
                        State: ClaimResolutionState.RESOLVED,
                        EvidenceNeeds: []),
                    new V5OwnedDecisionClaimV3(
                        "RELATES_TO",
                        ObjectParts: [new V5VisibleSourcePartRefV3(V5EvidenceReferenceScopesV3.Context, 0)],
                        State: ClaimResolutionState.RESOLVED,
                        EvidenceNeeds: []),
                ]),
            new V5OwnedSubjectDecisionV3(V5OwnedDecisionKindsV3.None, []),
        ]);

        Assert.Empty(V5OwnedDecisionContractV3.Validate(response, contract, packet));
        var proposals = V5OwnedDecisionAdapterV3.ToV2_1(response, contract, packet);
        Assert.Equal(2, proposals.Count);

        var atoms = new[]
        {
            new SemanticSourceAtom("OWNED-A", "source-a", 0, 1, 1, 0, "Alpha"),
            new SemanticSourceAtom("OWNED-B", "source-b", 1, 1, 1, 1, "Beta"),
            new SemanticSourceAtom("HALO-C", "source-c", 2, 1, 1, 2, "Gamma"),
        };
        var binding = ExactClaimBinderV2_1.Bind(
            "p5c-v3-adapter",
            proposals,
            atoms,
            ClaimBindingScope.Create(["OWNED-A", "OWNED-B"], ["OWNED-A", "OWNED-B", "HALO-C"]));

        Assert.True(binding.IsComplete);
        Assert.Equal(2, binding.Bound.Count);
        Assert.Equal("source-a:0-5|source-b:0-4", Assert.Single(binding.Bound.Where(item =>
            item.Claim.Predicate == "DESCRIBES")).Claim.Subject.Identity);
        var relation = Assert.Single(binding.Bound.Where(item => item.Claim.Predicate == "RELATES_TO")).Claim;
        Assert.Equal("source-a:0-5", relation.Subject.Identity);
        Assert.Equal("source-c:0-5", relation.Object!.Identity);
    }

    [Fact]
    public void Validator_forbids_whole_atom_retyping()
    {
        var packet = Packet();
        var contract = Contract();
        var response = new V5OwnedDecisionResponseV3([
            new V5OwnedSubjectDecisionV3(
                V5OwnedDecisionKindsV3.Claims,
                [new V5OwnedDecisionClaimV3("DESCRIBES", Value: "x", VerbatimText: "Alpha", EvidenceNeeds: [])]),
            new V5OwnedSubjectDecisionV3(V5OwnedDecisionKindsV3.None, []),
        ]);

        var issues = V5OwnedDecisionContractV3.Validate(response, contract, packet);
        Assert.Contains("whole-atom-verbatim-forbidden:subject:0:DESCRIBES", issues);
    }

    [Fact]
    public void Composer_is_deterministic_and_provider_free()
    {
        var first = V5OwnedDecisionRequestComposerV3.Compose(Contract(), Packet());
        var second = V5OwnedDecisionRequestComposerV3.Compose(Contract(), Packet());

        Assert.Equal(V5OwnedDecisionProtocolV3.ComposerVersion, first.ComposerVersion);
        Assert.Equal(first.PromptHash, second.PromptHash);
        Assert.Equal(first.SchemaHash, second.SchemaHash);
        Assert.Equal(first.RequestHash, second.RequestHash);
        Assert.Equal(first.Prompt, second.Prompt);
    }

    private static V5EvidencePacketV2_1 Packet()
    {
        var graph = EvidenceGraphBuilder.Build([
            new SourceObservation("E-A", "source-a", "OWNED-A", 0, EvidenceModality.TEXT, "Alpha", new StructuralSpan(0, 5)),
            new SourceObservation("E-B", "source-b", "OWNED-B", 1, EvidenceModality.TEXT, "Beta", new StructuralSpan(0, 4)),
            new SourceObservation("E-C", "source-c", "HALO-C", 2, EvidenceModality.TEXT, "Gamma", new StructuralSpan(0, 5)),
        ]);
        return new V5EvidencePacketV2_1([graph.Nodes[0], graph.Nodes[1]], [graph.Nodes[2]], [], [], [], []);
    }

    private static DocumentTaskContract Contract() => new(
        V5Protocol.TaskContractVersion,
        "p5c-owned-decision-contract",
        "Provider-free hard-closed subject contract prototype.",
        [new SemanticPredicateDefinition("DESCRIBES", "A unary fact.")],
        [new SemanticRelationDefinition("RELATES_TO", "A relation.")],
        [new ProjectionRequest("claims", "Claims.")],
        new EvidencePolicy(
            [EvidenceModality.TEXT],
            [EvidenceNeed.MORE_CONTEXT, EvidenceNeed.GLOBAL_TARGET, EvidenceNeed.IDENTITY_DISAMBIGUATION]),
        "retain-open",
        new ExecutionBudget(MaxSemanticModelCalls: 1));
}
