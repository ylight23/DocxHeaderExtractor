using System.Text.Json;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;
using V12 = DocxHeaderExtractor.Tests.GenericAudit.V1_2;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC054_BLIND_GENERALIZATION_AUDIT_V1, protocol step 5: the one reveal of GENERIC_AUDIT_ENGINE_V1.2-over-PDF_SOURCE_FACTS_V2's blind
/// SRC-054 proposals (44b88f4) against the frozen 296-claim Gold (3d52729).
/// <para>
/// Everything the reveal depends on is pinned first (<see cref="Src054RevealPinTests"/>): this harness, the
/// pinned GENERIC_EXACT_SCORER_V1, the proposals file by hash, the Gold and the registry. The raw score is
/// written once, with A99_SRC054_SCORE=1, and committed as written - a bad result included. Residuals are
/// classified afterwards; neither V1.2 nor the facts are changed and this baseline is not rewritten.
/// </para>
/// </summary>
public sealed class Src054BlindScoreTests
{
    internal const string Root = Src054BlindGeneralizationTests.Root;
    internal const string Proposals = Root + "/SRC-054.proposals.json";
    internal const string Pin = Root + "/SRC-054.reveal-pin.json";
    internal const string GoldPath = "eval/a99-closed-loop/gold/SRC-054.gold.json";
    internal const string HarnessFile = "tests/DocxHeaderExtractor.Tests/Src054BlindScoreTests.cs";

    [Fact]
    public void Score_the_blind_proposals_against_gold()
    {
        if (Environment.GetEnvironmentVariable("A99_SRC054_SCORE") != "1") return;
        AssertPinned();
        FreezeArtifact.AssertJson(Root, "SRC-054.blind-score.json", Score());
    }

    /// <summary>After the reveal: the committed score is still what the pinned inputs produce.</summary>
    [Fact]
    public void The_committed_score_is_reproducible()
    {
        if (!File.Exists(TestRepository.Path($"{Root}/SRC-054.blind-score.json"))) return; // not revealed yet
        AssertPinned();
        FreezeArtifact.AssertJson(Root, "SRC-054.blind-score.json", Score());
    }

    private static void AssertPinned()
    {
        using var pin = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Pin)));
        var p = pin.RootElement;
        Assert.Equal(p.GetProperty("harness").GetProperty("sha256").GetString(), CanonicalArtifactHash.OfTextFile(TestRepository.Path(HarnessFile)));
        Assert.Equal(p.GetProperty("scorer").GetProperty("sha256").GetString(), CanonicalArtifactHash.OfTextFile(TestRepository.Path(HeldOutProtocolV12.ScorerFile)));
        Assert.Equal(p.GetProperty("gold").GetProperty("authoredGoldSha256").GetString(), CanonicalArtifactHash.OfTextFile(TestRepository.Path(GoldPath)));
        Assert.Equal(p.GetProperty("gold").GetProperty("registryGoldSha256").GetString(), CanonicalGoldRegistry.Entry("SRC-054").GoldSha256);
        Assert.Equal(p.GetProperty("proposals").GetProperty("gitBlob").GetString(), Src029BlindScoreTests.GitBlob(TestRepository.Path(Proposals)));
        Assert.Equal(p.GetProperty("source").GetProperty("sha256").GetString(), CanonicalArtifactHash.OfBytes(TestRepository.Path(Src054BlindGeneralizationTests.Pdf)));
    }

    private static object Score()
    {
        var universe = ExactScorer.Universe.For("PDF", TestRepository.Path(Src054BlindGeneralizationTests.Pdf));
        var claims = ExactScorer.ReadGold(TestRepository.Path(GoldPath), universe);
        var score = ExactScorer.Compute(claims, ExactScorer.ReadProposals(TestRepository.Path(Proposals), universe));
        return new
        {
            artifactKind = "a99_generic_audit_blind_score",
            study = "SRC054_BLIND_GENERALIZATION_AUDIT_V1",
            engine = DocxHeaderExtractor.Tests.GenericAudit.PdfSourceFactsV2Evidence.EngineId,
            role = "HELD_OUT - the first measurement of PDF_SOURCE_FACTS_V2 (V1.2 logic)",
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
