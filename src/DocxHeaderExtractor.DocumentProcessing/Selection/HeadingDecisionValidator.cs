using DocxHeaderExtractor.Sources.Snapshot;

namespace DocxHeaderExtractor.DocumentProcessing.Selection;

internal static class HeadingDecisionValidator
{
    public static IReadOnlyList<ValidatedHeading> Validate(
        IReadOnlyDictionary<string, OccurrenceContext> contexts,
        IReadOnlyList<HeadingExtentDecision> decisions) => decisions
        .Where(decision => contexts.TryGetValue(decision.Id, out var context) && IsEligibleHeading(decision, context))
        .Select(decision =>
        {
            var context = contexts[decision.Id];
            return new ValidatedHeading(
                decision.Id, decision.HeadingSpan!, decision.SemanticFunction, context.StructuralScope,
                "source-grounded-pointer-span") { Parts = decision.Parts };
        })
        .ToArray();

    public static bool IsEligibleHeading(HeadingExtentDecision decision, OccurrenceContext context) =>
        context.EvidenceOrigins.All(origin => origin is "layout_parser" or "marker_parser" or "scope_detector" or "docx_parser" or "ooxml_parser" or "scan_reader") &&
        ValidateSpan(decision, context.RawText, out _) == "valid";

    private static string ValidateSpan(HeadingExtentDecision decision, string sourceText, out string? reason)
    {
        if (decision.HeadingSpan is null) { reason = "missing-pointer-span"; return "invalid"; }
        var span = decision.HeadingSpan;
        if (span.Start < 0 || span.End <= span.Start || span.End > sourceText.Length) { reason = "invalid-pointer-span"; return "invalid"; }
        var boundaries = DocxHeaderExtractor.Sources.Aliases.TextBoundaryMap.For(sourceText);
        if (!boundaries.Contains(span.Start) || !boundaries.Contains(span.End)) { reason = "invalid-pointer-boundary"; return "invalid"; }
        reason = null;
        return "valid";
    }
}
