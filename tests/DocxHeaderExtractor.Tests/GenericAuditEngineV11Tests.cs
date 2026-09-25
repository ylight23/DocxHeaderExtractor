using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// GENERIC_AUDIT_ENGINE_V1.1 - the six generic gaps of SRC029_BLIND_GENERALIZATION_AUDIT_V1, and the
/// measurements that decide whether it is fit to be frozen for SRC-041.
/// <para>
/// DOC-0123 and DOC-0133 are the calibration set V1 was built on; SRC-029 was V1's blind test and is
/// V1.1's development evidence - its numbers here are not a generalization result and are labelled so.
/// V1 (GenericAudit/, frozen with the SRC-029 pre-registration) is untouched: its proposals are scored
/// again beside V1.1's by the same scorer, which first has to reproduce V1's committed SRC-029 score.
/// </para>
/// </summary>
public sealed partial class GenericAuditEngineV11Tests
{
    private const string Root = "eval/a99-closed-loop/generic-audit-v1_1";
    private const string V1Root = "eval/a99-closed-loop/generic-audit-v1";

    public static TheoryData<string, string, string, string> Documents => new()
    {
        { "DOC-0123", "DOCX", "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/038_WB_Works_DB_SingleStage_NoSEASH_2025.docx", "CALIBRATION" },
        { "DOC-0133", "PDF", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/048_IBRD_Financial_Statements_March_2025.pdf", "CALIBRATION" },
        { "SRC-029", "PDF", "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/029_WB_RFP_Works_DesignBuild_2021.pdf", "DEVELOPMENT (V1 blind test; not a generalization result for V1.1)" },
    };

    internal static IReadOnlyList<SemanticHypothesis> Run(string media, string path)
    {
        var profile = media == "DOCX"
            ? SourceEvidenceProfile.FromDocx(TestRepository.Path(path))
            : SourceEvidenceProfile.FromPdf(TestRepository.Path(path));
        return SemanticAuditEngine.Propose(profile);
    }

    internal static readonly string[] EngineFiles =
    [
        "tests/DocxHeaderExtractor.Tests/GenericAudit/V1_1/SourceEvidence.cs",
        "tests/DocxHeaderExtractor.Tests/GenericAudit/V1_1/SemanticAuditEngine.cs",
    ];

    internal static object EngineIdentity() => new
    {
        engine = SemanticAuditEngine.EngineId,
        files = EngineFiles.Select(f => new { path = f, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(f)) }).ToArray(),
        constants = SemanticAuditEngine.Constants,
        ontology = OccurrenceSemanticAxesV3Id,
    };

    internal const string OccurrenceSemanticAxesV3Id = "OCCURRENCE_SEMANTIC_AXES_V3";

    [Theory]
    [MemberData(nameof(Documents))]
    public void Propose_and_freeze(string id, string media, string path, string role)
    {
        var hypotheses = Run(media, path);
        Assert.Equal(JsonSerializer.Serialize(hypotheses), JsonSerializer.Serialize(Run(media, path))); // deterministic

        FreezeArtifact.AssertJson(Root, $"{id}.proposals.v1_1.json", new
        {
            artifactKind = "a99_generic_audit_proposals",
            engineIdentity = EngineIdentity(),
            documentId = id,
            role,
            source = new { path, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(path)), media },
            modelCalls = 0,
            counts = new
            {
                sourceParts = hypotheses.Sum(h => h.Parts.Length),
                hypotheses = hypotheses.Count,
                leadParts = hypotheses.Sum(h => h.Parts.Count(p => p.Verbatim is not null)),
                byProposal = hypotheses.GroupBy(h => h.ProposedIsHeading).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                byRoles = hypotheses.GroupBy(h => string.Join("+", h.OccurrenceRoles)).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            },
            // Every TRUE and NEEDS_REVIEW hypothesis, and every FALSE one that was set apart from the body.
            hypotheses = hypotheses.Where(h => h.ProposedIsHeading != "FALSE" || h.Prominence >= 1000).ToArray(),
        });
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Score_v1_and_v1_1_against_gold(string id, string media, string path, string role)
    {
        var universe = ExactScorer.Universe.For(media, TestRepository.Path(path));
        var goldPath = TestRepository.Path($"{GoldAuthoredSourceTests.AuthoredRoot}/{id}.gold.json");
        var claims = ExactScorer.ReadGold(goldPath, universe);
        var v1Path = TestRepository.Path($"{V1Root}/{id}.proposals.v1.json");
        var v11Path = TestRepository.Path($"{Root}/{id}.proposals.v1_1.json");
        var v1 = ExactScorer.Compute(claims, ExactScorer.ReadProposals(v1Path, universe));
        var v11 = ExactScorer.Compute(claims, ExactScorer.ReadProposals(v11Path, universe));

        FreezeArtifact.AssertJson(Root, $"{id}.score.v1_1.json", new
        {
            artifactKind = "a99_generic_audit_engine_comparison",
            scorer = ExactScorer.ScorerId,
            documentId = id,
            role,
            gold = new { path = $"{GoldAuthoredSourceTests.AuthoredRoot}/{id}.gold.json", sha256 = CanonicalArtifactHash.OfTextFile(goldPath), claims = claims.Count },
            proposals = new
            {
                v1 = new { path = $"{V1Root}/{id}.proposals.v1.json", sha256 = CanonicalArtifactHash.OfTextFile(v1Path) },
                v1_1 = new { path = $"{Root}/{id}.proposals.v1_1.json", sha256 = CanonicalArtifactHash.OfTextFile(v11Path) },
            },
            modelProviderVlmCalls = 0,
            v1 = new { headline = v1.Headline(), axesOnTruePositives = v1.Axes() },
            v1_1 = new { headline = v11.Headline(), axesOnTruePositives = v11.Axes() },
            // Every Gold claim whose bucket moved, with both engines' hypotheses: gains and regressions alike.
            changedClaims = v1.Rows.Zip(v11.Rows).Where(z => z.First.Bucket != z.Second.Bucket).Select(z => new
            {
                goldText = z.First.Claim.Text,
                v1 = z.First.Bucket,
                v1_1 = z.Second.Bucket,
                regression = z.First.Bucket == "EXACT_TRUE",
                v1_1Engine = (z.Second.Exact is not null ? [z.Second.Exact] : z.Second.Overlapping)
                    .Select(h => new { h.Text, state = h.State, evidence = h.Evidence.Where(e => !e.StartsWith("followed by", StringComparison.Ordinal)).ToArray() }).ToArray(),
            }).ToArray(),
            v1_1Residuals = v11.Residuals(),
        });
    }

    /// <summary>The scorer is the protocol's: on V1's SRC-029 proposals it must give the committed raw score (adf11f4).</summary>
    [Fact]
    public void The_scorer_reproduces_the_committed_v1_src029_score()
    {
        const string pdf = "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/029_WB_RFP_Works_DesignBuild_2021.pdf";
        var universe = ExactScorer.Universe.For("PDF", TestRepository.Path(pdf));
        var claims = ExactScorer.ReadGold(TestRepository.Path($"{GoldAuthoredSourceTests.AuthoredRoot}/SRC-029.gold.json"), universe);
        using (var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{GoldAuthoredSourceTests.AuthoredRoot}/SRC-029.gold.json"))))
            Assert.Equal(
                gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().Select(c => c.GetProperty("identity").GetString()),
                claims.Select(c => c.Identity));
        var score = ExactScorer.Compute(claims, ExactScorer.ReadProposals(TestRepository.Path($"{V1Root}/SRC-029.proposals.v1.json"), universe));

        using var committed = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{V1Root}/SRC-029.blind-score.v1.json")));
        var membership = committed.RootElement.GetProperty("membership");
        Assert.Equal(membership.GetProperty("truePositives").GetInt32(), score.TruePositives);
        Assert.Equal(membership.GetProperty("falsePositives").GetInt32(), score.FalsePositives.Length);
        var buckets = committed.RootElement.GetProperty("goldBuckets");
        foreach (var bucket in buckets.EnumerateObject()) Assert.Equal(bucket.Value.GetInt32(), score.Count(bucket.Name));
        Assert.Equal(committed.RootElement.GetProperty("review").GetProperty("reviewNoiseOutsideGold").GetInt32(),
            score.ReviewOutsideGold.Count(h => !score.TouchesGold(h)));
    }

    // ---- generic in fact -------------------------------------------------------------------------

    [GeneratedRegex(@"\b(?:DOC|SRC)-\d{3,4}\b")] private static partial Regex DocumentId();
    [GeneratedRegex(@"todo10_8|heading_corpus|eval/|\.gold|gold-current|Gold[A-Z]", RegexOptions.IgnoreCase)] private static partial Regex SourcePathOrGold();
    [GeneratedRegex(@"(?:FontSize|Size|Page|Left|Row)\s*(?:==|>=|<=|>|<)\s*\d")] private static partial Regex AbsoluteGate();
    [GeneratedRegex(@"\.(?:StartsWith|EndsWith|Contains|Equals)\(\s*""|Text\s*==\s*""|""\s*==\s*\w*Text")] private static partial Regex LiteralTextComparison();
    [GeneratedRegex(@"\b(?:359|362|112|123|374|356|297)\b")] private static partial Regex GoldTotals();

    private static string CodeOf(string file) => string.Join("\n", File.ReadAllLines(TestRepository.Path(file))
        .Select(line => line.TrimStart()).Where(line => !line.StartsWith("//", StringComparison.Ordinal)));

    internal static object GenericityScan()
    {
        var evidence = CodeOf(EngineFiles[0]);
        var engine = CodeOf(EngineFiles[1]);
        var lexicalStart = evidence.IndexOf("internal static partial class LexicalShape", StringComparison.Ordinal);
        var lexicalEnd = evidence.IndexOf("internal sealed class SourceEvidenceProfile", StringComparison.Ordinal);
        var outsideLexical = evidence[..lexicalStart] + evidence[lexicalEnd..];
        var all = engine + "\n" + evidence;
        return new
        {
            documentIdReferences = DocumentId().Matches(all).Count,
            sourcePathOrGoldReferences = SourcePathOrGold().Matches(all).Count,
            absoluteTypographyOrPageGates = AbsoluteGate().Matches(all).Count,
            literalTextComparisonsInEngine = LiteralTextComparison().Matches(engine).Count,
            regexInEngine = Regex.Matches(engine, @"\bRegex\b").Count,
            literalTextOutsideLexicalShapes = LiteralTextComparison().Matches(outsideLexical).Count,
            goldTotalLiterals = GoldTotals().Matches(all).Count,
            repeatStatusReferences = Regex.Matches(all, @"RepeatStatus|repeatStatus").Count,
            lexicalShapes = Regex.Matches(evidence[lexicalStart..lexicalEnd], @"public static (?:bool|string\??|\(int Open, int Close\)) (\w+)").Select(m => m.Groups[1].Value).ToArray(),
            modelCalls = 0,
        };
    }

    [Fact]
    public void V1_1_is_generic_in_its_code()
    {
        var scan = JsonSerializer.SerializeToElement(GenericityScan());
        foreach (var name in new[] { "documentIdReferences", "sourcePathOrGoldReferences", "absoluteTypographyOrPageGates",
                     "literalTextComparisonsInEngine", "regexInEngine", "literalTextOutsideLexicalShapes", "goldTotalLiterals", "repeatStatusReferences" })
            Assert.True(scan.GetProperty(name).GetInt32() == 0, $"{name} = {scan.GetProperty(name).GetInt32()}");
    }
}
