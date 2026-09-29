namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Deterministic boundary between model-produced PDF decisions and source-grounded PDF headings.
/// It owns source lookup and span validation only; it does not call a provider or infer meaning,
/// relations, hierarchy, or product structure.
/// </summary>
internal static class PdfSemanticProposalBinder
{
    public static IReadOnlyList<PdfValidatedHeading> BindAndValidate(
        IReadOnlyDictionary<string, PdfSemanticSourceContext> contexts,
        IReadOnlyList<PdfBlockDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(decisions);
        return PdfProposalValidator.Validate(contexts, decisions);
    }

    public static IReadOnlyList<PdfSemanticSourceStageTrace> Trace(
        IReadOnlyDictionary<string, PdfSemanticSourceContext> contexts,
        IReadOnlyList<PdfBlockDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(decisions);
        return PdfProposalValidator.Trace(contexts, decisions);
    }
}
