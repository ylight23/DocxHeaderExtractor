using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Compatibility projection from generic structural authority to the existing heading output.
/// It performs no candidate selection, matching, validation, or hierarchy inference.
/// </summary>
public static class HeadingOutlineProjection
{
    public static IReadOnlyList<HeadingRecord> Project(ValidatedStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        return structure.Headings
            .OrderBy(element => element.Source.SourceOrdinal)
            .Select(ProjectHeading)
            .ToArray();
    }

    private static HeadingRecord ProjectHeading(ValidatedStructuralElement element)
    {
        if (element.Level is null)
            throw new InvalidOperationException($"Heading '{element.Id}' has no validated level.");

        return new HeadingRecord
        {
            Index = element.Source.SourceOrdinal,
            StableId = element.Source.SourceId,
            SourceId = element.Source.SourceId,
            Level = element.Level,
            Text = element.Text,
            HeadingSpan = new TextOffsetSpan(element.Source.Span.Start, element.Source.Span.End),
            Source = ParseSource(element.Decision.Origin),
            Confidence = element.Decision.Confidence,
            DecisionStatus = ParseStatus(element.Decision.Status),
            ConfidenceBasis = element.Decision.ConfidenceBasis,
            Disputed = element.Decision.Disputed,
        };
    }

    private static HeadingSource ParseSource(string value) => value.ToLowerInvariant() switch
    {
        "style" => HeadingSource.Style,
        "model" => HeadingSource.Model,
        "structure" => HeadingSource.Structure,
        "human" or "humancorrection" => HeadingSource.HumanCorrection,
        _ => HeadingSource.Heuristic,
    };

    private static HeadingDecisionStatus ParseStatus(string value) => value.ToLowerInvariant() switch
    {
        "autoacceptedevidence" => HeadingDecisionStatus.AutoAcceptedEvidence,
        "autoacceptedcalibrated" => HeadingDecisionStatus.AutoAcceptedCalibrated,
        "humanverified" => HeadingDecisionStatus.HumanVerified,
        _ => HeadingDecisionStatus.RequiresReview,
    };
}
