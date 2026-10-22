using System.Text.Json.Serialization;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>A vision model's choice between the OCR readings of one line, from a crop of the line at full resolution: one of the views, NONE
/// (no reading matches the image) or UNREADABLE; with an optional reading it proposes, which is only a correction candidate.</summary>
public sealed record VlmReadingChoice(
    [property: JsonPropertyName("lineId")] string LineId,
    [property: JsonPropertyName("choice")] string Choice,
    [property: JsonPropertyName("proposedReading")] string? ProposedReading);
