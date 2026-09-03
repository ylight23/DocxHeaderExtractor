using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Pure evidence contract retained by structural authority and compatibility projections.</summary>
public sealed record HeadingEvidence(
    [property: JsonPropertyName("numberingValid")] bool NumberingValid,
    [property: JsonPropertyName("siblingSequenceValid")] bool SiblingSequenceValid,
    [property: JsonPropertyName("formattingConsistent")] bool FormattingConsistent,
    [property: JsonPropertyName("modelConfirmed")] bool ModelConfirmed,
    [property: JsonPropertyName("treeValid")] bool TreeValid,
    [property: JsonPropertyName("status")] string Status);
