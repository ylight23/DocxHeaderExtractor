using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// N3.3 is a model-free ledger over frozen N3.2 silver heading occurrences. It measures first-loss
/// ownership before any baseline/R1 comparison and treats source-line occurrence identity as authority.
/// </summary>
public sealed class PdfN3SilverCandidateCensusProbe
{
    private const int SelectedBudget = 160;
    private const int SemanticCohortThreshold = 15;
    private static readonly string[] Stems = ["004", "030", "043", "058"];

    [Fact]
    public void WriteCensus()
    {
        var outputDirectory = Environment.GetEnvironmentVariable("BENCH_N3_CENSUS_DIRECTORY");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        Directory.CreateDirectory(outputDirectory);
        foreach (var stem in Stems)
            File.WriteAllText(Path.Combine(outputDirectory, $"{stem}-n3.3-census.v1.json"),
                JsonSerializer.Serialize(BuildCensus(root, stem), new JsonSerializerOptions { WriteIndented = true }));
    }

    [Theory]
    [InlineData("004")]
    [InlineData("030")]
    [InlineData("043")]
    [InlineData("058")]
    public void CommittedCensusReproducesFromFrozenPopulationAndSilver(string stem)
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var censusPath = Path.Combine(root, "eval", "benchmark-n3", "census", $"{stem}-n3.3-census.v1.json");
        if (!File.Exists(censusPath)) return;

        var expected = JsonSerializer.Serialize(BuildCensus(root, stem), new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(Normalize(expected), Normalize(File.ReadAllText(censusPath)));
    }

    private static object BuildCensus(string root, string stem)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "keys", "benchmark-n3", "manifest.v2.json")));
        var document = manifest.RootElement.GetProperty("documents").EnumerateArray()
            .Single(value => value.GetProperty("stem").GetString() == stem);
        var docxPath = Path.Combine(root, document.GetProperty("file").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        var labelPath = Path.Combine(root, "eval", "benchmark-n3", "silver-labels", $"{stem}-n3.2-silver-model-assisted.v1.json");
        using var label = JsonDocument.Parse(File.ReadAllText(labelPath));
        var labelRoot = label.RootElement;
        var packetRelative = labelRoot.GetProperty("sourcePacket").GetProperty("sourcePacketPath").GetString()!;
        using var packet = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, packetRelative.Replace('/', Path.DirectorySeparatorChar))));

        var documentSha256 = document.GetProperty("sourceDocumentSha256").GetString()!;
        Assert.Equal(documentSha256, packet.RootElement.GetProperty("documentSha256").GetString());
        Assert.Equal(documentSha256, Sha256(docxPath));

        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
        Assert.Equal(packet.RootElement.GetProperty("sourceLineExtractionFingerprint").GetString(), SourceFingerprint(snapshot.Lines));
        var lineIndexes = snapshot.Lines.Select((line, index) => (LineId: PdfCandidateProvenance.LineId(line), Index: index))
            .ToDictionary(value => value.LineId, value => value.Index, StringComparer.Ordinal);
        var occurrences = labelRoot.GetProperty("headingOccurrences").EnumerateArray().Select(occurrence =>
        {
            var stableId = occurrence.GetProperty("goldStableId").GetString()!;
            var sourceLineIds = occurrence.GetProperty("sourceLineIds").EnumerateArray().Select(line => line.GetString()!).ToArray();
            var indexes = sourceLineIds.Where(lineIndexes.ContainsKey).Select(lineId => lineIndexes[lineId]).ToArray();
            return (StableId: stableId, SourceLineIds: sourceLineIds,
                Occurrence: new PdfExtractorQualityBenchmarkProbe.Occurrence(stableId, [], indexes));
        }).ToArray();
        var unresolved = occurrences.Where(occurrence => occurrence.SourceLineIds.Length != occurrence.Occurrence.ResolvedIndexes!.Count).ToArray();
        var classified = PdfExtractorQualityBenchmarkProbe.Classify(docxPath, occurrences.Select(occurrence => occurrence.Occurrence).ToList());
        var identity = occurrences.ToDictionary(occurrence => occurrence.Occurrence.Label, occurrence => occurrence.StableId, StringComparer.Ordinal);

        var candidateConstructionLoss = new List<object>();
        var rankBudgetLoss = new List<object>();
        var deterministicEligibilityLoss = new List<object>();
        var decisionRelevant = new List<object>();
        foreach (var result in classified)
        {
            var stableId = identity[result.Occurrence.Label];
            object Row(string bucket) => new
            {
                stableId,
                status = result.Status,
                selected = result.Selected,
                coveringRank = result.CoveringRank,
                deterministicExclusionReason = result.DeterministicExclusionReason,
                bucket,
            };

            if (result.Status != "full") { candidateConstructionLoss.Add(Row("candidate_construction_loss")); continue; }
            if (!result.Selected) { rankBudgetLoss.Add(Row("rank_budget_loss")); continue; }
            if (result.DeterministicExclusionReason is not null) { deterministicEligibilityLoss.Add(Row("deterministic_eligibility_loss")); continue; }
            decisionRelevant.Add(Row("decision_relevant"));
        }

        return new
        {
            schemaVersion = 1,
            artifactKind = "n3_model_assisted_silver_model_free_census",
            documentId = stem,
            documentSha256,
            sourceLineExtractionFingerprint = packet.RootElement.GetProperty("sourceLineExtractionFingerprint").GetString(),
            populationManifest = "keys/benchmark-n3/manifest.v2.json",
            frozenSilverLabel = Path.GetRelativePath(root, labelPath).Replace('\\', '/'),
            labelAuthority = labelRoot.GetProperty("provenance").GetProperty("labelSource").GetString(),
            accuracyClaim = labelRoot.GetProperty("provenance").GetProperty("accuracyClaim").GetString(),
            identity = "documentSha256 + page + sourceLineIds/sourceSpan; candidateId is diagnostics-only within this run",
            modelCalls = "none",
            selectedBudget = SelectedBudget,
            unresolvedOccurrenceCount = unresolved.Length,
            unresolvedOccurrenceStableIds = unresolved.Select(occurrence => occurrence.StableId).ToArray(),
            denominators = new
            {
                silverHeadingOccurrences = occurrences.Length,
                fullCandidate = classified.Count(result => result.Status == "full"),
                selectedAt160 = classified.Count(result => result.Selected),
                answerIrrelevant = deterministicEligibilityLoss.Count,
                decisionRelevant = decisionRelevant.Count,
            },
            lossLedger = new
            {
                candidateConstructionLoss = candidateConstructionLoss.Count,
                rankBudgetLoss = rankBudgetLoss.Count,
                deterministicEligibilityLoss = deterministicEligibilityLoss.Count,
                decisionRelevant = decisionRelevant.Count,
            },
            semanticCohort = new
            {
                threshold = SemanticCohortThreshold,
                decisionRelevant = decisionRelevant.Count,
                eligible = decisionRelevant.Count >= SemanticCohortThreshold,
            },
            occurrences = new { candidateConstructionLoss, rankBudgetLoss, deterministicEligibilityLoss, decisionRelevant },
        };
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Normalize(string value) => value.Replace("\r\n", "\n");
    private static string SourceFingerprint(IReadOnlyList<PdfLine> lines) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', lines.Select((line, index) => $"{index}|{PdfCandidateProvenance.LineId(line)}"))))).ToLowerInvariant();
}
