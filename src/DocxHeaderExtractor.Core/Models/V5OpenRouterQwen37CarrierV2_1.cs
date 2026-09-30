using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// Where a capability claim sits, so "the gateway's metadata doesn't rule this out" and "a real call
/// through this exact route proved it" can never collapse into one bool. <see cref="ADVERTISED"/>
/// means only that a parameter NAME appears in the gateway's own supported-parameters listing for this
/// route - never that a specific VALUE for it is honored. Only <see cref="EMPIRICALLY_SUPPORTED"/> and
/// <see cref="EMPIRICALLY_UNSUPPORTED"/> come from an actual provider call.
/// </summary>
public enum V5RouteCapabilityStatus
{
    NOT_ADVERTISED,
    ADVERTISED,
    EMPIRICALLY_SUPPORTED,
    EMPIRICALLY_UNSUPPORTED,
    UNTESTED,
}

/// <summary>
/// One exact route: a gateway, the model slug sent on that gateway, the provider the gateway pins
/// inference to, and the API surface. Capability evidence is keyed by this whole tuple, never by
/// model name alone - a fact about openrouter-&gt;alibaba says nothing about an alibaba-direct route
/// or a different gateway, even for the identical model slug.
/// </summary>
public sealed record V5RouteIdentity(string Gateway, string Model, string Provider, string Api)
{
    public static V5RouteIdentity OpenRouterQwen37ChatCompletions { get; } =
        new("openrouter", "qwen/qwen3.7-flash", "alibaba", "chat-completions");
}

/// <summary>
/// Per-parameter capability state for one <see cref="V5RouteIdentity"/>. Provider-free: this record
/// never probes a provider itself, it only carries the result of provider calls this repository has
/// already made (or explicitly has not).
/// </summary>
public sealed record V5RouteCapabilities(
    V5RouteIdentity Route,
    V5RouteCapabilityStatus JsonObject,
    V5RouteCapabilityStatus JsonSchemaStrict,
    V5RouteCapabilityStatus Tools,
    V5RouteCapabilityStatus ToolChoiceAuto,
    V5RouteCapabilityStatus ToolChoiceNamed,
    V5RouteCapabilityStatus ToolChoiceRequired,
    string EvidenceSource);

/// <summary>
/// The current, real evidence for <see cref="V5RouteIdentity.OpenRouterQwen37ChatCompletions"/> -
/// nothing here is inferred or assumed beyond what a real call or a real metadata fetch already
/// established elsewhere in this repository's history.
/// </summary>
public static class V5OpenRouterQwen37RouteCapabilityRegistry
{
    public static V5RouteCapabilities Current { get; } = new(
        V5RouteIdentity.OpenRouterQwen37ChatCompletions,
        // The real 31-pack production cohort and the source-selection remediation canary both
        // transported successfully over exactly this route with response_format=json_object.
        JsonObject: V5RouteCapabilityStatus.EMPIRICALLY_SUPPORTED,
        // OpenRouter's endpoint metadata lists response_format as a supported parameter NAME, but
        // documents this model as not enforcing strict JSON-Schema mode; matches the existing
        // ProviderStructuredOutputRegistry.QwenFlashAlibaba (JsonSchemaStrictSupported: false). No
        // real call through this route has ever tried native strict mode.
        JsonSchemaStrict: V5RouteCapabilityStatus.ADVERTISED,
        // "tools" and "tool_choice" both appear in this endpoint's supported_parameters metadata.
        Tools: V5RouteCapabilityStatus.ADVERTISED,
        // No call through this route has ever sent tool_choice:"auto".
        ToolChoiceAuto: V5RouteCapabilityStatus.UNTESTED,
        // Two real canary calls (commit 71cc694: named-function tool_choice; commit 6a70d2f/fcdab6d:
        // tool_choice="required") each received an OpenRouter ROUTING-layer HTTP 404 ("No endpoints
        // found that support the provided 'tool_choice' value", failedRoutingStep "Filter by Tool
        // Compatibility") before the request ever reached Alibaba's model. A routing/carrier fact
        // about an advertised parameter NAME vs an honored VALUE - not a claim that Qwen3.7 itself
        // cannot use tools.
        ToolChoiceNamed: V5RouteCapabilityStatus.EMPIRICALLY_UNSUPPORTED,
        ToolChoiceRequired: V5RouteCapabilityStatus.EMPIRICALLY_UNSUPPORTED,
        EvidenceSource: "OpenRouter /models/qwen/qwen3.7-flash/endpoints supported_parameters metadata " +
            "+ real 31-pack production cohort (json_object, commit range ending 3a4f69a) " +
            "+ source-selection remediation canary (json_object, commit c287f19) " +
            "+ named-tool-choice canary (commit 71cc694: HTTP 404, Filter by Tool Compatibility) " +
            "+ required-tool-choice canary (commit 6a70d2f/fcdab6d: identical HTTP 404)");
}

/// <summary>
/// The OpenRouter transport for <see cref="CanonicalSemanticRequestV2_1"/> (via its serialized
/// <see cref="V5ComposedSemanticRequest"/>) over the one currently-pinned production route
/// (<see cref="V5RouteIdentity.OpenRouterQwen37ChatCompletions"/>): <c>response_format.type=json_object</c>,
/// Alibaba-pinned, no fallback. Owns transport only - it never reads or reinterprets the canonical
/// request's content, and consuming it never changes <see cref="V5ComposedSemanticRequest.RequestHash"/>.
/// <para>
/// This is now the one authority for the OpenRouter production body's literal shape.
/// <see cref="V5ProviderRequestBodyV2_1.Build"/> delegates here rather than duplicating it, so there is
/// never a second, independently-drifting serializer for the same wire body.
/// </para>
/// </summary>
public static class OpenRouterQwen37JsonObjectCarrierV2_1
{
    public static V5RouteIdentity Route => V5RouteIdentity.OpenRouterQwen37ChatCompletions;

    /// <summary>The carrier's natural entry point: the canonical request's own serialization, never a bare string the caller assembled by hand.</summary>
    public static V5ProviderRequestBodyV2_1 Build(V5ComposedSemanticRequest request, int maxCompletionTokens, V5ProviderEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(request);
        return BuildFromRaw(V5SystemPromptV2_1.Text, request.Prompt, maxCompletionTokens, envelope);
    }

    /// <summary>
    /// Legacy-compatible entry point - the exact literal body shape production has always sent, now
    /// defined in exactly one place. <see cref="V5ProviderRequestBodyV2_1.Build"/> is a thin facade
    /// over this.
    /// </summary>
    public static V5ProviderRequestBodyV2_1 BuildFromRaw(
        string systemPrompt, string userMessage, int maxCompletionTokens, V5ProviderEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(envelope);
        if (maxCompletionTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxCompletionTokens));

        object provider = string.IsNullOrWhiteSpace(envelope.Provider)
            ? new
            {
                zdr = false,
                data_collection = "deny",
                require_parameters = true,
                allow_fallbacks = true,
            }
            : new
            {
                order = new[] { envelope.Provider },
                allow_fallbacks = false,
                require_parameters = true,
                data_collection = "deny",
                zdr = false,
            };

        var body = new
        {
            model = envelope.Model,
            temperature = 0,
            max_tokens = maxCompletionTokens,
            reasoning = new { effort = envelope.Reasoning },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
            response_format = new { type = envelope.ResponseFormat },
            provider,
            stream = envelope.Streaming,
            usage = new { include = envelope.UsageInclude },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, CanonicalJson.Options);
        return new V5ProviderRequestBodyV2_1(bytes, Hashing.Sha256(Encoding.UTF8.GetString(bytes)), bytes.Length);
    }
}

/// <summary>
/// The complete, deterministic OpenRouter request body for the prepared-but-unsent ToolAuto carrier:
/// exactly one declared function (<see cref="OpenRouterQwen37ToolAutoCarrierV1.ToolName"/>), with
/// <c>tool_choice: "auto"</c> - never the named-function or <c>"required"</c> forms, both already
/// found <see cref="V5RouteCapabilityStatus.EMPIRICALLY_UNSUPPORTED"/> on this exact route. A separate
/// type from <see cref="V5ProviderRequestBodyV2_1"/> (production) and
/// <see cref="V5ForcedToolProviderRequestBodyV1"/> (the closed named/required-forcing experiment) so
/// none of the three can be confused with, or accidentally changed by, either of the others.
/// </summary>
public sealed record V5OpenRouterToolAutoProviderRequestBodyV1(byte[] PayloadBytes, string Hash, int Bytes)
{
    public static V5OpenRouterToolAutoProviderRequestBodyV1 Build(
        string systemPrompt, string userMessage, int maxCompletionTokens,
        string model, string providerRoutingSlug, string toolName, string toolDescription, object toolParametersSchema,
        string reasoningEffort)
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
            tool_choice = "auto",
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
        return new V5OpenRouterToolAutoProviderRequestBodyV1(bytes, Hashing.Sha256(Encoding.UTF8.GetString(bytes)), bytes.Length);
    }
}

/// <summary>
/// Provider-free preparation only: builds (never sends) the ToolAuto request shape described in
/// <see cref="V5OpenRouterToolAutoProviderRequestBodyV1"/>. Exists so the exact request can be
/// inspected and frozen before anyone decides whether one real canary call is worth paying for.
/// The tool's <c>parameters</c> schema is compiled from the existing generic
/// <see cref="V5StrictClaimSchemaCompilerV1"/> (<see cref="V5ClaimSchemaCarrier.ToolParameters"/>
/// shape) - the same TaskContract/claim-shape/source-part vocabulary the production wire already
/// uses, never a second ontology.
/// </summary>
public static class OpenRouterQwen37ToolAutoCarrierV1
{
    public const string ToolName = "submit_semantic_claims";
    public const string ToolDescription = "Submit the complete source-backed semantic claim response for this request.";

    public static V5RouteIdentity Route => V5RouteIdentity.OpenRouterQwen37ChatCompletions;

    public static V5OpenRouterToolAutoProviderRequestBodyV1 Build(
        V5ComposedSemanticRequest request, DocumentTaskContract contract, int maxCompletionTokens, V5ProviderEnvelope envelope,
        IReadOnlyList<string>? ownedAliasEnum = null, IReadOnlyList<string>? visibleAliasEnum = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(envelope);
        var parameters = V5StrictClaimSchemaCompilerV1.Compile(contract, V5ClaimSchemaCarrier.ToolParameters, ownedAliasEnum, visibleAliasEnum);
        return V5OpenRouterToolAutoProviderRequestBodyV1.Build(
            V5SystemPromptV2_1.Text, request.Prompt, maxCompletionTokens,
            envelope.Model, envelope.Provider, ToolName, ToolDescription, parameters, envelope.Reasoning);
    }
}
