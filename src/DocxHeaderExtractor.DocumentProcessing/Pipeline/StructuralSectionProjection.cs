using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Projects sections from validated outline elements and the validated ParentChild graph. It does
/// not create a second hierarchy or compare source text.
/// </summary>
public static class StructuralSectionProjection
{
    public static IReadOnlyList<StructuralSection> Project(
        ValidatedStructure structure,
        DocumentSourceCatalog sourceCatalog)
    {
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(sourceCatalog);

        var sourceIds = sourceCatalog.Units.Select(unit => unit.SourceId).ToHashSet(StringComparer.Ordinal);
        var anchors = structure.OutlineElements.ToArray();
        var elementById = structure.Elements.ToDictionary(element => element.Id, StringComparer.Ordinal);
        var sectionElementIds = anchors.Select(element => element.Id).ToHashSet(StringComparer.Ordinal);
        var parentByChild = structure.Relations
            .Where(relation => relation.Type == StructuralRelationType.ParentChild &&
                sectionElementIds.Contains(relation.FromId) && sectionElementIds.Contains(relation.ToId))
            .ToDictionary(relation => relation.ToId, relation => relation.FromId, StringComparer.Ordinal);
        var ordered = anchors
            .Select((element, index) => (element, index, ordinal: MinOrdinal(element)))
            .OrderBy(item => item.ordinal)
            .ThenBy(item => item.index)
            .ToArray();
        var sectionIdByElementId = ordered.ToDictionary(item => item.element.Id,
            item => $"section:{item.element.Id}", StringComparer.Ordinal);
        var sections = new List<StructuralSection>(ordered.Length);

        foreach (var item in ordered)
        {
            var parent = parentByChild.GetValueOrDefault(item.element.Id);
            var path = BuildPath(item.element.Id, parentByChild);
            var startOrdinal = item.ordinal;
            var nextBoundary = ordered
                .Where(next => next.ordinal > startOrdinal &&
                    SectionDepth(next.element.Id, parentByChild) <= SectionDepth(item.element.Id, parentByChild))
                .Select(next => next.ordinal)
                .DefaultIfEmpty(int.MaxValue)
                .Min();
            var structuralIds = structure.Elements
                .Where(element => MinOrdinal(element) >= startOrdinal && MinOrdinal(element) < nextBoundary)
                .Select(element => element.Id)
                .ToArray();
            var sectionSources = sourceCatalog.Units
                .Where(unit => unit.SourceOrdinal >= startOrdinal && unit.SourceOrdinal < nextBoundary)
                .Select(unit => unit.SourceId)
                .ToArray();
            var referencedSources = structure.Elements
                .Where(element => structuralIds.Contains(element.Id))
                .SelectMany(element => element.Sources.Select(source => source.SourceId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (referencedSources.Any(sourceId => !sourceIds.Contains(sourceId)))
                throw new InvalidOperationException("section-source-not-grounded");

            sections.Add(new StructuralSection(
                sectionIdByElementId[item.element.Id],
                item.element.Id,
                parent is null ? null : sectionIdByElementId[parent],
                path,
                sectionSources,
                structuralIds));
        }

        return sections;

        int MinOrdinal(ValidatedStructuralElement element) =>
            element.Sources.Count == 0 ? int.MaxValue : element.Sources.Min(source => source.SourceOrdinal);

        static IReadOnlyList<string> BuildPath(string elementId, IReadOnlyDictionary<string, string> parents)
        {
            var path = new List<string>();
            var current = elementId;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (seen.Add(current))
            {
                path.Add(current);
                if (!parents.TryGetValue(current, out current!)) break;
            }
            path.Reverse();
            return path;
        }

        static int SectionDepth(string elementId, IReadOnlyDictionary<string, string> parents)
        {
            var depth = 0;
            var current = elementId;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (parents.TryGetValue(current, out current!) && seen.Add(current)) depth++;
            return depth;
        }
    }
}
