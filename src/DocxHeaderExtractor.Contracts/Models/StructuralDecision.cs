using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Contracts.Models;

/// <summary>Decision metadata retained after heading validation.</summary>
public sealed record StructuralDecision(
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("confidenceBasis")] string ConfidenceBasis,
    [property: JsonPropertyName("disputed")] bool Disputed = false);
