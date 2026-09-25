using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Append-only amendment to the SRC-041 reveal pin (54be169). The pin recorded the hash of the whole Gold
/// registry file, which legitimately changes whenever a later document's Gold is added (SRC-042), so its
/// verifying test failed on unchanged SRC-041 facts. The pin test now re-verifies field by field and treats the
/// registry-file hash and the suite count as history (<see cref="RevealPin"/>); that edit changed the score
/// harness file the pin also hashed. Neither the pin nor the raw score (ba93900) is rewritten: this artifact
/// names the harness before and after, and the score harness accepts exactly this pair. The score computation
/// is unchanged, and its reproducibility test regenerates the committed score byte for byte.
/// </summary>
public sealed class Src041RevealPinAmendmentTests
{
    [Fact]
    public void Freeze_the_reveal_pin_amendment()
    {
        using var pin = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Src041BlindScoreTests.Pin)));
        FreezeArtifact.AssertJson(Src041BlindScoreTests.Root, "SRC-041.reveal-pin-amendment.v1_1.json", new
        {
            artifactKind = "a99_reveal_pin_amendment",
            study = "SRC041_BLIND_GENERALIZATION_AUDIT_V1",
            amends = new { path = Src041BlindScoreTests.Pin, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src041BlindScoreTests.Pin)), commit = "54be169" },
            modelProviderVlmCalls = 0,
            defect = "the pin test re-froze the whole pin, including the hash of the whole Gold registry file; adding SRC-042's Gold changed that file, so the test failed although no SRC-041 fact changed",
            fix = "the pin test re-verifies every pinned SRC-041 fact field by field and treats gold.registry.sha256 and checkpoint.fullSuite as the corpus state at the reveal (RevealPin.Verify); the score harness accepts the harness named here",
            harnessBefore = new { path = Src041BlindScoreTests.HarnessFile, sha256 = pin.RootElement.GetProperty("harness").GetProperty("sha256").GetString(), commit = "54be169" },
            harnessAfter = new { path = Src041BlindScoreTests.HarnessFile, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src041BlindScoreTests.HarnessFile)) },
            unchanged = new[]
            {
                "the pin artifact (54be169) and the raw held-out score (ba93900) - neither is rewritten",
                "Score(): byte-identical to the pinned harness (checked against 54be169 when this amendment was written); The_committed_score_is_reproducible regenerates the committed score byte for byte",
                "every pinned SRC-041 fact: Gold and registry-entry hash, source, engine identity, contract, scorer, pre-registration, proposals, computed checkpoint",
            },
        });
    }
}
