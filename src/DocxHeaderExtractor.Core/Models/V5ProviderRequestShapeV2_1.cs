using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.V5;

public enum StructuredOutputMode
{
    JsonSchemaStrict,
    JsonObject,
}

public sealed record ProviderStructuredOutputCapabilities(
    string Provider,
    string Model,
    bool JsonSchemaStrictSupported,
    bool JsonObjectSupported,
    string EvidenceSource)
{
    public StructuredOutputMode Select(bool requireStrict)
    {
        if (requireStrict)
        {
            if (!JsonSchemaStrictSupported)
                throw new InvalidOperationException("provider-strict-json-schema-capability-unverified");
            return StructuredOutputMode.JsonSchemaStrict;
        }
        if (JsonObjectSupported) return StructuredOutputMode.JsonObject;
        throw new InvalidOperationException("provider-has-no-supported-structured-output-mode");
    }
}

/// <summary>Provider-free capability record. It never probes a provider or runs inference.</summary>
public static class ProviderStructuredOutputRegistry
{
    public static ProviderStructuredOutputCapabilities QwenFlashAlibaba { get; } =
        new("Alibaba", "qwen/qwen3.7-flash", JsonSchemaStrictSupported: false, JsonObjectSupported: true,
            EvidenceSource: "explicit-preflight-registration; strict support unverified");
}

/// <summary>Provider-neutral response-format shape. Capability selection is explicit and provider-free.</summary>
public static class V5ProviderRequestShapeV2_1
{
    public static object ResponseFormat(StructuredOutputMode mode) => mode switch
    {
        StructuredOutputMode.JsonSchemaStrict => new
        {
            type = "json_schema",
            json_schema = new
            {
                name = SemanticClaimContractV2_1.SchemaVersion.Replace('-', '_').Replace('.', '_'),
                strict = true,
                schema = SemanticClaimContractV2_1.Schema(),
            },
        },
        StructuredOutputMode.JsonObject => new { type = "json_object" },
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string SerializeResponseFormat(StructuredOutputMode mode) =>
        JsonSerializer.Serialize(ResponseFormat(mode), CanonicalJson.Options);
}

/// <summary>
/// The system prompt sent alongside every v2.1 semantic-claim request. Kept as one constant so
/// preflight (which hashes the frozen provider request) and execution (which must send exactly what
/// preflight froze) can never accidentally diverge on this string.
/// </summary>
public static class V5SystemPromptV2_1
{
    public const string Text =
        "You are a task-defined semantic reasoner. Respond with a single JSON object matching the declared schema exactly.";
}

/// <summary>
/// The complete, deterministic OpenRouter request body for a v2.1 semantic-claim call - not just the
/// semantic prompt bytes the preflight already hashes, but everything that governs what is actually
/// sent: <c>max_tokens</c>, reasoning, response_format, provider routing, stream and usage.include.
/// Frozen here so preflight and execution can never silently diverge on a field that never appeared
/// in the semantic request bytes - the same finding that let a max_tokens change go unnoticed by a
/// preflight that hashed only the prompt.
/// </summary>
public sealed record V5ProviderRequestBodyV2_1(byte[] PayloadBytes, string Hash, int Bytes)
{
    public static V5ProviderRequestBodyV2_1 Build(
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
