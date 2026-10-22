
namespace DocxHeaderExtractor.Contracts.Routing;

/// <summary>What the uploaded file actually is, read from its bytes rather than its name.</summary>
public enum SourceType
{
    Unknown,
    Docx,
    Pdf,
}
