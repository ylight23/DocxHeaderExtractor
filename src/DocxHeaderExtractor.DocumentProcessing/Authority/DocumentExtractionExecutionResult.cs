using DocxHeaderExtractor.Core.Models;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.DocumentProcessing.Authority;

/// <summary>Processing-layer execution envelope with its outline projection.</summary>
public sealed record DocumentExtractionExecutionResult(
    DocumentExtractionResult Result,
    DocumentOutline Outline)
{
    /// <summary>Same-execution runtime sidecar. Not part of the compatibility JSON envelope.</summary>
    [JsonIgnore]
    public HeadingPipelineResult? HeadingPipeline { get; init; }
}
