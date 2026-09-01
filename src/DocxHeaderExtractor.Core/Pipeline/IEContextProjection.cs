using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Projects source-backed IE context; it does not create or validate facts.</summary>
public static class IEContextProjection
{
    public static IReadOnlyList<FactExtractionContext> Project(DocumentExtractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Chunks.Select(chunk =>
        {
            var slice = DocumentConsumerProjectionSupport.Slice(result, chunk);
            return new FactExtractionContext(
                chunk.Text,
                slice.Section.PathElementIds,
                slice.Context,
                slice.FigureTableContext,
                chunk.SourceIds,
                chunk.StructuralElementIds);
        }).ToArray();
    }
}
