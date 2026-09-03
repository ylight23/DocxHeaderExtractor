using DocxHeaderExtractor.Application.Processing;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing;

/// <summary>
/// Application processing implementation. Hosts inject this surface; it owns no provider or
/// source authority and continues to delegate structural authority to the existing pipeline.
/// </summary>
public sealed class DocumentProcessingService : IDocumentProcessingService
{
    private readonly AuthorityExtractionPipeline _authority;
    private readonly IFactSchemaPackRegistry _schemaPacks;
    private readonly IFactProposalProducer? _factProducer;
    private readonly ISchemaProposalProducer _schemaProducer;

    public DocumentProcessingService(
        AuthorityExtractionPipeline authority,
        IFactSchemaPackRegistry? schemaPacks = null,
        IFactProposalProducer? factProducer = null,
        ISchemaProposalProducer? schemaProducer = null)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _schemaPacks = schemaPacks ?? new InMemoryFactSchemaPackRegistry([]);
        _factProducer = factProducer;
        _schemaProducer = schemaProducer ?? new NoSchemaMatchProposalProducer();
    }

    public async Task<DocumentProcessingResult> ProcessAsync(
        DocumentProcessingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var execution = await _authority.RunDocumentWithCompatibilityAsync(request.InputPath, null, cancellationToken)
            .ConfigureAwait(false);
        var extraction = execution.Result;
        var compatibility = execution.CompatibilityOutline;
        var selection = request.Mode switch
        {
            DocumentProcessingMode.StructureOnly => NoSelection("structure-only"),
            DocumentProcessingMode.ExplicitSchemas => SchemaSelectionValidator.ValidateExplicit(
                request.SchemaKeys, _schemaPacks),
            DocumentProcessingMode.AutoSchemaDiscovery => await new SchemaDiscoveryRuntime(
                    _schemaPacks, _schemaProducer)
                .DiscoverAsync(extraction, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Mode), request.Mode, null),
        };

        if (!selection.Accepted)
            return Result(extraction, compatibility, selection, null);
        if (_factProducer is null)
            throw new InvalidOperationException("fact-proposal-producer-required");

        var facts = await new DocumentFactExtractionRuntime(_factProducer, _schemaPacks)
            .ExtractFactsAsync(
                extraction,
                new DocumentFactExtractionRequest(selection.Selection.SchemaKeys),
                cancellationToken)
            .ConfigureAwait(false);
        return Result(extraction, compatibility, selection, facts);
    }

    public void Dispose() => _authority.Dispose();

    public async Task<DocumentOutline> ProcessStructureOnlyAsync(
        string inputPath,
        IReadOnlySet<int>? quarantinedIndexes = null,
        CancellationToken cancellationToken = default)
    {
        var execution = await _authority.RunDocumentWithCompatibilityAsync(
                inputPath, quarantinedIndexes, cancellationToken)
            .ConfigureAwait(false);
        return execution.CompatibilityOutline;
    }

    private static DocumentProcessingResult Result(
        DocumentExtractionResult extraction,
        DocumentOutline compatibility,
        SchemaSelectionValidationResult selection,
        DocumentFactExtractionResult? facts)
    {
        var schemaSelection = selection.Selection;
        return new DocumentProcessingResult(
            extraction.DocumentIdentity,
            extraction,
            extraction.Structure,
            extraction.Sections,
            extraction.Chunks,
            facts?.Facts ?? [],
            schemaSelection,
            facts?.SchemaResults ?? [],
            compatibility,
            new DocumentProcessingAudit(selection, facts?.Audit));
    }

    private static SchemaSelectionValidationResult NoSelection(string reason) => new(
        new ValidatedSchemaSelection("not-requested", [], "not-requested", reason), []);
}
