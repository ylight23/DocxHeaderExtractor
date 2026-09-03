using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Processing-layer execution envelope with its compatibility projection.</summary>
public sealed record AuthorityPipelineExecutionResult(
    DocumentExtractionResult Result,
    DocumentOutline CompatibilityOutline);
