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
    [property: JsonPropertyName("relations")] IReadOnlyList<StructuralRelation> Relations,
    [property: JsonPropertyName("structuralContext")] IReadOnlyList<StructuralContextItem> StructuralContext);

/// <summary>Deterministic retrieval query. Filters constrain indexed metadata, not authority.</summary>
public sealed record RetrievalQuery
{
    public RetrievalQuery(
        string queryText,
        int topK = 10,
        IReadOnlyList<string>? documentIds = null,
        IReadOnlyList<string>? sectionIds = null,
        IReadOnlyList<StructuralElementType>? structuralTypes = null)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            throw new ArgumentException("Query text is required.", nameof(queryText));
        if (topK <= 0)
            throw new ArgumentOutOfRangeException(nameof(topK), topK, "TopK must be positive.");

        QueryText = queryText;
        TopK = topK;
        DocumentIds = documentIds?.ToArray();
        SectionIds = sectionIds?.ToArray();
        StructuralTypes = structuralTypes?.ToArray();
    }

    public string QueryText { get; }
    public int TopK { get; }
    public IReadOnlyList<string>? DocumentIds { get; }
    public IReadOnlyList<string>? SectionIds { get; }
    public IReadOnlyList<StructuralElementType>? StructuralTypes { get; }
}

/// <summary>Retrieval result; Score ranks text matches and is never structural authority.</summary>
public sealed record RetrievalHit(
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("chunkId")] string ChunkId,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("sourceIds")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("sectionPath")] IReadOnlyList<string> SectionPath,
    [property: JsonPropertyName("structuralContext")] IReadOnlyList<StructuralContextItem> StructuralContext,
    [property: JsonPropertyName("relations")] IReadOnlyList<StructuralRelation> Relations);

/// <summary>Canonical source excerpt metadata exposed to the fact proposal boundary.</summary>
public sealed record FactSourceExcerpt(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("text")] string Text);

/// <summary>Source-backed input context for a fact proposal/validation pipeline.</summary>
public sealed record FactExtractionContext
{
    // Compatibility constructor for existing IE consumers that do not yet carry document IDs.
    public FactExtractionContext(
        string sourceText,
        IReadOnlyList<string> sectionPath,
        IReadOnlyList<StructuralContextItem> nearbyStructuralElements,
        IReadOnlyList<StructuralContextItem> figureTableContext,
        IReadOnlyList<string> sourceIds,
        IReadOnlyList<string> structuralElementIds)
        : this(
            null,
            null,
            null,
            sourceText,
            sectionPath,
            nearbyStructuralElements,
            figureTableContext,
            sourceIds,
            structuralElementIds,
            [])
    {
    }

    public FactExtractionContext(
        string? documentId,
        string? chunkId,
        string? sectionId,
        string sourceText,
        IReadOnlyList<string> sectionPath,
        IReadOnlyList<StructuralContextItem> nearbyStructuralElements,
        IReadOnlyList<StructuralContextItem> figureTableContext,
        IReadOnlyList<string> sourceIds,
        IReadOnlyList<string> structuralElementIds,
        IReadOnlyList<FactSourceExcerpt> sourceUnits)
    {
        DocumentId = documentId;
        ChunkId = chunkId;
        SectionId = sectionId;
        SourceText = sourceText;
        SectionPath = sectionPath;
        NearbyStructuralElements = nearbyStructuralElements;
        FigureTableContext = figureTableContext;
        SourceIds = sourceIds;
        StructuralElementIds = structuralElementIds;
        SourceUnits = sourceUnits;
    }

    [JsonPropertyName("documentId")]
    public string? DocumentId { get; }

    [JsonPropertyName("chunkId")]
    public string? ChunkId { get; }

    [JsonPropertyName("sectionId")]
    public string? SectionId { get; }

    [JsonPropertyName("sourceText")]
    public string SourceText { get; }

    [JsonPropertyName("sectionPath")]
    public IReadOnlyList<string> SectionPath { get; }

    [JsonPropertyName("nearbyStructuralElements")]
    public IReadOnlyList<StructuralContextItem> NearbyStructuralElements { get; }

    [JsonPropertyName("figureTableContext")]
    public IReadOnlyList<StructuralContextItem> FigureTableContext { get; }

    [JsonPropertyName("sourceIds")]
    public IReadOnlyList<string> SourceIds { get; }

    [JsonPropertyName("structuralElementIds")]
    public IReadOnlyList<string> StructuralElementIds { get; }

    [JsonPropertyName("sourceUnits")]
    public IReadOnlyList<FactSourceExcerpt> SourceUnits { get; }
}
