using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Features;

public interface IDocumentFeatureDeriver
{
    DerivedDocumentFeatures Derive(SourceDocument source);
}
