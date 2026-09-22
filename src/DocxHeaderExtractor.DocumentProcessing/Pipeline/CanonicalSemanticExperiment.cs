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

    /// <summary>
    /// EXP_MASTHEAD_METADATA. One semantic clause: prominent text that identifies the document or
    /// the occasion it records is not promoted to a heading, and the tree-less relation is not a
    /// place to put text that establishes no structural unit.
    /// <para>
    /// An init property rather than a positional parameter, so every existing arm keeps the
    /// constructor it had and its prompt hash with it.
    /// </para>
    /// </summary>
    public bool ConstrainNonStructuralMetadata { get; init; }

    public static readonly CanonicalSemanticExperiment NonStructuralMetadataConstrained =
        new(false, false) { ConstrainNonStructuralMetadata = true };

    /// <summary>
    /// EXP_MASTHEAD_METADATA_E2. Not a second category list: one invariant, that heading membership
    /// is decided before placement and that parent-node:NONE cannot admit a span which establishes
    /// no structural unit. Separately switchable from E1 so the two are never measured together.
    /// </summary>
    public bool RequireMembershipBeforePlacement { get; init; }

    public static readonly CanonicalSemanticExperiment MembershipBeforePlacement =
        new(false, false) { RequireMembershipBeforePlacement = true };

    public string Name => RequireMembershipBeforePlacement
        ? "E2-membership-before-placement"
        : ConstrainNonStructuralMetadata
        ? "E1-masthead-metadata"
        : (CarryStructuralAncestors, CommunicatePartialSpan) switch
    {
        (false, false) => "B0-baseline",
        (true, false) => "B1-i7-structural-ancestors",
        (false, true) => "B2-i8-partial-span",
        _ => "B3-combined",
    };
}
