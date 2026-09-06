using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.AgentHarness;

/// <summary>
/// Support is a separate runtime fact from extraction completion. Unknown mode is not an error,
/// but it must not be presented as a supported high-confidence empty result.
/// </summary>
public sealed record DocumentSupportStatus(
    string ExtractionStatus,
    string ReliabilityStatus)
{
    public const string Completed = "COMPLETED";
    public const string CompletedWithoutSupportedRoute = "COMPLETED_WITHOUT_SUPPORTED_ROUTE";
    public const string SupportNotProven = "SUPPORT_NOT_PROVEN";
    public const string SupportEstablished = "SUPPORT_ESTABLISHED";

    public static DocumentSupportStatus From(DocumentOutline outline)
    {
        ArgumentNullException.ThrowIfNull(outline);
        var supportedRoute = !string.IsNullOrWhiteSpace(outline.DeterministicRoute) ||
                             outline.RouteAudit is not null ||
                             outline.Outcome?.EvidenceRoute is not null;
        var supportNotProven = outline.DocumentMode?.Mode == DocumentMode.Unknown || !supportedRoute;
        var emptyWithoutRoute = outline.ParagraphCount > 0 && outline.Headings.Count == 0 && !supportedRoute;
        return new(
            emptyWithoutRoute ? CompletedWithoutSupportedRoute : Completed,
            supportNotProven ? SupportNotProven : SupportEstablished);
    }
}
