using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Authority;

/// <summary>Common producer envelope used while DOCX and PDF producers converge on generic authority.</summary>
public sealed record StructuralAuthorityResult(
    ValidatedStructure Structure,
    RouteExecutionAudit? Audit,
    string Reason,
    IReadOnlySet<string>? EmittedElementIds = null);
