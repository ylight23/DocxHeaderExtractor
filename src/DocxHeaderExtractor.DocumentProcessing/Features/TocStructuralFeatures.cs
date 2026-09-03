using System.Collections.Frozen;

namespace DocxHeaderExtractor.DocumentProcessing.Features;

/// <summary>Occurrence-safe TOC adjacency facts keyed by immutable source identity.</summary>
public sealed record TocStructuralFeatures(FrozenSet<string> PrecedesTableOfContentsSourceIds)
{
    public bool PrecedesTableOfContents(string sourceId) =>
        PrecedesTableOfContentsSourceIds.Contains(sourceId);
}
