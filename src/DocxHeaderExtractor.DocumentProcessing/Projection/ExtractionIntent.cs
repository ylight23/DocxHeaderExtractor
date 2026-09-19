using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Projection;

/// <summary>What the user wants back. Kept small on purpose; it grows by projection, not by task.</summary>
public enum ExtractionTask
{
    DocumentStructure,
    Headings,
    CustomProjection,
}

public enum OutputFormat
{
    Json,
    Xlsx,
    Docx,
}

/// <summary>
/// The user's question, asked of a canonical document that already exists.
/// <para>
/// This runs strictly after the semantic boundary, and that ordering is the point. It may not
/// influence candidate selection, segmentation, isHeading, the source occurrence universe, the
/// model route, the binder, the canonical hierarchy, or which format holds authority. Asking for
/// level-1 headings must not send the model looking only for level-1 headings; the canonical graph
/// holds every real heading and the projection filters it. Otherwise the same file would mean
/// different things depending on what was asked of it, and no measurement across two questions
/// would be comparable.
/// </para>
/// </summary>
public sealed record ExtractionIntent
{
    public required ExtractionTask Task { get; init; }

    /// <summary>What the user said, kept verbatim for a custom projection to interpret.</summary>
    public string? UserInstruction { get; init; }

    /// <summary>Fields of the canonical record to emit. Empty means the task's default set.</summary>
    public IReadOnlyList<string> RequestedFields { get; init; } = [];

    public OutputFormat OutputFormat { get; init; } = OutputFormat.Json;
}

/// <summary>A canonical document plus one question about it.</summary>
public sealed record ProjectionRequest(DocumentExtractionResult Document, ExtractionIntent Intent);
