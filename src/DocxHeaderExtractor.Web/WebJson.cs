using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Web;

/// <summary>The JSON every endpoint writes: camelCase, nulls left out, enums as names, Vietnamese text unescaped.</summary>
internal static class WebJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    static JsonSerializerOptions Create()
    {
        var json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        json.Converters.Add(new JsonStringEnumConverter());
        return json;
    }
}
