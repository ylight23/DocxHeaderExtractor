using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Structural metadata attached to deterministic downstream consumer records.</summary>
public sealed record StructuralContextItem(
    [property: JsonPropertyName("elementId")] string ElementId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    [property: JsonPropertyName("type")] StructuralElementType Type,
    [property: JsonPropertyName("role")] ProposedRole Role,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("level")] int? Level,
    [property: JsonPropertyName("sources")] IReadOnlyList<SourceReference> Sources);

/// <summary>Retrieval-ready projection. Text is exactly the source-backed chunk text.</summary>
public sealed record RetrievalDocument(
    [property: JsonPropertyName("chunkId")] string ChunkId,
    [property: JsonPropertyName("sectionId")] string SectionId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("sourceIds")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("structuralElementIds")] IReadOnlyList<string> StructuralElementIds,
    [property: JsonPropertyName("sectionPath")] IReadOnlyList<string> SectionPath,
    [property: JsonPropertyName("structuralContext")] IReadOnlyList<StructuralContextItem> StructuralContext,
    [property: JsonPropertyName("relations")] IReadOnlyList<StructuralRelation> Relations);

/// <summary>Stable index document independent of any search or vector database SDK.</summary>
public sealed record SearchIndexDocument(
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("chunkId")] string ChunkId,
    [property: JsonPropertyName("sectionId")] string SectionId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("sourceIds")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("structuralTypes")] IReadOnlyList<string> StructuralTypes,
    [property: JsonPropertyName("sectionPath")] IReadOnlyList<string> SectionPath,
    [property: JsonPropertyName("relations")] IReadOnlyList<StructuralRelation> Relations);

/// <summary>Source-backed input context for a future fact proposal/validation pipeline.</summary>
public sealed record FactExtractionContext(
    [property: JsonPropertyName("sourceText")] string SourceText,
    [property: JsonPropertyName("sectionPath")] IReadOnlyList<string> SectionPath,
    [property: JsonPropertyName("nearbyStructuralElements")] IReadOnlyList<StructuralContextItem> NearbyStructuralElements,
    [property: JsonPropertyName("figureTableContext")] IReadOnlyList<StructuralContextItem> FigureTableContext,
    [property: JsonPropertyName("sourceIds")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("structuralElementIds")] IReadOnlyList<string> StructuralElementIds);
