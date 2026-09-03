using System.Collections.Frozen;

namespace DocxHeaderExtractor.Core.Application.Features;

/// <summary>Occurrence-safe TOC adjacency facts keyed by immutable source identity.</summary>
public sealed record TocStructuralFeatures(FrozenSet<string> PrecedesTableOfContentsSourceIds)
{
    public bool PrecedesTableOfContents(string sourceId) =>
        PrecedesTableOfContentsSourceIds.Contains(sourceId);
}
