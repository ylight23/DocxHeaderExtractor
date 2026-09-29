using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.DocumentProcessing.Authority;

/// <summary>Common producer envelope used while DOCX and PDF producers converge on generic authority.</summary>
public sealed record StructuralAuthorityResult(
    ValidatedStructure Structure,
    RouteExecutionAudit? Audit,
    string Reason,
    IReadOnlySet<string>? EmittedElementIds = null)
{
    /// <summary>Immutable semantic proposals captured before source-aware validation, if enabled.</summary>
    public SemanticAuthorityReplayBundle? ReplayBundle { get; init; }

    /// <summary>Persistence outcome owned by an explicitly configured experiment harness.</summary>
    public SemanticAuthorityReplayPersistenceResult? ReplayPersistence { get; init; }

    /// <summary>
    /// The parser-owned source catalog this producer reasoned over, carried out rather than
    /// reconstructed downstream.
    /// <para>
    /// A consumer must see the same occurrences the model was shown and the binder bound against.
    /// Rebuilding the catalog from the audit looked equivalent and was not: the audit records a
    /// readable rendering for a human, while the model and the binder use the declared projection,
    /// so the two could differ for exactly the occurrences where the difference matters. One parse,
    /// one catalog, carried end to end.
    /// </para>
    /// <para>
    /// Null where the producer has no source occurrences to report - an empty PDF, a failed parse -
    /// which is not the same as a catalog with no units.
    /// </para>
    /// </summary>
    public DocumentSourceCatalog? SourceCatalog { get; init; }
}
