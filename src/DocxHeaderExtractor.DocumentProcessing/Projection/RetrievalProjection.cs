using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Projection;

/// <summary>Projects generic extraction chunks into deterministic retrieval records.</summary>
public static class RetrievalProjection
{
    public static IReadOnlyList<RetrievalDocument> Project(DocumentExtractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Chunks.Select(chunk =>
        {
            var slice = DocumentConsumerProjectionSupport.Slice(result, chunk);
            return new RetrievalDocument(
                chunk.Id,
                chunk.SectionId,
                chunk.Text,
                chunk.SourceIds,
                chunk.StructuralElementIds,
                slice.Section.PathElementIds,
                slice.Context,
                slice.Relations);
        }).ToArray();
    }
}
