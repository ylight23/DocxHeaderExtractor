using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>
/// Normal authority extraction contract. Slim is retained behind the compatibility boundary and
/// is not exposed by this result.
/// </summary>
internal sealed record AuthoritySourceExtractionResult(
    SourceDocument Source,
    SlimCompatibilityContext Compatibility);
