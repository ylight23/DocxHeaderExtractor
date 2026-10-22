using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Contracts.Models;

public sealed record StructuralValidation(
    [property: JsonPropertyName("sourceOccurrenceGrounded")] bool SourceOccurrenceGrounded,
    [property: JsonPropertyName("sourceFactsPresent")] bool SourceFactsPresent,
    [property: JsonPropertyName("proposedSpanValid")] bool ProposedSpanValid,
    [property: JsonPropertyName("sourceSelectionValid")] bool SourceSelectionValid,
    [property: JsonPropertyName("validatedSourceCount")] int ValidatedSourceCount,
    [property: JsonPropertyName("typeValid")] bool TypeValid,
    [property: JsonPropertyName("levelValid")] bool LevelValid,
    [property: JsonPropertyName("parentValid")] bool ParentValid,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason,
    [property: JsonPropertyName("typeRoleValid")] bool TypeRoleValid = true)
{
    [JsonIgnore]
    public bool Accepted => RejectionReason is null;
}
