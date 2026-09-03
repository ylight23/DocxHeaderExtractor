using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Projects generic extraction into a search/index contract without an index SDK.</summary>
public static class SearchIndexProjection
{
    public static IReadOnlyList<SearchIndexDocument> Project(DocumentExtractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Chunks.Select(chunk =>
        {
            var slice = DocumentConsumerProjectionSupport.Slice(result, chunk);
            return new SearchIndexDocument(
                result.DocumentIdentity.DocumentId,
                chunk.Id,
                chunk.SectionId,
                chunk.Text,
                chunk.SourceIds,
                slice.Context.Select(item => item.Type.ToString()).Distinct(StringComparer.Ordinal).ToArray(),
                slice.Section.PathElementIds,
                slice.Relations,
                slice.Context);
        }).ToArray();
    }
}
