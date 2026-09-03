using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Projects source-backed IE context; it does not create or validate facts.</summary>
public static class IEContextProjection
{
    public static IReadOnlyList<FactExtractionContext> Project(DocumentExtractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Chunks.Select(chunk =>
        {
            var slice = DocumentConsumerProjectionSupport.Slice(result, chunk);
            var sourceUnits = chunk.SourceIds
                .Select(sourceId => result.SourceCatalog.Units.Single(unit => unit.SourceId == sourceId))
                .Select(unit => new FactSourceExcerpt(unit.SourceId, unit.SourceOrdinal, unit.Text))
                .ToArray();
            return new FactExtractionContext(
                result.DocumentIdentity.DocumentId,
                chunk.Id,
                chunk.SectionId,
                chunk.Text,
                slice.Section.PathElementIds,
                slice.Context,
                slice.FigureTableContext,
                chunk.SourceIds,
                chunk.StructuralElementIds,
                sourceUnits);
        }).ToArray();
    }
}
