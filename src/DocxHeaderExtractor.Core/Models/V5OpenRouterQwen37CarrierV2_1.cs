using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// The state recorded for one capability on one evidence axis. The axis is carried separately by
/// <see cref="V5RouteCapabilityEvidence"/>, so an API schema declaration, model documentation and a
/// real route observation can never be collapsed into one misleading capability bool.
/// </summary>
public enum V5CapabilityEvidenceState
{
    NOT_APPLICABLE,
    SUPPORTED,
    UNSUPPORTED,
    UNTESTED,
    UNTESTED_SUCCESSFULLY,
    NOT_NEEDED,
    NOT_IN_OVERVIEW,
    MAY_MENTION,
    ROUTING_REJECTED,
}

/// <summary>Observed model compliance with an invocation request after the route accepted it.</summary>
public enum V5InvocationReliability
{
    NOT_ASSESSED,
    RELIABLE,
    FAILED_CANARY,
}

/// <summary>
/// Capability evidence kept on its original axis. For example, the OpenRouter API schema can admit a
/// tool_choice value while the pinned model's documentation says nothing specific about that value, and
/// only a real request can establish whether the exact gateway/model/provider route accepts it.
/// </summary>
public sealed record V5RouteCapabilityEvidence(
    V5CapabilityEvidenceState ApiSchema,
    V5CapabilityEvidenceState ModelDocumentation,
    V5CapabilityEvidenceState OtherDocumentation,
    V5CapabilityEvidenceState RouteEmpirical,
    V5InvocationReliability InvocationReliability,
    string Reason);

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
    V5RouteCapabilityEvidence JsonObject,
    V5RouteCapabilityEvidence JsonSchemaStrict,
    V5RouteCapabilityEvidence Tools,
    V5RouteCapabilityEvidence ToolChoiceAuto,
    V5RouteCapabilityEvidence ToolChoiceNamed,
    V5RouteCapabilityEvidence ToolChoiceRequired,
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
        // The model page advertises JSON output and real cohorts transported json_object successfully.
        JsonObject: new(
            ApiSchema: V5CapabilityEvidenceState.NOT_APPLICABLE,
            ModelDocumentation: V5CapabilityEvidenceState.SUPPORTED,
            OtherDocumentation: V5CapabilityEvidenceState.NOT_APPLICABLE,
            RouteEmpirical: V5CapabilityEvidenceState.SUPPORTED,
            InvocationReliability: V5InvocationReliability.NOT_ASSESSED,
            Reason: "OpenRouter documents response_format JSON output; the 31-pack cohort and remediation canary succeeded with json_object."),
        // response_format is advertised as JSON output, not as strict JSON Schema for this model.
        // A strict probe would add no decision-useful evidence, so it is explicitly not needed.
        JsonSchemaStrict: new(
            ApiSchema: V5CapabilityEvidenceState.NOT_APPLICABLE,
            ModelDocumentation: V5CapabilityEvidenceState.UNSUPPORTED,
            OtherDocumentation: V5CapabilityEvidenceState.NOT_APPLICABLE,
            RouteEmpirical: V5CapabilityEvidenceState.NOT_NEEDED,
            InvocationReliability: V5InvocationReliability.NOT_ASSESSED,
            Reason: "OpenRouter explicitly says qwen/qwen3.7-flash supports JSON output without JSON-schema enforcement; it is not advertised as strict."),
        // Both the OpenRouter API schema and model page support tools; route acceptance and model
        // invocation are measured separately below on ToolChoiceAuto.
        Tools: new(
            ApiSchema: V5CapabilityEvidenceState.SUPPORTED,
            ModelDocumentation: V5CapabilityEvidenceState.SUPPORTED,
            OtherDocumentation: V5CapabilityEvidenceState.NOT_APPLICABLE,
            RouteEmpirical: V5CapabilityEvidenceState.SUPPORTED,
            InvocationReliability: V5InvocationReliability.FAILED_CANARY,
            Reason: "API schema and model documentation support tools; the P3 auto canary route was accepted, but the model emitted no tool call."),
        // The Overview ToolChoice union includes auto and the model page says it accepts tool_choice;
        // P3 confirms transport acceptance while measuring invocation reliability independently.
        ToolChoiceAuto: new(
            ApiSchema: V5CapabilityEvidenceState.SUPPORTED,
            ModelDocumentation: V5CapabilityEvidenceState.SUPPORTED,
            OtherDocumentation: V5CapabilityEvidenceState.NOT_APPLICABLE,
            RouteEmpirical: V5CapabilityEvidenceState.SUPPORTED,
            InvocationReliability: V5InvocationReliability.FAILED_CANARY,
            Reason: "P3 PACK_006 completed with HTTP success and finish_reason=stop, but produced plain assistant text and zero tool calls."),
        // Two real canary calls (commit 71cc694: named-function tool_choice; commit 6a70d2f/fcdab6d:
        // tool_choice="required") each received an OpenRouter ROUTING-layer HTTP 404 ("No endpoints
        // found that support the provided 'tool_choice' value", failedRoutingStep "Filter by Tool
        // Compatibility") before the request ever reached Alibaba's model. A routing/carrier fact
        // about an advertised parameter NAME vs an honored VALUE - not a claim that Qwen3.7 itself
        // cannot use tools.
        ToolChoiceNamed: new(
            ApiSchema: V5CapabilityEvidenceState.SUPPORTED,
            ModelDocumentation: V5CapabilityEvidenceState.SUPPORTED,
            OtherDocumentation: V5CapabilityEvidenceState.NOT_APPLICABLE,
            RouteEmpirical: V5CapabilityEvidenceState.ROUTING_REJECTED,
            InvocationReliability: V5InvocationReliability.NOT_ASSESSED,
            Reason: "Named function appears in the Overview ToolChoice union; the pinned route returned HTTP 404, Filter by Tool Compatibility."),
        ToolChoiceRequired: new(
            ApiSchema: V5CapabilityEvidenceState.NOT_IN_OVERVIEW,
            ModelDocumentation: V5CapabilityEvidenceState.NOT_APPLICABLE,
            OtherDocumentation: V5CapabilityEvidenceState.MAY_MENTION,
            RouteEmpirical: V5CapabilityEvidenceState.ROUTING_REJECTED,
            InvocationReliability: V5InvocationReliability.NOT_ASSESSED,
            Reason: "'required' is not in the Overview ToolChoice union; other compatibility documentation may mention it, but the pinned route returned HTTP 404, Filter by Tool Compatibility."),
        EvidenceSource: "OpenRouter API Overview (https://openrouter.ai/docs/api_reference/overview) " +
            "+ OpenRouter qwen/qwen3.7-flash model page (JSON output without JSON-schema enforcement; tools and tool_choice) " +
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

    /// <summary>
    /// Builds the same qualified JSON-object request shape as <see cref="BuildFromRaw"/>, with
    /// the current OpenRouter reasoning-enabled representation.  The effort member is deliberately
    /// omitted: callers use this only for a matched carrier experiment where reasoning is the sole
    /// body-level independent variable.
    /// </summary>
    public static V5ProviderRequestBodyV2_1 BuildFromRawReasoningEnabled(
        string systemPrompt, string userMessage, int maxCompletionTokens, V5ProviderEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(envelope);
        if (maxCompletionTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxCompletionTokens));

        object provider = string.IsNullOrWhiteSpace(envelope.Provider)
            ? new { zdr = false, data_collection = "deny", require_parameters = true, allow_fallbacks = true }
            : new { order = new[] { envelope.Provider }, allow_fallbacks = false, require_parameters = true, data_collection = "deny", zdr = false };
        var body = new
        {
            model = envelope.Model,
            temperature = 0,
            max_tokens = maxCompletionTokens,
            reasoning = new { enabled = true },
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
/// The same qualified OpenRouter json_object transport shape applied to the live v3 canonical
/// decision request. The prompt/schema remain v3-owned; this adapter centralizes the carrier bytes
/// in the existing body builder and does not enable provider-side schema enforcement.
/// </summary>
public static class OpenRouterQwen37JsonObjectCarrierV3
{
    public static V5RouteIdentity Route => V5RouteIdentity.OpenRouterQwen37ChatCompletions;

    public static V5ProviderRequestBodyV2_1 Build(
        V5ComposedSemanticDecisionRequestV3 request, int maxCompletionTokens, V5ProviderEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Model != Route.Model || envelope.Provider != Route.Provider || envelope.ResponseFormat != "json_object")
            throw new InvalidOperationException("v3-json-object-carrier-route-not-qualified");
        var expectedCompletionTokens = V5SemanticCompletionBudget.Compute(
            request.ResponseBounds.MaxDecisions,
            request.ResponseBounds.MaxDecisions,
            request.Utf8Bytes,
            V5SemanticDecisionResponseBoundsV3.ProviderCompletionCeiling);
        if (maxCompletionTokens != expectedCompletionTokens ||
            maxCompletionTokens > V5SemanticDecisionResponseBoundsV3.ProviderCompletionCeiling)
            throw new InvalidOperationException("v3-completion-token-budget-mismatch");
        return OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(
            V5SystemPromptV2_1.Text, request.Prompt, maxCompletionTokens, envelope);
    }
}

/// <summary>
/// The complete, deterministic OpenRouter request body for the prepared-but-unsent ToolAuto carrier:
/// exactly one declared function (<see cref="OpenRouterQwen37ToolAutoCarrierV1.ToolName"/>), with
/// <c>tool_choice: "auto"</c> - never the named-function or <c>"required"</c> forms, both already
/// found <see cref="V5CapabilityEvidenceState.UNSUPPORTED"/> on the route-empirical axis. A separate
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
