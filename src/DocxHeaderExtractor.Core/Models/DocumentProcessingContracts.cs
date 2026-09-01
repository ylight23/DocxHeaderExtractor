using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

public enum DocumentProcessingMode
{
    StructureOnly,
    ExplicitSchemas,
    AutoSchemaDiscovery,
}

/// <summary>Application request. Callers select a mode and schema keys, never authorities.</summary>
public sealed record DocumentProcessingRequest
{
    public DocumentProcessingRequest(
        string inputPath,
        DocumentProcessingMode mode,
        IEnumerable<string>? schemaKeys = null)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("Input path is required.", nameof(inputPath));
        if (mode == DocumentProcessingMode.ExplicitSchemas && schemaKeys is null)
            throw new ArgumentException("Explicit schema mode requires schema keys.", nameof(schemaKeys));
        var keys = schemaKeys?.ToArray() ?? [];
        if (mode != DocumentProcessingMode.ExplicitSchemas && keys.Length > 0)
            throw new ArgumentException("Schema keys are valid only for explicit schema mode.", nameof(schemaKeys));

        InputPath = inputPath;
        Mode = mode;
        SchemaKeys = keys;
    }

    [JsonPropertyName("inputPath")]
    public string InputPath { get; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonPropertyName("mode")]
    public DocumentProcessingMode Mode { get; }

    [JsonPropertyName("schemaKeys")]
    public IReadOnlyList<string> SchemaKeys { get; }
}

public sealed record DocumentProcessingAudit(
    [property: JsonPropertyName("schemaSelection")] SchemaSelectionValidationResult SchemaSelection,
    [property: JsonPropertyName("factAudit")] FactExtractionAudit? FactAudit);

/// <summary>
/// Unified application result. The generic extraction and validated facts are primary; heading
/// output is retained as an explicit compatibility projection only.
/// </summary>
public sealed record DocumentProcessingResult(
    [property: JsonPropertyName("documentIdentity")] DocumentIdentity DocumentIdentity,
    [property: JsonPropertyName("extraction")] DocumentExtractionResult Extraction,
    [property: JsonPropertyName("validatedStructure")] ValidatedStructure ValidatedStructure,
    [property: JsonPropertyName("sections")] IReadOnlyList<StructuralSection> Sections,
    [property: JsonPropertyName("chunks")] IReadOnlyList<DocumentChunk> Chunks,
    [property: JsonPropertyName("validatedFacts")] IReadOnlyList<ValidatedFact> ValidatedFacts,
    [property: JsonPropertyName("schemaSelection")] ValidatedSchemaSelection SchemaSelection,
    [property: JsonPropertyName("schemaResults")] IReadOnlyList<FactSchemaExtractionResult> SchemaResults,
    [property: JsonPropertyName("compatibilityOutline")] DocumentOutline CompatibilityOutline,
    [property: JsonPropertyName("audit")] DocumentProcessingAudit Audit);

public interface IDocumentProcessingService : IDisposable
{
    Task<DocumentProcessingResult> ProcessAsync(
        DocumentProcessingRequest request,
        CancellationToken cancellationToken = default);
}
