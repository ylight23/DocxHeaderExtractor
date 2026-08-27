using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Round 1A: materializes the exact candidate-construction loss ledger from the committed N3 census
/// and source-first silver occurrences. This is model-free and deliberately does not infer a producer
/// or causal owner when the frozen census did not record one.
/// </summary>
public sealed class PdfRound1CandidateLossLedgerProbe
{
    private static readonly string[] Documents = ["004", "030", "043", "058"];
    private sealed record LedgerRow(
        string DocumentId,
        string? DocumentSha256,
        string GoldStableId,
        string? Label,
        int? Page,
        string?[] SourceLineIds,
        JsonElement? SourceSpan,
        string?[] SourceTextLines,
        string? SourceText,
        string? Marker,
        string RepresentationType,
        string[] CandidateProducers,
        string[] OverlappingCandidates,
        string FirstLoss,
        string CausalOwner,
        string? CensusStatus,
        int? CoveringRank,
        string EvidenceNote);

    [Fact]
    public void WriteCandidateLossLedger()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_R1_CANDIDATE_LOSS_LEDGER");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var rows = Documents.SelectMany(document => ReadDocument(root, document)).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_round1_candidate_loss_ledger",
            phase = "round1a",
            modelCalls = 0,
            productionChanges = false,
            identity = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is diagnostics-only",
            selectionRule = "all frozen N3 silver heading occurrences in 004, 030, 043, 058 whose committed census bucket is candidate_construction_loss",
            ownerPolicy = "producer and causal owner remain unresolved unless the frozen artifacts prove them",
            documents = Documents.Select(document => new
            {
                documentId = document,
                reviewed = rows.Count(row => row.DocumentId == document),
                candidateConstructionLoss = rows.Count(row => row.DocumentId == document),
                occurrences = rows.Where(row => row.DocumentId == document).ToArray()
            }).ToArray(),
            totals = new
            {
                reviewed = Documents.Sum(document => ReadDenominator(root, document)),
                candidateConstructionLoss = rows.Length
            }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void CommittedCensusLossCountsMatchFrozenRound1Population()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var expected = new Dictionary<string, int> { ["004"] = 10, ["030"] = 30, ["043"] = 1, ["058"] = 6 };
        foreach (var (document, count) in expected)
        {
            var path = Path.Combine(root, "eval", "benchmark-n3", "census", $"{document}-n3.3-census.v1.json");
            Assert.True(File.Exists(path), $"missing frozen census for {document}");
            using var census = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(count, census.RootElement.GetProperty("lossLedger").GetProperty("candidateConstructionLoss").GetInt32());
        }
    }

    private static IEnumerable<LedgerRow> ReadDocument(string root, string document)
    {
        var censusPath = Path.Combine(root, "eval", "benchmark-n3", "census", $"{document}-n3.3-census.v1.json");
        var silverPath = Path.Combine(root, "eval", "benchmark-n3", "silver-labels", $"{document}-n3.2-silver-model-assisted.v1.json");
        using var census = JsonDocument.Parse(File.ReadAllText(censusPath));
        using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
        var headings = silver.RootElement.GetProperty("headingOccurrences")
            .EnumerateArray()
            .ToDictionary(item => item.GetProperty("goldStableId").GetString()!, StringComparer.Ordinal);

        foreach (var loss in census.RootElement.GetProperty("occurrences").GetProperty("candidateConstructionLoss").EnumerateArray())
        {
            var stableId = loss.GetProperty("stableId").GetString()!;
            if (!headings.TryGetValue(stableId, out var heading))
            {
                yield return new LedgerRow(document, null, stableId, null, null, [], null, [], null, null,
                    "not_materialized_in_frozen_census", [], [], "UNRESOLVED", "UNRESOLVED", null, null,
                    "Frozen census row has no matching source occurrence in the committed silver artifact.");
                continue;
            }

            var sourceSpan = heading.GetProperty("sourceSpan");
            var rankElement = loss.GetProperty("coveringRank");
            yield return new LedgerRow(
                document,
                census.RootElement.GetProperty("documentSha256").GetString(),
                stableId,
                heading.GetProperty("label").GetString(),
                heading.GetProperty("page").GetInt32(),
                heading.GetProperty("sourceLineIds").EnumerateArray().Select(x => x.GetString()).ToArray(),
                sourceSpan.Clone(),
                heading.GetProperty("sourceTextLines").EnumerateArray().Select(x => x.GetString()).ToArray(),
                heading.GetProperty("sourceText").GetString(),
                heading.TryGetProperty("marker", out var marker) ? marker.GetString() : null,
                "not_materialized_in_frozen_census",
                [], [],
                "CANDIDATE_PRODUCER_NOT_TRIGGERED_OR_BOUNDARY_UNRESOLVED",
                "UNRESOLVED",
                loss.GetProperty("status").GetString(),
                rankElement.ValueKind == JsonValueKind.Null ? (int?)null : rankElement.GetInt32(),
                "Frozen N3 census proves candidate-construction loss but does not preserve producer-level trace for this occurrence.");
        }
    }

    private static int ReadDenominator(string root, string document)
    {
        var path = Path.Combine(root, "eval", "benchmark-n3", "census", $"{document}-n3.3-census.v1.json");
        using var census = JsonDocument.Parse(File.ReadAllText(path));
        return census.RootElement.GetProperty("denominators").GetProperty("silverHeadingOccurrences").GetInt32();
    }
}
