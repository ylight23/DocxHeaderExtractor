using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Joins each canonical N2-S live run to its N1.3-S decisionRelevant cohort by source occurrence
/// identity (never candidate id). "Validated" means some produced <c>PdfHierarchyFactAudit</c> item
/// covers every required source line of the occurrence; "emitted" means that same item's
/// <c>sourceFactId</c> also appears in the run's <c>canonicalGroundings</c> (the final aligned
/// output). Reported separately from end-to-end recall, and separately from each other - a heading
/// can validate without ever reaching the emitted product.
/// </summary>
public sealed class PdfN2SReconciliationProbe
{
    private static readonly string[] Stems = ["003", "057"];

    [Fact]
    public void WriteReconciliation()
    {
        var outputDirectory = Environment.GetEnvironmentVariable("BENCH_N2S_RECONCILIATION_DIRECTORY");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        Directory.CreateDirectory(outputDirectory);
        foreach (var stem in Stems)
            File.WriteAllText(
                Path.Combine(outputDirectory, $"{stem}-n2-s-reconciliation.v1.json"),
                JsonSerializer.Serialize(BuildReconciliation(root, stem), new JsonSerializerOptions { WriteIndented = true }));
    }

    [Theory]
    [InlineData("003")]
    [InlineData("057")]
    public void CommittedReconciliationReproducesFromCommittedRunAndCensus(string stem)
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var path = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "reconciliation", $"{stem}-n2-s-reconciliation.v1.json");
        if (!File.Exists(path)) return;

        var expected = JsonSerializer.Serialize(BuildReconciliation(root, stem), new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(Normalize(expected), Normalize(File.ReadAllText(path)));
    }

    private static string Normalize(string json) => json.Replace("\r\n", "\n");

    private static object BuildReconciliation(string root, string stem)
    {
        var silverPath = Path.Combine(root, "eval", "benchmark-n0", "silver-labels", $"{stem}-n1.2-silver-model-assisted.v1.json");
        var censusPath = Path.Combine(root, "eval", "benchmark-n0", "census", $"{stem}-n1.3-census.v1.json");
        var runPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "runs", $"{stem}-n2-s-run.v1.json");

        using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
        using var census = JsonDocument.Parse(File.ReadAllText(censusPath));
        using var run = JsonDocument.Parse(File.ReadAllText(runPath));

        var lineIdsByStableId = silver.RootElement.GetProperty("headingOccurrences").EnumerateArray()
            .ToDictionary(
                o => o.TryGetProperty("goldStableId", out var g) ? g.GetString()! : o.GetProperty("silverStableId").GetString()!,
                o => o.GetProperty("sourceLineIds").EnumerateArray().Select(l => l.GetString()!).ToArray(),
                StringComparer.Ordinal);

        var decisionRelevant = census.RootElement.GetProperty("occurrences").GetProperty("decisionRelevant")
            .EnumerateArray().Select(o => o.GetProperty("stableId").GetString()!).ToArray();

        var row = run.RootElement.GetProperty("rows")[0];
        var semanticLaneStatus = row.GetProperty("semanticLaneStatus").GetString();
        var spanLaneStatus = row.GetProperty("spanLaneStatus").GetString();
        var items = row.GetProperty("items").EnumerateArray().ToArray();
        var emittedSourceFactIds = row.GetProperty("canonicalGroundings").EnumerateArray()
            .Select(g => g.GetProperty("sourceFactId").GetString()!).ToHashSet(StringComparer.Ordinal);

        var perOccurrence = new List<object>();
        int validatedCount = 0, emittedCount = 0;
        foreach (var stableId in decisionRelevant)
        {
            if (!lineIdsByStableId.TryGetValue(stableId, out var required))
            {
                perOccurrence.Add(new { stableId, validated = false, emitted = false, error = "no_source_line_ids_in_silver" });
                continue;
            }

            var covering = items.FirstOrDefault(item =>
            {
                var lineIds = item.GetProperty("lineIds").EnumerateArray().Select(l => l.GetString()).ToHashSet(StringComparer.Ordinal);
                return required.All(lineIds.Contains);
            });
            var validated = covering.ValueKind == JsonValueKind.Object;
            var emitted = validated && emittedSourceFactIds.Contains(covering.GetProperty("sourceFactId").GetString()!);
            if (validated) validatedCount++;
            if (emitted) emittedCount++;
            perOccurrence.Add(new
            {
                stableId,
                validated,
                emitted,
                coveringSourceFactId = validated ? covering.GetProperty("sourceFactId").GetString() : null,
            });
        }

        return new
        {
            schemaVersion = 1,
            artifactKind = "n2_s_reconciliation",
            documentId = stem,
            identity = "documentSha256 (via run/census) + page + sourceLineIds; candidateId is diagnostics-only within one run",
            laneStatus = new { semanticLaneStatus, spanLaneStatus },
            denominators = new
            {
                decisionRelevant = decisionRelevant.Length,
                validated = validatedCount,
                emitted = emittedCount,
            },
            conditionalSemanticRecall = new
            {
                validatedOverDecisionRelevant = decisionRelevant.Length == 0 ? 0.0 : Math.Round(validatedCount / (double)decisionRelevant.Length, 4),
                emittedOverDecisionRelevant = decisionRelevant.Length == 0 ? 0.0 : Math.Round(emittedCount / (double)decisionRelevant.Length, 4),
                note = "Conditional on N1.3-S's decisionRelevant cohort, not an end-to-end recall over all reviewed occurrences.",
            },
            occurrences = perOccurrence,
        };
    }
}
