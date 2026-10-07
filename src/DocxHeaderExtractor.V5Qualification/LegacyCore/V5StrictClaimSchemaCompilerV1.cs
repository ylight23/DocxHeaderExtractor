using DocxHeaderExtractor.Infrastructure.AI.QualifiedInference;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// Compiles a strict, provider-facing JSON Schema for the v2.1 claim wire from a
/// <see cref="DocumentTaskContract"/> alone - generic Core, no document-specific vocabulary. Built for
/// the qwen3.8-27b:free model-capability canary (measuring whether native
/// <c>response_format.type=json_schema, strict=true</c> holds V5's generic <c>claims[]</c> ontology
/// while structurally preventing more contract violations than the loose <c>json_object</c> schema
/// <see cref="SemanticClaimContractV2_1.Schema"/> can), never used by production's own composer/schema.
/// <para>
/// One <c>oneOf</c> branch per <see cref="V5ClaimShapeV2_1"/> - each branch's <c>predicate</c> is
/// pinned to a single-value <c>enum</c> (not <c>const</c>: enum-of-one is the more broadly supported
/// keyword across strict-mode implementations), so a UNARY predicate's branch has no <c>object</c>
/// property at all and a RELATION predicate's branch has no <c>value</c> property at all -
/// <c>additionalProperties: false</c> then makes RELATION+value and UNARY+object structurally
/// unrepresentable rather than merely discouraged. <c>existingClaimId</c> is intentionally omitted:
/// this canary's 3 packs seed no <c>openOrConflictedClaims</c>, so refinement is out of scope here,
/// not a general production limitation.
/// </para>
/// <para>
/// Deliberately restricted to the keyword subset most strict-mode implementations agree on - type,
/// properties, required, additionalProperties, enum, items, oneOf - and avoids <c>const</c>,
/// <c>minLength</c>/<c>minItems</c>/<c>minimum</c>, <c>pattern</c>, <c>format</c> and <c>$ref</c>/<c>$defs</c>,
/// none of which TaskContract/binder authority depends on: the binder remains the exact-coordinate
/// authority regardless of what the schema layer manages to reject early.
/// </para>
/// </summary>
/// <summary>
/// Where a compiled schema is destined. Same predicate vocabulary, arity and authority either way -
/// only how a genuinely-optional field is represented differs, because the two destinations enforce
/// that differently. Native strict JSON Schema (OpenAI/OpenRouter <c>response_format.json_schema</c>)
/// requires every declared property to appear in <c>required</c>, so an optional field is represented
/// as nullable-but-required. An ordinary (non-<c>strict</c>) function-calling <c>parameters</c> schema
/// carries no such rule, so the same field is simply omitted from <c>required</c> and left absent when
/// not used - preserving the native v2.1 wire's own optionality (<c>{"sourceAlias":"L0016:S0"}</c> with
/// nothing else) rather than forcing null boilerplate a model never had to emit before.
/// </summary>
public enum V5ClaimSchemaCarrier
{
    NativeStrictJsonSchema,
    ToolParameters,
}

public static class V5StrictClaimSchemaCompilerV1
{
    public const string Version = "v5-strict-claim-schema-compiler-1";

    /// <param name="ownedAliasEnum">When given, every claim subject's sourceAlias is constrained to this
    /// set (a pack's owned aliases). Null leaves it an unconstrained string - dynamic alias enums are a
    /// per-pack, per-request refinement, not a property of the contract alone.</param>
    /// <param name="visibleAliasEnum">Same, for every claim object's sourceAlias (a pack's visible aliases).</param>
    public static object Compile(DocumentTaskContract contract, IReadOnlyList<string>? ownedAliasEnum = null, IReadOnlyList<string>? visibleAliasEnum = null) =>
        Compile(contract, V5ClaimSchemaCarrier.NativeStrictJsonSchema, ownedAliasEnum, visibleAliasEnum);

    /// <param name="carrier">See <see cref="V5ClaimSchemaCarrier"/>. Only optionality representation changes.</param>
    public static object Compile(
        DocumentTaskContract contract, V5ClaimSchemaCarrier carrier,
        IReadOnlyList<string>? ownedAliasEnum = null, IReadOnlyList<string>? visibleAliasEnum = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        contract.Validate();
        var branches = V5ClaimShapesV2_1.Generate(contract)
            .Select(shape => ClaimBranch(shape, carrier, ownedAliasEnum, visibleAliasEnum))
            .ToArray();
        return new
        {
            type = "object",
            additionalProperties = false,
            properties = new { claims = new { type = "array", items = new { oneOf = branches } } },
            required = new[] { "claims" },
        };
    }

    private static object ClaimBranch(
        V5ClaimShapeV2_1 shape, V5ClaimSchemaCarrier carrier, IReadOnlyList<string>? ownedAliasEnum, IReadOnlyList<string>? visibleAliasEnum)
    {
        var strict = carrier == V5ClaimSchemaCarrier.NativeStrictJsonSchema;
        var properties = new Dictionary<string, object>
        {
            ["subject"] = EndpointSchema(carrier, ownedAliasEnum),
            ["predicate"] = new { type = "string", @enum = new[] { shape.Name } },
            ["state"] = new { type = "string", @enum = ProviderFacingStates() },
            ["evidenceNeeds"] = new { type = "array", items = new { type = "string", @enum = Enum.GetNames<EvidenceNeed>() } },
        };
        // Under native strict mode every declared property must be required; a field that is
        // genuinely optional on the wire (an OPEN unary claim may have no value yet) is represented by
        // allowing null rather than by omitting it. An ordinary tool-parameters schema has no such
        // rule, so the field is simply left out of "required" and omitted from arguments when unused.
        var optionalKeys = new List<string>();
        if (shape.ValueAllowed)
        {
            properties["value"] = strict ? new { type = new[] { "string", "null" } } : new { type = "string" };
            if (!strict) optionalKeys.Add("value");
        }
        if (shape.ObjectAllowed) properties["object"] = EndpointSchema(carrier, visibleAliasEnum);

        return new
        {
            type = "object",
            additionalProperties = false,
            properties,
            required = properties.Keys.Except(optionalKeys).ToArray(),
        };
    }

    private static string[] ProviderFacingStates() =>
        Enum.GetNames<ClaimResolutionState>().Where(name => name != nameof(ClaimResolutionState.EXHAUSTED)).ToArray();

    private static object EndpointSchema(V5ClaimSchemaCarrier carrier, IReadOnlyList<string>? aliasEnum)
    {
        var strict = carrier == V5ClaimSchemaCarrier.NativeStrictJsonSchema;
        object aliasProperty = aliasEnum is { Count: > 0 }
            ? new { type = "string", @enum = aliasEnum }
            : new { type = "string" };
        // Only sourceAlias is mandatory on a real part: a whole-alias selection - the policy's default -
        // has none of the other four fields at all. Under native strict mode each is nullable-but-
        // required (strict mode's "every declared property is required" rule leaves no other way to
        // say "absent"); for a tool-parameters schema they are ordinary optional properties, so the
        // native wire's own shorthand ({"sourceAlias":"..."} alone) is representable without change.
        var partProperties = new Dictionary<string, object>
        {
            ["sourceAlias"] = aliasProperty,
            ["verbatimText"] = strict ? new { type = new[] { "string", "null" } } : new { type = "string" },
            ["occurrence"] = strict ? new { type = new[] { "integer", "null" } } : new { type = "integer" },
            ["leftExactContext"] = strict ? new { type = new[] { "string", "null" } } : new { type = "string" },
            ["rightExactContext"] = strict ? new { type = new[] { "string", "null" } } : new { type = "string" },
        };
        var optionalPartKeys = strict ? Array.Empty<string>() : new[] { "verbatimText", "occurrence", "leftExactContext", "rightExactContext" };
        return new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                sourceParts = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = partProperties,
                        required = partProperties.Keys.Except(optionalPartKeys).ToArray(),
                    },
                },
            },
            required = new[] { "sourceParts" },
        };
    }
}

/// <summary>
/// The complete, deterministic OpenRouter request body for a strict-schema (<c>response_format.type =
/// json_schema, strict = true</c>) call - the same body-freezing discipline
/// <see cref="V5ProviderRequestBodyV2_1"/> already applies to the production <c>json_object</c> wire,
/// kept as a separate type so this experimental variant can never be confused with, or accidentally
/// change, the production body shape. Provider routing is pinned exactly like the production body:
/// <c>order: [routingSlug]</c>, <c>allow_fallbacks: false</c>, <c>require_parameters: true</c> - so a
/// provider that cannot honor every named parameter is refused by routing rather than silently
/// substituted.
/// </summary>
public sealed record V5StrictSchemaProviderRequestBodyV1(byte[] PayloadBytes, string Hash, int Bytes)
{
    public static V5StrictSchemaProviderRequestBodyV1 Build(
        string systemPrompt, string userMessage, int maxCompletionTokens,
        string model, string providerRoutingSlug, object compiledSchema, string schemaName, string reasoningEffort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRoutingSlug);
        ArgumentNullException.ThrowIfNull(compiledSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        if (maxCompletionTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxCompletionTokens));

        var body = new
        {
            model,
            temperature = 0,
            max_tokens = maxCompletionTokens,
            reasoning = new { effort = reasoningEffort },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new { name = schemaName, strict = true, schema = compiledSchema },
            },
            provider = new
            {
                order = new[] { providerRoutingSlug },
                allow_fallbacks = false,
                require_parameters = true,
                data_collection = "deny",
                zdr = false,
            },
            stream = true,
            usage = new { include = true },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, CanonicalJson.Options);
        return new V5StrictSchemaProviderRequestBodyV1(bytes, Hashing.Sha256(Encoding.UTF8.GetString(bytes)), bytes.Length);
    }
}
