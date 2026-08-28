using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Application.Features;

public interface ITocStructuralFeatureDeriver
{
    TocStructuralFeatures Derive(
        SourceDocument source,
        IReadOnlySet<string> tocEntrySourceIds);
}
