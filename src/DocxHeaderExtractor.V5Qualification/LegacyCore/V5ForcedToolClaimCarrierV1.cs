using DocxHeaderExtractor.Infrastructure.AI.QualifiedInference;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// Three-valued capability state for a claim this repository has not yet verified against a real
/// provider response. Never collapsed to a plain bool: "the metadata doesn't rule it out" and "a real
/// call proved it" are different claims, and this type keeps them from being confused with each other.
/// </summary>
public enum V5CapabilityStatus
{
    Supported,
    Unsupported,
    Unverified,
}

/// <summary>
/// Tool/function-calling capabilities for one provider/model pairing - independent of, and never a
/// substitute for, <see cref="ProviderStructuredOutputCapabilities"/>. A model can support forced
/// function calling with an argument schema while still not supporting native
/// <c>response_format.json_schema</c> strict mode, and the two must be tracked separately rather than
/// one being inferred from the other.
/// </summary>
public sealed record V5ToolCallingCapabilities(
    string Provider,
    string Model,
    bool ToolsSupported,
    bool SpecificToolChoiceSupported,
    bool ToolArgumentSchemaSupported,
    V5CapabilityStatus FunctionStrictFlagSupported,
    V5CapabilityStatus ToolArgumentStrictEnforcement,
    string EvidenceSource);

/// <summary>Provider-free capability record. Never probes a provider or runs inference.</summary>
public static class V5ToolCallingCapabilityRegistry
{
    /// <summary>
    /// From OpenRouter's public <c>/models/qwen/qwen3.7-flash/endpoints</c> metadata (its one and only
    /// endpoint, Alibaba, lists <c>tools</c> and <c>tool_choice</c> in <c>supported_parameters</c>, but
    /// not <c>structured_outputs</c> - matching <see cref="ProviderStructuredOutputRegistry.QwenFlashAlibaba"/>'s
    /// existing <c>JsonSchemaStrictSupported: false</c>, now independently confirmed against live
    /// metadata) and OpenRouter's own tool-calling documentation, which does not document a
    /// <c>function.strict</c> field at all (unlike <c>response_format.json_schema.strict</c>, which it
    /// does document) and explicitly states that tool-call arguments are model-generated JSON the
    /// calling application must still validate. Neither source says whether Alibaba's decoder actually
    /// enforces every keyword of an advertised argument schema - that is answerable only by a real
    /// provider measurement.
    /// </summary>
    public static V5ToolCallingCapabilities QwenFlashAlibaba { get; } = new(
        "alibaba", "qwen/qwen3.7-flash",
        ToolsSupported: true,
        SpecificToolChoiceSupported: true,
        ToolArgumentSchemaSupported: true,
        FunctionStrictFlagSupported: V5CapabilityStatus.Unverified,
        ToolArgumentStrictEnforcement: V5CapabilityStatus.Unverified,
        EvidenceSource: "OpenRouter /models/qwen/qwen3.7-flash/endpoints (tools, tool_choice present; structured_outputs absent) " +
            "+ OpenRouter tool-calling docs (no function.strict field documented; arguments require application-side validation)");
}

/// <summary>
/// The complete, deterministic OpenRouter request body for the forced-function-call output carrier -
/// a transport for V5's existing generic <c>claims[]</c> response, never a second ontology and never a
/// normal agent tool loop. Exactly one function is offered and <c>tool_choice</c> pins it by name, so
/// the model has no channel choice to make; there is no second turn, no tool execution, and no result
/// sent back - the function's arguments are the candidate semantic response, fed to the unmodified
/// v2.1 codec/binder exactly as a json_object response body would be.
/// <para>
/// A separate type from <see cref="V5ProviderRequestBodyV2_1"/> (production's json_object path) and
/// <see cref="V5StrictSchemaProviderRequestBodyV1"/> (the closed qwen3.8 native-strict experiment) so
/// none of the three can be confused with, or accidentally changed by, either of the others. No
/// <c>response_format</c> field is sent at all - Qwen3.7/Alibaba does not support
/// <c>response_format.json_schema</c>, and mixing <c>json_object</c> with forced tool calling would
/// muddy which surface produced which behavior.
/// </para>
/// </summary>
public sealed record V5ForcedToolProviderRequestBodyV1(byte[] PayloadBytes, string Hash, int Bytes)
{
    public static V5ForcedToolProviderRequestBodyV1 Build(
        string systemPrompt, string userMessage, int maxCompletionTokens,
        string model, string providerRoutingSlug, string toolName, string toolDescription, object toolParametersSchema,
        string reasoningEffort) =>
        BuildWithToolChoice(
            systemPrompt, userMessage, maxCompletionTokens, model, providerRoutingSlug, toolName, toolDescription,
            toolParametersSchema, reasoningEffort, new { type = "function", function = new { name = toolName } });

    /// <summary>
    /// Variant A2: identical request in every other respect to <see cref="Build"/> - same schema, same
    /// one declared tool, same routing/reasoning/no-fallback - except <c>tool_choice</c> is the plain
    /// string <c>"required"</c> (OpenRouter's generic "call at least one tool" mode) instead of the
    /// named-function-forcing object shape Qwen3.7/Alibaba's routing rejected with 404 "Filter by Tool
    /// Compatibility". With exactly one tool declared, "required" reaches nearly the same outcome
    /// (the model has no tool but this one to call) through a shape that mode's own support is
    /// independent of the named-choice mechanism just proven unsupported - genuinely unverified until
    /// measured, never assumed.
    /// </summary>
    public static V5ForcedToolProviderRequestBodyV1 BuildWithRequiredToolChoice(
        string systemPrompt, string userMessage, int maxCompletionTokens,
        string model, string providerRoutingSlug, string toolName, string toolDescription, object toolParametersSchema,
        string reasoningEffort) =>
        BuildWithToolChoice(
            systemPrompt, userMessage, maxCompletionTokens, model, providerRoutingSlug, toolName, toolDescription,
            toolParametersSchema, reasoningEffort, "required");

    private static V5ForcedToolProviderRequestBodyV1 BuildWithToolChoice(
        string systemPrompt, string userMessage, int maxCompletionTokens,
        string model, string providerRoutingSlug, string toolName, string toolDescription, object toolParametersSchema,
        string reasoningEffort, object toolChoice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRoutingSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolDescription);
        ArgumentNullException.ThrowIfNull(toolParametersSchema);
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
            tools = new object[]
            {
                new
                {
                    type = "function",
                    function = new { name = toolName, description = toolDescription, parameters = toolParametersSchema },
                },
            },
            tool_choice = toolChoice,
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
        return new V5ForcedToolProviderRequestBodyV1(bytes, Hashing.Sha256(Encoding.UTF8.GetString(bytes)), bytes.Length);
    }
}
