using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Append-only amendment to the SRC-054 reveal pin (d33da0f), made when the user approved Gold R2 (2026-09-26). The
/// pin named the authored Gold file and its registry entry; R2 replaced both after the reveal. R1 is kept byte for
/// byte at <see cref="Src054BlindScoreTests.GoldR1"/>, and the score harness now reads it there - which changed the
/// harness file the pin hashed. Neither the pin nor the raw score (912dcc7) is rewritten: the pinned Gold hash is R1's
/// and still verifies, the registry-entry hash and the harness hash become history, and this artifact names the
/// harness before and after. The score computation is unchanged and reproduces the committed score byte for byte.
/// </summary>
public sealed class Src054RevealPinAmendmentTests
{
    /// <summary>The score harness after this amendment; <see cref="Src054BlindScoreTests"/> accepts exactly this one.</summary>
    internal const string HarnessAfter = "eb3f4d3402fa7cbaaa22ea567fb9b230d60fb658950e3cdcb5dabfa42a1597e7";

    [Fact]
    public void Freeze_the_reveal_pin_amendment()
    {
        Assert.Equal(HarnessAfter, CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src054BlindScoreTests.HarnessFile)));
        using var pin = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Src054BlindScoreTests.Pin)));
        FreezeArtifact.AssertJson(Src054BlindScoreTests.Root, "SRC-054.reveal-pin-amendment.json", new
        {
            artifactKind = "a99_reveal_pin_amendment",
            study = "SRC054_BLIND_GENERALIZATION_AUDIT_V1",
            amends = new { path = Src054BlindScoreTests.Pin, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src054BlindScoreTests.Pin)), commit = "d33da0f" },
            modelProviderVlmCalls = 0,
            cause = "Gold R2 (user-approved 2026-09-26) replaced the authored Gold file and its registry entry after the reveal",
            fix = "Gold R1 is kept byte for byte and the score harness reads it there; the pin test treats gold.registryGoldSha256 and harness.sha256 as the state at the reveal (RevealPin.Verify); the score harness accepts the harness named here",
            goldR1 = new
            {
                path = Src054BlindScoreTests.GoldR1,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src054BlindScoreTests.GoldR1)),
                pinnedAuthoredGoldSha256 = pin.RootElement.GetProperty("gold").GetProperty("authoredGoldSha256").GetString(),
            },
            registryGoldSha256AtReveal = pin.RootElement.GetProperty("gold").GetProperty("registryGoldSha256").GetString(),
            harnessBefore = new { path = Src054BlindScoreTests.HarnessFile, sha256 = pin.RootElement.GetProperty("harness").GetProperty("sha256").GetString(), commit = "d33da0f" },
            harnessAfter = new { path = Src054BlindScoreTests.HarnessFile, sha256 = HarnessAfter },
            unchanged = new[]
            {
                "the pin artifact (d33da0f) and the raw held-out score (912dcc7) - neither is rewritten",
                "the Gold the score is computed against: R1, whose hash is the pinned authored-Gold hash",
                "Score(): the same computation over the same bytes; The_committed_score_is_reproducible regenerates the committed score byte for byte",
            },
        });
    }
}
