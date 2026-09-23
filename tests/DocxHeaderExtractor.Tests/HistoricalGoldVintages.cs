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
}
