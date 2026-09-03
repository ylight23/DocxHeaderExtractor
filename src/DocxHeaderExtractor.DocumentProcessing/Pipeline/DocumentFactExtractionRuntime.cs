using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Runs multiple schema packs over one already parsed document extraction. Parsing and IE context
/// projection happen once; each pack gets its own semantic authority through the routed boundary.
/// </summary>
public sealed class DocumentFactExtractionRuntime : IDocumentFactExtractionService
{
    private readonly IFactProposalProducer _producer;
    private readonly IFactSchemaPackRegistry _packs;
    private readonly FactProposalValidator _validator;

    public DocumentFactExtractionRuntime(
        IFactProposalProducer producer,
        IFactSchemaPackRegistry packs,
        FactProposalValidator? validator = null)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _packs = packs ?? throw new ArgumentNullException(nameof(packs));
        _validator = validator ?? new FactProposalValidator();
    }

    public async Task<DocumentFactExtractionResult> ExtractFactsAsync(
        DocumentExtractionResult extraction,
        DocumentFactExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(request);

        var selectedPacks = request.SchemaKeys
            .Select(key => ResolvePack(key))
            .ToArray();
        var contexts = IEContextProjection.Project(extraction);
        var routedAuthority = new SchemaRoutedFactSemanticAuthority(_packs);
        var schemaResults = new List<FactSchemaExtractionResult>(selectedPacks.Length);
        var allProduced = new List<ProducedFactProposal>();

        foreach (var pack in selectedPacks)
        {
            var produced = new List<ProducedFactProposal>();
            var failures = new List<FactProducerFailure>();
            foreach (var context in contexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var productionRequest = new FactProposalProductionRequest(context, pack.Schema);
                try
                {
                    var result = await _producer.ProduceAsync(productionRequest, cancellationToken)
                        .ConfigureAwait(false);
                    produced.AddRange(result.Proposals);
                    failures.AddRange(result.Failures);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures.Add(new FactProducerFailure(
                        _producer.GetType().Name,
                        context.ChunkId!,
                        pack.Key,
                        "producer-failure:" + exception.Message));
                }
            }

            var authority = new FactAuthorityRuntime(
                new InMemoryFactSchemaRegistry([pack.Schema]),
                routedAuthority,
                _validator);
            var authorityResult = authority.Evaluate(
                extraction,
                produced.Select(item => item.Proposal));
            var normalizedFacts = authorityResult.ValidatedFacts
                .Select(NormalizeFact)
                .OrderBy(fact => fact.Id, StringComparer.Ordinal)
                .ToArray();
            var normalizedRejections = authorityResult.Rejections
                .OrderBy(rejection => rejection.ProposalId, StringComparer.Ordinal)
                .ThenBy(rejection => rejection.Reason, StringComparer.Ordinal)
                .ToArray();
            var normalizedFailures = failures
                .OrderBy(failure => failure.ContextChunkId, StringComparer.Ordinal)
                .ThenBy(failure => failure.ProducerId, StringComparer.Ordinal)
                .ThenBy(failure => failure.Reason, StringComparer.Ordinal)
                .ToArray();

            allProduced.AddRange(produced);
            schemaResults.Add(new FactSchemaExtractionResult(
                pack.Key,
                pack.Version,
                normalizedFacts,
                normalizedRejections,
                normalizedFailures));
        }

        schemaResults = schemaResults
            .OrderBy(result => result.SchemaKey, StringComparer.Ordinal)
            .ToList();
        var facts = schemaResults
            .SelectMany(result => result.ValidatedFacts)
            .OrderBy(fact => fact.SchemaKey, StringComparer.Ordinal)
            .ThenBy(fact => fact.Id, StringComparer.Ordinal)
            .ToArray();
        var allRejections = schemaResults
            .SelectMany(result => result.Rejections)
            .OrderBy(rejection => rejection.SchemaKey, StringComparer.Ordinal)
            .ThenBy(rejection => rejection.ProposalId, StringComparer.Ordinal)
            .ToArray();
        var allFailures = schemaResults
            .SelectMany(result => result.ProducerFailures)
            .OrderBy(failure => failure.SchemaKey, StringComparer.Ordinal)
            .ThenBy(failure => failure.ContextChunkId, StringComparer.Ordinal)
            .ToArray();

        return new DocumentFactExtractionResult(
            extraction.DocumentIdentity,
            facts,
            schemaResults,
            new FactExtractionAudit(
                allProduced
                    .OrderBy(item => item.Proposal.SchemaKey, StringComparer.Ordinal)
                    .ThenBy(item => item.Proposal.ProposalId, StringComparer.Ordinal)
                    .ToArray(),
                allRejections,
                allFailures));
    }

    private IFactSchemaPack ResolvePack(string key) =>
        _packs.TryGet(key, out var pack)
            ? pack
            : throw new InvalidOperationException("fact-schema-pack-missing:" + key);

    private static ValidatedFact NormalizeFact(ValidatedFact fact) => fact with
    {
        Fields = fact.Fields
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ThenBy(field => field.Source.SourceOrdinal)
            .ThenBy(field => field.Source.Span.Start)
            .ThenBy(field => field.Source.Span.End)
            .ToArray(),
    };
}
