using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7F1TreatmentDeltaAuditTests
{
    private sealed record Fixture(byte[] Control, byte[] Treatment, byte[] Store, byte[] Map, string PromptHash);
    private static Fixture Create()
    {
        PdfLine Line(double top, string text) => new(1, top, 12, text, 1, "", 0,
            20, 180, "Times-Bold", "", Bottom: top - 10, Top: top);
        var source = PdfSourceAdapter.BuildWithDetails([
            Line(10, "Tiêu đề <&> 😀"), Line(30, "Continuation"), Line(50, "Read-only body")], new string('a', 64));
        var pack = new SemanticEvidencePack("synthetic:PACK_001", 1,
            source.Snapshot.Evidence.Take(2).ToArray(), source.Snapshot.Evidence.ToArray());
        var request = P7PilotRequestPreflight.F1(source.Snapshot, source.Details, pack);
        var composer = new OpenRouterQwen37InferenceRequestComposer();
        var map = SpatialCanonical.Bytes(request.Owned.Select(issued => new {
            occurrence = issued.Id, alias = issued.Atom.Alias, ordinal = issued.Atom.Ordinal, page = issued.Atom.Page }).ToArray());
        return new(composer.Build(request.ControlSystemPrompt, request.ControlUserMessage, 32768),
            composer.Build(request.SystemPrompt, request.UserMessage, 32768), request.EvidenceStore.CanonicalBytes(),
            map, SpatialCanonical.Hash(Encoding.UTF8.GetBytes(request.SystemPrompt)));
    }
    private static F1TreatmentDeltaResult Validate(Fixture f) => P7F1TreatmentDeltaAudit.Validate(
        f.Control, f.Treatment, f.Store, f.Map, f.PromptHash);
    private static byte[] Edit(byte[] body, Action<JsonNode> edit)
    {
        var root = JsonNode.Parse(body)!; edit(root);
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }
    private static byte[] EditUser(byte[] body, Action<JsonNode> edit) => Edit(body, root => {
        var user = JsonNode.Parse(root["messages"]![1]!["content"]!.GetValue<string>())!;
        edit(user); root["messages"]![1]!["content"] = JsonSerializer.Serialize(user);
    });

    [Fact] public void Composite_B_retains_exact_control_semantics_universe_and_parser_evidence()
    {
        var f = Create(); var r = Validate(f);
        Assert.Equal(2, r.IssuedOccurrences);
        Assert.True(r.CarrierEqual); Assert.True(r.EmbeddedStageInputByteIdentical);
        Assert.True(r.IssuedUniverseEqual); Assert.True(r.ParserEvidenceEqual); Assert.True(r.SemanticDefinitionsEqual);
        Assert.Equal("UNVERIFIABLE_ASSERTION_NOT_SEMANTIC_AUTHORITY", r.Interpretation);
        using var c = JsonDocument.Parse(f.Control); using var b = JsonDocument.Parse(f.Treatment);
        Assert.Equal(32768, c.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(32768, b.RootElement.GetProperty("max_tokens").GetInt32());
        // Establish a no-op user rewrite control so negative tests cannot pass due to serializer drift.
        Assert.Equal(r, Validate(f with { Treatment = EditUser(f.Treatment, _ => { }) }));
    }

    [Theory]
    [InlineData("model")] [InlineData("temperature")] [InlineData("max_tokens")]
    [InlineData("reasoning")] [InlineData("response_format")] [InlineData("provider")]
    [InlineData("stream")] [InlineData("usage")]
    public void Unregistered_provider_carrier_changes_are_rejected(string field)
    {
        var f = Create(); f = f with { Treatment = Edit(f.Treatment, root => root[field] = "changed") };
        var error = Assert.Throws<InvalidOperationException>(() => Validate(f));
        Assert.Equal("P7_F1_DELTA_REJECTED:carrier-drift:" + field, error.Message);
    }
    [Fact] public void Added_carrier_field_is_not_silently_ignored()
    {
        var f = Create(); f = f with { Treatment = Edit(f.Treatment, root => root["tools"] = new JsonArray()) };
        Assert.Equal("P7_F1_DELTA_REJECTED:unregistered-fields", Assert.Throws<InvalidOperationException>(() => Validate(f)).Message);
    }
    [Fact] public void Frozen_prompt_hash_prevents_unregistered_semantic_or_instruction_changes()
    {
        var f = Create(); f = f with { Treatment = Edit(f.Treatment, root => root["messages"]![0]!["content"] = "new instructions") };
        Assert.Equal("P7_F1_DELTA_REJECTED:unregistered-B-prompt", Assert.Throws<InvalidOperationException>(() => Validate(f)).Message);
    }
    [Theory] [InlineData("TEXT")] [InlineData("CONTEXT")]
    public void Embedded_control_source_and_read_only_context_cannot_drift(string kind)
    {
        var f = Create(); f = f with { Treatment = EditUser(f.Treatment, user => {
            if (kind == "TEXT") user["stageInput"]!["occurrences"]![0]!["text"] = "changed";
            else user["stageInput"]!["contextOnlyEvidence"] = new JsonArray();
        }) };
        Assert.Equal("P7_F1_DELTA_REJECTED:stage-input-byte-drift", Assert.Throws<InvalidOperationException>(() => Validate(f)).Message);
    }
    [Fact] public void Semantically_equal_stage_input_with_different_bytes_is_rejected()
    {
        var f = Create(); f = f with { Treatment = Edit(f.Treatment, root => {
            var user = root["messages"]![1]!["content"]!.GetValue<string>();
            root["messages"]![1]!["content"] = user.Replace("\"stageInput\":{", "\"stageInput\":{ ", StringComparison.Ordinal);
        }) };
        Assert.Equal("P7_F1_DELTA_REJECTED:stage-input-byte-drift", Assert.Throws<InvalidOperationException>(() => Validate(f)).Message);
    }
    [Fact] public void Even_a_rehashed_prompt_must_keep_the_shared_F1_semantic_definitions()
    {
        var f = Create(); var body = Edit(f.Treatment, root => {
            var prompt = root["messages"]![0]!["content"]!.GetValue<string>();
            root["messages"]![0]!["content"] = prompt.Replace("OTHER means", "OTHER no longer means", StringComparison.Ordinal);
        });
        using var json = JsonDocument.Parse(body);
        var hash = SpatialCanonical.Hash(Encoding.UTF8.GetBytes(json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!));
        f = f with { Treatment = body, PromptHash = hash };
        Assert.Equal("P7_F1_DELTA_REJECTED:semantic-definition-drift", Assert.Throws<InvalidOperationException>(() => Validate(f)).Message);
    }
    [Theory] [InlineData("sourceSha256")] [InlineData("sourceAliasUniverseSha256")] [InlineData("evidenceStoreSha256")]
    public void Cross_document_or_snapshot_binding_is_rejected(string field)
    {
        var f = Create(); f = f with { Treatment = EditUser(f.Treatment, user => user[field] = new string('b', 64)) };
        Assert.Equal("P7_F1_DELTA_REJECTED:store-identity-drift", Assert.Throws<InvalidOperationException>(() => Validate(f)).Message);
    }
    [Theory] [InlineData("REORDER")] [InlineData("FOREIGN")] [InlineData("DUPLICATE")]
    public void Decision_subjects_cannot_reorder_expand_or_duplicate(string kind)
    {
        var f = Create(); f = f with { Treatment = EditUser(f.Treatment, user =>
            user["decisionSubjects"] = kind switch { "REORDER" => new JsonArray("O2", "O1"),
                "FOREIGN" => new JsonArray("O1", "O99"), _ => new JsonArray("O1", "O1") }) };
        Assert.Throws<InvalidOperationException>(() => Validate(f));
    }
    [Theory] [InlineData("SELECTABLE")] [InlineData("ALIAS")] [InlineData("FACT")]
    [InlineData("SEMANTIC_PREDICATE")]
    public void Source_projection_must_remain_exact_parser_facts(string kind)
    {
        var f = Create(); f = f with { Treatment = EditUser(f.Treatment, user => {
            var row = user["sourceEvidence"]![0]!;
            switch (kind)
            {
                case "SELECTABLE": row["selectable"] = false; break;
                case "ALIAS": row["source"]!["sourceAlias"] = "foreign"; break;
                case "FACT": row["source"]!["fields"]![0]!["value"] = "forged"; break;
                default: row["isHeading"] = true; break;
            }
        }) };
        Assert.Throws<InvalidOperationException>(() => Validate(f));
    }
    [Theory] [InlineData("page")] [InlineData("ordinal")] [InlineData("alias")]
    public void Private_issue_mapping_cannot_rebind_source_identity(string field)
    {
        var f = Create(); f = f with { Map = Edit(f.Map, map => map[0]![field] = field == "alias" ? JsonValue.Create("foreign") : JsonValue.Create(999)) };
        Assert.Throws<InvalidOperationException>(() => Validate(f));
    }
    [Theory] [InlineData("protocolVersion")] [InlineData("stage")] [InlineData("interpretationCharacterCap")]
    [InlineData("gold")]
    public void Treatment_contract_and_extra_Gold_fields_fail_closed(string field)
    {
        var f = Create(); f = f with { Treatment = EditUser(f.Treatment, user =>
            user[field] = field == "interpretationCharacterCap" ? JsonValue.Create(999) : JsonValue.Create("unregistered")) };
        Assert.Throws<InvalidOperationException>(() => Validate(f));
    }
    [Fact] public void Duplicate_JSON_properties_are_rejected_before_comparison()
    {
        var f = Create(); var body = Encoding.UTF8.GetString(f.Treatment);
        f = f with { Treatment = Encoding.UTF8.GetBytes("{\"messages\":[]," + body[1..]) };
        Assert.Equal("P7_F1_DELTA_REJECTED:duplicate-field", Assert.Throws<InvalidOperationException>(() => Validate(f)).Message);
    }
    [Fact] public void Actual_frozen_audit_receipt_does_not_certify_semantics_or_unlock_provider()
    {
        var bytes = File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.frozen-f1-treatment-delta-audit.v1.json"));
        Assert.Equal("39d12e524364a47e5cfee641e3564bdefa1cac587a98c9075ed72886cc3978f2", SpatialCanonical.Hash(bytes));
        using var json = JsonDocument.Parse(bytes); var r = json.RootElement;
        Assert.Equal(7, r.GetProperty("pairCount").GetInt32()); Assert.Equal(14, r.GetProperty("requestCount").GetInt32());
        Assert.Equal(643, r.GetProperty("issuedOccurrences").GetInt32());
        Assert.Equal(49152, r.GetProperty("controlResponseByteCap").GetInt32());
        Assert.Equal(262144, r.GetProperty("treatmentResponseByteCap").GetInt32());
        Assert.False(r.GetProperty("semanticTruthOrModelAccuracyCertified").GetBoolean());
        Assert.False(r.GetProperty("referenceOnlyEffectClaim").GetBoolean()); Assert.False(r.GetProperty("geometryOnlyEffectClaim").GetBoolean());
        Assert.Equal(0, r.GetProperty("providerCalls").GetInt32());
        Assert.False(r.GetProperty("originalRequestsChanged").GetBoolean()); Assert.False(r.GetProperty("goldChanged").GetBoolean());
        Assert.False(r.GetProperty("productionChanged").GetBoolean());
        Assert.Equal("LOCKED", r.GetProperty("providerExecution").GetString());
        Assert.Equal("LOCKED", r.GetProperty("productionPromotion").GetString());
        Assert.Equal("OPEN_DOWNSTREAM_REQUEST_FREEZE_PENDING", r.GetProperty("d23").GetString());
    }
}
