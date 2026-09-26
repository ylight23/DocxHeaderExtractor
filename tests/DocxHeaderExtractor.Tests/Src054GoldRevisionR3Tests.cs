using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC-054 Gold R3 (user-approved 2026-09-26): Gold R2 (297) plus one occurrence the source review missed, found from
/// source evidence while adjudicating the V1.3 Gold candidate L0548 (GENERIC_AUDIT_ENGINE_V1.3.freeze).
/// <para>
/// 'Provision for losses on loans and other exposures' stands alone in plain Times-Roman at the margin atop p15, over
/// its own prose: the S054_Q1 shape the user decided TRUE ('after a finished paragraph or at a page top'). The review
/// listed that decision's occurrences by hand and this one was left off the list. It takes the axes of an S054_Q1
/// sibling. R2 is kept byte for byte at <see cref="Src054GoldRevisionR2Tests.GoldR2"/>; R1 stays the held-out
/// authority of the raw score (912dcc7, F1 0.799). Written once with A99_SRC054_R3=1; the checks run every time.
/// </para>
/// </summary>
public sealed class Src054GoldRevisionR3Tests
{
    private const string Dir = "eval/a99-closed-loop/source-review-v1";
    private const string Record = Dir + "/SRC-054.gold-revision-r3.v1.json";
    private const string Added = "L0548:S0";
    private const string Template = "L0234:S0"; // an S054_Q1 plain standalone label in R1 and R2

    [Fact]
    public void Revise()
    {
        if (Environment.GetEnvironmentVariable("A99_SRC054_R3") != "1") return;
        var live = TestRepository.Path(Src054BlindScoreTests.GoldPath);
        var r2Path = TestRepository.Path(Src054GoldRevisionR2Tests.GoldR2);
        Assert.Equal(CanonicalArtifactHash.OfTextFile(r2Path), CanonicalArtifactHash.OfTextFile(live)); // one-shot

        var r3 = BuildR3();
        FreezeArtifact.AssertJson(Dir, "SRC-054.gold-revision-r3.v1.json", new
        {
            artifactKind = "a99_gold_revision",
            authorityId = "SRC-054",
            approvedBy = "USER",
            approvedAt = "2026-09-26",
            modelProviderVlmCalls = 0,
            r1 = new
            {
                path = Src054BlindScoreTests.GoldR1,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src054BlindScoreTests.GoldR1)),
                total = 296,
                role = "the Gold frozen at the reveal: the held-out authority of the raw score (912dcc7, F1 0.799), unchanged",
            },
            r2 = new
            {
                path = Src054GoldRevisionR2Tests.GoldR2,
                sha256 = CanonicalArtifactHash.OfTextFile(r2Path),
                commit = "ca6e16d",
                total = 297,
                role = "the previous corrected authority, kept byte for byte",
            },
            r3 = new { path = Src054BlindScoreTests.GoldPath, total = 298, role = "the corrected source authority from this revision on" },
            foundBy = "adjudicating the V1.3 development Gold candidate SRC-054 L0548 from source context",
            corrections = new[]
            {
                new
                {
                    alias = Added,
                    text = "Provision for losses on loans and other exposures",
                    r2 = "NON_HEADING",
                    r3 = "HEADING",
                    error = "the S054_Q1 occurrences were listed by hand in the review tool; this page-top one was left off the list",
                    sourceEvidence = "plain Times-Roman, alone on its line at the margin atop p15 (the page number of p14 before it), over its own prose - the S054_Q1 shape",
                },
            },
            correctedGoldDiagnostic = GoldRevision.Diagnostic(r3, Src054BlindGeneralizationTests.Pdf, Src054BlindScoreTests.Proposals,
                "DIAGNOSTIC - the committed blind V1.2 proposals (44b88f4) against Gold R3; not a held-out score"),
            rule = "the pipeline does not benefit backwards in time from a Gold correction: the held-out score stays F1 0.799 against R1",
        });

        var record = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Record));
        r3["approval"]!["approvedAt"] = "2026-09-26";
        var provenance = r3["provenance"]!.AsArray();
        provenance.Add(new JsonObject
        {
            ["path"] = "gold-revision:SRC-054:r2-297-to-r3-298:2026-09-26",
            ["sha256"] = CanonicalArtifactHash.OfTextFile(r2Path),
            ["role"] = "GOLD_REVISION_PREDECESSOR",
        });
        provenance.Add(new JsonObject { ["path"] = Record, ["sha256"] = record, ["role"] = "GOLD_REVISION_RECORD" });
        File.WriteAllBytes(live, new UTF8Encoding(false).GetBytes(r3.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n")));
    }

    [Fact]
    public void The_authored_gold_is_r2_with_exactly_the_approved_addition()
    {
        if (!File.Exists(TestRepository.Path(Record))) return; // not revised yet
        using var r2 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Src054GoldRevisionR2Tests.GoldR2)));
        using var live = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Src054BlindScoreTests.GoldPath)));
        static string[] Claims(JsonDocument d) => d.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(c => c.GetRawText()).ToArray();
        static string First(string claim) => JsonDocument.Parse(claim).RootElement.GetProperty("sourceParts")[0].GetProperty("sourceAlias").GetString()!;

        var after = Claims(live);
        Assert.Equal(298, live.RootElement.GetProperty("semanticHeadingTotal").GetInt32());
        Assert.Equal(298, after.Length);
        Assert.Equal(Claims(r2).Order(StringComparer.Ordinal), after.Where(c => First(c) != Added).Order(StringComparer.Ordinal));
        Assert.Single(after, c => First(c) == Added);
        Assert.Equal(BuildR3()["occurrence"]!["claims"]!.ToJsonString(), JsonNode.Parse(live.RootElement.GetProperty("occurrence").GetProperty("claims").GetRawText())!.ToJsonString());
    }

    private static JsonNode BuildR3() => GoldRevision.Add(
        JsonNode.Parse(File.ReadAllText(TestRepository.Path(Src054GoldRevisionR2Tests.GoldR2)))!,
        Src054BlindGeneralizationTests.Pdf, Template,
        [new GoldRevision.Addition(Added, null, "S054_Q1_PLAIN_STANDALONE_LABEL",
            "Gold R3 (user-approved 2026-09-26), under S054_Q1: a short standalone line in plain Times-Roman at the margin at a page top, over its own prose; left off the review's hand list in R1 and R2")]);
}
