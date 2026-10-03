using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>P6S-C scorer: immutable four-call raw capture, then multi-axis Gold audit offline.</summary>
public sealed class V5P6SCandidateAuthorityCanaryScoreTests
{
    private const string ResultPath = "artifacts/v5-p6s-candidate-authority/p6sc-canary/result.v1.json";
    private const string Root = "artifacts/v5-p6s-candidate-authority/p6sc-canary";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string GoldBasisPath = "eval/a99-closed-loop/gold-current/evaluation-basis.v1.json";
    private static readonly (string Id, string Pdf)[] Documents = [ ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) ];

    [Fact]
    public void P6SC_scores_frozen_candidate_decisions_on_semantics_extent_contract_and_final_identity()
    {
        using var result = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ResultPath)));
        Assert.Equal(4, result.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.False(result.RootElement.GetProperty("goldRead").GetBoolean());
        var plans = Documents.ToDictionary(doc => doc.Id, doc =>
            PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{CanonicalSemanticSourceHash.Compute(TestRepository.Path(doc.Pdf))}.json"), doc.Id), StringComparer.Ordinal);
        var gold = ReadGold(plans);
        var modelPredictions = new List<Prediction>(); var predictions = new List<Prediction>(); var representations = new List<Prediction>(); var contractRows = new List<object>();

        foreach (var row in result.RootElement.GetProperty("rows").EnumerateArray())
        {
            var documentId = row.GetProperty("DocumentId").GetString()!;
            var plan = plans[documentId];
            var pack = plan.Packs.Single(item => item.PackOrdinal == row.GetProperty("PackOrdinal").GetInt32());
            Assert.Equal("stop", row.GetProperty("finishReason").GetString());
            Assert.Equal(0, row.GetProperty("retryCount").GetInt32());
            using var raw = JsonDocument.Parse(row.GetProperty("rawResponse").GetString()!);
            var parsed = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(pack, raw.RootElement.GetRawText());
            foreach (var decision in parsed.AcceptedBeforeOverlapQuarantine)
            {
                var binding = SemanticSourcePartBinder.Bind(plan.SourceAtoms, decision.Candidate.Parts);
                Assert.True(binding.IsBound, binding.Reason);
                var prediction = new Prediction(documentId, decision.Candidate.Id, binding.Parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray(), decision.Candidate.Kind.ToString());
                if (decision.Kind == DocxHeaderExtractor.Core.V5.V5CandidateDecisionKind.HEADING) modelPredictions.Add(prediction); else representations.Add(prediction);
            }
            foreach (var decision in parsed.Headings)
            {
                var binding = SemanticSourcePartBinder.Bind(plan.SourceAtoms, decision.Candidate.Parts);
                Assert.True(binding.IsBound, binding.Reason);
                predictions.Add(new Prediction(documentId, decision.Candidate.Id, binding.Parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray(), "HEADING"));
            }
            contractRows.Add(new { documentId, pack = pack.PackId, parserAccepted = true, accepted = parsed.Accepted.Count, quarantined = parsed.Quarantined.Count, overlapQuarantined = parsed.Quarantined.Count(item => item.Reason == "candidate-overlap-conflict") });
        }

        var fullGoldRows = gold.SelectMany(pair => pair.Value.Select(item => (DocumentId: pair.Key, Gold: item))).ToArray();
        var ownedAliasesByDocument = result.RootElement.GetProperty("rows").EnumerateArray().GroupBy(row => row.GetProperty("DocumentId").GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.SelectMany(row => plans[group.Key].Packs.Single(pack => pack.PackOrdinal == row.GetProperty("PackOrdinal").GetInt32()).OwnedAliases).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var goldRows = fullGoldRows.Where(item => ownedAliasesByDocument.TryGetValue(item.DocumentId, out var owned) && owned.Contains(item.Gold.Parts[0].Alias)).ToArray();
        Assert.NotEmpty(goldRows);
        var exactTp = goldRows.Count(item => predictions.Any(prediction => prediction.DocumentId == item.DocumentId && Same(prediction.Parts, item.Gold.Parts)));
        var semanticTp = goldRows.Count(item => modelPredictions.Any(prediction => prediction.DocumentId == item.DocumentId && Overlap(prediction.Parts, item.Gold.Parts)));
        var exactFp = predictions.Count(prediction => !goldRows.Any(item => item.DocumentId == prediction.DocumentId && Same(prediction.Parts, item.Gold.Parts)));
        var semanticFp = modelPredictions.Count(prediction => !goldRows.Any(item => item.DocumentId == prediction.DocumentId && Overlap(prediction.Parts, item.Gold.Parts)));
        var partial = goldRows.Count(item => !predictions.Any(prediction => prediction.DocumentId == item.DocumentId && Same(prediction.Parts, item.Gold.Parts)) && predictions.Any(prediction => prediction.DocumentId == item.DocumentId && Overlap(prediction.Parts, item.Gold.Parts)));
        var wrongFunction = goldRows.Count(item => !predictions.Any(prediction => prediction.DocumentId == item.DocumentId && Overlap(prediction.Parts, item.Gold.Parts)) && representations.Any(prediction => prediction.DocumentId == item.DocumentId && Overlap(prediction.Parts, item.Gold.Parts)));
        FreezeArtifact.AssertJson(Root, "gold-multiaxis-score.v1.json", new
        {
            schemaVersion = "v5-p6sc-candidate-authority-multiaxis-score-v1", providerCalls = 0, frozenProviderCallsInSourceCapture = 4, goldRead = true, goldMutation = "NONE", runtimeChanged = false,
            goldAuthority = ReadGoldBasis(),
            evaluationPopulation = new { fullDocumentGoldDiagnostic = fullGoldRows.Length, packOwnedGold = goldRows.Length, rule = "Gold primary source alias is owned by one of the four executed P05 packs" },
            scorer = new { headingRegionDetection = "pre-overlap issued HEADING C# overlaps Gold source parts; this is not exact extent", extent = "issued candidate source parts containment/overlap taxonomy", contract = "candidate parser + deterministic binder + overlap conflict projection", finalExactOccurrence = "post-quarantine bound source parts equal Gold", relationSelectionAccuracy = "NOT_EVALUABLE_WITHOUT_RELATION_GOLD" },
            contract = new { parserBinderRows = contractRows, modelHeadingCandidates = modelPredictions.Count, finalAcceptedHeadingCandidates = predictions.Count, acceptedRepresentationCandidates = representations.Count, quarantine = contractRows.Sum(row => (int)row.GetType().GetProperty("quarantined")!.GetValue(row)!) },
            headingRegionDetection = Metric(semanticTp, semanticFp, goldRows.Length - semanticTp), extentSelection = new { exactGold = exactTp, partialOrSplitGold = partial, noHeadingCandidate = goldRows.Length - semanticTp, representationOverlapGold = wrongFunction },
            finalExactOccurrence = Metric(exactTp, exactFp, goldRows.Length - exactTp), modelDecisionsBeforeOverlapProjection = modelPredictions, finalAcceptedPredictions = predictions, representationPredictions = representations,
        });
    }

    private static Dictionary<string, List<Gold>> ReadGold(IReadOnlyDictionary<string, PdfCandidateAuthorityDocumentPlan> plans) => Documents.ToDictionary(doc => doc.Id, doc =>
    {
        using var json = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"eval/a99-closed-loop/gold-current/documents/{doc.Id}.gold.v1.json")));
        return json.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().Select(claim =>
        {
            var sourceParts = JsonSerializer.Deserialize<List<SemanticSourcePart>>(claim.GetProperty("sourceParts").GetRawText())!;
            var binding = SemanticSourcePartBinder.Bind(plans[doc.Id].SourceAtoms, sourceParts);
            Assert.True(binding.IsBound, binding.Reason);
            return new Gold(binding.Parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray());
        }).ToList();
    }, StringComparer.Ordinal);
    private static object ReadGoldBasis()
    {
        using var basis = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldBasisPath)));
        Assert.Equal("USER_RECONFIRMED_FROZEN", basis.RootElement.GetProperty("status").GetString());
        Assert.Equal(139, basis.RootElement.GetProperty("cohort").GetProperty("totalGoldOccurrences").GetInt32());
        var rows = Documents.Select(doc =>
        {
            var expected = basis.RootElement.GetProperty("cohort").GetProperty("documentsById").GetProperty(doc.Id);
            var bytes = File.ReadAllBytes(TestRepository.Path($"eval/a99-closed-loop/gold-current/documents/{doc.Id}.gold.v1.json"));
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            Assert.Equal(expected.GetProperty("canonicalGoldSha256").GetString(), actual);
            Assert.Equal(CanonicalSemanticSourceHash.Compute(TestRepository.Path(doc.Pdf)), expected.GetProperty("sourceSha256").GetString());
            return new { documentId = doc.Id, sourceSha256 = expected.GetProperty("sourceSha256").GetString(), goldSha256 = actual, count = expected.GetProperty("semanticHeadingTotal").GetInt32() };
        }).ToArray();
        return new { basis = GoldBasisPath, status = "USER_RECONFIRMED_FROZEN", documents = rows };
    }
    private static bool Same(IReadOnlyList<Part> x, IReadOnlyList<Part> y) => x.Count == y.Count && x.Zip(y).All(pair => pair.First == pair.Second);
    private static bool Overlap(IReadOnlyList<Part> x, IReadOnlyList<Part> y) => x.Any(a => y.Any(b => a.Alias == b.Alias && a.Start < b.End && b.Start < a.End));
    private static object Metric(int tp, int fp, int fn) { var p = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var r = tp + fn == 0 ? 0d : (double)tp / (tp + fn); return new { truePositive = tp, falsePositive = fp, falseNegative = fn, precision = Math.Round(p, 4), recall = Math.Round(r, 4), f1 = Math.Round(p + r == 0 ? 0 : 2 * p * r / (p + r), 4) }; }
    private sealed record Part(string Alias, int Start, int End); private sealed record Gold(IReadOnlyList<Part> Parts); private sealed record Prediction(string DocumentId, string Candidate, IReadOnlyList<Part> Parts, string Kind);
}
