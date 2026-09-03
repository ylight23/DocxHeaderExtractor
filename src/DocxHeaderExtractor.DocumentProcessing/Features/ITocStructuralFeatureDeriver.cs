using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Features;

public interface ITocStructuralFeatureDeriver
{
    TocStructuralFeatures Derive(
        SourceDocument source,
        IReadOnlySet<string> tocEntrySourceIds);
}
