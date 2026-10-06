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
        new("alibaba", "qwen/qwen3.7-flash", JsonSchemaStrictSupported: false, JsonObjectSupported: true,
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
