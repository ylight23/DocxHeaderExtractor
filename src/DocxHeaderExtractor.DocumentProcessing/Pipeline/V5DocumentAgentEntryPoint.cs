using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Provider-neutral V5 entrypoint. Hosts supply a task contract and a typed semantic reasoner;
/// source adapters only normalize parser observations into the shared evidence graph.
/// </summary>
public sealed class V5DocumentAgentEntryPoint
{
    private readonly IEvidenceRetriever _retriever;
    private readonly EvidencePlanner _planner;
    private readonly IVisualEvidenceReasoner? _visual;

    public V5DocumentAgentEntryPoint(
        IEvidenceRetriever? retriever = null,
        EvidencePlanner? planner = null,
        IVisualEvidenceReasoner? visual = null)
    {
        _retriever = retriever ?? new InMemoryEvidenceRetriever();
        _planner = planner ?? new EvidencePlanner();
        _visual = visual;
    }

    public Task<DocumentAgentExecutionResult> RunAsync(
        DocumentSourceCatalog sourceCatalog,
        DocumentTaskContract contract,
        ISemanticReasoner reasoner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceCatalog);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(reasoner);
        var evidence = V5SourceEvidenceAdapter.Build(sourceCatalog);
        var atoms = sourceCatalog.Units.Select(unit => new SemanticSourceAtom(
            $"S{unit.SourceOrdinal:0000}",
            unit.SourceId,
            unit.SourceOrdinal,
            unit.SourceAnchor.Page ?? 0,
            unit.SourceOrdinal,
            0,
            unit.Text)).ToArray();
        return new DocumentAgentRuntime(reasoner, _retriever, _planner, _visual)
            .RunAsync(contract, evidence, atoms, cancellationToken);
    }
}
