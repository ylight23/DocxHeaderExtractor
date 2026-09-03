using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

/// <summary>Source authority boundary for document readers.</summary>
public interface IDocumentSource
{
    SourceDocument Read(string path);
}
