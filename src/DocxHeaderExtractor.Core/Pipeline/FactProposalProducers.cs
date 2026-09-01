using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

public static class FactProposalModelRequestBuilder
{
    public static FactProposalModelRequest Build(FactProposalProductionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new FactProposalModelRequest(
            request.RequestId,
            request.Context.ChunkId!,
            request.Schema,
            request.Context.SourceUnits);
    }
}

/// <summary>Strict parser for the closed model response contract.</summary>
public static class FactProposalModelResponseParser
{
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static IReadOnlyList<FactProposal> Parse(
        string rawJson,
        FactProposalModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(rawJson);
        ArgumentNullException.ThrowIfNull(request);

        WireResponse response;
        try
        {
            response = JsonSerializer.Deserialize<WireResponse>(rawJson, StrictJson)
                ?? throw new FactProposalModelResponseException("model-response-empty");
        }
        catch (FactProposalModelResponseException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new FactProposalModelResponseException("model-response-malformed-json:" + exception.Message);
        }

        if (response.Proposals is null)
            throw new FactProposalModelResponseException("model-response-missing-proposals");

        return response.Proposals.Select(proposal => ToProposal(proposal, request)).ToArray();
    }

    private static FactProposal ToProposal(WireProposal? proposal, FactProposalModelRequest request)
    {
        if (proposal is null)
            throw new FactProposalModelResponseException("model-response-null-proposal");
        if (string.IsNullOrWhiteSpace(proposal.ProposalId))
            throw new FactProposalModelResponseException("model-response-missing-proposal-id");
        if (!string.Equals(proposal.ContextChunkId, request.ContextChunkId, StringComparison.Ordinal))
            throw new FactProposalModelResponseException("model-response-context-mismatch");
        if (!string.Equals(proposal.SchemaKey, request.Schema.Key, StringComparison.Ordinal))
            throw new FactProposalModelResponseException("model-response-schema-mismatch");
        if (proposal.Fields is null)
            throw new FactProposalModelResponseException("model-response-missing-fields");

        var fields = proposal.Fields.Select(field =>
        {
            if (field is null || string.IsNullOrWhiteSpace(field.FieldName))
                throw new FactProposalModelResponseException("model-response-missing-field-name");
            if (string.IsNullOrWhiteSpace(field.SourceId))
                throw new FactProposalModelResponseException("model-response-missing-source");
            if (field.Span?.Start is null || field.Span.End is null)
                throw new FactProposalModelResponseException("model-response-missing-span");
            return new ProposedFactField(
                field.FieldName,
                field.SourceId,
                new StructuralSpan(field.Span.Start.Value, field.Span.End.Value));
        }).ToArray();

        return new FactProposal(
            proposal.ProposalId,
            proposal.ContextChunkId!,
            proposal.SchemaKey!,
            fields,
            proposal.Confidence);
    }

    private sealed class WireResponse
    {
        [JsonPropertyName("proposals")]
        public List<WireProposal?>? Proposals { get; init; }
    }

    private sealed class WireProposal
    {
        [JsonPropertyName("proposalId")]
        public string? ProposalId { get; init; }

        [JsonPropertyName("contextChunkId")]
        public string? ContextChunkId { get; init; }

        [JsonPropertyName("schemaKey")]
        public string? SchemaKey { get; init; }

        [JsonPropertyName("fields")]
        public List<WireField?>? Fields { get; init; }

        [JsonPropertyName("confidence")]
        public double? Confidence { get; init; }
    }

    private sealed class WireField
    {
        [JsonPropertyName("fieldName")]
        public string? FieldName { get; init; }

        [JsonPropertyName("sourceId")]
        public string? SourceId { get; init; }

        [JsonPropertyName("span")]
        public WireSpan? Span { get; init; }
    }

    private sealed class WireSpan
    {
        [JsonPropertyName("start")]
        public int? Start { get; init; }

        [JsonPropertyName("end")]
        public int? End { get; init; }
    }
}

public sealed class RuleFactProposalProducer : IFactProposalProducer
{
    private readonly IFactProposalRule _rule;

    public RuleFactProposalProducer(IFactProposalRule rule)
    {
        _rule = rule ?? throw new ArgumentNullException(nameof(rule));
    }

    public Task<FactProposalProductionResult> ProduceAsync(
        FactProposalProductionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var proposals = _rule.Propose(request) ?? [];
            var produced = proposals.Select(proposal =>
            {
                if (!string.Equals(proposal.SchemaKey, request.Schema.Key, StringComparison.Ordinal))
                    throw new FactProposalModelResponseException("rule-schema-mismatch");
                if (!string.Equals(proposal.ContextChunkId, request.Context.ChunkId, StringComparison.Ordinal))
                    throw new FactProposalModelResponseException("rule-context-mismatch");
                return new ProducedFactProposal(
                    proposal,
                    new FactProposalProvenance(_rule.RuleId, "rule", request.RequestId));
            }).ToArray();
            return Task.FromResult(new FactProposalProductionResult(produced, []));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Task.FromResult(Failure(request, _rule.RuleId, "rule-failure:" + exception.Message));
        }
    }

    private static FactProposalProductionResult Failure(
        FactProposalProductionRequest request,
        string producerId,
        string reason) => new(
        [],
        [new FactProducerFailure(producerId, request.Context.ChunkId!, request.Schema.Key, reason)]);
}

public sealed class FactProposalModelProducer : IFactProposalProducer
{
    private readonly string _producerId;
    private readonly IFactProposalModel _model;

    public FactProposalModelProducer(string producerId, IFactProposalModel model)
    {
        _producerId = string.IsNullOrWhiteSpace(producerId)
            ? throw new ArgumentException("Producer ID is required.", nameof(producerId))
            : producerId;
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public async Task<FactProposalProductionResult> ProduceAsync(
        FactProposalProductionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var modelRequest = FactProposalModelRequestBuilder.Build(request);
            var rawJson = await _model.CompleteAsync(modelRequest, cancellationToken).ConfigureAwait(false);
            var proposals = FactProposalModelResponseParser.Parse(rawJson, modelRequest)
                .Select(proposal => new ProducedFactProposal(
                    proposal,
                    new FactProposalProvenance(
                        _producerId,
                        "model",
                        request.RequestId,
                        StableHash(rawJson))))
                .ToArray();
            return new FactProposalProductionResult(proposals, []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new FactProposalProductionResult(
                [],
                [new FactProducerFailure(
                    _producerId,
                    request.Context.ChunkId!,
                    request.Schema.Key,
                    "model-failure:" + exception.Message)]);
        }
    }

    private static string StableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class CompositeFactProposalProducer : IFactProposalProducer
{
    private readonly IReadOnlyList<IFactProposalProducer> _producers;

    public CompositeFactProposalProducer(IEnumerable<IFactProposalProducer> producers)
    {
        ArgumentNullException.ThrowIfNull(producers);
        _producers = producers.ToArray();
    }

    public async Task<FactProposalProductionResult> ProduceAsync(
        FactProposalProductionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var produced = new List<ProducedFactProposal>();
        var failures = new List<FactProducerFailure>();
        foreach (var producer in _producers)
        {
            try
            {
                var result = await producer.ProduceAsync(request, cancellationToken).ConfigureAwait(false);
                produced.AddRange(result.Proposals);
                failures.AddRange(result.Failures);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add(new FactProducerFailure(
                    producer.GetType().Name,
                    request.Context.ChunkId!,
                    request.Schema.Key,
                    "producer-failure:" + exception.Message));
            }
        }

        var unique = produced
            .GroupBy(item => ProposalSignature(item.Proposal), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return new FactProposalProductionResult(unique, failures);
    }

    private static string ProposalSignature(FactProposal proposal) => string.Join(
        "|",
        new[] { proposal.ContextChunkId, proposal.SchemaKey }
            .Concat(proposal.Fields
                .OrderBy(field => field.FieldName, StringComparer.Ordinal)
                .ThenBy(field => field.SourceId, StringComparer.Ordinal)
                .ThenBy(field => field.Span.Start)
                .ThenBy(field => field.Span.End)
                .Select(field => string.Join(":", field.FieldName, field.SourceId, field.Span.Start, field.Span.End))));
}

/// <summary>Frozen response model for deterministic producer replay; it performs no provider calls.</summary>
public sealed class ReplayFactProposalModel : IFactProposalModel
{
    private readonly IReadOnlyDictionary<string, string> _responses;

    public ReplayFactProposalModel(IReadOnlyDictionary<string, string> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        _responses = new Dictionary<string, string>(responses, StringComparer.Ordinal);
    }

    public int ProviderCalls => 0;

    public Task<string> CompleteAsync(
        FactProposalModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_responses.TryGetValue(request.RequestId, out var response))
            throw new InvalidOperationException("replay-request-not-found");
        return Task.FromResult(response);
    }
}
