using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Chunk policy for generic document output, independent of model prompt chunking.</summary>
public sealed record DocumentChunkingPolicy(
    int MaxTokenEstimate = 800,
    double CharsPerToken = 1.85);

/// <summary>
/// Builds deterministic chunks by concatenating source-catalog text. It never uses structural text
/// as document body text and never calls an LLM.
/// </summary>
public static class SectionChunkProjection
{
    public static IReadOnlyList<DocumentChunk> Project(
        IReadOnlyList<StructuralSection> sections,
        DocumentSourceCatalog sourceCatalog,
        ValidatedStructure structure,
        DocumentChunkingPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(sourceCatalog);
        ArgumentNullException.ThrowIfNull(structure);
        policy ??= new DocumentChunkingPolicy();
        if (policy.MaxTokenEstimate <= 0 || policy.CharsPerToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(policy));

        var sources = sourceCatalog.Units.ToDictionary(unit => unit.SourceId, StringComparer.Ordinal);
        var elements = structure.Elements.ToDictionary(element => element.Id, StringComparer.Ordinal);
        var chunks = new List<DocumentChunk>();
        foreach (var section in sections)
        {
            var missingSourceIds = section.SourceIds
                .Where(sourceId => !sources.ContainsKey(sourceId))
                .ToArray();
            if (missingSourceIds.Length > 0)
                throw new InvalidOperationException("chunk-source-not-grounded");

            var sectionSources = section.SourceIds
                .Select(sourceId => sources[sourceId])
                .OrderBy(unit => unit.SourceOrdinal)
                .ThenBy(unit => unit.SourceId, StringComparer.Ordinal)
                .ToArray();
            var structuralIds = section.StructuralElementIds
                .Where(elements.ContainsKey)
                .ToHashSet(StringComparer.Ordinal);
            var currentSources = new List<DocumentSourceUnit>();
            foreach (var source in sectionSources)
            {
                currentSources.Add(source);
                var estimate = Estimate(currentSources);
                if (estimate <= policy.MaxTokenEstimate) continue;
                currentSources.RemoveAt(currentSources.Count - 1);
                if (currentSources.Count > 0)
                    chunks.Add(Materialize(section, chunks.Count + 1, currentSources, structuralIds, elements, policy));
                currentSources = [source];
            }
            if (currentSources.Count > 0)
                chunks.Add(Materialize(section, chunks.Count + 1, currentSources, structuralIds, elements, policy));
        }
        return chunks;

        int Estimate(IReadOnlyList<DocumentSourceUnit> units) =>
            Math.Max(1, (int)Math.Ceiling(string.Join('\n', units.Select(unit => unit.Text)).Length / policy.CharsPerToken));
    }

    private static DocumentChunk Materialize(
        StructuralSection section,
        int ordinal,
        IReadOnlyList<DocumentSourceUnit> sources,
        IReadOnlySet<string> structuralIds,
        IReadOnlyDictionary<string, ValidatedStructuralElement> elements,
        DocumentChunkingPolicy policy)
    {
        var text = string.Join('\n', sources.Select(source => source.Text));
        var sourceIdSet = sources.Select(source => source.SourceId).ToHashSet(StringComparer.Ordinal);
        var structural = structuralIds
            .Where(id => elements[id].Sources.Any(source => sourceIdSet.Contains(source.SourceId)))
            .ToArray();
        return new DocumentChunk(
            $"{section.Id}:chunk:{ordinal}",
            section.Id,
            sources.Select(source => source.SourceId).ToArray(),
            structural,
            text,
            Math.Max(1, (int)Math.Ceiling(text.Length / policy.CharsPerToken)));
    }
}
