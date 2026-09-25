using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;
using V12 = DocxHeaderExtractor.Tests.GenericAudit.V1_2;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// PDF_SOURCE_FACTS_V2_DEVELOPMENT_DIAGNOSTIC: GENERIC_AUDIT_ENGINE_V1.2, unchanged, over PDF_SOURCE_FACTS_V1 and over
/// V2, scored with GENERIC_EXACT_SCORER_V1 against each document's frozen Gold. The documents are the ones V1.2 was
/// developed on or has already been revealed on, so nothing here is a held-out measurement: SRC-053's raw score stays
/// F1 0 (bf15b80), and the V2 figure beside it is causal evidence for the fix, not a result. The V1 column must
/// reproduce the committed scores; the V2 column shows what the facts alone change. SRC-054 is not run.
/// Written once with A99_FACTS_V2_DIAGNOSTIC=1, or when the artifact is missing.
/// </summary>
public sealed class PdfSourceFactsV2DiagnosticTests
{
    private const string Dir = "eval/a99-closed-loop/pdf-source-facts-v2";

    private static readonly (string Id, string Pdf, string? CommittedScore)[] Documents =
    [
        ("SRC-029", Src029SourceReviewTests.Pdf, null),
        ("SRC-041", Src041BlindGeneralizationTests.Pdf, null),
        ("SRC-042", Src042BlindGeneralizationTests.Pdf, "eval/a99-closed-loop/generic-audit-v1_2/SRC-042.blind-score.v1_2.json"),
        ("SRC-044", Src044BlindGeneralizationTests.Pdf, "eval/a99-closed-loop/generic-audit-v1_2/SRC-044.blind-score.v1_2.json"),
        ("SRC-053", Src053BlindGeneralizationTests.Pdf, "eval/a99-closed-loop/generic-audit-v1_2/SRC-053.blind-score.v1_2.json"),
    ];

    [Fact]
    public void Score_v1_2_over_both_fact_versions()
    {
        var artifact = TestRepository.Path($"{Dir}/pdf-source-facts-v2-diagnostic.v1.json");
        if (File.Exists(artifact) && Environment.GetEnvironmentVariable("A99_FACTS_V2_DIAGNOSTIC") != "1") return;

        var rows = Documents.Select(d =>
        {
            var gold = $"eval/a99-closed-loop/gold/{d.Id}.gold.json";
            var v1 = Headline(d.Pdf, gold, PdfSourceFactsVersion.V1_NominalFontSize);
            var v2 = Headline(d.Pdf, gold, PdfSourceFactsVersion.V2_EffectivePointSize);
            if (d.CommittedScore is { } committed)
            {
                // The V1 column is the committed raw score, reproduced.
                using var score = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(committed)));
                Assert.Equal(score.RootElement.GetProperty("headline").GetProperty("f1").GetDouble(), v1.f1);
            }
            return new
            {
                documentId = d.Id,
                role = d.Id == "SRC-053" ? "the source that exposed PDF_TEXT_MATRIX_TYPOGRAPHY_NOT_READ; its held-out score stays the raw F1 0" : "already revealed / developed on: a regression check of the facts change",
                gold = new { path = gold, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(gold)) },
                pdfSourceFactsV1 = v1,
                pdfSourceFactsV2 = v2,
            };
        }).ToArray();

        FreezeArtifact.AssertJson(Dir, "pdf-source-facts-v2-diagnostic.v1.json", new
        {
            artifactKind = "a99_pdf_source_facts_development_diagnostic",
            study = "PDF_SOURCE_FACTS_V2_DEVELOPMENT_DIAGNOSTIC",
            engine = V12.SemanticAuditEngine.EngineId,
            engineOverV2 = PdfSourceFactsV2Evidence.EngineId,
            scorer = ExactScorer.ScorerId,
            status = "DEVELOPMENT_DIAGNOSTIC - not a held-out measurement; no engine change was made against it",
            modelProviderVlmCalls = 0,
            rows,
        });
    }

    private sealed record Result(int goldClaims, int engineTrue, int truePositives, int falsePositives, int falseNegatives,
        double precision, double recall, double f1, int needsReviewOnGold);

    private static Result Headline(string pdf, string gold, PdfSourceFactsVersion facts)
    {
        var path = TestRepository.Path(pdf);
        var hypotheses = V12.SemanticAuditEngine.Propose(PdfSourceFactsV2Evidence.FromPdf(path, facts));
        var proposals = Path.Combine(Path.GetTempPath(), $"a99-facts-{Guid.NewGuid():N}.json");
        try
        {
            // The same shape and the same filter as a committed proposals file.
            File.WriteAllText(proposals, JsonSerializer.Serialize(new
            {
                hypotheses = hypotheses.Where(h => h.ProposedIsHeading != "FALSE" || h.Prominence >= 1000).ToArray(),
            }, FreezeArtifact.Json));
            var universe = ExactScorer.Universe.For("PDF", path);
            var claims = ExactScorer.ReadGold(TestRepository.Path(gold), universe);
            using var headline = JsonDocument.Parse(JsonSerializer.Serialize(
                ExactScorer.Compute(claims, ExactScorer.ReadProposals(proposals, universe)).Headline(), FreezeArtifact.Json));
            var h = headline.RootElement;
            return new Result(h.GetProperty("goldClaims").GetInt32(), h.GetProperty("TRUE").GetInt32(),
                h.GetProperty("truePositives").GetInt32(), h.GetProperty("falsePositives").GetInt32(), h.GetProperty("falseNegatives").GetInt32(),
                h.GetProperty("truePrecision").GetDouble(), h.GetProperty("trueRecall").GetDouble(), h.GetProperty("f1").GetDouble(),
                h.GetProperty("goldBuckets").GetProperty("EXACT_NEEDS_REVIEW").GetInt32());
        }
        finally
        {
            File.Delete(proposals);
        }
    }
}
