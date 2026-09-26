using System.Text.Json;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The checkpoint before the reveal (user, 2026-09-25, on SRC-042; the same checkpoint here): the Gold gate, computed here rather than asserted by
/// hand, and every hash the reveal depends on. Committed before <see cref="Src095BlindScoreTests"/> opens the
/// proposals; the proposals are pinned by hash and git blob only - their content is not read.
/// </summary>
public sealed class Src095RevealPinTests
{
    [Fact]
    public void Freeze_the_reveal_pin()
    {
        var pdf = TestRepository.Path(Src095BlindGeneralizationTests.Pdf);
        var gold = TestRepository.Path(Src095BlindScoreTests.GoldPath);
        var universe = ExactScorer.Universe.For("PDF", pdf);
        var claims = ExactScorer.ReadGold(gold, universe); // binds every claim; throws on any that does not
        using var goldDoc = JsonDocument.Parse(File.ReadAllText(gold));
        var root = goldDoc.RootElement;
        var spans = claims.SelectMany(c => c.Spans).ToArray();
        var shared = spans.GroupBy(s => s.Alias).Sum(g => g.SelectMany((a, i) => g.Skip(i + 1).Where(b => a.Start < b.End && b.Start < a.End)).Count());
        using var prereg = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Src095BlindGeneralizationTests.Root}/SRC-095.preregistration.json")));

        var pin = new
        {
            artifactKind = "a99_generic_audit_reveal_pin",
            study = "SRC095_BLIND_GENERALIZATION_AUDIT_V1",
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
                fullSuite = "GREEN (1549/1549 at c7d1f4b)",
                releaseBuild = "0 errors",
                gitDiffCheck = "PASS",
            },
            gold = new
            {
                commit = "c7d1f4b",
                path = Src095BlindScoreTests.GoldPath,
                authoredGoldSha256 = CanonicalArtifactHash.OfTextFile(gold),
                registryGoldSha256 = CanonicalGoldRegistry.Entry("SRC-095").GoldSha256,
                registry = new { path = CanonicalGoldRegistry.RegistryRelativePath, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(CanonicalGoldRegistry.RegistryRelativePath)), commit = "77ff675" },
            },
            source = new { path = Src095BlindGeneralizationTests.Pdf, sha256 = CanonicalArtifactHash.OfBytes(pdf) },
            engine = new { commit = "81bcb33 (V1.3 + PDF_SOURCE_FACTS_V3 frozen)", identity = Src095BlindGeneralizationTests.EngineIdentity() },
            engineMatchesPreregistration = System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(prereg.RootElement.GetProperty("engineIdentity").GetRawText()),
                System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Src095BlindGeneralizationTests.EngineIdentity(), FreezeArtifact.Json))),
            requestContract = new
            {
                requestVersion = "V2_ATTENTION_FREE over PDF_SOURCE_FACTS_V3",
                path = Src095BlindGeneralizationTests.ContractFreeze,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src095BlindGeneralizationTests.ContractFreeze)),
            },
            scorer = new
            {
                id = ExactScorer.ScorerId,
                path = HeldOutProtocolV12.ScorerFile,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(HeldOutProtocolV12.ScorerFile)),
            },
            harness = new { path = Src095BlindScoreTests.HarnessFile, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src095BlindScoreTests.HarnessFile)) },
            preregistration = new { commit = "068c2cf", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Src095BlindGeneralizationTests.Root}/SRC-095.preregistration.json")) },
            packingPolicy = "FIXED_OWNED_COUNT_120 (default); COHERENT_REGION_SEGMENTATION_V1 not selected",
            proposals = new
            {
                commit = "6d1fc0d",
                path = Src095BlindScoreTests.Proposals,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src095BlindScoreTests.Proposals)),
                gitBlob = Src029BlindScoreTests.GitBlob(TestRepository.Path(Src095BlindScoreTests.Proposals)),
                contentRead = false,
            },
            rules = new[]
            {
                "reveal once; the raw score is committed as written, a bad result included",
                "residuals are classified afterwards; V1.3 and PDF_SOURCE_FACTS_V3 are not changed and this baseline is not rewritten",
                "a Gold error found later becomes a new Gold revision; this score stays against the Gold pinned here",
                "only then: the cross-document summary, the hierarchy evaluation and the final pipeline verdict",
            },
        };
        // Written once before the reveal; afterwards every pinned SRC-095 fact is re-verified, and the registry
        // file as a whole and the suite count are the corpus state at the reveal (RevealPin).
        RevealPin.Verify(Src095BlindScoreTests.Root, "SRC-095.reveal-pin.json", pin,
            "gold.registry.sha256", "checkpoint.fullSuite");
    }
}
