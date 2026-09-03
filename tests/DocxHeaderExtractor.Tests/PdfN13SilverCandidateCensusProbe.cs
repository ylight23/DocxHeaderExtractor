using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// N1.3-S: model-free candidate/selection/eligibility census over N1.2-S's silver occurrences. It
/// reuses the established candidate/rank/selection join
/// (<see cref="PdfExtractorQualityBenchmarkProbe.Classify"/>) rather than reimplementing it, so this
/// census sits on the one join the project already trusts. No model call, no retuning, no silver-label
/// change - human audit correctness is not a precondition for this phase, only for how strongly N2-S
/// results may later be claimed.
/// <para>
/// Occurrence identity is source authority: a silver occurrence's <c>sourceLineIds</c> are resolved
/// against <em>this run's</em> extraction by exact line-id match, never by re-deriving from text and
/// never treating a candidate id as identity across runs. Before resolving anything, the packet's own
/// <c>sourceLineExtractionFingerprint</c> is recomputed from the current snapshot and compared - the
/// project's standing lesson that a representation's authority must be re-verified on the exact
/// production path, not assumed from a prior run.
/// </para>
/// <para>
/// The loss ledger buckets are mutually exclusive and ordered by where in the pipeline an occurrence
/// is first lost: an occurrence without full candidate coverage never reaches the rank/budget
/// question; one that fails rank/budget never reaches the deterministic-eligibility question. Nothing
/// is double-counted between candidate-construction loss, rank/budget loss, and deterministic-
/// eligibility loss.
/// </para>
/// </summary>
public sealed class PdfN13SilverCandidateCensusProbe
{
    private static readonly (string Stem, string Relative)[] Documents =
    [
        ("003", @"01_phap_quy\003_Luat_Doanh_nghiep_59-2020-QH14.docx"),
        ("029", @"02_hop_dong_mua_sam\029_WB_RFP_Works_DesignBuild_2021.docx"),
        ("042", @"03_tai_chinh_ke_toan\042_IDA_Financial_Statements_June_2025.docx"),
        ("057", @"04_giao_trinh\057_Quantitative_Methods_in_Finance_Lecture_Notes.docx"),
    ];

    private const int EligibilityThreshold = 15;

    [Fact]
    public void WriteCensus()
    {
        var outputDirectory = Environment.GetEnvironmentVariable("BENCH_N13_CENSUS_DIRECTORY");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        Directory.CreateDirectory(outputDirectory);

        foreach (var (stem, relative) in Documents)
        {
            var docxPath = Path.Combine(root, "todo10_8", "heading_corpus_95_word", relative);
            var silverPath = Path.Combine(root, "eval", "benchmark-n0", "silver-labels", $"{stem}-n1.2-silver-model-assisted.v1.json");
            File.WriteAllText(
                Path.Combine(outputDirectory, $"{stem}-n1.3-census.v1.json"),
                JsonSerializer.Serialize(BuildCensus(stem, docxPath, silverPath), new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Theory]
    [InlineData("003")]
    [InlineData("029")]
    [InlineData("042")]
    [InlineData("057")]
    public void CommittedCensusReproducesFromTheCurrentBuild(string stem)
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var censusPath = Path.Combine(root, "eval", "benchmark-n0", "census", $"{stem}-n1.3-census.v1.json");
        if (!File.Exists(censusPath)) return; // not yet materialized in this checkout

        var relative = Documents.First(d => d.Stem == stem).Relative;
        var docxPath = Path.Combine(root, "todo10_8", "heading_corpus_95_word", relative);
        var silverPath = Path.Combine(root, "eval", "benchmark-n0", "silver-labels", $"{stem}-n1.2-silver-model-assisted.v1.json");

        var expected = JsonSerializer.Serialize(BuildCensus(stem, docxPath, silverPath), new JsonSerializerOptions { WriteIndented = true });
        var actual = File.ReadAllText(censusPath);
        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    private static string Normalize(string json) => json.Replace("\r\n", "\n");

    private static object BuildCensus(string stem, string docxPath, string silverPath)
    {
        using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
        var silverRoot = silver.RootElement;
        var (documentSha256, fingerprint) = silverRoot.TryGetProperty("sourcePacket", out var sp)
            ? (sp.GetProperty("documentSha256").GetString()!, sp.GetProperty("sourceLineExtractionFingerprint").GetString()!)
            : (silverRoot.GetProperty("documentSha256").GetString()!, silverRoot.GetProperty("sourceLineExtractionFingerprint").GetString()!);

        Assert.Equal(documentSha256, Sha256(docxPath));

        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
        Assert.Equal(fingerprint, SourceFingerprint(snapshot.Lines));

        var indexByLineId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < snapshot.Lines.Count; i++)
            indexByLineId.TryAdd(PdfCandidateProvenance.LineId(snapshot.Lines[i]), i);

        var occurrences = silverRoot.GetProperty("headingOccurrences").EnumerateArray()
            .Select(o =>
            {
                var stableId = o.TryGetProperty("goldStableId", out var g) ? g.GetString()! : o.GetProperty("silverStableId").GetString()!;
                var lineIds = o.GetProperty("sourceLineIds").EnumerateArray().Select(l => l.GetString()!).ToArray();
                var resolved = lineIds
                    .Select(lineId => indexByLineId.TryGetValue(lineId, out var idx) ? idx : -1)
                    .Where(idx => idx >= 0)
                    .ToArray();
                return (StableId: stableId, LineIds: lineIds,
                    Occurrence: new PdfExtractorQualityBenchmarkProbe.Occurrence(stableId, [], resolved));
            })
            .ToList();

        var unresolved = occurrences.Where(o => o.Occurrence.ResolvedIndexes!.Count != o.LineIds.Length).ToArray();

        var classified = PdfExtractorQualityBenchmarkProbe.Classify(
            docxPath, occurrences.Select(o => o.Occurrence).ToList());

        var byStableId = occurrences.ToDictionary(o => o.Occurrence.Label, o => o.StableId, StringComparer.Ordinal);

        var candidateConstructionLoss = new List<object>();
        var rankBudgetLoss = new List<object>();
        var deterministicEligibilityLoss = new List<object>();
        var decisionRelevant = new List<object>();

        foreach (var c in classified)
        {
            var stableId = byStableId[c.Occurrence.Label];
            object Row(string bucket) => new
            {
                stableId,
                status = c.Status,
                selected = c.Selected,
                coveringRank = c.CoveringRank,
                deterministicExclusionReason = c.DeterministicExclusionReason,
                bucket,
            };

            if (c.Status != "full") { candidateConstructionLoss.Add(Row("candidate_construction_loss")); continue; }
            if (!c.Selected) { rankBudgetLoss.Add(Row("rank_budget_loss")); continue; }
            if (c.DeterministicExclusionReason is not null) { deterministicEligibilityLoss.Add(Row("deterministic_eligibility_loss")); continue; }
            decisionRelevant.Add(Row("decision_relevant"));
        }

        var fullCandidate = classified.Count(c => c.Status == "full");
        var selectedAt160 = classified.Count(c => c.Selected);
        var answerIrrelevant = deterministicEligibilityLoss.Count;
        var decisionRelevantCount = decisionRelevant.Count;

        return new
        {
            schemaVersion = 1,
            artifactKind = "n1_3_silver_model_free_census",
            documentId = stem,
            documentSha256,
            sourceLineExtractionFingerprint = fingerprint,
            identity = "documentSha256 + page + sourceLineIds; candidateId is diagnostics-only within this run, never cross-run identity",
            unresolvedOccurrenceCount = unresolved.Length,
            unresolvedOccurrenceStableIds = unresolved.Select(o => o.StableId).ToArray(),
            denominators = new
            {
                silverReviewed = occurrences.Count,
                fullCandidate,
                selectedAt160,
                answerIrrelevant,
                decisionRelevant = decisionRelevantCount,
            },
            lossLedger = new
            {
                candidateConstructionLoss = candidateConstructionLoss.Count,
                rankBudgetLoss = rankBudgetLoss.Count,
                deterministicEligibilityLoss = deterministicEligibilityLoss.Count,
                decisionRelevant = decisionRelevantCount,
            },
            eligibility = new
            {
                rule = $"decisionRelevant >= {EligibilityThreshold}",
                decisionRelevant = decisionRelevantCount,
                eligibleForN2S = decisionRelevantCount >= EligibilityThreshold,
            },
            occurrences = new
            {
                candidateConstructionLoss,
                rankBudgetLoss,
                deterministicEligibilityLoss,
                decisionRelevant,
            },
        };
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string SourceFingerprint(IReadOnlyList<PdfLine> lines) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', lines.Select((line, index) => $"{index}|{PdfCandidateProvenance.LineId(line)}"))))).ToLowerInvariant();
}
