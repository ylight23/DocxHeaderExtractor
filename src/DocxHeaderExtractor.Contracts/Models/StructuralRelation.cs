using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Contracts.Models;

public sealed record StructuralRelation(
    [property: JsonPropertyName("fromId")] string FromId,
    [property: JsonPropertyName("toId")] string ToId,
    [property: JsonPropertyName("type")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] StructuralRelationType Type);
