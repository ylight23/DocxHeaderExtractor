using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Authority;

/// <summary>Processing-layer execution envelope with its outline projection.</summary>
public sealed record DocumentExtractionExecutionResult(
    DocumentExtractionResult Result,
    DocumentOutline Outline);
