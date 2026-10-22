using System.Text.Json.Serialization;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

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
