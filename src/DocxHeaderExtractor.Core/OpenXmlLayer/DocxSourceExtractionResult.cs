using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>Compatibility Slim output and source-only facts from one DOCX extraction.</summary>
public sealed record DocxSourceExtractionResult(SlimDocument Slim, SourceDocument Source);
