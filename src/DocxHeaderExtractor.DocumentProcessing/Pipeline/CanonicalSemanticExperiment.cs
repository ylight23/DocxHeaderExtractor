namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Which interventions are active for one run. Each is separately switchable so a measurement can
/// be attributed to one of them.
/// <para>
/// Combining two and finding an improvement tells you nothing about which one produced it, so the
/// arms are prepared and frozen separately: a baseline, one arm per intervention, and no combined
/// arm until each has been measured alone.
/// </para>
/// </summary>
internal sealed record CanonicalSemanticExperiment(
    bool CarryStructuralAncestors,
    bool CommunicatePartialSpan)
{
    /// <summary>B0. What ships today.</summary>
    public static readonly CanonicalSemanticExperiment Baseline = new(false, false);

    /// <summary>B1, I7. Context payload changes; prompt and source are untouched.</summary>
    public static readonly CanonicalSemanticExperiment StructuralAncestorsOnly = new(true, false);

    /// <summary>B2, I8. Prompt contract changes; context and source are untouched.</summary>
    public static readonly CanonicalSemanticExperiment PartialSpanOnly = new(false, true);

    public string Name => (CarryStructuralAncestors, CommunicatePartialSpan) switch
    {
        (false, false) => "B0-baseline",
        (true, false) => "B1-i7-structural-ancestors",
        (false, true) => "B2-i8-partial-span",
        _ => "B3-combined",
    };
}
