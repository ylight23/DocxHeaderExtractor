using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>Source authority boundary for document readers.</summary>
public interface IDocumentSource
{
    SourceDocument Read(string path);
}
