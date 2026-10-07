namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Where a test that replays a frozen artifact reads the source universe it was produced over. The v1 snapshots are the
/// canonical authority of the first PDF universe (host-font geometry); a replay reads them rather than the live parse, which
/// is now font independent and, for SRC-089, a different universe.
/// </summary>
internal static class FrozenSourceSnapshots
{
    public static string V1Root => TestRepository.Path("eval/a99-closed-loop/pdf-canonical-source-v1");
}
