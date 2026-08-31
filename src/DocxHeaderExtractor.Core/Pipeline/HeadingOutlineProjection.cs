using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Compatibility projection from generic structural authority to the existing heading output.
/// It performs no candidate selection, matching, validation, or hierarchy inference.
/// </summary>
public static class HeadingOutlineProjection
{
    public static DocumentOutline Project(
        DocumentOutline template,
        ValidatedStructure structure)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(structure);
        return new DocumentOutline
        {
            File = template.File,
            ParagraphCount = template.ParagraphCount,
            CandidateCount = template.CandidateCount,
            Headings = Project(structure),
            ElapsedMs = template.ElapsedMs,
            Model = template.Model,
            DocumentMode = template.DocumentMode,
            DeterministicRoute = template.DeterministicRoute,
            RouteAudit = template.RouteAudit,
            Diagnostics = template.Diagnostics,
            Provenance = template.Provenance,
            ProductOutput = template.ProductOutput,
            DecisionAudit = template.DecisionAudit,
            Outcome = template.Outcome,
        };
    }

    public static IReadOnlyList<HeadingRecord> Project(ValidatedStructure structure)
        => Project(structure, null);

    public static IReadOnlyList<HeadingRecord> Project(
        ValidatedStructure structure,
        IReadOnlySet<string>? emittedElementIds)
    {
        ArgumentNullException.ThrowIfNull(structure);
        return structure.OutlineElements
            .Where(element => emittedElementIds is null || emittedElementIds.Contains(element.Id))
            // ValidatedStructure.Elements already carries the producer's canonical order. Sorting
            // by source ordinal here loses distinct PDF occurrences that share one paragraph.
            .Select(ProjectHeading)
            .ToArray();
    }

    private static HeadingRecord ProjectHeading(ValidatedStructuralElement element)
    {
        var source = element.Sources.FirstOrDefault();
        if (source is null)
            throw new InvalidOperationException($"Structural element '{element.Id}' has no validated source.");
        var metadata = element.ProjectionMetadata;

        return new HeadingRecord
        {
            Index = source.SourceOrdinal,
            StableId = source.StableId ?? source.SourceId,
            SourceId = metadata?.CompatibilitySourceId ?? source.SourceId,
            Level = element.Level,
            Text = element.Text,
            OriginalText = metadata?.OriginalText,
            HeadingSpan = new TextOffsetSpan(source.Span.Start, source.Span.End),
            InlineBody = metadata?.InlineBody,
            InlineBodySpan = metadata?.InlineBodySpan is { } bodySpan
                ? new TextOffsetSpan(bodySpan.Start, bodySpan.End)
                : null,
            BoundarySource = metadata?.BoundarySource,
            StyleId = metadata?.StyleId,
            Source = ParseSource(element.Decision.Origin),
            Confidence = element.Decision.Confidence,
            ModelConfirmed = metadata?.ModelConfirmed ?? false,
            CriticConfirmed = metadata?.CriticConfirmed ?? false,
            DecisionStatus = ParseStatus(element.Decision.Status),
            ConfidenceBasis = element.Decision.ConfidenceBasis,
            AcceptanceSignature = metadata?.AcceptanceSignature,
            CalibrationSamples = metadata?.CalibrationSamples ?? 0,
            Evidence = metadata?.Evidence,
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
