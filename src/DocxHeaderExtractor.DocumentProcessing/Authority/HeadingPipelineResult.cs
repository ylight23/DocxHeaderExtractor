using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.DocumentProcessing.Authority;

/// <summary>
/// Runtime result of the DOCX/PDF heading pipeline. Only Structure is structural authority;
/// projection context, source catalog, audit, emitted IDs and reason are execution/output data.
/// This record carries those separate responsibilities without granting them graph authority.
/// </summary>
public sealed record HeadingPipelineResult(
    ValidatedStructure Structure,
    PipelineExecutionAudit? Audit,
    string Reason,
    IReadOnlySet<string>? EmittedElementIds = null)
{
    /// <summary>Runtime output compatibility data; never part of graph authority or serialized audit.</summary>
    [JsonIgnore]
    public HeadingProjectionContext ProjectionContext { get; init; } = HeadingProjectionContext.Empty;

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
