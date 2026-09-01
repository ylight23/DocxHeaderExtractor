using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

internal static class DocumentConsumerProjectionSupport
{
    internal static ConsumerSlice Slice(DocumentExtractionResult result, DocumentChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(chunk);

        var section = result.Sections.FirstOrDefault(item => item.Id == chunk.SectionId)
            ?? throw new InvalidOperationException("consumer-section-not-grounded");
        var sources = result.SourceCatalog.Units.ToDictionary(unit => unit.SourceId, StringComparer.Ordinal);
        var missingSourceIds = chunk.SourceIds.Where(sourceId => !sources.ContainsKey(sourceId)).ToArray();
        if (missingSourceIds.Length > 0)
            throw new InvalidOperationException("consumer-source-not-grounded");

        var sourceText = string.Join('\n', chunk.SourceIds.Select(sourceId => sources[sourceId].Text));
        if (!string.Equals(sourceText, chunk.Text, StringComparison.Ordinal))
            throw new InvalidOperationException("consumer-chunk-text-not-source-backed");

        var elements = result.Structure.Elements.ToDictionary(element => element.Id, StringComparer.Ordinal);
        var referencedElementIds = chunk.StructuralElementIds
            .Concat(section.PathElementIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (referencedElementIds.Any(elementId => !elements.ContainsKey(elementId)))
            throw new InvalidOperationException("consumer-structural-reference-not-grounded");

        var context = referencedElementIds
            .Select(elementId => ToContext(elements[elementId], sources))
            .ToArray();
        var contextIds = context.Select(item => item.ElementId).ToHashSet(StringComparer.Ordinal);
        var relations = result.Structure.Relations
            .Where(relation => contextIds.Contains(relation.FromId) && contextIds.Contains(relation.ToId))
            .ToArray();

        return new ConsumerSlice(
            section,
            chunk,
            context,
            context.Where(item => item.Type is StructuralElementType.Figure or
                StructuralElementType.Table or StructuralElementType.FigureTitle or
                StructuralElementType.TableTitle or StructuralElementType.Caption).ToArray(),
            relations);
    }

    private static StructuralContextItem ToContext(
        ValidatedStructuralElement element,
        IReadOnlyDictionary<string, DocumentSourceUnit> sources)
    {
        if (element.Sources.Any(source => !sources.ContainsKey(source.SourceId)))
            throw new InvalidOperationException("consumer-structural-source-not-grounded");
        return new StructuralContextItem(
            element.Id,
            element.Type,
            element.Role,
            element.Text,
            element.Level,
            element.Sources);
    }
}

internal sealed record ConsumerSlice(
    StructuralSection Section,
    DocumentChunk Chunk,
    IReadOnlyList<StructuralContextItem> Context,
    IReadOnlyList<StructuralContextItem> FigureTableContext,
    IReadOnlyList<StructuralRelation> Relations);
