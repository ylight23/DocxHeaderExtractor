using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

public sealed record FactProductionAuthorityResult(
    IReadOnlyList<ProducedFactProposal> ProducedProposals,
    FactAuthorityResult AuthorityResult,
    IReadOnlyList<FactProducerFailure> ProducerFailures);

/// <summary>Runs proposal producers over source-backed contexts, then delegates authority to Round 7.</summary>
public sealed class FactProposalProductionRuntime
{
    private readonly IFactProposalProducer _producer;
    private readonly FactAuthorityRuntime _authority;

    public FactProposalProductionRuntime(
        IFactProposalProducer producer,
        FactAuthorityRuntime authority)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    public async Task<FactProductionAuthorityResult> EvaluateAsync(
        DocumentExtractionResult extraction,
        IReadOnlyList<FactSchemaDefinition> selectedSchemas,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(selectedSchemas);
        var contexts = IEContextProjection.Project(extraction);
        var produced = new List<ProducedFactProposal>();
        var failures = new List<FactProducerFailure>();
        foreach (var context in contexts)
        {
            foreach (var schema in selectedSchemas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new FactProposalProductionRequest(context, schema);
                try
                {
                    var result = await _producer.ProduceAsync(request, cancellationToken).ConfigureAwait(false);
                    produced.AddRange(result.Proposals);
                    failures.AddRange(result.Failures);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures.Add(new FactProducerFailure(
                        _producer.GetType().Name,
                        context.ChunkId!,
                        schema.Key,
                        "producer-failure:" + exception.Message));
                }
            }
        }

        var authority = _authority.Evaluate(extraction, produced.Select(item => item.Proposal));
        return new FactProductionAuthorityResult(produced, authority, failures);
    }
}
