using DocxHeaderExtractor.Contracts.Models;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.DocumentProcessing.Output;

/// <summary>Processing-layer execution envelope with its outline projection.</summary>
public sealed record RunResult(
    DocumentExtractionResult Result,
    DocumentOutline Outline)
{
    /// <summary>Same-execution runtime sidecar. Not part of the compatibility JSON envelope.</summary>
    [JsonIgnore]
    public LaneResult? HeadingPipeline { get; init; }
}
