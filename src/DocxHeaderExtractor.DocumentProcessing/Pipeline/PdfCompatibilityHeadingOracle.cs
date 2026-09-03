using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Legacy heading-shaped result retained only for unregistered PDF probes and compatibility
/// diagnostics. Normal PDF authority uses <see cref="PdfTextbookOutlineResult"/> instead.
/// </summary>
public sealed record PdfCompatibilityHeadingOracle(
    IReadOnlyList<HeadingRecord> Headings,
    string Reason,
    RouteExecutionAudit? Audit = null);
