namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Deterministic boundary between model-produced PDF decisions and source-grounded PDF headings.
/// It owns source lookup and span validation only; it does not call a provider or infer meaning,
/// relations, hierarchy, or product structure.
/// </summary>
internal static class PdfSemanticProposalBinder
{
    public static IReadOnlyList<PdfValidatedHeading> BindAndValidate(
        PdfCanonicalSourceUniverse universe,
        IReadOnlyList<PdfBlockDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(decisions);
        return PdfProposalValidator.Validate(universe.Contexts, decisions);
    }

    public static IReadOnlyList<PdfCandidateStageTrace> Trace(
        PdfCanonicalSourceUniverse universe,
        IReadOnlyList<PdfBlockDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(decisions);
        return PdfProposalValidator.Trace(universe.Contexts, decisions);
    }
}
