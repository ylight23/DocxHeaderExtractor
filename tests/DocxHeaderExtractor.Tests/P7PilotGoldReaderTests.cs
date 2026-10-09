using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7PilotGoldReaderTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly byte[] Policy = Bytes(new { approvalSha256 = "user-approval", policy = "independent-title-subtitle" });
    private static readonly ReviewOccurrence[] Source = [new("A", 1, 6), new("B", 1, 4), new("C", 1, 4), new("D", 2, 3)];
    private static readonly ReviewedPart[] Parts = [new("A", 0, 6), new("B", 0, 4)];
    private static byte[] Gold() => Bytes(new
    {
        version = P7ApprovedPilotGold.Version, status = "USER_APPROVED_PILOT_GOLD",
        offsetConvention = P7ApprovedPilotGold.OffsetConvention, approvalSha256 = "user-approval",
        evaluationPolicySha256 = Hash(Policy), document = "SYNTHETIC",
        scope = new EvaluationScope(P7EvaluationUniverse.Version, "pdf", "universe", "snapshot", [1], P7EvaluationUniverse.CrossingPolicy),
        reviewedRows = new PilotApprovedRow[] {
            new("A", "native-a", 0, 1, "ESTABLISHES_STRUCTURE", true, true, "H1"),
            new("B", "native-b", 1, 1, "ESTABLISHES_STRUCTURE", true, false, "H1"),
            new("C", "native-c", 2, 1, "OTHER", false, false, null) },
        annotations = new EvaluationAnnotation[] {
            new("A", "ADJUDICATED", "ESTABLISHES_STRUCTURE", true, Parts),
            new("B", "ADJUDICATED", "ESTABLISHES_STRUCTURE", true, Parts),
            new("C", "ADJUDICATED", "OTHER", false, []),
            new("D", "OUT_OF_EVALUATION_SCOPE", null, null, null) },
        units = new PilotGoldUnit[] { new("A", Parts, "C", false) }
    });

    private static byte[] Score(PilotScoringInput gold)
    {
        PilotPrediction[][] batches = [
            [new("exact", "A", Parts)], [],
            [new("under", "A", [Parts[0]])],
            [new("over", "A", [.. Parts, new("C", 0, 4)])],
            [new("fp", "C", [new("C", 0, 4)])],
            [new("cross", "A", [.. Parts, new("C", 0, 4), new("D", 0, 3)])],
            [new("one", "A", Parts), new("two", "A", Parts)],
            [new("invalid", "UNKNOWN", [new("UNKNOWN", 0, 1)])] ];
        var scores = batches.Select(p => P7PilotScorer.Score(gold.Scope, Source, gold.Annotations, gold.Units, p)).ToArray();
        Assert.Equal(1, scores[0].Exact); Assert.Equal(2, scores[0].MembershipTP);
        Assert.Equal(2, scores[1].MembershipFN); Assert.Equal("UNDEREXTENT", scores[2].Boundaries[0].Outcome);
        Assert.Equal("OVEREXTENT", scores[3].Boundaries[0].Outcome);
        Assert.Equal(1, scores[4].MembershipFP); Assert.Equal(1, scores[4].NotEvaluable);
        Assert.Equal(1, scores[5].MembershipFP); Assert.Equal(1, scores[5].NotEvaluable);
        Assert.Equal(4, scores[5].Boundaries[0].FullPrediction.Count);
        Assert.Equal(1, scores[6].OverlappingPairs); Assert.Equal(1, scores[6].DuplicateAnchors);
        Assert.Equal(1, scores[7].ContractInvalid); Assert.Null(scores[7].MembershipTP);
        return Bytes(scores);
    }

    [Theory]
    [InlineData("DELETED")]
    [InlineData("ALL_TEXT_CHANGED")]
    [InlineData("UNKNOWN_ROLE_ADDED")]
    [InlineData("MALFORMED_UNSCORED_SIDECAR")]
    public void Actual_file_reader_and_all_scoring_fields_are_independent_of_sidecar(string mutation)
    {
        // Only test-owned files in a fresh isolated directory; never touch the real Gold archive.
        var directory = Path.Combine(Path.GetTempPath(), "p7-reader-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var goldPath = Path.Combine(directory, "gold.json"); var policyPath = Path.Combine(directory, "policy.json");
        var sidecarPath = Path.Combine(directory, "reviewer-interpretations.json");
        try
        {
            var gold = Gold(); File.WriteAllBytes(goldPath, gold); File.WriteAllBytes(policyPath, Policy);
            var sidecar = JsonNode.Parse("{\"notes\":[{\"role\":\"OLD_ROLE\",\"explanation\":\"Old explanation\",\"alternativeInterpretation\":\"Another view\"}]}")!;
            File.WriteAllBytes(sidecarPath, Bytes(sidecar));
            var before = P7PilotGoldReader.Load(goldPath, policyPath); var baseline = Score(before);
            if (mutation == "DELETED") File.Delete(sidecarPath);
            else if (mutation == "MALFORMED_UNSCORED_SIDECAR") File.WriteAllBytes(sidecarPath, "{not-json"u8.ToArray());
            else
            {
                if (mutation == "ALL_TEXT_CHANGED")
                    foreach (var key in sidecar["notes"]![0]!.AsObject().Select(p => p.Key).ToArray())
                        sidecar["notes"]![0]![key] = "Contradictory arbitrary interpretation " + key;
                else sidecar["notes"]!.AsArray().Add(new JsonObject { ["role"] = "NEVER_BEFORE_SEEN_ROLE", ["explanation"] = "Not a task label" });
                File.WriteAllBytes(sidecarPath, Bytes(sidecar));
            }
            var after = P7PilotGoldReader.Load(goldPath, policyPath);
            Assert.Equal(Bytes(before), Bytes(after)); Assert.Equal(baseline, Score(after));
            Assert.Equal(gold, File.ReadAllBytes(goldPath));
        }
        finally
        {
            foreach (var file in new[] { goldPath, policyPath, sidecarPath }) if (File.Exists(file)) File.Delete(file);
            Directory.Delete(directory); // no recursive deletion; test-created empty directory only
        }
    }

    [Fact] public void Policy_byte_change_is_not_treated_like_unscored_sidecar_change() =>
        Assert.Throws<InvalidOperationException>(() => P7PilotGoldReader.Read(Gold(), [.. Policy, (byte)' ']));
    [Fact] public void Approval_mismatch_stays_fail_closed()
    {
        var policy = Bytes(new { approvalSha256 = "different" }); var gold = JsonNode.Parse(Gold())!;
        gold["evaluationPolicySha256"] = Hash(policy);
        Assert.Throws<InvalidOperationException>(() => P7PilotGoldReader.Read(Bytes(gold), policy));
    }
    [Fact] public void Unapproved_gold_version_is_rejected()
    {
        var gold = JsonNode.Parse(Gold())!; gold["status"] = "PROPOSED";
        Assert.Throws<InvalidOperationException>(() => P7PilotGoldReader.Read(Bytes(gold), Policy));
    }
    [Fact] public void Role_in_scored_decision_row_is_rejected_unlike_unknown_sidecar_role()
    {
        var gold = JsonNode.Parse(Gold())!; gold["reviewedRows"]![0]!["unitRole"] = "UNKNOWN_ROLE";
        Assert.Throws<InvalidOperationException>(() => P7PilotGoldReader.Read(Bytes(gold), Policy));
    }
    [Fact] public void Real_qualification_receipt_requires_full_score_isolation_and_locked_provider()
    {
        var bytes = File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.gold-v2-freeze-qualification.v1.json"));
        Assert.Equal("e5076f39fb3508b22f965c6c650c9df279802b70756f8c6dc52142b2f41098d9", Hash(bytes));
        using var json = JsonDocument.Parse(bytes);
        var r = json.RootElement;
        Assert.Equal("614b2e09e7568446d343dbab18f4f9d405007657d6f5a665fb1538909de073f7", r.GetProperty("goldManifestSha256").GetString());
        Assert.Equal("1083c2ddd1b9183225feeba4638a751f9be228cd8e6a535aa0203d5a3ad5909a", r.GetProperty("scorerManifestSha256").GetString());
        Assert.Equal(15, r.GetProperty("sidecarIsolationDocumentChecks").GetInt32());
        Assert.Equal(123, r.GetProperty("sidecarIsolationScoreComparisons").GetInt32());
        Assert.Equal(213, r.GetProperty("adjudicatedMembershipRows").GetInt32());
        Assert.Equal(15, r.GetProperty("oracleExactBoundaries").GetInt32());
        Assert.Equal(20, r.GetProperty("explicitUtf16SpansValidated").GetInt32());
        Assert.True(r.GetProperty("allParsedGoldAndFullScoresByteIdentical").GetBoolean());
        Assert.True(r.GetProperty("knownMembershipFPsScoredDespiteNotEvaluableBoundary").GetBoolean());
        Assert.Equal(0, r.GetProperty("providerCalls").GetInt32());
        Assert.Equal("NOT_FROZEN", r.GetProperty("requestManifests").GetString());
        Assert.Equal("LOCKED", r.GetProperty("productionPromotion").GetString());
    }

    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
