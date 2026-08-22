using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Normalized output from one deterministic outline adapter. Adapters may use DOCX, PDF, or a
/// supplied sidecar; the common policy only needs their route, priority, headings, and audit.
/// </summary>
internal sealed record OutlineEvidenceAdapterResult(
    string Route,
    int Priority,
    IReadOnlyList<HeadingRecord> Headings,
    string Reason,
    RouteExecutionAudit? Audit = null)
{
    public bool HasHeadings => Headings.Count > 0;
}

/// <summary>
/// Chooses one declared evidence source without making later adapters depend on the output count
/// of earlier adapters. Priority is policy, not an extractor-side fallback chain.
/// </summary>
internal static class OutlineEvidenceAdapterRegistry
{
    public static OutlineEvidenceAdapterResult? Select(IEnumerable<OutlineEvidenceAdapterResult> results) =>
        results
            .Where(result => result.HasHeadings)
            .OrderBy(result => result.Priority)
            .ThenBy(result => result.Route, StringComparer.Ordinal)
            .FirstOrDefault();
}
