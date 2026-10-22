using DocxHeaderExtractor.Sources.Common;
using DocxHeaderExtractor.DocumentProcessing.Selection;

namespace DocxHeaderExtractor.DocumentProcessing.Binding;

/// <summary>
/// Deterministic boundary between model-produced extent decisions and source-grounded headings.
/// It owns source lookup and span validation only; it does not call a provider or infer meaning,
/// relations, hierarchy, or product structure.
/// </summary>
internal static class DecisionBinder
{
    public static IReadOnlyList<ValidatedHeading> BindAndValidate(
        IReadOnlyDictionary<string, OccurrenceContext> contexts,
        IReadOnlyList<HeadingExtentDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(decisions);
        return HeadingDecisionValidator.Validate(contexts, decisions);
    }
}
