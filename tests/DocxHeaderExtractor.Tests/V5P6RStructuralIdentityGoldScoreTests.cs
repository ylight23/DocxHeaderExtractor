using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>Gold is read only after P6R primary/retry raw evidence has been hash-verified.</summary>
public sealed class V5P6RStructuralIdentityGoldScoreTests
{
    private const string Root = "artifacts/v5-p6r-structural-identity-resolution";
    private const string BaselinePath = "eval/a99-closed-loop/production-rebaseline-v1/production-rebaseline-score.v1.json";
    private static readonly (string Id, string Pdf)[] Documents = [("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095)];
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private sealed record Prediction(string Identity, ExactScorer.Span[] Spans, string Text);

    [Fact]
    public void Score_canonical_P6R_heading_selections_after_primary_and_retry_hash_verification()
    {
        using var manifestDoc = Read("execution-manifest.v1.json");
        using var primaryDoc = Read("result.v1.json");
        using var freezeDoc = Read("response-hash-freeze.v1.json");
        using var retryDoc = Read("contract-invalid-pack-retries.v1.json");
        var manifest = manifestDoc.RootElement; var primary = primaryDoc.RootElement; var freeze = freezeDoc.RootElement; var retries = retryDoc.RootElement;
        Assert.False(primary.GetProperty("goldRead").GetBoolean()); Assert.False(retries.GetProperty("goldRead").GetBoolean());
        Assert.Equal(Hash(File.ReadAllText(TestRepository.Path($"{Root}/result.v1.json"))), freeze.GetProperty("resultFileSha256").GetString());
        Assert.Equal(Hash(File.ReadAllText(TestRepository.Path($"{Root}/execution-manifest.v1.json"))), freeze.GetProperty("manifestFileSha256").GetString());
        Assert.Equal(Hash(File.ReadAllText(TestRepository.Path($"{Root}/result.v1.json"))), retries.GetProperty("primaryResultSha256").GetString());

        var manifestRows = manifest.GetProperty("rows").EnumerateArray().ToDictionary(Key, StringComparer.Ordinal);
        var primaryRows = primary.GetProperty("rows").EnumerateArray().ToDictionary(Key, StringComparer.Ordinal);
        var retryRows = retries.GetProperty("rows").EnumerateArray().ToDictionary(Key, StringComparer.Ordinal);
        Assert.Equal(31, primaryRows.Count); Assert.Equal(primaryRows.Values.Count(row => !row.GetProperty("contractValid").GetBoolean()), retryRows.Count);
        foreach (var row in primaryRows.Values)
        {
            var request = manifestRows[Key(row)];
            Assert.True(row.GetProperty("transportAccepted").GetBoolean()); Assert.Equal("stop", row.GetProperty("finishReason").GetString());
            Assert.Equal(request.GetProperty("providerRequestHash").GetString(), row.GetProperty("providerRequestHash").GetString());
            Assert.Equal(Hash(row.GetProperty("rawContent").GetString()!), row.GetProperty("rawContentSha256").GetString());
            Assert.Equal(Hash(row.GetProperty("rawSse").GetString()!), row.GetProperty("rawSseSha256").GetString());
        }
        foreach (var row in retryRows.Values)
        {
            var request = manifestRows[Key(row)];
            Assert.True(row.GetProperty("transportAccepted").GetBoolean()); Assert.Equal("stop", row.GetProperty("finishReason").GetString());
            Assert.True(row.GetProperty("headingScoreEligible").GetBoolean());
            Assert.Equal(request.GetProperty("providerRequestHash").GetString(), row.GetProperty("providerRequestHash").GetString());
            Assert.Equal(Hash(row.GetProperty("rawContent").GetString()!), row.GetProperty("rawContentSha256").GetString());
            Assert.Equal(Hash(row.GetProperty("rawSse").GetString()!), row.GetProperty("rawSseSha256").GetString());
        }

        var plans = Documents.Select(document => PdfHeadingMembershipProductionAdapter.PrepareStructuralIdentityResolution(TestRepository.Path(document.Pdf), document.Id, Contract)).ToArray();
        var prepared = plans.SelectMany(plan => plan.Packs).ToDictionary(pack => $"{pack.Pack.DocumentId}|{pack.Pack.PackId}", StringComparer.Ordinal);
        Assert.Equal(31, prepared.Count);
        foreach (var row in manifestRows.Values) Assert.Equal(row.GetProperty("providerRequestHash").GetString(), prepared[Key(row)].Pack.ProviderRequestHash);

        // First authorized Gold read: retries deterministically replace all and only primary-invalid packs.
        var gold = Documents.ToDictionary(document => document.Id, document => ExactScorer.ReadGold(
            TestRepository.Path($"eval/a99-closed-loop/gold/{document.Id}.gold.json"), ExactScorer.Universe.For("PDF", TestRepository.Path(document.Pdf))), StringComparer.Ordinal);
        var predictions = Documents.ToDictionary(document => document.Id, _ => new List<Prediction>(), StringComparer.Ordinal);
        var canonical = new List<object>(); var rawProposals = 0; var bound = 0; var quarantine = 0;
        foreach (var pair in primaryRows)
        {
            var useRetry = retryRows.TryGetValue(pair.Key, out var retry);
            var row = useRetry ? retry : pair.Value;
            var pack = prepared[pair.Key].Pack;
            var content = row.GetProperty("rawContent").GetString()!;
            var bytes = row.GetProperty("rawContentUtf8Bytes").GetInt32();
            using var raw = JsonDocument.Parse(content);
            rawProposals += raw.RootElement.GetProperty("headings").GetArrayLength();
            using var headingsOnly = JsonDocument.Parse(JsonSerializer.Serialize(new { headings = raw.RootElement.GetProperty("headings") }));
            var parsed = V5FreeHeadingCandidateProtocolV1.ParseAndBindSourceParts(headingsOnly.RootElement, bytes, 49_152, pack.Registry,
                Enumerable.Range(0, pack.Registry.AtomCount).ToHashSet());
            bound += parsed.Response.Occurrences.Count; quarantine += parsed.Quarantined.Count;
            foreach (var locator in parsed.Response.Occurrences)
            {
                var endpoint = pack.Registry.Decode(locator); var spans = endpoint.Parts.Select(part => new ExactScorer.Span(part.Alias, part.Start, part.End)).ToArray();
                predictions[pack.DocumentId].Add(new Prediction(ExactScorer.Identity(spans), spans, string.Join(" ", endpoint.Parts.Select(part => part.Text))));
            }
            canonical.Add(new { key = pair.Key, selection = useRetry ? "FIRST_RETRY_HEADING_SCORE_ELIGIBLE" : "PRIMARY_CONTRACT_VALID", rawContentSha256 = row.GetProperty("rawContentSha256").GetString() });
        }

        var documents = new List<object>(); var totalGold = 0; var totalHypotheses = 0; var tp = 0; var fp = 0; var fn = 0;
        foreach (var document in Documents)
        {
            var unique = predictions[document.Id].GroupBy(item => item.Identity, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            var hypotheses = unique.Select((item, index) => new ExactScorer.Hypothesis(index, item.Text, "TRUE", [], null, null, [], "TITLE", null, [], item.Identity, null, item.Spans)).ToArray();
            var score = ExactScorer.Compute(gold[document.Id], hypotheses).Headline();
            var item = JsonSerializer.SerializeToElement(score); totalGold += gold[document.Id].Count; totalHypotheses += unique.Length;
            tp += item.GetProperty("truePositives").GetInt32(); fp += item.GetProperty("falsePositives").GetInt32(); fn += item.GetProperty("falseNegatives").GetInt32();
            documents.Add(new { documentId = document.Id, gold = gold[document.Id].Count, distinctBoundOccurrences = unique.Length, exact = score });
        }
        var precision = totalHypotheses == 0 ? 0 : Math.Round((double)tp / totalHypotheses, 4);
        var recall = totalGold == 0 ? 0 : Math.Round((double)tp / totalGold, 4);
        var f1 = precision + recall == 0 ? 0 : Math.Round(2 * precision * recall / (precision + recall), 4);
        using var baseline = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(BaselinePath)));
        var baseTotal = baseline.RootElement.GetProperty("total");
        FreezeArtifact.AssertJson(Root, "gold-score-after-contract-retries.v1.json", new
        {
            schemaVersion = "v5-p6r-structural-identity-resolution-gold-score-v1",
            authority = new { gold = "current canonical 139-occurrence Gold; read-only", scorer = ExactScorer.ScorerId, unit = "document-scoped exact source alias/UTF-16 span", placement = "off", runtime = "UNCHANGED" },
            primary = new { logicalProviderCalls = 31, contractValidPacks = primaryRows.Values.Count(row => row.GetProperty("contractValid").GetBoolean()) },
            retries = new { logicalProviderCalls = retryRows.Count, retrySelection = "first retry only for each primary contract-invalid pack", headingScoreEligible = retryRows.Values.Count(row => row.GetProperty("headingScoreEligible").GetBoolean()), contractValid = retryRows.Values.Count(row => row.GetProperty("contractValid").GetBoolean()) },
            canonicalSelection = canonical, rawHeadingProposals = rawProposals, boundOccurrences = bound, headingQuarantine = quarantine,
            exact = new { goldClaims = totalGold, distinctBoundOccurrences = totalHypotheses, truePositives = tp, falsePositives = fp, falseNegatives = fn, precision, recall, f1 },
            productionBaseline = new { truePositives = baseTotal.GetProperty("tp").GetInt32(), falsePositives = baseTotal.GetProperty("fp").GetInt32(), falseNegatives = baseTotal.GetProperty("fn").GetInt32(), f1 = baseTotal.GetProperty("f1").GetDouble() },
            deltaVsProductionBaseline = new { tp = tp - baseTotal.GetProperty("tp").GetInt32(), fp = fp - baseTotal.GetProperty("fp").GetInt32(), fn = fn - baseTotal.GetProperty("fn").GetInt32(), f1 = Math.Round(f1 - baseTotal.GetProperty("f1").GetDouble(), 4) },
            documents, promotion = false, promotionBlocker = "P6R representation contract remains invalid on canonical retries; qualification-only lane",
            goldMutation = "NONE", providerCallsDuringScore = 0,
        });
    }

    private static JsonDocument Read(string name) => JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/{name}")));
    private static string Key(JsonElement row) => $"{row.GetProperty("documentId").GetString()}|{row.GetProperty("packId").GetString()}";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
