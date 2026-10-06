using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Deterministic boundary between model-produced extent decisions and source-grounded headings.
/// It owns source lookup and span validation only; it does not call a provider or infer meaning,
/// relations, hierarchy, or product structure.
/// </summary>
internal static class HeadingProposalBinder
{
    public static IReadOnlyList<ValidatedHeading> BindAndValidate(
        IReadOnlyDictionary<string, HeadingSourceContext> contexts,
        IReadOnlyList<HeadingExtentDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(decisions);
        return HeadingProposalValidator.Validate(contexts, decisions);
    }

    public static IReadOnlyList<HeadingSourceStageTrace> Trace(
        IReadOnlyDictionary<string, HeadingSourceContext> contexts,
        IReadOnlyList<HeadingExtentDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(decisions);
        return HeadingProposalValidator.Trace(contexts, decisions);
    }
}
