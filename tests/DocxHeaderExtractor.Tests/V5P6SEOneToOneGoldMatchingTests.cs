using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P6S-E is an offline re-score of the immutable P6S-D raw responses.  Unlike the
/// earlier region-coverage diagnostic, each issued heading candidate and each Gold
/// occurrence may participate in at most one match.
/// </summary>
public sealed class V5P6SEOneToOneGoldMatchingTests
{
    private const string CaptureRoot = "artifacts/v5-p6s-candidate-authority/p6sd-full31";
    private const string OutputRoot = "artifacts/v5-p6s-candidate-authority/p6se-one-to-one-score";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string GoldBasisPath = "eval/a99-closed-loop/gold-current/evaluation-basis.v1.json";
    private static readonly (string Id, string Pdf, string GoldSha, int GoldCount)[] Documents =
    [
        ("SRC-089", SourcePdfCorpus.Src089, "50d9e57225d9cf9dcf174e5b5b7422158c06d7327241d77fc4d019831b8a808d", 36),
        ("SRC-095", SourcePdfCorpus.Src095, "8c7cea0ada3d3af2a2fe9f7f48375575f40e987442a1d4586462ad7706f58ef5", 103),
    ];

    private sealed record Part(string Alias, int Start, int End);
    private sealed record Gold(string DocumentId, int Ordinal, string Identity, IReadOnlyList<Part> Parts);
    private sealed record Prediction(string DocumentId, string PackId, string CandidateId, string Identity, IReadOnlyList<Part> Parts, string Kind);
    private sealed record Edge(Prediction Prediction, Gold Gold, string MatchKind, double Coverage, int Rank);
    private sealed record GoldDiagnostic(string DocumentId, int GoldOrdinal, string GoldIdentity, string Outcome,
        object? ModelSelection, object? FinalOutput, IReadOnlyList<string> RepresentationCandidates);

    [Fact]
    public void P6SE_rescores_immutable_full31_capture_with_one_to_one_matching()
    {
        var capturePath = TestRepository.Path($"{CaptureRoot}/result.v1.json");
        using var capture = JsonDocument.Parse(File.ReadAllText(capturePath));
        var captureRoot = capture.RootElement;
        Assert.Equal(31, captureRoot.GetProperty("providerCalls").GetInt32());
        Assert.False(captureRoot.GetProperty("goldRead").GetBoolean());
        Assert.Equal(0, captureRoot.GetProperty("retry").GetInt32());
        Assert.False(captureRoot.GetProperty("repair").GetBoolean());
        Assert.False(captureRoot.GetProperty("fallback").GetBoolean());

        var plans = Documents.ToDictionary(document => document.Id, document =>
        {
            var sourceSha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(document.Pdf));
            return PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(
                TestRepository.Path($"{SnapshotRoot}/{sourceSha}.json"), document.Id);
        }, StringComparer.Ordinal);
        var gold = ReadAndVerifyGold(plans);

        var modelHeadings = new List<Prediction>();
        var finalHeadings = new List<Prediction>();
        var representations = new List<Prediction>();
        var packs = new List<object>();
        foreach (var row in captureRoot.GetProperty("rows").EnumerateArray())
        {
            var documentId = row.GetProperty("documentId").GetString()!;
            var packId = row.GetProperty("PackId").GetString()!;
            var plan = plans[documentId];
            var pack = plan.Packs.Single(value => value.PackId == packId);
            Assert.True(row.GetProperty("transportAccepted").GetBoolean());
            Assert.Equal("stop", row.GetProperty("finishReason").GetString());
            Assert.Equal(0, row.GetProperty("retryCount").GetInt32());
            var raw = row.GetProperty("rawResponse").GetString()!;
            Assert.Equal(Hash(raw), row.GetProperty("rawResponseSha256").GetString());

            var parsed = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(pack, raw);
            foreach (var decision in parsed.AcceptedBeforeOverlapQuarantine)
            {
                var binding = SemanticSourcePartBinder.Bind(plan.SourceAtoms, decision.Candidate.Parts);
                Assert.True(binding.IsBound, binding.Reason);
                var item = PredictionFrom(documentId, packId, decision.Candidate.Id, binding.Parts, decision.Kind.ToString());
                if (decision.Kind == V5CandidateDecisionKind.HEADING) modelHeadings.Add(item); else representations.Add(item);
            }
            foreach (var decision in parsed.Headings)
            {
                var binding = SemanticSourcePartBinder.Bind(plan.SourceAtoms, decision.Candidate.Parts);
                Assert.True(binding.IsBound, binding.Reason);
                finalHeadings.Add(PredictionFrom(documentId, packId, decision.Candidate.Id, binding.Parts, "HEADING"));
            }
            packs.Add(new
            {
                documentId, packId, rawDecisions = parsed.RawDecisionCount,
                acceptedBeforeOverlap = parsed.AcceptedBeforeOverlapQuarantine.Count,
                finalAccepted = parsed.Accepted.Count,
                overlapQuarantined = parsed.Quarantined.Count(value => value.Reason == "candidate-overlap-conflict"),
            });
        }

        Assert.Equal(31, packs.Count);
        var allGold = gold.Values.SelectMany(values => values).OrderBy(value => value.DocumentId, StringComparer.Ordinal)
            .ThenBy(value => value.Ordinal).ToArray();
        var modelMatch = MatchOneToOne(modelHeadings, allGold);
        var finalMatch = MatchOneToOne(finalHeadings, allGold);
        Assert.Equal(modelMatch.Matches.Count, modelMatch.Matches.Select(value => CandidateKey(value.Prediction)).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(modelMatch.Matches.Count, modelMatch.Matches.Select(value => GoldKey(value.Gold)).Distinct(StringComparer.Ordinal).Count());
        // Regression for the ambiguity that invalidated the old region-coverage metric: C434 spans
        // both occurrences, while C441 is exact only for Article 8.  A candidate cannot credit both.
        var chapterTwo = modelMatch.ByGold["SRC-089:12"];
        var articleEight = modelMatch.ByGold["SRC-089:13"];
        Assert.Equal("C434", chapterTwo.Prediction.CandidateId);
        Assert.Equal("C441", articleEight.Prediction.CandidateId);
        Assert.NotEqual(CandidateKey(chapterTwo.Prediction), CandidateKey(articleEight.Prediction));
        var finalCandidateKeys = finalHeadings.Select(CandidateKey).ToHashSet(StringComparer.Ordinal);
        var representationCounts = representations.GroupBy(value => value.DocumentId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => new { documentId = group.Key, count = group.Count() }).ToArray();

        var diagnostics = allGold.Select(item => DescribeGold(item, modelMatch, finalMatch, representations, finalCandidateKeys)).ToArray();
        var conflictSet = modelMatch.Matches.Where(value => !finalCandidateKeys.Contains(CandidateKey(value.Prediction)))
            .Select(value => new
            {
                documentId = value.Gold.DocumentId, goldOrdinal = value.Gold.Ordinal, goldIdentity = value.Gold.Identity,
                packId = value.Prediction.PackId, candidateId = value.Prediction.CandidateId, candidateIdentity = value.Prediction.Identity,
                matchKind = value.MatchKind, coverage = Math.Round(value.Coverage, 4),
                classification = "MODEL_MATCHED_THEN_OVERLAP_OR_CONTRACT_QUARANTINED",
            }).ToArray();

        var output = new
        {
            schemaVersion = "v5-p6se-one-to-one-candidate-gold-score-v1",
            execution = new
            {
                providerCallsDuringScore = 0, frozenProviderCallsInCapture = 31, goldRead = true,
                goldMutation = "NONE", runtimeChanged = false, rawCapture = $"{CaptureRoot}/result.v1.json",
                rawCaptureSha256 = Hash(File.ReadAllBytes(capturePath)),
            },
            goldAuthority = GoldAuthority(plans),
            scorer = new
            {
                matching = "deterministic greedy one-to-one bipartite matching; each Gold occurrence and issued HEADING candidate may match once",
                edgePriority = new[] { "EXACT_IDENTITY", "FULL_GOLD_COVERAGE", "HIGHEST_GOLD_COVERAGE", "STABLE_DOCUMENT_GOLD_CANDIDATE_TIEBREAK" },
                modelSelection = "pre-overlap issued HEADING candidates", conflictSet = "pre-overlap matched candidates removed by final overlap/contract projection",
                finalFailClosedOutput = "post-quarantine accepted HEADING candidates", relationSelectionAccuracy = "NOT_EVALUABLE_WITHOUT_RELATION_GOLD",
            },
            metrics = new
            {
                modelSelection = Metrics(modelHeadings, allGold, modelMatch),
                conflictSet = new
                {
                    modelMatchedCandidates = modelMatch.Matches.Count,
                    candidatesRemovedBeforeFinalOutput = modelHeadings.Count(value => !finalCandidateKeys.Contains(CandidateKey(value))),
                    matchedGoldLostToFinalProjection = conflictSet.Length,
                    retainedModelMatches = modelMatch.Matches.Count - conflictSet.Length,
                },
                finalFailClosedOutput = Metrics(finalHeadings, allGold, finalMatch),
                extentOutcomeCounts = diagnostics.GroupBy(value => value.Outcome, StringComparer.Ordinal).OrderBy(value => value.Key, StringComparer.Ordinal)
                    .ToDictionary(value => value.Key, value => value.Count()),
                representationDecisions = new
                {
                    total = representations.Count, byDocument = representationCounts,
                    relationAccuracy = "NOT_EVALUABLE_WITHOUT_RELATION_GOLD",
                },
                contract = new
                {
                    packs, parserAcceptedPacks = packs.Count, acceptedHeadingBeforeProjection = modelHeadings.Count,
                    acceptedHeadingAfterProjection = finalHeadings.Count, rebindRefusals = 0,
                },
                perDocument = Documents.Select(document => PerDocument(document.Id, modelHeadings, finalHeadings, allGold, representations)).ToArray(),
            },
            goldOccurrenceDiagnostics = diagnostics,
            conflictSet,
            modelHeadingCandidatesBeforeOverlapQuarantine = modelHeadings,
            finalHeadingCandidatesAfterOverlapQuarantine = finalHeadings,
            representationCandidates = representations,
        };
        FreezeArtifact.AssertJson(OutputRoot, "full31-one-to-one-gold-score.v1.json", output);
    }

    private static object PerDocument(string id, IReadOnlyList<Prediction> model, IReadOnlyList<Prediction> final,
        IReadOnlyList<Gold> gold, IReadOnlyList<Prediction> representations)
    {
        var localGold = gold.Where(value => value.DocumentId == id).ToArray();
        var modelLocal = model.Where(value => value.DocumentId == id).ToArray();
        var finalLocal = final.Where(value => value.DocumentId == id).ToArray();
        return new
        {
            documentId = id, gold = localGold.Length,
            modelSelection = Metrics(modelLocal, localGold, MatchOneToOne(modelLocal, localGold)),
            finalFailClosedOutput = Metrics(finalLocal, localGold, MatchOneToOne(finalLocal, localGold)),
            representationDecisions = representations.Count(value => value.DocumentId == id),
        };
    }

    private static GoldDiagnostic DescribeGold(Gold gold, Matching model, Matching final, IReadOnlyList<Prediction> representations,
        IReadOnlySet<string> finalCandidateKeys)
    {
        var modelEdge = model.ByGold.TryGetValue(GoldKey(gold), out var before) ? before : null;
        var finalEdge = final.ByGold.TryGetValue(GoldKey(gold), out var after) ? after : null;
        var representationsOverlapping = representations.Where(value => value.DocumentId == gold.DocumentId && Overlap(value.Parts, gold.Parts))
            .Select(value => value.CandidateId).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var outcome = finalEdge is not null ? ExtentOutcome(finalEdge) :
            modelEdge is not null && !finalCandidateKeys.Contains(CandidateKey(modelEdge.Prediction)) ? "CONFLICT_LOSS" :
            modelEdge is not null ? ExtentOutcome(modelEdge) : representationsOverlapping.Length > 0 ? "ROLE_ERROR" : "NO_MODEL_PROPOSAL";
        return new GoldDiagnostic(gold.DocumentId, gold.Ordinal, gold.Identity, outcome,
            modelEdge is null ? null : new { packId = modelEdge.Prediction.PackId, candidateId = modelEdge.Prediction.CandidateId, identity = modelEdge.Prediction.Identity, modelEdge.MatchKind, coverage = Math.Round(modelEdge.Coverage, 4) },
            finalEdge is null ? null : new { packId = finalEdge.Prediction.PackId, candidateId = finalEdge.Prediction.CandidateId, identity = finalEdge.Prediction.Identity, finalEdge.MatchKind, coverage = Math.Round(finalEdge.Coverage, 4) },
            representationsOverlapping);
    }

    private static string ExtentOutcome(Edge edge) => edge.MatchKind switch
    {
        "EXACT_IDENTITY" => "EXACT",
        "FULL_GOLD_COVERAGE" => "EXCESS_EXTENT",
        _ => "PARTIAL_OR_BOUNDARY_MISMATCH",
    };

    private sealed record Matching(IReadOnlyList<Edge> Matches, IReadOnlyDictionary<string, Edge> ByGold);

    private static Matching MatchOneToOne(IReadOnlyList<Prediction> predictions, IReadOnlyList<Gold> gold)
    {
        var edges = predictions.SelectMany(prediction => gold.Where(item => item.DocumentId == prediction.DocumentId)
            .Select(item => CreateEdge(prediction, item)).Where(edge => edge is not null).Cast<Edge>())
            .OrderByDescending(value => value.Rank).ThenByDescending(value => value.Coverage)
            .ThenBy(value => value.Gold.DocumentId, StringComparer.Ordinal).ThenBy(value => value.Gold.Ordinal)
            .ThenBy(value => value.Prediction.CandidateId, StringComparer.Ordinal).ToArray();
        var usedCandidates = new HashSet<string>(StringComparer.Ordinal); var usedGold = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<Edge>();
        foreach (var edge in edges)
            if (usedCandidates.Add(CandidateKey(edge.Prediction)) && usedGold.Add(GoldKey(edge.Gold))) matches.Add(edge);
        return new Matching(matches, matches.ToDictionary(value => GoldKey(value.Gold), StringComparer.Ordinal));
    }

    private static Edge? CreateEdge(Prediction prediction, Gold gold)
    {
        if (!Overlap(prediction.Parts, gold.Parts)) return null;
        var coverage = Coverage(gold.Parts, prediction.Parts);
        var exact = prediction.Identity == gold.Identity;
        var full = coverage >= .9999;
        return new Edge(prediction, gold, exact ? "EXACT_IDENTITY" : full ? "FULL_GOLD_COVERAGE" : "OVERLAP", coverage, exact ? 3 : full ? 2 : 1);
    }

    private static object Metrics(IReadOnlyList<Prediction> predictions, IReadOnlyList<Gold> gold, Matching matching) =>
        Metric(matching.Matches.Count, predictions.Count - matching.Matches.Count, gold.Count - matching.Matches.Count);

    private static Dictionary<string, List<Gold>> ReadAndVerifyGold(IReadOnlyDictionary<string, PdfCandidateAuthorityDocumentPlan> plans)
    {
        using var basis = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldBasisPath)));
        Assert.Equal("USER_RECONFIRMED_FROZEN", basis.RootElement.GetProperty("status").GetString());
        Assert.Equal(139, basis.RootElement.GetProperty("cohort").GetProperty("totalGoldOccurrences").GetInt32());
        var result = new Dictionary<string, List<Gold>>(StringComparer.Ordinal);
        foreach (var document in Documents)
        {
            var bytes = File.ReadAllBytes(TestRepository.Path($"eval/a99-closed-loop/gold-current/documents/{document.Id}.gold.v1.json"));
            Assert.Equal(document.GoldSha, Hash(bytes));
            using var json = JsonDocument.Parse(bytes);
            var rows = new List<Gold>(); var ordinal = 0;
            foreach (var claim in json.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray())
            {
                ordinal++;
                var sourceParts = JsonSerializer.Deserialize<List<SemanticSourcePart>>(claim.GetProperty("sourceParts").GetRawText())!;
                var binding = SemanticSourcePartBinder.Bind(plans[document.Id].SourceAtoms, sourceParts);
                Assert.True(binding.IsBound, binding.Reason);
                rows.Add(new Gold(document.Id, ordinal, binding.Identity, binding.Parts.Select(value => new Part(value.Alias, value.Start, value.End)).ToArray()));
            }
            Assert.Equal(document.GoldCount, rows.Count);
            result.Add(document.Id, rows);
        }
        return result;
    }

    private static object GoldAuthority(IReadOnlyDictionary<string, PdfCandidateAuthorityDocumentPlan> plans) => new
    {
        basis = GoldBasisPath, status = "USER_RECONFIRMED_FROZEN", goldMutation = "NONE", total = 139,
        documents = Documents.Select(document => new { documentId = document.Id, sourceSha256 = plans[document.Id].SourceSha256, goldSha256 = document.GoldSha, goldOccurrences = document.GoldCount }).ToArray(),
    };

    private static Prediction PredictionFrom(string documentId, string packId, string candidateId, IReadOnlyList<BoundSourcePart> parts, string kind)
    {
        var mapped = parts.Select(value => new Part(value.Alias, value.Start, value.End)).ToArray();
        return new Prediction(documentId, packId, candidateId, string.Join("|", mapped.Select(value => $"{value.Alias}:{value.Start}-{value.End}")), mapped, kind);
    }
    private static string CandidateKey(Prediction value) => $"{value.DocumentId}:{value.PackId}:{value.CandidateId}";
    private static string GoldKey(Gold value) => $"{value.DocumentId}:{value.Ordinal}";
    private static bool Overlap(IReadOnlyList<Part> left, IReadOnlyList<Part> right) => left.Any(a => right.Any(b => a.Alias == b.Alias && Math.Max(a.Start, b.Start) < Math.Min(a.End, b.End)));
    private static double Coverage(IReadOnlyList<Part> gold, IReadOnlyList<Part> predicted)
    {
        var total = gold.Sum(value => value.End - value.Start); if (total == 0) return 0;
        var covered = 0;
        foreach (var expected in gold)
        {
            var intervals = predicted.Where(value => value.Alias == expected.Alias).Select(value => (Start: Math.Max(value.Start, expected.Start), End: Math.Min(value.End, expected.End)))
                .Where(value => value.End > value.Start).OrderBy(value => value.Start).ToArray();
            var start = -1; var end = -1;
            foreach (var interval in intervals)
                if (start < 0) { start = interval.Start; end = interval.End; }
                else if (interval.Start <= end) end = Math.Max(end, interval.End);
                else { covered += end - start; start = interval.Start; end = interval.End; }
            if (start >= 0) covered += end - start;
        }
        return Math.Min(1d, (double)covered / total);
    }
    private static object Metric(int tp, int fp, int fn)
    {
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        return new { truePositive = tp, falsePositive = fp, falseNegative = fn, precision = Math.Round(precision, 4), recall = Math.Round(recall, 4), f1 = Math.Round(precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall), 4) };
    }
    private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
