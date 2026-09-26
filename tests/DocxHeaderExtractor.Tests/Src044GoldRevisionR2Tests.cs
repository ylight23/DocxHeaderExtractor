using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC-044 Gold R2 (user-approved 2026-09-26): Gold R1 (c2dfee5, 201 claims) plus two occurrences the source review
/// missed, found from source evidence while adjudicating the V1.3 Gold candidate L0236 (GENERIC_AUDIT_ENGINE_V1.3.freeze).
/// <para>
/// On p8 three parallel blocks each open with an 11pt bold label at the margin over left-column prose, a chart to the
/// right: 'Net Investment Portfolio' (L0208, in R1), 'Borrowing Portfolio' (L0220) and 'Equity and Capital Adequacy'
/// (L0236). Extraction joined the last two to the chart's unit line ('In billions of U.S. dollars') in one atom, and
/// the review's whole-line rule passed them over. Each is added as the verbatim title inside its atom, like SRC-054's
/// 'Equity-to-Loans Ratio', with the axes of their sibling L0208.
/// </para>
/// <para>
/// Two layers of authority stay apart, as for SRC-054: R1 is the Gold at the reveal (5cf6431), kept byte for byte at
/// <see cref="Src044BlindScoreTests.GoldR1"/>; the raw held-out score (382c58c, F1 0.9824) is and stays scored against
/// it. The score of the same proposals against R2 is a diagnostic. Written once with A99_SRC044_R2=1; the checks run
/// every time.
/// </para>
/// </summary>
public sealed class Src044GoldRevisionR2Tests
{
    private const string Dir = "eval/a99-closed-loop/source-review-v1";
    private const string Record = Dir + "/SRC-044.gold-revision-r2.v1.json";
    private const string Template = "L0208:S0"; // 'Net Investment Portfolio', the Gold sibling of both additions
    private const string UnitLine = " In billions"; // where each atom runs on into the chart's unit line
    private static readonly string[] Added = ["L0220:S0", "L0236:S0"];

    [Fact]
    public void Revise()
    {
        if (Environment.GetEnvironmentVariable("A99_SRC044_R2") != "1") return;
        var live = TestRepository.Path(Src044BlindScoreTests.GoldPath);
        Assert.Equal(CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src044BlindScoreTests.GoldR1)), CanonicalArtifactHash.OfTextFile(live)); // one-shot

        var r2 = BuildR2();
        FreezeArtifact.AssertJson(Dir, "SRC-044.gold-revision-r2.v1.json", new
        {
            artifactKind = "a99_gold_revision",
            authorityId = "SRC-044",
            approvedBy = "USER",
            approvedAt = "2026-09-26",
            modelProviderVlmCalls = 0,
            r1 = new
            {
                path = Src044BlindScoreTests.GoldR1,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src044BlindScoreTests.GoldR1)),
                commit = "c2dfee5",
                total = 201,
                role = "the Gold frozen at the reveal: the held-out authority of the raw score (382c58c, F1 0.9824), kept byte for byte, never overwritten",
            },
            r2 = new { path = Src044BlindScoreTests.GoldPath, total = 203, role = "the corrected source authority from this revision on" },
            foundBy = "adjudicating the V1.3 development Gold candidate SRC-044 L0236 from source context; its sibling L0220 has the same shape on the same page",
            corrections = Added.Select(alias => new
            {
                alias,
                text = Title(alias),
                r1 = "NON_HEADING",
                r2 = "HEADING",
                selection = "VERBATIM_TEXT: the title only; the chart's unit line in the same atom is no part of it",
                error = "extraction joined the label to the chart's unit line in one atom, and the review's whole-line rule for 11pt bold labels did not see it",
                sourceEvidence = "p8: an 11pt bold label at the margin over its own left-column prose with its chart to the right - the block shape of 'Net Investment Portfolio' (L0208, in R1)",
            }).ToArray(),
            correctedGoldDiagnostic = GoldRevision.Diagnostic(r2, Src044BlindGeneralizationTests.Pdf, Src044BlindScoreTests.Proposals,
                "DIAGNOSTIC - the committed blind V1.2 proposals (6d59c9d) against Gold R2; not a held-out score"),
            rule = "the pipeline does not benefit backwards in time from a Gold correction: the held-out score stays F1 0.9824 against R1; the figure above is a diagnostic",
        });

        var record = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Record));
        r2["approval"]!["approvalBasis"] = "USER_APPROVED_GOLD_REVISION";
        r2["approval"]!["approvedAt"] = "2026-09-26";
        var provenance = r2["provenance"]!.AsArray();
        provenance.Add(new JsonObject
        {
            ["path"] = "gold-revision:SRC-044:r1-201-to-r2-203:2026-09-26",
            ["sha256"] = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Src044BlindScoreTests.GoldR1)),
            ["role"] = "GOLD_REVISION_PREDECESSOR",
        });
        provenance.Add(new JsonObject { ["path"] = Record, ["sha256"] = record, ["role"] = "GOLD_REVISION_RECORD" });
        File.WriteAllBytes(live, new UTF8Encoding(false).GetBytes(r2.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n")));
    }

    [Fact]
    public void The_authored_gold_is_r1_with_exactly_the_approved_additions()
    {
        if (!File.Exists(TestRepository.Path(Record))) return; // not revised yet
        using var r1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Src044BlindScoreTests.GoldR1)));
        using var live = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Src044BlindScoreTests.GoldPath)));
        static string[] Claims(JsonDocument d) => d.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(c => c.GetRawText()).ToArray();
        static string First(string claim) => JsonDocument.Parse(claim).RootElement.GetProperty("sourceParts")[0].GetProperty("sourceAlias").GetString()!;

        var after = Claims(live);
        Assert.Equal(203, live.RootElement.GetProperty("semanticHeadingTotal").GetInt32());
        Assert.Equal(203, after.Length);
        // Every R1 claim is in R2 unchanged; the only new claims are the two additions.
        Assert.Equal(Claims(r1).Order(StringComparer.Ordinal), after.Where(c => !Added.Contains(First(c))).Order(StringComparer.Ordinal));
        Assert.Equal(Added, after.Select(First).Where(Added.Contains).Order(StringComparer.Ordinal));
        Assert.Equal(BuildR2()["occurrence"]!["claims"]!.ToJsonString(), JsonNode.Parse(live.RootElement.GetProperty("occurrence").GetProperty("claims").GetRawText())!.ToJsonString());
    }

    /// <summary>The label's own text: the atom up to where it runs on into the chart's unit line.</summary>
    private static string Title(string alias)
    {
        var text = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Src044BlindGeneralizationTests.Pdf)).Atoms.Single(a => a.Alias == alias).Text;
        var cut = text.IndexOf(UnitLine, StringComparison.Ordinal);
        Assert.True(cut > 0, text);
        return text[..cut];
    }

    private static JsonNode BuildR2() => GoldRevision.Add(
        JsonNode.Parse(File.ReadAllText(TestRepository.Path(Src044BlindScoreTests.GoldR1)))!,
        Src044BlindGeneralizationTests.Pdf, Template,
        Added.Select(alias => new GoldRevision.Addition(alias, Title(alias), "GOLD_REVISION_R2_SECTION_LABEL",
            "Gold R2 (user-approved 2026-09-26): an 11pt bold label at the margin over its own left-column prose with its chart to the right, like its Gold sibling 'Net Investment Portfolio'; the atom runs on into the chart's unit line, which is no part of the title")).ToArray());
}
