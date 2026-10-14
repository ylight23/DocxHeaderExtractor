using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;

namespace DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;

/// <summary>One exact heading extent selected by an authority implementation.</summary>
internal sealed record HeadingExtentDecision(
    string Id,
    string Reason,
    TextOffsetSpan? HeadingSpan = null,
    string? SemanticFunction = null,
    IReadOnlyList<CanonicalSemanticBoundPart>? Parts = null);

/// <summary>Source-grounded heading accepted by the common binder.</summary>
internal sealed record ValidatedHeading(
    string SourceId,
    TextOffsetSpan HeadingSpan,
    string? SemanticFunction,
    string StructuralScope,
    string ValidationBasis)
{
    public IReadOnlyList<CanonicalSemanticBoundPart>? Parts { get; init; }
}

/// <summary>Parser-owned UTF-16 boundaries for exact source spans.</summary>
internal static class SourceTextBoundaryMap
{
    public static IReadOnlyList<int> For(string sourceText) =>
        DocxHeaderExtractor.Core.Semantics.Binding.SourceTextBoundaryMap.For(sourceText);

    public static bool Contains(string sourceText, int offset) =>
        offset >= 0 && offset <= sourceText.Length && For(sourceText).Contains(offset);
}

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
        context.EvidenceOrigins.All(origin => origin is "layout_parser" or "marker_parser" or "scope_detector" or "docx_parser" or "ooxml_parser") &&
        ValidateSpan(decision, context.RawText, out _) == "valid";

    private static string ValidateSpan(HeadingExtentDecision decision, string sourceText, out string? reason)
    {
        if (decision.HeadingSpan is null) { reason = "missing-pointer-span"; return "invalid"; }
        var span = decision.HeadingSpan;
        if (span.Start < 0 || span.End <= span.Start || span.End > sourceText.Length) { reason = "invalid-pointer-span"; return "invalid"; }
        if (!SourceTextBoundaryMap.Contains(sourceText, span.Start) || !SourceTextBoundaryMap.Contains(sourceText, span.End)) { reason = "invalid-pointer-boundary"; return "invalid"; }
        reason = null;
        return "valid";
    }
}
