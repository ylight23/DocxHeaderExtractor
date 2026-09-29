using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free tests for the forced-function-call output carrier prototype: the same generic V5
/// claim schema, offered as a function's <c>parameters</c> instead of
/// <c>response_format.json_schema</c>, forced by <c>tool_choice</c> so the model has no channel
/// choice - never a second ontology, never a normal agent tool loop with a result turn. Nothing here
/// calls a provider.
/// </summary>
public sealed class V5ForcedToolClaimCarrierV1Tests
{
    private const string ToolName = "submit_semantic_claims";
    private const string ToolDescription = "Submit the complete source-backed semantic claim response for the current task.";

    // ---- schema carrier: same ontology, tool-parameters optionality ----------------------------

    [Fact]
    public void Claims_wrapper_and_predicate_branches_are_unchanged_by_carrier_choice()
    {
        var contract = Contract();
        var native = SerializeToElement(V5StrictClaimSchemaCompilerV1.Compile(contract, V5ClaimSchemaCarrier.NativeStrictJsonSchema));
        var tool = SerializeToElement(V5StrictClaimSchemaCompilerV1.Compile(contract, V5ClaimSchemaCarrier.ToolParameters));

        Assert.True(native.GetProperty("properties").TryGetProperty("claims", out _));
        Assert.True(tool.GetProperty("properties").TryGetProperty("claims", out _));

        string[] PredicateNames(JsonElement root) => root.GetProperty("properties").GetProperty("claims").GetProperty("items").GetProperty("oneOf")
            .EnumerateArray().Select(b => b.GetProperty("properties").GetProperty("predicate").GetProperty("enum")[0].GetString()!)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(PredicateNames(native), PredicateNames(tool));
        Assert.Equal(7, PredicateNames(tool).Length);
    }

    [Fact]
    public void Relation_branch_has_no_value_and_unary_branch_has_no_object_under_the_tool_carrier()
    {
        var root = SerializeToElement(V5StrictClaimSchemaCompilerV1.Compile(Contract(), V5ClaimSchemaCarrier.ToolParameters));
        var branches = root.GetProperty("properties").GetProperty("claims").GetProperty("items").GetProperty("oneOf").EnumerateArray().ToArray();

        var unary = branches.Single(b => b.GetProperty("properties").GetProperty("predicate").GetProperty("enum")[0].GetString() == "STRUCTURAL_REGION");
        Assert.True(unary.GetProperty("properties").TryGetProperty("value", out _));
        Assert.False(unary.GetProperty("properties").TryGetProperty("object", out _));

        var relation = branches.Single(b => b.GetProperty("properties").GetProperty("predicate").GetProperty("enum")[0].GetString() == "REFERENCES");
        Assert.True(relation.GetProperty("properties").TryGetProperty("object", out _));
        Assert.False(relation.GetProperty("properties").TryGetProperty("value", out _));
    }

    [Fact]
    public void AdditionalProperties_false_is_retained_at_every_object_level_under_the_tool_carrier()
    {
        var json = JsonSerializer.Serialize(V5StrictClaimSchemaCompilerV1.Compile(Contract(), V5ClaimSchemaCarrier.ToolParameters));
        var root = SerializeToElement(V5StrictClaimSchemaCompilerV1.Compile(Contract(), V5ClaimSchemaCarrier.ToolParameters));
        var objectSchemaCount = CountObjectSchemas(root);
        var additionalPropertiesFalseCount = System.Text.RegularExpressions.Regex.Matches(json, "\"additionalProperties\":false").Count;
        Assert.Equal(objectSchemaCount, additionalPropertiesFalseCount);
    }

    [Fact]
    public void Alias_only_semantics_remain_alias_only_under_the_tool_carrier()
    {
        // Under NativeStrictJsonSchema every part field is nullable-but-required; under ToolParameters
        // only sourceAlias is required at all, so {"sourceAlias":"X"} alone is a schema-valid part -
        // the native v2.1 wire's own shorthand, not a null-boilerplate rewrite of it.
        var root = SerializeToElement(V5StrictClaimSchemaCompilerV1.Compile(Contract(), V5ClaimSchemaCarrier.ToolParameters));
        var branch = root.GetProperty("properties").GetProperty("claims").GetProperty("items").GetProperty("oneOf").EnumerateArray().First();
        var partSchema = branch.GetProperty("properties").GetProperty("subject").GetProperty("properties")
            .GetProperty("sourceParts").GetProperty("items");
        var required = partSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray();
        Assert.Equal(["sourceAlias"], required);
        Assert.True(partSchema.GetProperty("properties").TryGetProperty("verbatimText", out _));
    }

    [Fact]
    public void Native_carrier_behavior_is_unchanged_value_stays_nullable_required()
    {
        var root = SerializeToElement(V5StrictClaimSchemaCompilerV1.Compile(Contract(), V5ClaimSchemaCarrier.NativeStrictJsonSchema));
        var unary = root.GetProperty("properties").GetProperty("claims").GetProperty("items").GetProperty("oneOf")
            .EnumerateArray().Single(b => b.GetProperty("properties").GetProperty("predicate").GetProperty("enum")[0].GetString() == "STRUCTURAL_REGION");
        var required = unary.GetProperty("required").EnumerateArray().Select(r => r.GetString()).Order(StringComparer.Ordinal).ToArray();
        Assert.Contains("value", required);
        var partSchema = unary.GetProperty("properties").GetProperty("subject").GetProperty("properties").GetProperty("sourceParts").GetProperty("items");
        var partRequired = partSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["leftExactContext", "occurrence", "rightExactContext", "sourceAlias", "verbatimText"], partRequired);
    }

    // ---- request body: forced tool, pinned routing, no response_format --------------------------

    [Fact]
    public void Exactly_one_tool_is_defined_with_a_deterministic_name_and_forced_choice()
    {
        var schema = V5StrictClaimSchemaCompilerV1.Compile(Contract(), V5ClaimSchemaCarrier.ToolParameters);
        var bodyA = V5ForcedToolProviderRequestBodyV1.Build("sys", "user", 1024, "qwen/qwen3.7-flash", "alibaba", ToolName, ToolDescription, schema, "none");
        var bodyB = V5ForcedToolProviderRequestBodyV1.Build("sys", "user", 1024, "qwen/qwen3.7-flash", "alibaba", ToolName, ToolDescription, schema, "none");
        Assert.Equal(bodyA.Hash, bodyB.Hash);
        Assert.True(bodyA.PayloadBytes.AsSpan().SequenceEqual(bodyB.PayloadBytes));

        var root = JsonDocument.Parse(bodyA.PayloadBytes).RootElement;
        var tools = root.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal(ToolName, tools[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(ToolName, root.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("function", root.GetProperty("tool_choice").GetProperty("type").GetString());
    }

    [Fact]
    public void Body_pins_alibaba_routing_qwen37_model_no_fallback_and_reasoning_none()
    {
        var schema = V5StrictClaimSchemaCompilerV1.Compile(Contract(), V5ClaimSchemaCarrier.ToolParameters);
        var body = V5ForcedToolProviderRequestBodyV1.Build("sys", "user", 1024, "qwen/qwen3.7-flash", "alibaba", ToolName, ToolDescription, schema, "none");
        var root = JsonDocument.Parse(body.PayloadBytes).RootElement;
        Assert.Equal("qwen/qwen3.7-flash", root.GetProperty("model").GetString());
        Assert.Equal("none", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("alibaba", root.GetProperty("provider").GetProperty("order")[0].GetString());
        Assert.False(root.GetProperty("provider").GetProperty("allow_fallbacks").GetBoolean());
        Assert.True(root.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
    }

    [Fact]
    public void Body_never_includes_native_response_format()
    {
        var schema = V5StrictClaimSchemaCompilerV1.Compile(Contract(), V5ClaimSchemaCarrier.ToolParameters);
        var body = V5ForcedToolProviderRequestBodyV1.Build("sys", "user", 1024, "qwen/qwen3.7-flash", "alibaba", ToolName, ToolDescription, schema, "none");
        Assert.DoesNotContain("response_format", System.Text.Encoding.UTF8.GetString(body.PayloadBytes), StringComparison.Ordinal);
    }

    // ---- streamed tool-call reassembly -----------------------------------------------------------

    [Fact]
    public void Streamed_argument_fragments_reconstruct_byte_identically_regardless_of_chunking()
    {
        var expected = """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"STRUCTURAL_REGION","value":"Alpha","state":"RESOLVED","evidenceNeeds":[]}]}""";
        var chunked = new[]
        {
            new V5ToolCallDeltaFragment(0, "call_1", "submit_semantic_claims", expected[..20]),
            new V5ToolCallDeltaFragment(0, null, null, expected[20..47]),
            new V5ToolCallDeltaFragment(0, null, null, expected[47..]),
        };
        var reassembled = V5ToolCallArgumentsReassembler.Reassemble(chunked);
        Assert.Equal(1, reassembled.Count);
        Assert.Equal("call_1", reassembled[0].Id);
        Assert.Equal("submit_semantic_claims", reassembled[0].FunctionName);
        Assert.Equal(expected, reassembled[0].Arguments);
    }

    [Fact]
    public void Multiple_tool_call_indices_reassemble_independently_and_stay_ordered()
    {
        var fragments = new[]
        {
            new V5ToolCallDeltaFragment(1, "call_b", "submit_semantic_claims", "{\"b\":"),
            new V5ToolCallDeltaFragment(0, "call_a", "submit_semantic_claims", "{\"a\":"),
            new V5ToolCallDeltaFragment(1, null, null, "2}"),
            new V5ToolCallDeltaFragment(0, null, null, "1}"),
        };
        var reassembled = V5ToolCallArgumentsReassembler.Reassemble(fragments);
        Assert.Equal(2, reassembled.Count);
        Assert.Equal(0, reassembled[0].Index);
        Assert.Equal("{\"a\":1}", reassembled[0].Arguments);
        Assert.Equal(1, reassembled[1].Index);
        Assert.Equal("{\"b\":2}", reassembled[1].Arguments);
    }

    // ---- layered qualification: each failure fails closed, on its own layer --------------------

    [Fact]
    public void Zero_tool_calls_fails_closed_as_NO_TOOL_CALL()
    {
        var result = V5ForcedToolQualifierV1.Qualify([], ToolName, "req-1", Contract(), Atoms(), Scope());
        Assert.Equal(V5ForcedToolOutcome.NO_TOOL_CALL, result.Outcome);
    }

    [Fact]
    public void Multiple_tool_calls_fails_closed_as_MULTIPLE_TOOL_CALLS()
    {
        var calls = new[]
        {
            new V5ReassembledToolCall(0, "c1", ToolName, "{}"),
            new V5ReassembledToolCall(1, "c2", ToolName, "{}"),
        };
        var result = V5ForcedToolQualifierV1.Qualify(calls, ToolName, "req-1", Contract(), Atoms(), Scope());
        Assert.Equal(V5ForcedToolOutcome.MULTIPLE_TOOL_CALLS, result.Outcome);
    }

    [Fact]
    public void Wrong_tool_name_fails_closed_as_WRONG_TOOL_NAME()
    {
        var calls = new[] { new V5ReassembledToolCall(0, "c1", "some_other_function", "{}") };
        var result = V5ForcedToolQualifierV1.Qualify(calls, ToolName, "req-1", Contract(), Atoms(), Scope());
        Assert.Equal(V5ForcedToolOutcome.WRONG_TOOL_NAME, result.Outcome);
    }

    [Fact]
    public void Malformed_json_arguments_fail_closed_as_ARGUMENTS_NOT_JSON()
    {
        var calls = new[] { new V5ReassembledToolCall(0, "c1", ToolName, "{not json") };
        var result = V5ForcedToolQualifierV1.Qualify(calls, ToolName, "req-1", Contract(), Atoms(), Scope());
        Assert.Equal(V5ForcedToolOutcome.ARGUMENTS_NOT_JSON, result.Outcome);
    }

    [Fact]
    public void Empty_arguments_fail_closed_as_ARGUMENTS_NOT_JSON()
    {
        var calls = new[] { new V5ReassembledToolCall(0, "c1", ToolName, "") };
        var result = V5ForcedToolQualifierV1.Qualify(calls, ToolName, "req-1", Contract(), Atoms(), Scope());
        Assert.Equal(V5ForcedToolOutcome.ARGUMENTS_NOT_JSON, result.Outcome);
    }

    [Fact]
    public void An_unknown_field_fails_closed_as_ARGUMENT_SCHEMA_INVALID_not_repaired()
    {
        var arguments = """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"STRUCTURAL_REGION","value":"Alpha","state":"RESOLVED","evidenceNeeds":[],"unexpectedField":true}]}""";
        var calls = new[] { new V5ReassembledToolCall(0, "c1", ToolName, arguments) };
        var result = V5ForcedToolQualifierV1.Qualify(calls, ToolName, "req-1", Contract(), Atoms(), Scope());
        Assert.Equal(V5ForcedToolOutcome.ARGUMENT_SCHEMA_INVALID, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public void A_relation_with_a_value_fails_closed_as_TASK_CONTRACT_INVALID_not_coerced()
    {
        var arguments = """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"REFERENCES","value":"should not be here","object":{"sourceParts":[{"sourceAlias":"A2"}]},"state":"RESOLVED","evidenceNeeds":[]}]}""";
        var calls = new[] { new V5ReassembledToolCall(0, "c1", ToolName, arguments) };
        var result = V5ForcedToolQualifierV1.Qualify(calls, ToolName, "req-1", Contract(), Atoms(), Scope());
        Assert.Equal(V5ForcedToolOutcome.TASK_CONTRACT_INVALID, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public void Well_formed_alias_only_arguments_feed_into_the_unmodified_codec_and_binder()
    {
        var arguments = """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"STRUCTURAL_REGION","value":"Alpha","state":"RESOLVED","evidenceNeeds":[]}]}""";
        var calls = new[] { new V5ReassembledToolCall(0, "c1", ToolName, arguments) };
        var result = V5ForcedToolQualifierV1.Qualify(calls, ToolName, "req-1", Contract(), Atoms(), Scope());
        Assert.Equal(V5ForcedToolOutcome.BOUND, result.Outcome);
        Assert.NotNull(result.Response);
        Assert.NotNull(result.Binding);
        Assert.True(result.Binding!.IsComplete);
        Assert.Equal("S1:0-5", result.Binding.Bound[0].Claim.Subject.Identity);
    }

    [Fact]
    public void Binding_refused_fails_closed_without_hiding_which_claim_it_was()
    {
        var arguments = """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A9"}]},"predicate":"STRUCTURAL_REGION","value":"Ghost","state":"RESOLVED","evidenceNeeds":[]}]}""";
        var calls = new[] { new V5ReassembledToolCall(0, "c1", ToolName, arguments) };
        var result = V5ForcedToolQualifierV1.Qualify(calls, ToolName, "req-1", Contract(), Atoms(), Scope());
        Assert.Equal(V5ForcedToolOutcome.BINDING_REFUSED, result.Outcome);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public void Tool_carrier_reproduces_identical_binder_output_to_direct_codec_plus_binder()
    {
        var arguments = """{"claims":[{"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"STRUCTURAL_REGION","value":"Alpha","state":"RESOLVED","evidenceNeeds":[]}]}""";
        var contract = Contract();
        var atoms = Atoms();
        var scope = Scope();

        var direct = SemanticClaimResponseCodecV2_1.Parse(JsonDocument.Parse(arguments).RootElement, contract);
        var directBinding = ExactClaimBinderV2_1.Bind("req-1", direct.Claims, atoms, scope);

        var viaCarrier = V5ForcedToolQualifierV1.Qualify(
            [new V5ReassembledToolCall(0, "c1", ToolName, arguments)], ToolName, "req-1", contract, atoms, scope);

        Assert.Equal(directBinding.IsComplete, viaCarrier.Binding!.IsComplete);
        Assert.Equal(directBinding.Bound[0].Claim.Subject.Identity, viaCarrier.Binding.Bound[0].Claim.Subject.Identity);
        Assert.Equal(directBinding.Bound[0].Claim.ClaimId, viaCarrier.Binding.Bound[0].Claim.ClaimId);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static int CountObjectSchemas(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return 0;
        var count = schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object" ? 1 : 0;
        if (schema.TryGetProperty("properties", out var properties))
            count += properties.EnumerateObject().Sum(p => CountObjectSchemas(p.Value));
        if (schema.TryGetProperty("items", out var items)) count += CountObjectSchemas(items);
        if (schema.TryGetProperty("oneOf", out var oneOf)) count += oneOf.EnumerateArray().Sum(CountObjectSchemas);
        return count;
    }

    private static JsonElement SerializeToElement(object value) => JsonSerializer.SerializeToElement(value);

    private static DocumentTaskContract Contract() =>
        DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();

    private static IReadOnlyList<SemanticSourceAtom> Atoms() =>
        [new("A1", "S1", 1, 1, 1, 0, "Alpha"), new("A2", "S2", 2, 1, 1, 1, "Beta")];

    private static ClaimBindingScope Scope() => ClaimBindingScope.Create(["A1", "A2"], ["A1", "A2"]);
}
