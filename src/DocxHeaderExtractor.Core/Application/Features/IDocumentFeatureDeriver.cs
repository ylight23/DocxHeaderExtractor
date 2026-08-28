using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Application.Features;

public interface IDocumentFeatureDeriver
{
    DerivedDocumentFeatures Derive(SourceDocument source);
}
