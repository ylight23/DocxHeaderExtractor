using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free contract tests for source-backed claim protocol v2.</summary>
public sealed class V5ClaimProtocolV2Tests
{
    [Fact]
    public void Minimal_unary_resolved_claim_is_decodable()
    {
        var response = Parse(ValidUnaryJson());
        Assert.Single(response.Claims);
        Assert.Equal("DESCRIBES", response.Claims[0].Predicate);
    }

    [Fact]
    public void Relation_claim_requires_and_accepts_object_endpoint()
    {
        var response = Parse("""{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"WHOLE_ALIAS"}]},"predicate":"RELATES_TO","object":{"sourceParts":[{"sourceAlias":"A2","selectionMode":"WHOLE_ALIAS"}]},"state":"RESOLVED"}]}""");
        Assert.NotNull(response.Claims[0].Object);
    }

    [Fact]
    public void Open_claim_requires_evidence_need()
    {
        var response = Parse("""{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"WHOLE_ALIAS"}]},"predicate":"DESCRIBES","state":"OPEN","evidenceNeeds":["MORE_CONTEXT"]}]}""");
        Assert.Equal(ClaimResolutionState.OPEN, response.Claims[0].State);
    }

    [Fact]
    public void Empty_claims_are_valid()
    {
        Assert.Empty(Parse("{\"claims\":[]}").Claims);
    }

    [Fact]
    public void Missing_subject_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse("""{"claims":[{"predicate":"DESCRIBES","state":"RESOLVED"}]}"""));
    }

    [Fact]
    public void Missing_predicate_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse("""{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"WHOLE_ALIAS"}]},"state":"RESOLVED"}]}"""));
    }

    [Fact]
    public void Illegal_predicate_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(ValidUnaryJson("NOT_IN_CONTRACT")));
    }

    [Fact]
    public void Relation_without_object_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse("""{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"WHOLE_ALIAS"}]},"predicate":"RELATES_TO","state":"RESOLVED"}]}"""));
    }

    [Fact]
    public void Unknown_claim_field_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse("""{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"WHOLE_ALIAS"}]},"predicate":"DESCRIBES","state":"RESOLVED","claimId":"model-owned"}]}"""));
    }

    [Fact]
    public void Invented_coordinates_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse("""{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1","selectionMode":"WHOLE_ALIAS","start":0}]},"predicate":"DESCRIBES","state":"RESOLVED"}]}"""));
    }

    [Fact]
    public void Empty_source_parts_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse("""{"claims":[{"subject":{"sourceParts":[]},"predicate":"DESCRIBES","state":"RESOLVED"}]}"""));
    }

    [Fact]
    public void Unknown_source_alias_is_refused_by_harness_binder()
    {
        var response = Parse(ValidUnaryJson(alias: "UNKNOWN"));
        var result = ExactClaimBinderV2.Bind("request-1", response.Claims, Atoms());
        Assert.False(result.IsComplete);
        Assert.Contains("UNKNOWN", result.Refusals.Values.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Multipart_source_parts_preserve_order()
    {
        var proposal = new SemanticClaimProposalV2(
            Endpoint(Part("A1"), Part("A2")), "DESCRIBES", "joined", State: ClaimResolutionState.RESOLVED);
        var result = ExactClaimBinderV2.Bind("request-1", [proposal], Atoms());
        Assert.True(result.IsComplete);
        Assert.Equal(["A1", "A2"], result.Bound[0].Claim.Subject.Parts.Select(part => part.Alias));
    }

    [Fact]
    public void Harness_claim_identity_is_deterministic()
    {
        var first = ExactClaimBinderV2.Bind("request-1", [Proposal("A1")], Atoms());
        var second = ExactClaimBinderV2.Bind("request-1", [Proposal("A1")], Atoms());
        Assert.Equal(first.Bound[0].Claim.ClaimId, second.Bound[0].Claim.ClaimId);
        Assert.StartsWith(HarnessClaimIdentityV2.Prefix, first.Bound[0].Claim.ClaimId, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_physical_occurrences_get_distinct_harness_ids()
    {
        var atoms = Atoms().Append(new SemanticSourceAtom("A3", "S3", 3, 1, 3, 0, "Alpha")).ToArray();
        var first = ExactClaimBinderV2.Bind("request-1", [Proposal("A1")], atoms);
        var second = ExactClaimBinderV2.Bind("request-1", [Proposal("A3")], atoms);
        Assert.NotEqual(first.Bound[0].Claim.ClaimId, second.Bound[0].Claim.ClaimId);
    }

    [Fact]
    public void Refinement_existing_claim_id_is_preserved_as_reference()
    {
        var proposal = Proposal("A1") with { ExistingClaimId = "v5claim2-existing" };
        var result = ExactClaimBinderV2.Bind("request-1", [proposal], Atoms());
        Assert.Equal("v5claim2-existing", result.Bound[0].ExistingClaimId);
        Assert.NotEqual("v5claim2-existing", result.Bound[0].Claim.ClaimId);
    }

    [Fact]
    public void Strict_schema_response_format_serializes_recursive_schema()
    {
        var json = V5ProviderRequestShapeV2.SerializeResponseFormat(StructuredOutputMode.JsonSchemaStrict);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("json_schema", document.RootElement.GetProperty("type").GetString());
        Assert.True(document.RootElement.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.True(document.RootElement.GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("claims").GetProperty("items").GetProperty("properties").GetProperty("subject").GetProperty("properties").GetProperty("sourceParts").GetProperty("minItems").GetInt32() > 0);
    }

    [Fact]
    public void Json_object_fallback_request_embeds_complete_schema()
    {
        var composed = V5SemanticRequestComposerV2.Compose(Contract(), Packet());
        using var document = JsonDocument.Parse(composed.Prompt);
        Assert.Equal(V5Protocol.ClaimSchemaVersionV2, document.RootElement.GetProperty("protocolVersion").GetString());
        Assert.Equal("sourceParts", document.RootElement.GetProperty("responseSchema").GetProperty("properties").GetProperty("claims").GetProperty("items").GetProperty("properties").GetProperty("subject").GetProperty("properties").EnumerateObject().First().Name);
        Assert.DoesNotContain("gold", composed.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V2_request_hash_is_deterministic()
    {
        var first = V5SemanticRequestComposerV2.Compose(Contract(), Packet());
        var second = V5SemanticRequestComposerV2.Compose(Contract(), Packet());
        Assert.Equal(first.RequestHash, second.RequestHash);
        Assert.Equal(SemanticClaimContractV2.SchemaHash(), first.SchemaHash);
    }

    [Fact]
    public void V2_codec_and_schema_share_the_same_claim_fields()
    {
        var schema = JsonSerializer.Serialize(SemanticClaimContractV2.Schema());
        Assert.Contains("existingClaimId", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("claimId", string.Join(',', SemanticClaimContractV2.ClaimFields), StringComparison.Ordinal);
    }

    [Fact]
    public void Capability_selection_is_provider_free_and_json_object_fallback_is_explicit()
    {
        var capability = ProviderStructuredOutputRegistry.QwenFlashAlibaba;
        Assert.Throws<InvalidOperationException>(() => capability.Select(requireStrict: true));
        Assert.Equal(StructuredOutputMode.JsonObject, capability.Select(requireStrict: false));
        Assert.Contains("unverified", capability.EvidenceSource, StringComparison.Ordinal);
    }

    [Fact]
    public void V2_protocol_has_no_gold_or_provider_dependency()
    {
        var composed = V5SemanticRequestComposerV2.Compose(Contract(), Packet());
        Assert.Equal("v5-semantic-request-composer-2", composed.ComposerVersion);
        Assert.DoesNotContain("providerCall", composed.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("goldRead", composed.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    private static SemanticClaimResponseV2 Parse(string json) =>
        SemanticClaimResponseCodecV2.Parse(JsonDocument.Parse(json).RootElement, Contract());

    private static string ValidUnaryJson(string predicate = "DESCRIBES", string alias = "A1") =>
        $"{{\"claims\":[{{\"subject\":{{\"sourceParts\":[{{\"sourceAlias\":\"{alias}\",\"selectionMode\":\"WHOLE_ALIAS\"}}]}},\"predicate\":\"{predicate}\",\"value\":\"fact\",\"state\":\"RESOLVED\"}}]}}";

    private static SemanticClaimProposalV2 Proposal(string alias) =>
        new(Endpoint(Part(alias)), "DESCRIBES", "fact", State: ClaimResolutionState.RESOLVED);

    private static ClaimSourceEndpoint Endpoint(params SemanticSourcePart[] parts) => new(parts);

    private static SemanticSourcePart Part(string alias) =>
        new(alias, CanonicalSemanticSelectionMode.WholeAlias);

    private static IReadOnlyList<SemanticSourceAtom> Atoms() =>
        [new("A1", "S1", 1, 1, 1, 0, "Alpha"), new("A2", "S2", 2, 1, 1, 1, "Beta")];

    private static DocumentTaskContract Contract() => new(
        V5Protocol.TaskContractVersion,
        "v5-v2-test",
        "Source-backed test task.",
        [new SemanticPredicateDefinition("DESCRIBES", "A unary fact.")],
        [new SemanticRelationDefinition("RELATES_TO", "A relation.")],
        [new ProjectionRequest("claims", "Claims.")],
        new EvidencePolicy([EvidenceModality.TEXT], [EvidenceNeed.MORE_CONTEXT]),
        "retain-open",
        new ExecutionBudget(MaxSemanticModelCalls: 1));

    private static V5EvidencePacket Packet() => new([], [], [], [], [], []);
}
