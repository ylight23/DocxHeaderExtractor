using System.Text.Json;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The checkpoint before the reveal (user, 2026-09-25): the Gold gate, computed here rather than asserted by
/// hand, and every hash the reveal depends on. Committed before <see cref="Src042BlindScoreTests"/> opens the
/// proposals; the proposals are pinned by hash and git blob only - their content is not read.
/// </summary>
public sealed class Src042RevealPinTests
{
    [Fact]
    public void Freeze_the_reveal_pin()
    {
        var pdf = TestRepository.Path(Src042BlindGeneralizationTests.Pdf);
        var gold = TestRepository.Path(Src042BlindScoreTests.GoldPath);
        var universe = ExactScorer.Universe.For("PDF", pdf);
        var claims = ExactScorer.ReadGold(gold, universe); // binds every claim; throws on any that does not
        using var goldDoc = JsonDocument.Parse(File.ReadAllText(gold));
        var root = goldDoc.RootElement;
        var spans = claims.SelectMany(c => c.Spans).ToArray();
        var shared = spans.GroupBy(s => s.Alias).Sum(g => g.SelectMany((a, i) => g.Skip(i + 1).Where(b => a.Start < b.End && b.Start < a.End)).Count());
        using var prereg = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{HeldOutProtocolV12.Root}/SRC-042.preregistration.v1_2.json")));

        var pin = new
        {
            artifactKind = "a99_generic_audit_reveal_pin",
            study = "SRC042_BLIND_GENERALIZATION_AUDIT_V1",
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
                fullSuite = "GREEN (1433/1433 at c3de89e)",
                releaseBuild = "0 errors",
                gitDiffCheck = "PASS",
            },
            gold = new
            {
                commit = "c3de89e",
                path = Src042BlindScoreTests.GoldPath,
                authoredGoldSha256 = CanonicalArtifactHash.OfTextFile(gold),
                registryGoldSha256 = CanonicalGoldRegistry.Entry("SRC-042").GoldSha256,
                registry = new { path = CanonicalGoldRegistry.RegistryRelativePath, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(CanonicalGoldRegistry.RegistryRelativePath)), commit = "c838489" },
            },
            source = new { path = Src042BlindGeneralizationTests.Pdf, sha256 = CanonicalArtifactHash.OfBytes(pdf) },
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
            harness = new { path = Src042BlindScoreTests.HarnessFile, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src042BlindScoreTests.HarnessFile)) },
            preregistration = new { commit = "1e7bc7f", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{HeldOutProtocolV12.Root}/SRC-042.preregistration.v1_2.json")) },
            packingPolicy = "FIXED_OWNED_COUNT_120 (default); COHERENT_REGION_SEGMENTATION_V1 not selected",
            proposals = new
            {
                commit = "de9f150",
                path = Src042BlindScoreTests.Proposals,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src042BlindScoreTests.Proposals)),
                gitBlob = Src029BlindScoreTests.GitBlob(TestRepository.Path(Src042BlindScoreTests.Proposals)),
                contentRead = false,
            },
            rules = new[]
            {
                "reveal once; the raw score is committed as written, a bad result included",
                "residuals are classified afterwards; V1.2 is not changed and this baseline is not rewritten",
                "a Gold error found later becomes a new Gold revision; this score stays against the Gold pinned here",
                "only then: the historical 262 delta audit, and SRC-044 if the gate passes",
            },
        };
        // Written once before the reveal; afterwards every pinned SRC-042 fact is re-verified, and the registry
        // file as a whole and the suite count are the corpus state at the reveal (RevealPin).
        RevealPin.Verify(Src042BlindScoreTests.Root, "SRC-042.reveal-pin.v1_2.json", pin,
            "gold.registry.sha256", "checkpoint.fullSuite");
    }
}
