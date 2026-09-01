using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Untrusted fact field proposal. Values are deliberately absent.</summary>
public sealed record ProposedFactField(
    [property: JsonPropertyName("fieldName")] string FieldName,
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("span")] StructuralSpan Span);

/// <summary>Untrusted fact proposal submitted by a rule or model.</summary>
public sealed record FactProposal(
    [property: JsonPropertyName("proposalId")] string ProposalId,
    [property: JsonPropertyName("contextChunkId")] string ContextChunkId,
    [property: JsonPropertyName("schemaKey")] string SchemaKey,
    [property: JsonPropertyName("fields")] IReadOnlyList<ProposedFactField> Fields,
    [property: JsonPropertyName("confidence")] double? Confidence = null);

public sealed record FactFieldSchema(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("allowMultiple")] bool AllowMultiple);

public sealed record FactSchemaDefinition(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("fields")] IReadOnlyList<FactFieldSchema> Fields);

public interface IFactSchemaRegistry
{
    bool TryGet(string schemaKey, out FactSchemaDefinition schema);
}

/// <summary>Deterministic registry for generic schemas; domain plugins can provide their own.</summary>
public sealed class InMemoryFactSchemaRegistry : IFactSchemaRegistry
{
    private readonly IReadOnlyDictionary<string, FactSchemaDefinition> _schemas;

    public InMemoryFactSchemaRegistry(IEnumerable<FactSchemaDefinition> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        var materialized = schemas.ToArray();
        if (materialized.Any(schema => string.IsNullOrWhiteSpace(schema.Key)))
            throw new InvalidOperationException("empty-fact-schema-key");
        if (materialized.Select(schema => schema.Key).Distinct(StringComparer.Ordinal).Count() != materialized.Length)
            throw new InvalidOperationException("duplicate-fact-schema-key");
        _schemas = materialized.ToDictionary(schema => schema.Key, StringComparer.Ordinal);
    }

    public bool TryGet(string schemaKey, out FactSchemaDefinition schema) =>
        _schemas.TryGetValue(schemaKey, out schema!);
}

/// <summary>Value materialized from a canonical source slice before semantic authority.</summary>
public sealed record ValidatedFactField(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("source")] SourceReference Source);

public sealed record FactSemanticContext(
    FactProposal Proposal,
    FactSchemaDefinition Schema,
    IReadOnlyList<ValidatedFactField> Fields);

public sealed record FactSemanticDecision(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("basis")] string Basis,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason);

public interface IFactSemanticAuthority
{
    FactSemanticDecision Validate(FactSemanticContext context);
}

public sealed record FactValidation(
    [property: JsonPropertyName("contextGrounded")] bool ContextGrounded,
    [property: JsonPropertyName("sourceGrounded")] bool SourceGrounded,
    [property: JsonPropertyName("spanGrounded")] bool SpanGrounded,
    [property: JsonPropertyName("semanticAccepted")] bool SemanticAccepted);

public sealed record FactAuthority(
    [property: JsonPropertyName("basis")] string Basis,
    [property: JsonPropertyName("confidence")] double? Confidence);

public sealed record ValidatedFact(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("schemaKey")] string SchemaKey,
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("chunkId")] string ChunkId,
    [property: JsonPropertyName("sectionId")] string SectionId,
    [property: JsonPropertyName("fields")] IReadOnlyList<ValidatedFactField> Fields,
    [property: JsonPropertyName("contextElementIds")] IReadOnlyList<string> ContextElementIds,
    [property: JsonPropertyName("validation")] FactValidation Validation,
    [property: JsonPropertyName("authority")] FactAuthority Authority);

public sealed record RejectedFactProposal(
    [property: JsonPropertyName("proposalId")] string ProposalId,
    [property: JsonPropertyName("schemaKey")] string SchemaKey,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record FactAuthorityResult(
    [property: JsonPropertyName("validatedFacts")] IReadOnlyList<ValidatedFact> ValidatedFacts,
    [property: JsonPropertyName("rejections")] IReadOnlyList<RejectedFactProposal> Rejections);

public sealed record FactProposalValidationOutcome(
    ValidatedFact? Fact,
    RejectedFactProposal? Rejection);
