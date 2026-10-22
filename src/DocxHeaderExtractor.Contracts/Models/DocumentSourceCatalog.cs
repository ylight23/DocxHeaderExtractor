using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Contracts.Models;

/// <summary>
/// One source-owned unit used by sections and chunks. Text is copied from the parser/source
/// catalog; it is never generated from structural labels or model output.
/// </summary>
public sealed record DocumentSourceUnit(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("sourceAnchor")] SourceAnchor SourceAnchor,
    [property: JsonPropertyName("sourceSpan")] StructuralSpan SourceSpan);

/// <summary>Validated, unique source inventory shared by all generic document projections.</summary>
public sealed class DocumentSourceCatalog
{
    public DocumentSourceCatalog(IEnumerable<DocumentSourceUnit> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        var materialized = units.OrderBy(unit => unit.SourceOrdinal).ThenBy(unit => unit.SourceId, StringComparer.Ordinal).ToArray();
        if (materialized.Any(unit => string.IsNullOrWhiteSpace(unit.SourceId)))
            throw new InvalidOperationException("source-catalog-empty-source-id");
        if (materialized.Select(unit => unit.SourceId).Distinct(StringComparer.Ordinal).Count() != materialized.Length)
            throw new InvalidOperationException("duplicate-source-id");
        if (materialized.Any(unit => !unit.SourceSpan.IsValidFor(unit.Text)))
            throw new InvalidOperationException("source-catalog-span-invalid");
        Units = new ReadOnlyCollection<DocumentSourceUnit>(materialized);
    }

    [JsonPropertyName("units")]
    public IReadOnlyList<DocumentSourceUnit> Units { get; }
}
