using DocxHeaderExtractor.Core.Models;

namespace Accuracy99Baseline;

/// <summary>Small, deterministic validators for the Phase B annotation contract.</summary>
public static class PhaseBContracts
{
    public static bool IsValidSourceSpan(string rawText, StructuralSpan span) =>
        rawText is not null
        && span.Start >= 0
        && span.End <= rawText.Length
        && (span.Start < span.End || (rawText.Length == 0 && span.Start == 0 && span.End == 0));

    public static bool IsHeadingSpanTextConsistent(string rawText, StructuralSpan span, string headingText) =>
        IsValidSourceSpan(rawText, span) && string.Equals(
            rawText[span.Start..span.End], headingText, StringComparison.Ordinal);

    public static bool IsExhaustive(IReadOnlyList<string?> labels) =>
        labels.Count > 0 && labels.All(label => label is "HEADING" or "NON_HEADING" or "UNCERTAIN" or "EXCLUDED");

    public static bool HasDuplicateGoldIdentity(IEnumerable<(string DocumentId, string SourceId, int Start, int End)> occurrences) =>
        occurrences.GroupBy(item => (item.DocumentId, item.SourceId, item.Start, item.End)).Any(group => group.Count() > 1);

    public static bool IsMetricMeasurable(int denominator, bool eligible) => eligible && denominator > 0;

    public static bool IsBlindHoldoutFrozen(string status, bool blind, int documentCount, bool hashesFrozen, bool labelsFrozen) =>
        string.Equals(status, "FROZEN", StringComparison.Ordinal) && blind && documentCount > 0 && hashesFrozen && labelsFrozen;
}
