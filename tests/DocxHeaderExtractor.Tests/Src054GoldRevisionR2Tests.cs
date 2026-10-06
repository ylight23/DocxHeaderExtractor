using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC-054 Gold R2 (user-approved 2026-09-26): Gold R1 (3d52729, 296 claims) less one and plus two occurrences, after
/// three adjudication errors of the source review were found from source evidence while classifying the held-out
/// residuals (96e9e5e).
/// <para>
/// Two layers of authority stay apart. R1 is the Gold frozen at the reveal: the raw held-out score (912dcc7, F1 0.799)
/// is and stays scored against it, and it is kept byte for byte at <see cref="SourcePdfCorpus.Src054GoldR1"/>. R2 is
/// the corrected source authority from here on; the score of the same committed proposals against it is a diagnostic,
/// never a new held-out score - the pipeline does not benefit backwards in time from a Gold correction.
/// Written once with A99_SRC054_R2=1; the checks run every time.
/// </para>
/// </summary>
public sealed class Src054GoldRevisionR2Tests
{
    private const string Dir = "eval/a99-closed-loop/source-review-v1";
    private const string Record = Dir + "/SRC-054.gold-revision-r2.v1.json";
    private const string Removed = "L1312:S0";
    private static readonly string[] Added = ["L0508:S0", "L0582:S0"];
    private const string Template = "L0610:S0"; // 'Results from Borrowing activities', the Gold sibling of both additions

    /// <summary>Gold R2 byte for byte; the authored Gold became R3 after it (<see cref="Src054GoldRevisionR3Tests"/>).</summary>
    internal const string GoldR2 = "eval/a99-closed-loop/gold-history/SRC-054.gold.r2.json";

    private static readonly object[] Corrections =
    [
        new
        {
            alias = Removed,
            text = "Total Guarantees and Credit Enhancements Received . . . . .",
            r1 = "HEADING",
            r2 = "NON_HEADING",
            error = "a table's total row - bold, with dot leaders to its figures - admitted because the review tool's prose test skipped the heading below it",
            sourceEvidence = "Table 21's last row, dot leaders, the next line is the 11pt heading 'Grant Making Facilities'",
        },
        new
        {
            alias = Added[0],
            text = "Results from Lending Activities",
            r1 = "NON_HEADING",
            r2 = "HEADING",
            error = "a page-initial 10pt bold section label rejected because the plain label under it ('Loan Interest Revenue', S054_Q1) was not skipped by the review tool's prose test",
            sourceEvidence = "10pt Times-Bold at the margin atop p14, over the S054_Q1 label and its prose; its sibling 'Results from Borrowing activities' (p16) is a heading in R1",
        },
        new
        {
            alias = Added[1],
            text = "Results from Investing activities",
            r1 = "NON_HEADING",
            r2 = "HEADING",
            error = "the same as the line above ('Net Investment Revenue' under it)",
            sourceEvidence = "10pt Times-Bold at the margin atop p16, over the S054_Q1 label and its prose; the same sibling",
        },
    ];

    [Fact]
    public void Revise()
    {
        if (Environment.GetEnvironmentVariable("A99_SRC054_R2") != "1") return;
        var live = TestRepository.Path(SourcePdfCorpus.Src054Gold);
        Assert.Equal(CanonicalArtifactHash.OfTextFile(TestRepository.Path(SourcePdfCorpus.Src054GoldR1)), CanonicalArtifactHash.OfTextFile(live)); // one-shot

        var r2 = BuildR2();
        var diagnostic = Diagnostic(r2);
        FreezeArtifact.AssertJson(Dir, "SRC-054.gold-revision-r2.v1.json", new
        {
            artifactKind = "a99_gold_revision",
            authorityId = "SRC-054",
            approvedBy = "USER",
            approvedAt = "2026-09-26",
            modelProviderVlmCalls = 0,
            r1 = new
            {
                path = SourcePdfCorpus.Src054GoldR1,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(SourcePdfCorpus.Src054GoldR1)),
                commit = "3d52729",
                total = 296,
                role = "the Gold frozen at the reveal: the held-out authority of the raw score (912dcc7, F1 0.799), kept byte for byte, never overwritten",
            },
            r2 = new
            {
                path = SourcePdfCorpus.Src054Gold,
                total = 297,
                role = "the corrected source authority from this revision on",
            },
            foundBy = "classifying the held-out residuals (96e9e5e): the engine was right on all three, and each correction rests on source evidence alone",
            corrections = Corrections,
            correctedGoldDiagnostic = diagnostic,
            rule = "the pipeline does not benefit backwards in time from a Gold correction: the held-out score stays F1 0.799 against R1; the figure above is a diagnostic of the same committed proposals against R2",
        });

        var record = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Record));
        r2["approval"]!["approvalBasis"] = "USER_APPROVED_GOLD_REVISION";
        r2["approval"]!["approvedAt"] = "2026-09-26";
        var provenance = r2["provenance"]!.AsArray();
        provenance.Add(new JsonObject
        {
            ["path"] = "gold-revision:SRC-054:r1-296-to-r2-297:2026-09-26",
            ["sha256"] = CanonicalArtifactHash.OfTextFile(TestRepository.Path(SourcePdfCorpus.Src054GoldR1)),
            ["role"] = "GOLD_REVISION_PREDECESSOR",
        });
        provenance.Add(new JsonObject { ["path"] = Record, ["sha256"] = record, ["role"] = "GOLD_REVISION_RECORD" });
        File.WriteAllBytes(live, new UTF8Encoding(false).GetBytes(r2.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n")));
    }

    [Fact]
    public void The_authored_gold_is_r1_with_exactly_the_approved_corrections()
    {
        if (!File.Exists(TestRepository.Path(Record))) return; // not revised yet
        using var r1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(SourcePdfCorpus.Src054GoldR1)));
        using var live = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldR2)));
        static string[] Claims(JsonDocument d) => d.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(c => c.GetRawText()).ToArray();
        static string First(string claim) => JsonDocument.Parse(claim).RootElement.GetProperty("sourceParts")[0].GetProperty("sourceAlias").GetString()!;

        var before = Claims(r1);
        var after = Claims(live);
        Assert.Equal(297, live.RootElement.GetProperty("semanticHeadingTotal").GetInt32());
        Assert.Equal(297, after.Length);
        // Every R1 claim but the removed one is in R2 unchanged; the only new claims are the two additions.
        Assert.Equal(before.Where(c => First(c) != Removed).Order(StringComparer.Ordinal),
            after.Where(c => !Added.Contains(First(c))).Order(StringComparer.Ordinal));
        Assert.Equal(Added, after.Select(First).Where(Added.Contains).Order(StringComparer.Ordinal));
        Assert.Equal(BuildR2()["occurrence"]!["claims"]!.ToJsonString(), JsonNode.Parse(live.RootElement.GetProperty("occurrence").GetProperty("claims").GetRawText())!.ToJsonString());
    }

    /// <summary>R1's claims less the total row, plus the two section labels shaped like their sibling, in source order.</summary>
    private static JsonNode BuildR2()
    {
        var gold = JsonNode.Parse(File.ReadAllText(TestRepository.Path(SourcePdfCorpus.Src054GoldR1)))!;
        var atoms = PdfSourceOccurrenceAdapter.Build(TestRepository.Path(SourcePdfCorpus.Src054)).Atoms;
        var byAlias = atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        var claims = gold["occurrence"]!["claims"]!.AsArray().Select(c => c!.DeepClone()).ToList();
        Assert.Single(claims, c => c["sourceParts"]![0]!["sourceAlias"]!.GetValue<string>() == Removed);
        claims.RemoveAll(c => c["sourceParts"]![0]!["sourceAlias"]!.GetValue<string>() == Removed);
        var template = claims.Single(c => c["sourceParts"]![0]!["sourceAlias"]!.GetValue<string>() == Template);
        foreach (var alias in Added)
        {
            Assert.DoesNotContain(claims, c => c["sourceParts"]![0]!["sourceAlias"]!.GetValue<string>() == alias);
            var atom = byAlias[alias];
            var binding = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(
                [new SemanticSourcePart(alias, CanonicalSemanticSelectionMode.WholeAlias, null, null)]));
            Assert.True(binding.IsBound, binding.Reason);
            var claim = template.DeepClone();
            claim["approvedWording"] = atom.Text;
            claim["sourceParts"]![0]!["sourceAlias"] = alias;
            claim["identity"] = binding.Identity;
            claim["projectedText"] = atom.Text;
            var bound = claim["boundParts"]![0]!;
            bound["sourceAlias"] = alias;
            bound["page"] = atom.Page;
            bound["row"] = atom.Row;
            bound["utf16Span"] = new JsonObject { ["start"] = 0, ["end"] = atom.Text.Length };
            bound["text"] = atom.Text;
            claim["pattern"] = "GOLD_REVISION_R2_SECTION_LABEL";
            claim["evidence"] = "Gold R2 (user-approved 2026-09-26): a page-initial 10pt bold standalone label at the margin over its own region, like its Gold sibling 'Results from Borrowing activities'; rejected in R1 by a review-tool error";
            claims.Add(claim);
        }
        var ordinal = atoms.ToDictionary(a => a.Alias, a => a.Ordinal, StringComparer.Ordinal);
        var ordered = claims.OrderBy(c => ordinal[c["boundParts"]![0]!["sourceAlias"]!.GetValue<string>()])
            .ThenBy(c => c["boundParts"]![0]!["utf16Span"]!["start"]!.GetValue<int>()).ToArray();
        gold["occurrence"]!["claims"] = new JsonArray(ordered);
        gold["semanticHeadingTotal"] = ordered.Length;
        return gold;
    }

    private static object Diagnostic(JsonNode r2)
    {
        var path = Path.Combine(Path.GetTempPath(), $"a99-src054-r2-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, r2.ToJsonString(FreezeArtifact.Json));
            var universe = ExactScorer.Universe.For("PDF", TestRepository.Path(SourcePdfCorpus.Src054));
            var score = ExactScorer.Compute(ExactScorer.ReadGold(path, universe),
                ExactScorer.ReadProposals(TestRepository.Path(SourcePdfCorpus.Src054Proposals), universe));
            using var headline = JsonDocument.Parse(JsonSerializer.Serialize(score.Headline(), FreezeArtifact.Json));
            var h = headline.RootElement;
            return new
            {
                status = "DIAGNOSTIC - the committed blind proposals (44b88f4) against Gold R2; not a held-out score",
                scorer = ExactScorer.ScorerId,
                goldClaims = h.GetProperty("goldClaims").GetInt32(),
                truePositives = h.GetProperty("truePositives").GetInt32(),
                falsePositives = h.GetProperty("falsePositives").GetInt32(),
                falseNegatives = h.GetProperty("falseNegatives").GetInt32(),
                precision = h.GetProperty("truePrecision").GetDouble(),
                recall = h.GetProperty("trueRecall").GetDouble(),
                f1 = h.GetProperty("f1").GetDouble(),
            };
        }
        finally
        {
            File.Delete(path);
        }
    }
}
