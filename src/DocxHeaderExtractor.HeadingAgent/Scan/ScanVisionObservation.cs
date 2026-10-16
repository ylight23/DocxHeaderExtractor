using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>What a vision model saw of one line of a page image: closed answers, each with "uncertain" allowed. A model's judgement, not a
/// measurement: origin VLM_OBSERVATION, and the agent is told so.</summary>
public sealed record VlmLineObservation(
    [property: JsonPropertyName("lineId")] string LineId,
    [property: JsonPropertyName("appearsBold")] string AppearsBold,
    [property: JsonPropertyName("relativeSize")] string RelativeSize,
    [property: JsonPropertyName("alignment")] string Alignment);

/// <summary>A vision model's choice between the OCR readings of one line, from a crop of the line at full resolution: one of the views, NONE
/// (no reading matches the image) or UNREADABLE; with an optional reading it proposes, which is only a correction candidate.</summary>
public sealed record VlmReadingChoice(
    [property: JsonPropertyName("lineId")] string LineId,
    [property: JsonPropertyName("choice")] string Choice,
    [property: JsonPropertyName("proposedReading")] string? ProposedReading);

/// <summary>
/// The vision observation of one page: the image it saw (hash), the model and prompt version that produced it, the line observations and reading
/// choices that passed validation, what was rejected and why, and the failure when the call did not succeed. Stored once per image, model,
/// prompt version and set of asked lines; never rewritten.
/// </summary>
public sealed record VlmPageObservation(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("imageSha256")] string ImageSha256,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("promptVersion")] string PromptVersion,
    [property: JsonPropertyName("askedLines")] IReadOnlyList<string> AskedLines,
    [property: JsonPropertyName("askedReadings")] IReadOnlyList<string> AskedReadings,
    [property: JsonPropertyName("lines")] IReadOnlyList<VlmLineObservation> Lines,
    [property: JsonPropertyName("readings")] IReadOnlyList<VlmReadingChoice> Readings,
    [property: JsonPropertyName("rejected")] IReadOnlyList<string> Rejected,
    [property: JsonPropertyName("failure")] string? Failure,
    [property: JsonPropertyName("providerCostUsd")] double? ProviderCostUsd)
{
    public const string CurrentVersion = "SCAN_VLM_OBSERVATION_V1";

    public static readonly string[] BoldValues = ["yes", "no", "uncertain"];
    public static readonly string[] SizeValues = ["larger", "same", "smaller", "uncertain"];
    public static readonly string[] AlignmentValues = ["left", "center", "right", "justified", "uncertain"];
    public const string NoReading = "NONE", Unreadable = "UNREADABLE";
}
