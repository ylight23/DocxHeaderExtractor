using System.Text.Json;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC041_BLIND_GENERALIZATION_AUDIT_V1, protocol step 5: the one reveal of GENERIC_AUDIT_ENGINE_V1.1's blind
/// SRC-041 proposals (10b3317) against the frozen 280-claim Gold (3dca86f).
/// <para>
/// Everything the reveal depends on is pinned first (<see cref="Src041RevealPinTests"/>): this harness, the
/// pinned GENERIC_EXACT_SCORER_V1, the proposals file by hash, the Gold and the registry. The raw score is
/// written once, with A99_SRC041_SCORE=1, and committed as written - a bad result included. Residuals are
/// classified afterwards; V1.1 is not changed and this baseline is not rewritten.
/// </para>
/// </summary>
public sealed class Src041BlindScoreTests
{
    internal const string Root = Src041BlindGeneralizationTests.Root;
    internal const string Proposals = Root + "/SRC-041.proposals.v1_1.json";
    internal const string Pin = Root + "/SRC-041.reveal-pin.v1_1.json";
    internal const string GoldPath = "eval/a99-closed-loop/gold/SRC-041.gold.json";
    internal const string HarnessFile = "tests/DocxHeaderExtractor.Tests/Src041BlindScoreTests.cs";
    internal const string Amendment = Root + "/SRC-041.reveal-pin-amendment.v1_1.json";

    [Fact]
    public void Score_the_blind_proposals_against_gold()
    {
        if (Environment.GetEnvironmentVariable("A99_SRC041_SCORE") != "1") return;
        AssertPinned();
        FreezeArtifact.AssertJson(Root, "SRC-041.blind-score.v1_1.json", Score());
    }

    /// <summary>After the reveal: the committed score is still what the pinned inputs produce.</summary>
    [Fact]
    public void The_committed_score_is_reproducible()
    {
        if (!File.Exists(TestRepository.Path($"{Root}/SRC-041.blind-score.v1_1.json"))) return; // not revealed yet
        AssertPinned();
        FreezeArtifact.AssertJson(Root, "SRC-041.blind-score.v1_1.json", Score());
    }

    private static void AssertPinned()
    {
        using var pin = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Pin)));
        var p = pin.RootElement;
        // The harness is the pinned one, or the one an append-only amendment names (the score computation is
        // re-checked by regenerating the committed score byte for byte, below).
        var harness = CanonicalArtifactHash.OfTextFile(TestRepository.Path(HarnessFile));
        var pinnedHarness = p.GetProperty("harness").GetProperty("sha256").GetString();
        if (harness != pinnedHarness)
        {
            using var amendment = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Amendment)));
            Assert.Equal(pinnedHarness, amendment.RootElement.GetProperty("harnessBefore").GetProperty("sha256").GetString());
            Assert.Equal(harness, amendment.RootElement.GetProperty("harnessAfter").GetProperty("sha256").GetString());
        }
        Assert.Equal(p.GetProperty("scorer").GetProperty("sha256").GetString(), CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src041BlindGeneralizationTests.ScorerFile)));
        Assert.Equal(p.GetProperty("gold").GetProperty("authoredGoldSha256").GetString(), CanonicalArtifactHash.OfTextFile(TestRepository.Path(GoldPath)));
        Assert.Equal(p.GetProperty("gold").GetProperty("registryGoldSha256").GetString(), CanonicalGoldRegistry.Entry("SRC-041").GoldSha256);
        Assert.Equal(p.GetProperty("proposals").GetProperty("gitBlob").GetString(), Src029BlindScoreTests.GitBlob(TestRepository.Path(Proposals)));
        Assert.Equal(p.GetProperty("source").GetProperty("sha256").GetString(), CanonicalArtifactHash.OfBytes(TestRepository.Path(Src041BlindGeneralizationTests.Pdf)));
    }

    private static object Score()
    {
        var universe = ExactScorer.Universe.For("PDF", TestRepository.Path(Src041BlindGeneralizationTests.Pdf));
        var claims = ExactScorer.ReadGold(TestRepository.Path(GoldPath), universe);
        var score = ExactScorer.Compute(claims, ExactScorer.ReadProposals(TestRepository.Path(Proposals), universe));
        return new
        {
            artifactKind = "a99_generic_audit_blind_score",
            study = "SRC041_BLIND_GENERALIZATION_AUDIT_V1",
            engine = SemanticAuditEngine.EngineId,
            role = "HELD_OUT - the first generalization measurement of V1.1",
            scorer = ExactScorer.ScorerId,
            pin = new { path = Pin, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Pin)) },
            proposals = new { path = Proposals, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Proposals)), gitBlob = Src029BlindScoreTests.GitBlob(TestRepository.Path(Proposals)) },
            gold = new { path = GoldPath, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(GoldPath)), claims = claims.Count },
            modelProviderVlmCalls = 0,
            headline = score.Headline(),
            axesOnTruePositives = score.Axes(),
            residuals = score.Residuals(),
        };
    }
}

/// <summary>
/// The checkpoint before the reveal (user, 2026-09-25): the Gold gate, computed here rather than asserted by
/// hand, and every hash the reveal depends on. Committed before <see cref="Src041BlindScoreTests"/> opens the
/// proposals; the proposals are pinned by hash and git blob only - their content is not read.
/// </summary>
public sealed class Src041RevealPinTests
{
    [Fact]
    public void Freeze_the_reveal_pin()
    {
        var pdf = TestRepository.Path(Src041BlindGeneralizationTests.Pdf);
        var gold = TestRepository.Path(Src041BlindScoreTests.GoldPath);
        var universe = ExactScorer.Universe.For("PDF", pdf);
        var claims = ExactScorer.ReadGold(gold, universe); // binds every claim; throws on any that does not
        using var goldDoc = JsonDocument.Parse(File.ReadAllText(gold));
        var root = goldDoc.RootElement;
        var spans = claims.SelectMany(c => c.Spans).ToArray();
        var shared = spans.GroupBy(s => s.Alias).Sum(g => g.SelectMany((a, i) => g.Skip(i + 1).Where(b => a.Start < b.End && b.Start < a.End)).Count());
        using var prereg = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Src041BlindGeneralizationTests.Root}/SRC-041.preregistration.v1_1.json")));

        var pin = new
        {
            artifactKind = "a99_generic_audit_reveal_pin",
            study = "SRC041_BLIND_GENERALIZATION_AUDIT_V1",
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
                fullSuite = "GREEN (1404/1404 at 3dca86f)",
                releaseBuild = "0 errors",
                gitDiffCheck = "PASS",
            },
            gold = new
            {
                commit = "3dca86f",
                path = Src041BlindScoreTests.GoldPath,
                authoredGoldSha256 = CanonicalArtifactHash.OfTextFile(gold),
                registryGoldSha256 = CanonicalGoldRegistry.Entry("SRC-041").GoldSha256,
                registry = new { path = CanonicalGoldRegistry.RegistryRelativePath, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(CanonicalGoldRegistry.RegistryRelativePath)), commit = "e185f24" },
            },
            source = new { path = Src041BlindGeneralizationTests.Pdf, sha256 = CanonicalArtifactHash.OfBytes(pdf) },
            engine = new { commit = "3cf504f", identity = GenericAuditEngineV11Tests.EngineIdentity() },
            engineMatchesPreregistration = System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(prereg.RootElement.GetProperty("engineIdentity").GetRawText()),
                System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(GenericAuditEngineV11Tests.EngineIdentity(), FreezeArtifact.Json))),
            requestContract = new
            {
                requestVersion = "V2_ATTENTION_FREE",
                path = Src041BlindGeneralizationTests.ContractFreeze,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src041BlindGeneralizationTests.ContractFreeze)),
            },
            scorer = new
            {
                id = ExactScorer.ScorerId,
                path = Src041BlindGeneralizationTests.ScorerFile,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src041BlindGeneralizationTests.ScorerFile)),
            },
            harness = new { path = Src041BlindScoreTests.HarnessFile, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src041BlindScoreTests.HarnessFile)) },
            preregistration = new { commit = "35a2040", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Src041BlindGeneralizationTests.Root}/SRC-041.preregistration.v1_1.json")) },
            packingPolicy = "FIXED_OWNED_COUNT_120 (default); COHERENT_REGION_SEGMENTATION_V1 not selected",
            proposals = new
            {
                commit = "10b3317",
                path = Src041BlindScoreTests.Proposals,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src041BlindScoreTests.Proposals)),
                gitBlob = Src029BlindScoreTests.GitBlob(TestRepository.Path(Src041BlindScoreTests.Proposals)),
                contentRead = false,
            },
            rules = new[]
            {
                "reveal once; the raw score is committed as written, a bad result included",
                "residuals are classified afterwards; V1.1 is not changed and this baseline is not rewritten",
                "a Gold error found later becomes a new Gold revision; this score stays against the Gold pinned here",
                "only then: SRC041_HISTORICAL_297_DELTA_AUDIT_V1",
            },
        };
        // The registry file as a whole and the suite count record the corpus at the reveal; the harness hash is
        // superseded by the append-only amendment, whose exact before/after pair AssertPinned enforces.
        RevealPin.Verify(Src041BlindScoreTests.Root, "SRC-041.reveal-pin.v1_1.json", pin,
            "gold.registry.sha256", "checkpoint.fullSuite", "harness.sha256");
    }
}
