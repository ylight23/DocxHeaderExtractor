using System.Text.Json.Serialization;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>What a vision model saw of one line of a page image: closed answers, each with "uncertain" allowed. A model's judgement, not a
/// measurement: origin VLM_OBSERVATION, and the agent is told so.</summary>
public sealed record VlmLineObservation(
    [property: JsonPropertyName("lineId")] string LineId,
    [property: JsonPropertyName("appearsBold")] string AppearsBold,
    [property: JsonPropertyName("relativeSize")] string RelativeSize,
    [property: JsonPropertyName("alignment")] string Alignment);
