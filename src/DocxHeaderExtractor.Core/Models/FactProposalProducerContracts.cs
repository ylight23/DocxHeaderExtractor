using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Input explicitly scopes a producer to one context and one registered schema.</summary>
public sealed record FactProposalProductionRequest
{
    public FactProposalProductionRequest(FactExtractionContext context, FactSchemaDefinition schema)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        if (string.IsNullOrWhiteSpace(context.DocumentId) || string.IsNullOrWhiteSpace(context.ChunkId))
            throw new InvalidOperationException("fact-production-context-identity-missing");
        if (context.SourceUnits.Count == 0)
            throw new InvalidOperationException("fact-production-context-sources-missing");
        RequestId = FactProposalRequestIdentity.Create(context, schema);
    }

    [JsonIgnore]
    public FactExtractionContext Context { get; }

    [JsonIgnore]
    public FactSchemaDefinition Schema { get; }

    public string RequestId { get; }
}

/// <summary>Closed request sent to a fact proposal model. It contains source text, not authority.</summary>
public sealed record FactProposalModelRequest(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("contextChunkId")] string ContextChunkId,
    [property: JsonPropertyName("schema")] FactSchemaDefinition Schema,
    [property: JsonPropertyName("sources")] IReadOnlyList<FactSourceExcerpt> Sources)
{
    [JsonPropertyName("offsetSources")]
    public IReadOnlyList<FactProposalOffsetSource> OffsetSources { get; init; } = [];
}

public sealed record FactProposalProvenance(
    [property: JsonPropertyName("producerId")] string ProducerId,
    [property: JsonPropertyName("producerKind")] string ProducerKind,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("responseHash")] string? ResponseHash = null);

public sealed record ProducedFactProposal(
    [property: JsonPropertyName("proposal")] FactProposal Proposal,
    [property: JsonPropertyName("provenance")] FactProposalProvenance Provenance);

public sealed record FactProducerFailure(
    [property: JsonPropertyName("producerId")] string ProducerId,
    [property: JsonPropertyName("contextChunkId")] string ContextChunkId,
    [property: JsonPropertyName("schemaKey")] string SchemaKey,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record FactProposalProductionResult(
    [property: JsonPropertyName("proposals")] IReadOnlyList<ProducedFactProposal> Proposals,
    [property: JsonPropertyName("failures")] IReadOnlyList<FactProducerFailure> Failures);

public interface IFactProposalProducer
{
    Task<FactProposalProductionResult> ProduceAsync(
        FactProposalProductionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IFactProposalModel
{
    Task<string> CompleteAsync(
        FactProposalModelRequest request,
        CancellationToken cancellationToken = default);
}

public interface IFactProposalRule
{
    string RuleId { get; }

    IReadOnlyList<FactProposal> Propose(FactProposalProductionRequest request);
}

public sealed class FactProposalModelResponseException : Exception
{
    public FactProposalModelResponseException(string reason) : base(reason)
    {
    }
}

internal static class FactProposalRequestIdentity
{
    public static string Create(FactExtractionContext context, FactSchemaDefinition schema)
    {
        var sourceIdentity = string.Join(
            "|",
            context.SourceUnits
                .OrderBy(source => source.SourceId, StringComparer.Ordinal)
                .Select(source => string.Join(":", source.SourceId, source.Text)));
        return "request:" + StableHash(string.Join(
            "|",
            context.DocumentId,
            context.ChunkId,
            schema.Key,
            sourceIdentity));
    }

    public static string StableHash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
