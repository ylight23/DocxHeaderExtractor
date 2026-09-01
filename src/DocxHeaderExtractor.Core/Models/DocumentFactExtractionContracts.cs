using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Application request that names schema packs, never proposals or authorities.</summary>
public sealed record DocumentFactExtractionRequest
{
    public DocumentFactExtractionRequest(IEnumerable<string> schemaKeys)
    {
        ArgumentNullException.ThrowIfNull(schemaKeys);
        var normalized = schemaKeys
            .Select(key => key?.Trim() ?? string.Empty)
            .ToArray();
        if (normalized.Length == 0 || normalized.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one non-empty schema key is required.", nameof(schemaKeys));

        SchemaKeys = normalized
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
    }

    [JsonPropertyName("schemaKeys")]
    public IReadOnlyList<string> SchemaKeys { get; }
}

public sealed record FactSchemaExtractionResult(
    [property: JsonPropertyName("schemaKey")] string SchemaKey,
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("validatedFacts")] IReadOnlyList<ValidatedFact> ValidatedFacts,
    [property: JsonPropertyName("rejections")] IReadOnlyList<RejectedFactProposal> Rejections,
    [property: JsonPropertyName("producerFailures")] IReadOnlyList<FactProducerFailure> ProducerFailures);

/// <summary>Audit-only view of untrusted proposals and failures; it is never the public fact list.</summary>
public sealed record FactExtractionAudit(
    [property: JsonPropertyName("producedProposals")] IReadOnlyList<ProducedFactProposal> ProducedProposals,
    [property: JsonPropertyName("rejections")] IReadOnlyList<RejectedFactProposal> Rejections,
    [property: JsonPropertyName("producerFailures")] IReadOnlyList<FactProducerFailure> ProducerFailures);

/// <summary>Application fact result. Facts are validated values only.</summary>
public sealed record DocumentFactExtractionResult(
    [property: JsonPropertyName("documentIdentity")] DocumentIdentity DocumentIdentity,
    [property: JsonPropertyName("facts")] IReadOnlyList<ValidatedFact> Facts,
    [property: JsonPropertyName("schemaResults")] IReadOnlyList<FactSchemaExtractionResult> SchemaResults,
    [property: JsonPropertyName("audit")] FactExtractionAudit Audit);

public interface IDocumentFactExtractionService
{
    Task<DocumentFactExtractionResult> ExtractFactsAsync(
        DocumentExtractionResult extraction,
        DocumentFactExtractionRequest request,
        CancellationToken cancellationToken = default);
}
