using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Semantics.Binding;

public static class SemanticSourceAliasCatalog
{
    /// <summary>Builds stable aliases from the canonical source catalog, in source order.</summary>
    public static IReadOnlyList<SemanticSourceAlias> FromCatalog(DocumentSourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Units
            .OrderBy(unit => unit.SourceOrdinal)
            .ThenBy(unit => unit.SourceId, StringComparer.Ordinal)
            .Select((unit, index) => new SemanticSourceAlias(
                $"S{index + 1:0000}", unit.SourceId, unit.SourceOrdinal, unit.Text, unit.SourceSpan,
                unit.SourceAnchor))
            .ToArray();
    }
}
