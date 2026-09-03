using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Authority;

/// <summary>Processing-layer execution envelope with its compatibility projection.</summary>
public sealed record AuthorityPipelineExecutionResult(
    DocumentExtractionResult Result,
    DocumentOutline CompatibilityOutline);
