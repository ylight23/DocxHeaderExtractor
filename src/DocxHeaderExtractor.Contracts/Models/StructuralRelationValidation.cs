using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Contracts.Models;

public sealed record StructuralRelationValidation(
    [property: JsonPropertyName("endpointsPresent")] bool EndpointsPresent,
    [property: JsonPropertyName("distinctEndpoints")] bool DistinctEndpoints,
    [property: JsonPropertyName("typeValid")] bool TypeValid,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason)
{
    [JsonIgnore]
    public bool Accepted => RejectionReason is null;
}
