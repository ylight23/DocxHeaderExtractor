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
    private static readonly (string Id, string Pdf)[] Documents = [ ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) ];

    [Fact]
    public void P6SC_scores_frozen_candidate_decisions_on_semantics_extent_contract_and_final_identity()
    {
        using var result = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ResultPath)));
        Assert.Equal(4, result.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.False(result.RootElement.GetProperty("goldRead").GetBoolean());
        var plans = Documents.ToDictionary(doc => doc.Id, doc =>
            PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{CanonicalSemanticSourceHash.Compute(TestRepository.Path(doc.Pdf))}.json"), doc.Id), StringComparer.Ordinal);
        var gold = ReadGold();
        var predictions = new List<Prediction>(); var representations = new List<Prediction>(); var contractRows = new List<object>();

        foreach (var row in result.RootElement.GetProperty("rows").EnumerateArray())
        {
            var documentId = row.GetProperty("DocumentId").GetString()!;
            var plan = plans[documentId];
            var pack = plan.Packs.Single(item => item.PackOrdinal == row.GetProperty("PackOrdinal").GetInt32());
            Assert.Equal("stop", row.GetProperty("finishReason").GetString());
            Assert.Equal(0, row.GetProperty("retryCount").GetInt32());
            using var raw = JsonDocument.Parse(row.GetProperty("rawResponse").GetString()!);
            var parsed = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(pack, raw.RootElement.GetRawText());
            foreach (var decision in parsed.Accepted)
            {
                var binding = SemanticSourcePartBinder.Bind(plan.SourceAtoms, decision.Candidate.Parts);
                Assert.True(binding.IsBound, binding.Reason);
                var prediction = new Prediction(documentId, decision.Candidate.Id, binding.Parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray(), decision.Candidate.Kind.ToString());
                if (decision.Kind == DocxHeaderExtractor.Core.V5.V5CandidateDecisionKind.HEADING) predictions.Add(prediction); else representations.Add(prediction);
            }
            contractRows.Add(new { documentId, pack = pack.PackId, parserAccepted = true, accepted = parsed.Accepted.Count, quarantined = parsed.Quarantined.Count, overlapQuarantined = parsed.Quarantined.Count(item => item.Reason == "candidate-overlap-conflict") });
        }

        var goldRows = gold.SelectMany(pair => pair.Value.Select(item => (DocumentId: pair.Key, Gold: item))).ToArray();
        var exactTp = goldRows.Count(item => predictions.Any(prediction => prediction.DocumentId == item.DocumentId && Same(prediction.Parts, item.Gold.Parts)));
        var semanticTp = goldRows.Count(item => predictions.Any(prediction => prediction.DocumentId == item.DocumentId && Overlap(prediction.Parts, item.Gold.Parts)));
        var exactFp = predictions.Count(prediction => !goldRows.Any(item => item.DocumentId == prediction.DocumentId && Same(prediction.Parts, item.Gold.Parts)));
        var semanticFp = predictions.Count(prediction => !goldRows.Any(item => item.DocumentId == prediction.DocumentId && Overlap(prediction.Parts, item.Gold.Parts)));
        var partial = goldRows.Count(item => !predictions.Any(prediction => prediction.DocumentId == item.DocumentId && Same(prediction.Parts, item.Gold.Parts)) && predictions.Any(prediction => prediction.DocumentId == item.DocumentId && Overlap(prediction.Parts, item.Gold.Parts)));
        var wrongFunction = goldRows.Count(item => !predictions.Any(prediction => prediction.DocumentId == item.DocumentId && Overlap(prediction.Parts, item.Gold.Parts)) && representations.Any(prediction => prediction.DocumentId == item.DocumentId && Overlap(prediction.Parts, item.Gold.Parts)));
        FreezeArtifact.AssertJson(Root, "gold-multiaxis-score.v1.json", new
        {
            schemaVersion = "v5-p6sc-candidate-authority-multiaxis-score-v1", providerCalls = 0, frozenProviderCallsInSourceCapture = 4, goldRead = true, goldMutation = "NONE", runtimeChanged = false,
            scorer = new { semanticCandidate = "heading C# overlaps Gold source parts", extent = "issued candidate source parts exact/overlap", contract = "candidate parser + deterministic binder", finalExactOccurrence = "bound source parts equal Gold" },
            contract = new { parserBinderRows = contractRows, acceptedHeadingCandidates = predictions.Count, acceptedRepresentationCandidates = representations.Count, quarantine = contractRows.Sum(row => (int)row.GetType().GetProperty("quarantined")!.GetValue(row)!) },
            semanticCandidate = Metric(semanticTp, semanticFp, goldRows.Length - semanticTp), extentSelection = new { exactGold = exactTp, partialOrSplitGold = partial, noHeadingCandidate = goldRows.Length - semanticTp, representationOverlapGold = wrongFunction },
            finalExactOccurrence = Metric(exactTp, exactFp, goldRows.Length - exactTp), predictions = predictions, representationPredictions = representations,
        });
    }

    private static Dictionary<string, List<Gold>> ReadGold() => Documents.ToDictionary(doc => doc.Id, doc =>
    {
        using var json = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"eval/a99-closed-loop/gold/{doc.Id}.gold.json")));
        return json.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().Select(claim => new Gold(claim.GetProperty("boundParts").EnumerateArray().Select(part => new Part(part.GetProperty("sourceAlias").GetString()!, part.GetProperty("utf16Span").GetProperty("start").GetInt32(), part.GetProperty("utf16Span").GetProperty("end").GetInt32())).ToArray())).ToList();
    }, StringComparer.Ordinal);
    private static bool Same(IReadOnlyList<Part> x, IReadOnlyList<Part> y) => x.Count == y.Count && x.Zip(y).All(pair => pair.First == pair.Second);
    private static bool Overlap(IReadOnlyList<Part> x, IReadOnlyList<Part> y) => x.Any(a => y.Any(b => a.Alias == b.Alias && a.Start < b.End && b.Start < a.End));
    private static object Metric(int tp, int fp, int fn) { var p = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var r = tp + fn == 0 ? 0d : (double)tp / (tp + fn); return new { truePositive = tp, falsePositive = fp, falseNegative = fn, precision = Math.Round(p, 4), recall = Math.Round(r, 4), f1 = Math.Round(p + r == 0 ? 0 : 2 * p * r / (p + r), 4) }; }
    private sealed record Part(string Alias, int Start, int End); private sealed record Gold(IReadOnlyList<Part> Parts); private sealed record Prediction(string DocumentId, string Candidate, IReadOnlyList<Part> Parts, string Kind);
}
