using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Stable identity for one generic document extraction.</summary>
public sealed record DocumentIdentity(
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("sourceKind")] string SourceKind,
    [property: JsonPropertyName("sourcePath")] string SourcePath);

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

/// <summary>
/// A section projection anchored by a validated outline element. Parentage comes only from the
/// validated ParentChild graph; this type does not infer hierarchy from text.
/// </summary>
public sealed record StructuralSection(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("anchorElementId")] string AnchorElementId,
    [property: JsonPropertyName("parentSectionId")] string? ParentSectionId,
    [property: JsonPropertyName("pathElementIds")] IReadOnlyList<string> PathElementIds,
    [property: JsonPropertyName("sourceIds")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("structuralElementIds")] IReadOnlyList<string> StructuralElementIds);

/// <summary>Source-backed chunk for downstream retrieval/IE consumers.</summary>
public sealed record DocumentChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sectionId")] string SectionId,
    [property: JsonPropertyName("sourceIds")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("structuralElementIds")] IReadOnlyList<string> StructuralElementIds,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("tokenEstimate")] int TokenEstimate);

/// <summary>Deterministic generic extraction envelope. Heading output is a later compatibility projection.</summary>
public sealed record DocumentExtractionResult(
    [property: JsonPropertyName("documentIdentity")] DocumentIdentity DocumentIdentity,
    [property: JsonPropertyName("sourceCatalog")] DocumentSourceCatalog SourceCatalog,
    [property: JsonPropertyName("structure")] ValidatedStructure Structure,
    [property: JsonPropertyName("sections")] IReadOnlyList<StructuralSection> Sections,
    [property: JsonPropertyName("chunks")] IReadOnlyList<DocumentChunk> Chunks,
    [property: JsonPropertyName("provenance")] DocumentExtractionProvenance Provenance);

public sealed record DocumentExtractionProvenance(
    [property: JsonPropertyName("route")] string Route,
    [property: JsonPropertyName("sourceCatalogKind")] string SourceCatalogKind,
    [property: JsonPropertyName("providerCalls")] int ProviderCalls)
{
    /// <summary>
    /// Which execution contract produced this result. Two cohorts must never be compared as though
    /// they were one: a run whose authority came from a PDF discovered on the filesystem answered a
    /// different question from a run whose authority was the uploaded DOCX itself.
    /// </summary>
    [property: JsonPropertyName("executionContract")]
    public string ExecutionContract { get; init; } = ExecutionContracts.ExplicitUploadedDocxCanonical;
}

/// <summary>Named so a benchmark cannot silently mix cohorts.</summary>
public static class ExecutionContracts
{
    /// <summary>Authority was the uploaded file, identified by its own bytes.</summary>
    public const string ExplicitUploadedDocxCanonical = "EXPLICIT_UPLOADED_DOCX_CANONICAL";

    /// <summary>Authority was the uploaded PDF, extracted from that PDF alone.</summary>
    public const string ExplicitUploadedPdfCanonical = "EXPLICIT_UPLOADED_PDF_CANONICAL";

    /// <summary>Authority came from a PDF found beside the input or in a corpus directory.</summary>
    public const string LegacyAutoDiscoveredPdfRoute = "LEGACY_AUTO_DISCOVERED_PDF_ROUTE";
}
