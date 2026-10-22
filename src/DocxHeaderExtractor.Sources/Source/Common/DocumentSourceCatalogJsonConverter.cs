using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Common;

/// <summary>
/// Reads and writes a <see cref="DocumentSourceCatalog"/> as <c>{"units":[...]}</c>. The catalog's constructor validates and orders its
/// units, so reading goes through it: a stored catalog is checked the same way a built one is.
/// </summary>
internal sealed class DocumentSourceCatalogJsonConverter : JsonConverter<DocumentSourceCatalog>
{
    public override DocumentSourceCatalog Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("source-catalog-expected-object");
        reader.Read();
        if (reader.TokenType != JsonTokenType.PropertyName || reader.GetString() != "units") throw new JsonException("source-catalog-expected-units");
        reader.Read();
        var units = JsonSerializer.Deserialize<DocumentSourceUnit[]>(ref reader, options) ?? throw new JsonException("source-catalog-units-null");
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException("source-catalog-expected-end");
        return new DocumentSourceCatalog(units);
    }

    public override void Write(Utf8JsonWriter writer, DocumentSourceCatalog value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("units");
        JsonSerializer.Serialize(writer, value.Units, options);
        writer.WriteEndObject();
    }
}
