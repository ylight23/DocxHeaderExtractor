namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Named, byte-preserved vintages of Gold that the live registry has since superseded.
/// <para>
/// A correction to Gold does not invalidate a historical experiment's already-frozen result - it
/// changes what an authority id resolves to <i>now</i>. <see cref="CanonicalGoldRegistry"/> is
/// deliberately a single live authority with no fallback, so any test that reproduces bytes
/// captured against an older vintage must keep pinning that vintage explicitly, by path and hash,
/// via <see cref="CanonicalGoldRegistry.EntryAt"/> / <see cref="CanonicalGoldRegistry.ResolveAt"/>,
/// rather than resolving whatever the authority id currently names.
/// </para>
/// </summary>
internal static class HistoricalGoldVintages
{
    /// <summary>
    /// DOC-0252 before the 2026-09-23 human-approved Gold correction that added ITEM-505430BB
    /// ("International Comparison Program (ICP) / TECHNICAL ADVISORY GROUP (TAG)", identity
    /// <c>L0513:S0:0-38|L0514:S0:0-30</c>) as an accepted <c>DocumentTitle</c> claim.
    /// <para>
    /// Every experiment that froze a result while this text was NOT Gold - the masthead
    /// negative-control family (F1/F2/F3), Stage-1/open-set membership, the two-stage semantic
    /// contract design, the none-relation policy corpus audit, and the direct-semantic-probe /
    /// context-ablation cohort - pins this vintage so its frozen conclusions keep describing the
    /// Gold they actually ran against. The current, corrected Gold is <c>R2</c>; this is <c>R1</c>.
    /// </para>
    /// </summary>
    public const string Doc0252R1Path =
        "eval/a99-closed-loop/gold-current/documents/DOC-0252.pre-item505430bb-document-label-correction.gold.v1.json";

    public const string Doc0252R1Sha256 =
        "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";

    /// <summary>
    /// DOC-0252 after the ITEM-505430BB correction, before the 2026-09-23 human-approved Gold
    /// correction that reclassified ITEM-CCE2C592 ("Agenda", identity <c>L0519:S0:0-6</c>) from
    /// <c>SectionHeading</c> (STRUCTURAL_UNIT) to <c>DocumentTitle</c> (DOCUMENT_LABEL / abstract
    /// semantic function IDENTITY).
    /// <para>
    /// Every current-conclusion experiment that ran between the two corrections - the F1/Agenda
    /// direct-semantic-probe and context-ablation rescoring, the Haiku blind and context-ablation
    /// confirmations, STRUCTURED_EVIDENCE_CONTEXT_V2_EXECUTION, CONTEXT_TOPOLOGY_DISAGREEMENT_
    /// SIGNAL_V1, and SELECTIVE_SEMANTIC_ESCALATION_V1 (text adjudicator and VLM) - froze its Agenda
    /// expected label as STRUCTURAL_UNIT under this vintage. Those artifacts are correct
    /// descriptions of what they scored under R2 and are not rewritten; new rescoring under R3 is a
    /// separate, new artifact. The current, corrected Gold is <c>R3</c>; this is <c>R2</c>.
    /// </para>
    /// </summary>
    public const string Doc0252R2Path =
        "eval/a99-closed-loop/gold-current/documents/DOC-0252.pre-agenda-identity-correction.gold.v1.json";

    public const string Doc0252R2Sha256 =
        "2ab040e93a06d6c8afa1e6b3daf97350bea7cec45477f8bb23b607abee4c313e";
}
