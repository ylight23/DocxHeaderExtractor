using System.Text.Json;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The checkpoint before the reveal (user, 2026-09-25, on SRC-042; the same checkpoint here): the Gold gate, computed here rather than asserted by
/// hand, and every hash the reveal depends on. Committed before <see cref="Src053BlindScoreTests"/> opens the
/// proposals; the proposals are pinned by hash and git blob only - their content is not read.
/// </summary>
public sealed class Src053RevealPinTests
{
    [Fact]
    public void Freeze_the_reveal_pin()
    {
        var pdf = TestRepository.Path(Src053BlindGeneralizationTests.Pdf);
        var gold = TestRepository.Path(Src053BlindScoreTests.GoldPath);
        var universe = ExactScorer.Universe.For("PDF", pdf);
        var claims = ExactScorer.ReadGold(gold, universe); // binds every claim; throws on any that does not
        using var goldDoc = JsonDocument.Parse(File.ReadAllText(gold));
        var root = goldDoc.RootElement;
        var spans = claims.SelectMany(c => c.Spans).ToArray();
        var shared = spans.GroupBy(s => s.Alias).Sum(g => g.SelectMany((a, i) => g.Skip(i + 1).Where(b => a.Start < b.End && b.Start < a.End)).Count());
        using var prereg = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{HeldOutProtocolV12.Root}/SRC-053.preregistration.v1_2.json")));

        var pin = new
        {
            artifactKind = "a99_generic_audit_reveal_pin",
            study = "SRC053_BLIND_GENERALIZATION_AUDIT_V1",
            checkpoint = new
            {
                goldClaims = claims.Count,
                bind = $"{claims.Count}/{root.GetProperty("occurrence").GetProperty("claims").GetArrayLength()}",
                duplicateIdentities = claims.Count - claims.Select(c => c.Identity).Distinct(StringComparer.Ordinal).Count(),
                sharedCharacters = shared,
                sourceHash = root.GetProperty("source").GetProperty("sourceSha256").GetString() == CanonicalArtifactHash.OfBytes(pdf) ? "PASS" : "FAIL",
                hierarchyEvaluable = root.GetProperty("declaredCapabilities").GetProperty("hierarchyEvaluable").GetBoolean(),
                engineProposalsOpened = "NO",
                modelProviderCalls = 0,
                fullSuite = "GREEN (1458/1458 at 3e86964)",
                releaseBuild = "0 errors",
                gitDiffCheck = "PASS",
            },
            gold = new
            {
                commit = "3e86964",
                path = Src053BlindScoreTests.GoldPath,
                authoredGoldSha256 = CanonicalArtifactHash.OfTextFile(gold),
                registryGoldSha256 = CanonicalGoldRegistry.Entry("SRC-053").GoldSha256,
                registry = new { path = CanonicalGoldRegistry.RegistryRelativePath, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(CanonicalGoldRegistry.RegistryRelativePath)), commit = "4a6cc63" },
            },
            source = new { path = Src053BlindGeneralizationTests.Pdf, sha256 = CanonicalArtifactHash.OfBytes(pdf) },
            engine = new { commit = "ad44d78", identity = GenericAuditEngineV12Tests.EngineIdentity() },
            engineMatchesPreregistration = System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(prereg.RootElement.GetProperty("engineIdentity").GetRawText()),
                System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(GenericAuditEngineV12Tests.EngineIdentity(), FreezeArtifact.Json))),
            requestContract = new
            {
                requestVersion = "V2_ATTENTION_FREE",
                path = HeldOutProtocolV12.ContractFreeze,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(HeldOutProtocolV12.ContractFreeze)),
            },
            scorer = new
            {
                id = ExactScorer.ScorerId,
                path = HeldOutProtocolV12.ScorerFile,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(HeldOutProtocolV12.ScorerFile)),
            },
            harness = new { path = Src053BlindScoreTests.HarnessFile, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src053BlindScoreTests.HarnessFile)) },
            preregistration = new { commit = "e0cd016", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{HeldOutProtocolV12.Root}/SRC-053.preregistration.v1_2.json")) },
            packingPolicy = "FIXED_OWNED_COUNT_120 (default); COHERENT_REGION_SEGMENTATION_V1 not selected",
            proposals = new
            {
                commit = "d3152ce",
                path = Src053BlindScoreTests.Proposals,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src053BlindScoreTests.Proposals)),
                gitBlob = Src029BlindScoreTests.GitBlob(TestRepository.Path(Src053BlindScoreTests.Proposals)),
                contentRead = false,
            },
            rules = new[]
            {
                "reveal once; the raw score is committed as written, a bad result included",
                "residuals are classified afterwards; V1.2 is not changed and this baseline is not rewritten",
                "a Gold error found later becomes a new Gold revision; this score stays against the Gold pinned here",
                "only then: the historical 277 delta audit, and SRC-054 if the gate passes",
            },
        };
        // Written once before the reveal; afterwards every pinned SRC-053 fact is re-verified, and the registry
        // file as a whole and the suite count are the corpus state at the reveal (RevealPin).
        RevealPin.Verify(Src053BlindScoreTests.Root, "SRC-053.reveal-pin.v1_2.json", pin,
            "gold.registry.sha256", "checkpoint.fullSuite");
    }
}
