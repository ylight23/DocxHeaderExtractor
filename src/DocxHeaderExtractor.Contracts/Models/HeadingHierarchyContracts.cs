using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>The heading hierarchy's only supported relation.</summary>
public enum StructuralRelationType
{
    ParentChild = 0,
}

public sealed record StructuralRelation(
    [property: JsonPropertyName("fromId")] string FromId,
    [property: JsonPropertyName("toId")] string ToId,
    [property: JsonPropertyName("type")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] StructuralRelationType Type);

/// <summary>Untrusted relation proposal. Endpoints are structural element IDs, never source IDs.</summary>
public sealed record StructuralRelationProposal(
    [property: JsonPropertyName("fromId")] string FromId,
    [property: JsonPropertyName("toId")] string ToId,
    [property: JsonPropertyName("type")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] StructuralRelationType Type);

public sealed record StructuralRelationValidation(
    [property: JsonPropertyName("endpointsPresent")] bool EndpointsPresent,
    [property: JsonPropertyName("distinctEndpoints")] bool DistinctEndpoints,
    [property: JsonPropertyName("typeValid")] bool TypeValid,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason)
{
    [JsonIgnore]
    public bool Accepted => RejectionReason is null;
}
