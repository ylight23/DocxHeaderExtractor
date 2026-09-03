using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// N0 selects a fresh review population before any output, label, or model result is read. It is
/// deliberately model-free and selects by corpus domain plus document identifier only after the
/// already-frozen A3 usability screen has admitted the document.
/// </summary>
public sealed class PdfN0ReviewPopulationBootstrapProbe
{
    private const int SelectedBudget = 160;
    private const int MinimumSelected = 20;
    private const int MinimumDecisionRelevant = 15;

    private static readonly string[] TargetStrata =
    [
        "01_phap_quy",
        "02_hop_dong_mua_sam",
        "03_tai_chinh_ke_toan",
        "04_giao_trinh",
    ];

    // A document is excluded if any prior benchmark or reviewed audit supplied its labels. This is
    // provenance policy, not a statement about extraction quality.
    private static readonly HashSet<string> PreviouslyReviewed = new(StringComparer.Ordinal)
    {
        "001", "028", "032", "041", "054", "056", "091", "092",
    };

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_N0_BOOTSTRAP_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpusRoot = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var chosen = new List<CandidateDocument>();
        foreach (var stratum in TargetStrata)
        {
            var domainDirectory = Path.Combine(corpusRoot, stratum);
            CandidateDocument? selected = null;
            foreach (var docxPath in Directory.EnumerateFiles(domainDirectory, "*.docx", SearchOption.TopDirectoryOnly)
                         .OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                var stem = Path.GetFileNameWithoutExtension(docxPath).Split('_', 2)[0];
                if (PreviouslyReviewed.Contains(stem)) continue;

                var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
                var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
                var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);
                var top = ranked.Take(SelectedBudget).ToArray();
                var answerIrrelevant = top.Count(candidate =>
                    contexts.TryGetValue(candidate.SourceId, out var context) &&
                    PdfExtractorQualityBenchmarkProbe.DeterministicExclusionReason(context) is not null);
                var decisionRelevant = top.Length - answerIrrelevant;

                if (top.Length < MinimumSelected || decisionRelevant < MinimumDecisionRelevant) continue;
                selected = new CandidateDocument(stratum, stem, docxPath, snapshot.CandidateBlocks.Count, top.Length, decisionRelevant);
                break;
            }

            Assert.NotNull(selected);
            chosen.Add(selected!);
        }

        var payload = new
        {
            artifactKind = "n0_review_population_bootstrap",
            selectionRule = new
            {
                a3Usability = $"selected@{SelectedBudget} >= {MinimumSelected} AND decisionRelevant >= {MinimumDecisionRelevant}",
                targetStrata = TargetStrata,
                excludedReviewedDocuments = PreviouslyReviewed.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                tieBreak = "lowest unreviewed numeric document id within each stratum",
            },
            documents = chosen.Select(document => new
            {
                stem = document.Stem,
                domain = document.Domain,
                relativePath = Path.GetRelativePath(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), document.Path).Replace('\\', '/'),
                sourceDocumentSha256 = Sha256(document.Path),
                candidateCount = document.CandidateCount,
                selectedAt160 = document.SelectedAt160,
                decisionRelevant = document.DecisionRelevant,
            }),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void ManifestPinsTheNewPopulationAndEvidenceContract()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var manifestPath = Path.Combine(root, "keys", "benchmark-n0", "manifest.json");
        var bootstrapPath = Path.Combine(root, "keys", "benchmark-n0", "bootstrap.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        using var bootstrap = JsonDocument.Parse(File.ReadAllText(bootstrapPath));
        var documentStems = manifest.RootElement.GetProperty("documents").EnumerateArray()
            .Select(document => document.GetProperty("stem").GetString()!).ToArray();
        Assert.Equal(["003", "029", "042", "057"], documentStems);

        var bootstrapStems = bootstrap.RootElement.GetProperty("documents").EnumerateArray()
            .Select(document => document.GetProperty("stem").GetString()!).ToArray();
        Assert.Equal(documentStems, bootstrapStems);

        foreach (var document in manifest.RootElement.GetProperty("documents").EnumerateArray())
        {
            var path = Path.Combine(root, document.GetProperty("file").GetString()!.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"N0 source document must remain available: {path}");
            Assert.Equal(document.GetProperty("sourceDocumentSha256").GetString(), Sha256(path));
        }

        var labels = manifest.RootElement.GetProperty("precisionTaxonomy").EnumerateArray()
            .Select(label => label.GetString()!).ToArray();
        Assert.Equal(["TRUE_HEADING", "TOC_ENTRY", "MULTI_HEADING_COMPOSITE", "NON_HEADING", "UNCERTAIN"], labels);

        var required = manifest.RootElement.GetProperty("artifactRetentionContract")
            .GetProperty("reviewedOutputRequiredFields").EnumerateArray().Select(field => field.GetString()).ToArray();
        Assert.Contains("sourceFactId", required);
        Assert.Contains("spanCheckpointReference", required);
        Assert.Contains("candidateId diagnostics-only", required);
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed record CandidateDocument(
        string Domain,
        string Stem,
        string Path,
        int CandidateCount,
        int SelectedAt160,
        int DecisionRelevant);
}
