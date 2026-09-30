using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P5C provider-free contract and real-pack gates.  V3 does not replace v2.1 transport here; it
/// proves that subject ownership can be encoded by position while reusing the existing exact binder.
/// </summary>
public sealed class V5OwnedDecisionProtocolV3Tests
{
    [Fact]
    public void Schema_requires_exactly_one_decision_per_owned_subject_and_has_no_subject_alias_output()
    {
        var packet = Packet();
        using var schema = JsonDocument.Parse(JsonSerializer.Serialize(
            V5OwnedDecisionProtocolV3.ResponseSchema(Contract(), packet), CanonicalJson.Options));
        var decisions = schema.RootElement.GetProperty("properties").GetProperty("decisions");

        Assert.Equal(packet.SubjectEvidence.Count, decisions.GetProperty("minItems").GetInt32());
        Assert.Equal(packet.SubjectEvidence.Count, decisions.GetProperty("maxItems").GetInt32());
        var raw = schema.RootElement.GetRawText();
        Assert.DoesNotContain("sourceAlias", raw, StringComparison.Ordinal);
        Assert.Contains("ownedIndex", raw, StringComparison.Ordinal);
        Assert.Contains("visibleIndex", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_owned_decision_is_rejected_before_binding()
    {
        using var payload = JsonDocument.Parse("""
            {"decisions":[{"disposition":"NO_CLAIM","claims":[]}]}
            """);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            V5OwnedDecisionProtocolV3.ParseAndTranslate(payload.RootElement, Contract(), Packet()));
        Assert.Equal("decision-count-mismatch:1:2", ex.Message);
    }

    [Fact]
    public void Halo_subject_field_is_not_part_of_the_v3_claim_contract()
    {
        using var payload = JsonDocument.Parse("""
            {
              "decisions":[
                {
                  "disposition":"CLAIMS",
                  "claims":[{
                    "subject":{"sourceParts":[{"sourceAlias":"HALO"}]},
                    "predicate":"DESCRIBES",
                    "value":"fact",
                    "state":"RESOLVED",
                    "evidenceNeeds":[]
                  }]
                },
                {"disposition":"NO_CLAIM","claims":[]}
              ]
            }
            """);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            V5OwnedDecisionProtocolV3.ParseAndTranslate(payload.RootElement, Contract(), Packet()));
        Assert.Equal("claim-field-not-in-contract:subject", ex.Message);
    }

    [Fact]
    public void Context_only_evidence_can_be_a_relation_object_but_not_the_implicit_subject()
    {
        using var payload = JsonDocument.Parse("""
            {
              "decisions":[
                {
                  "disposition":"CLAIMS",
                  "claims":[{
                    "predicate":"RELATES_TO",
                    "object":{"sourceParts":[{"visibleIndex":1}]},
                    "state":"RESOLVED",
                    "evidenceNeeds":[]
                  }]
                },
                {"disposition":"NO_CLAIM","claims":[]}
              ]
            }
            """);
        var translation = V5OwnedDecisionProtocolV3.ParseAndTranslate(payload.RootElement, Contract(), Packet());
        var proposal = Assert.Single(translation.Proposals).Proposal;
        Assert.Equal("OWNED1", Assert.Single(proposal.Subject.SourceParts).SourceAlias);
        Assert.Equal("HALO", Assert.Single(proposal.Object!.SourceParts).SourceAlias);

        var binding = ExactClaimBinderV2_1.Bind(
            "p5c-halo-object", translation.Proposals, Atoms(),
            ClaimBindingScope.Create(["OWNED1", "OWNED2"], ["OWNED1", "HALO", "OWNED2"]));
        Assert.True(binding.IsComplete);
        Assert.Equal("OWNED1", Assert.Single(binding.Bound).Claim.Subject.Parts[0].Alias);
        Assert.Equal("HALO", binding.Bound[0].Claim.Object!.Parts[0].Alias);
    }

    [Fact]
    public void Multipart_subject_remains_representable_using_later_owned_indices()
    {
        using var payload = JsonDocument.Parse("""
            {
              "decisions":[
                {
                  "disposition":"CLAIMS",
                  "subjectSelection":{"additionalOwnedParts":[{"ownedIndex":1}]},
                  "claims":[{
                    "predicate":"DESCRIBES",
                    "value":"multipart",
                    "state":"RESOLVED",
                    "evidenceNeeds":[]
                  }]
                },
                {"disposition":"NO_CLAIM","claims":[]}
              ]
            }
            """);
        var translation = V5OwnedDecisionProtocolV3.ParseAndTranslate(payload.RootElement, Contract(), Packet());
        var proposal = Assert.Single(translation.Proposals).Proposal;
        Assert.Equal(["OWNED1", "OWNED2"], proposal.Subject.SourceParts.Select(part => part.SourceAlias));

        var binding = ExactClaimBinderV2_1.Bind(
            "p5c-multipart", translation.Proposals, Atoms(),
            ClaimBindingScope.Create(["OWNED1", "OWNED2"], ["OWNED1", "HALO", "OWNED2"]));
        Assert.True(binding.IsComplete);
        Assert.Equal(2, binding.Bound[0].Claim.Subject.Parts.Count);
    }

    [Fact]
    public void No_claim_is_explicit_and_cannot_hide_claims_or_selection()
    {
        using var claimsPayload = JsonDocument.Parse("""
            {
              "decisions":[
                {"disposition":"NO_CLAIM","claims":[{"predicate":"DESCRIBES","state":"RESOLVED","evidenceNeeds":[]}]},
                {"disposition":"NO_CLAIM","claims":[]}
              ]
            }
            """);
        Assert.Equal("no-claim-decision-carries-claims",
            Assert.Throws<InvalidOperationException>(() =>
                V5OwnedDecisionProtocolV3.ParseAndTranslate(claimsPayload.RootElement, Contract(), Packet())).Message);

        using var selectionPayload = JsonDocument.Parse("""
            {
              "decisions":[
                {"disposition":"NO_CLAIM","subjectSelection":{"verbatimText":"Owned"},"claims":[]},
                {"disposition":"NO_CLAIM","claims":[]}
              ]
            }
            """);
        Assert.Equal("no-claim-decision-carries-subject-selection",
            Assert.Throws<InvalidOperationException>(() =>
                V5OwnedDecisionProtocolV3.ParseAndTranslate(selectionPayload.RootElement, Contract(), Packet())).Message);
    }

    [Fact]
    public void Real_src095_pack_boundary_can_claim_L1472_only_from_its_owner_pack()
    {
        var contract = DocumentStructureTaskContract.Create();
        var requests = V5PdfPreflightBuilder.BuildOwnedDecisionV3Requests(
            TestRepository.Path(SourcePdfCorpus.Src095), "SRC-095", contract,
            V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId);

        var contextPack = requests.Single(request => request.PackId.EndsWith("PACK_016", StringComparison.Ordinal));
        var ownerPack = requests.Single(request => request.PackId.EndsWith("PACK_017", StringComparison.Ordinal));
        Assert.Contains(contextPack.Packet.ContextOnlyEvidence, node => node.SourceAlias == "L1472:S0");
        Assert.DoesNotContain(contextPack.Packet.SubjectEvidence, node => node.SourceAlias == "L1472:S0");
        var ownerIndex = Array.FindIndex(ownerPack.Packet.SubjectEvidence.ToArray(), node => node.SourceAlias == "L1472:S0");
        Assert.True(ownerIndex >= 0);

        var decisions = Enumerable.Range(0, ownerPack.Packet.SubjectEvidence.Count)
            .Select(index => index == ownerIndex
                ? (object)new
                {
                    disposition = V5OwnedDecisionProtocolV3.Claims,
                    claims = new object[]
                    {
                        new
                        {
                            predicate = "STRUCTURAL_REGION",
                            value = "11.2.1. Frame Types",
                            state = "RESOLVED",
                            evidenceNeeds = Array.Empty<string>(),
                        },
                    },
                }
                : new { disposition = V5OwnedDecisionProtocolV3.NoClaim, claims = Array.Empty<object>() })
            .ToArray();
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { decisions }));
        var translation = V5OwnedDecisionProtocolV3.ParseAndTranslate(payload.RootElement, contract, ownerPack.Packet);

        var atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(SourcePdfCorpus.Src095));
        var binding = ExactClaimBinderV2_1.Bind(
            ownerPack.PackId,
            translation.Proposals,
            atoms,
            ClaimBindingScope.Create(ownerPack.OwnedAliases, ownerPack.VisibleAliases));

        Assert.True(binding.IsComplete);
        var bound = Assert.Single(binding.Bound);
        Assert.Equal("L1472:S0", Assert.Single(bound.Claim.Subject.Parts).Alias);
        Assert.Equal("11.2.1. Frame Types", Assert.Single(bound.Claim.Subject.Parts).Text);
    }

    [Fact]
    public void Every_real_v3_pack_freezes_decision_cardinality_to_its_owned_count()
    {
        var contract = DocumentStructureTaskContract.Create();
        var src089 = V5PdfPreflightBuilder.BuildOwnedDecisionV3Requests(
            TestRepository.Path(SourcePdfCorpus.Src089), "SRC-089", contract,
            V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId);
        var src095 = V5PdfPreflightBuilder.BuildOwnedDecisionV3Requests(
            TestRepository.Path(SourcePdfCorpus.Src095), "SRC-095", contract,
            V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId);

        var all = src089.Concat(src095).ToArray();
        Assert.Equal(31, all.Length);
        foreach (var pack in all)
        {
            using var request = JsonDocument.Parse(pack.Request.Prompt);
            var decisions = request.RootElement.GetProperty("responseSchema").GetProperty("properties").GetProperty("decisions");
            Assert.Equal(pack.OwnedAliases.Count, decisions.GetProperty("minItems").GetInt32());
            Assert.Equal(pack.OwnedAliases.Count, decisions.GetProperty("maxItems").GetInt32());
            Assert.Equal(pack.OwnedAliases, pack.Packet.SubjectEvidence.Select(node => node.SourceAlias));
            Assert.Empty(pack.Packet.SubjectEvidence.Select(node => node.SourceAlias)
                .Intersect(pack.Packet.ContextOnlyEvidence.Select(node => node.SourceAlias), StringComparer.Ordinal));
        }
    }

    private static V5EvidencePacketV2_1 Packet()
    {
        var graph = EvidenceGraphBuilder.Build([
            new SourceObservation("e1", "s1", "OWNED1", 0, EvidenceModality.TEXT, "Owned one", new StructuralSpan(0, 9)),
            new SourceObservation("halo", "sh", "HALO", 1, EvidenceModality.TEXT, "Halo", new StructuralSpan(0, 4)),
            new SourceObservation("e2", "s2", "OWNED2", 2, EvidenceModality.TEXT, "Owned two", new StructuralSpan(0, 9)),
        ]);
        return new V5EvidencePacketV2_1([graph.Nodes[0], graph.Nodes[2]], [graph.Nodes[1]], [], [], [], []);
    }

    private static IReadOnlyList<SemanticSourceAtom> Atoms() =>
    [
        new("OWNED1", "s1", 0, 1, 1, 0, "Owned one"),
        new("HALO", "sh", 1, 1, 1, 1, "Halo"),
        new("OWNED2", "s2", 2, 1, 1, 2, "Owned two"),
    ];

    private static DocumentTaskContract Contract() => new(
        V5Protocol.TaskContractVersion, "p5c-v3-test", "P5C owned decision test.",
        [new SemanticPredicateDefinition("DESCRIBES", "A unary fact.")],
        [new SemanticRelationDefinition("RELATES_TO", "A relation.")],
        [new ProjectionRequest("claims", "Claims.")],
        new EvidencePolicy([EvidenceModality.TEXT], [EvidenceNeed.MORE_CONTEXT, EvidenceNeed.GLOBAL_TARGET, EvidenceNeed.IDENTITY_DISAMBIGUATION]),
        "retain-open", new ExecutionBudget(MaxSemanticModelCalls: 1));
}
