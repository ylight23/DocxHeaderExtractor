using System.Text.Json;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>Provider-neutral response-format shape for v2.1. Capability selection is explicit and provider-free.</summary>
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
