using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;
using V12 = DocxHeaderExtractor.Tests.GenericAudit.V1_2;
using V13 = DocxHeaderExtractor.Tests.GenericAudit.V1_3;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// GENERIC_AUDIT_ENGINE_V1.3 over PDF_SOURCE_FACTS_V3 - development measurements. Every document here has been developed
/// on or revealed already (SRC-053 and SRC-054 are now development evidence), so nothing here is a generalization result.
/// Each PDF is scored three ways with GENERIC_EXACT_SCORER_V1 against its authored Gold (SRC-054: Gold R2): V1.2 over
/// PDF_SOURCE_FACTS_V2 (the baseline), V1.2 over V3 (what the facts change alone does) and V1.3 over V3.
/// </summary>
public sealed class GenericAuditEngineV13Tests
{
    internal const string Root = "eval/a99-closed-loop/generic-audit-v1_3";

    public static TheoryData<string, string, string> Documents => new()
    {
        { "DOC-0123", "DOCX", "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/038_WB_Works_DB_SingleStage_NoSEASH_2025.docx" },
        { "DOC-0133", "PDF", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/048_IBRD_Financial_Statements_March_2025.pdf" },
        { "SRC-029", "PDF", Src029SourceReviewTests.Pdf },
        { "SRC-041", "PDF", Src041BlindGeneralizationTests.Pdf },
        { "SRC-042", "PDF", Src042BlindGeneralizationTests.Pdf },
        { "SRC-044", "PDF", Src044BlindGeneralizationTests.Pdf },
        { "SRC-053", "PDF", Src053BlindGeneralizationTests.Pdf },
        { "SRC-054", "PDF", Src054BlindGeneralizationTests.Pdf },
    };

    internal static readonly string[] EngineFiles =
    [
        "tests/DocxHeaderExtractor.Tests/GenericAudit/V1_3/SourceEvidence.cs",
        "tests/DocxHeaderExtractor.Tests/GenericAudit/V1_3/SemanticAuditEngine.cs",
    ];

    internal static object EngineIdentity() => new
    {
        engine = V13.SemanticAuditEngine.EngineId,
        sourceFacts = PdfSourceFactsVersions.Id(PdfSourceFactsVersion.V3_RobustGlyphStatistics),
        files = EngineFiles.Select(f => new { path = f, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(f)) }).ToArray(),
        constants = V13.SemanticAuditEngine.Constants,
        ontology = GenericAuditEngineV12Tests.OccurrenceSemanticAxesV3Id,
    };

    internal static IReadOnlyList<V13.SemanticHypothesis> Run(string media, string path)
    {
        var profile = media == "DOCX"
            ? V13.SourceEvidenceProfile.FromDocx(TestRepository.Path(path))
            : V13.SourceEvidenceProfile.FromPdf(TestRepository.Path(path));
        return V13.SemanticAuditEngine.Propose(profile);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Score_development_documents(string id, string media, string path)
    {
        if (Environment.GetEnvironmentVariable("A99_V13_DEV") != "1" && File.Exists(TestRepository.Path($"{Root}/{id}.dev-score.v1_3.json"))) return;
        var hypotheses = Run(media, path);
        Assert.Equal(JsonSerializer.Serialize(hypotheses), JsonSerializer.Serialize(Run(media, path))); // deterministic
        var v13 = Score(media, path, id, Serialize(hypotheses.Where(h => h.ProposedIsHeading != "FALSE" || h.Prominence >= 1000)));
        object? v12v2 = null, v12v3 = null;
        if (media == "PDF")
        {
            v12v2 = Score(media, path, id, Serialize(V12.SemanticAuditEngine.Propose(PdfSourceFactsV2Evidence.FromPdf(TestRepository.Path(path), PdfSourceFactsVersion.V2_EffectivePointSize))
                .Where(h => h.ProposedIsHeading != "FALSE" || h.Prominence >= 1000)));
            v12v3 = Score(media, path, id, Serialize(V12.SemanticAuditEngine.Propose(PdfSourceFactsV2Evidence.FromPdf(TestRepository.Path(path), PdfSourceFactsVersion.V3_RobustGlyphStatistics))
                .Where(h => h.ProposedIsHeading != "FALSE" || h.Prominence >= 1000)));
        }
        else
            v12v2 = Score(media, path, id, Serialize(GenericAuditEngineV12Tests.Run(media, path).Where(h => h.ProposedIsHeading != "FALSE" || h.Prominence >= 1000)));

        FreezeArtifact.AssertJson(Root, $"{id}.dev-score.v1_3.json", new
        {
            artifactKind = "a99_generic_audit_development_score",
            documentId = id,
            role = "DEVELOPMENT - not a generalization result",
            engineIdentity = EngineIdentity(),
            gold = new { path = $"{GoldAuthoredSourceTests.AuthoredRoot}/{id}.gold.json", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{GoldAuthoredSourceTests.AuthoredRoot}/{id}.gold.json")) },
            modelCalls = 0,
            v1_2_overFactsV2 = v12v2,
            v1_2_overFactsV3 = v12v3,
            v1_3_overFactsV3 = v13,
            v1_3_hypotheses = hypotheses.Where(h => h.ProposedIsHeading != "FALSE" || h.Prominence >= 1000).ToArray(),
        });
    }

    private static string Serialize<T>(IEnumerable<T> hypotheses) =>
        JsonSerializer.Serialize(new { hypotheses = hypotheses.ToArray() }, FreezeArtifact.Json);

    /// <summary>The scorer's headline and residuals for a proposals JSON (the committed proposals' shape).</summary>
    private static object Score(string media, string path, string id, string proposalsJson)
    {
        var file = Path.Combine(Path.GetTempPath(), $"a99-v13-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, proposalsJson);
            var universe = ExactScorer.Universe.For(media, TestRepository.Path(path));
            var claims = ExactScorer.ReadGold(TestRepository.Path($"{GoldAuthoredSourceTests.AuthoredRoot}/{id}.gold.json"), universe);
            var score = ExactScorer.Compute(claims, ExactScorer.ReadProposals(file, universe));
            return new { headline = score.Headline(), residuals = score.Residuals() };
        }
        finally
        {
            File.Delete(file);
        }
    }
}
