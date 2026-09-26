using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Append-only amendment to the SRC-044 reveal pin (5cf6431), made when the user approved Gold R2 (2026-09-26). The
/// pin named the authored Gold file and its registry entry; R2 replaced both after the reveal. R1 is kept byte for
/// byte at <see cref="Src044BlindScoreTests.GoldR1"/>, and the score harness now reads it there - which changed the
/// harness file the pin hashed. Neither the pin nor the raw score (382c58c) is rewritten: the pinned Gold hash is R1's
/// and still verifies, the registry-entry hash and the harness hash become history, and this artifact names the
/// harness before and after. The score computation is unchanged and reproduces the committed score byte for byte.
/// </summary>
public sealed class Src044RevealPinAmendmentTests
{
    /// <summary>The score harness after this amendment; <see cref="Src044BlindScoreTests"/> accepts exactly this one.</summary>
    internal const string HarnessAfter = "56e957d90c051682d5e523c870325971f3636a63efb30a548624172bcfef9fc9";

    [Fact]
    public void Freeze_the_reveal_pin_amendment()
    {
        Assert.Equal(HarnessAfter, CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src044BlindScoreTests.HarnessFile)));
        using var pin = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Src044BlindScoreTests.Pin)));
        FreezeArtifact.AssertJson(Src044BlindScoreTests.Root, "SRC-044.reveal-pin-amendment.json", new
        {
            artifactKind = "a99_reveal_pin_amendment",
            study = "SRC044_BLIND_GENERALIZATION_AUDIT_V1",
            amends = new { path = Src044BlindScoreTests.Pin, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src044BlindScoreTests.Pin)), commit = "5cf6431" },
            modelProviderVlmCalls = 0,
            cause = "Gold R2 (user-approved 2026-09-26) replaced the authored Gold file and its registry entry after the reveal",
            fix = "Gold R1 is kept byte for byte and the score harness reads it there; the pin test treats gold.registryGoldSha256 and harness.sha256 as the state at the reveal (RevealPin.Verify); the score harness accepts the harness named here",
            goldR1 = new
            {
                path = Src044BlindScoreTests.GoldR1,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src044BlindScoreTests.GoldR1)),
                pinnedAuthoredGoldSha256 = pin.RootElement.GetProperty("gold").GetProperty("authoredGoldSha256").GetString(),
            },
            registryGoldSha256AtReveal = pin.RootElement.GetProperty("gold").GetProperty("registryGoldSha256").GetString(),
            harnessBefore = new { path = Src044BlindScoreTests.HarnessFile, sha256 = pin.RootElement.GetProperty("harness").GetProperty("sha256").GetString(), commit = "5cf6431" },
            harnessAfter = new { path = Src044BlindScoreTests.HarnessFile, sha256 = HarnessAfter },
            unchanged = new[]
            {
                "the pin artifact (5cf6431) and the raw held-out score (382c58c) - neither is rewritten",
                "the Gold the score is computed against: R1, whose hash is the pinned authored-Gold hash",
                "Score(): the same computation over the same bytes; The_committed_score_is_reproducible regenerates the committed score byte for byte",
            },
        });
    }
}
