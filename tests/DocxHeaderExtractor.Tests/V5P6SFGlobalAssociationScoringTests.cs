using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P6S-F corrects P6S-E's metric taxonomy.  Overlap is an association/extent
/// diagnostic only; strict identity is the occurrence metric.  Association uses a
/// deterministic global maximum-weight bipartite assignment rather than greedy edges.
/// </summary>
public sealed class V5P6SFGlobalAssociationScoringTests
{
    private const string CaptureRoot = "artifacts/v5-p6s-candidate-authority/p6sd-full31";
    private const string CaptureArtifact = "result.v1.json";
    private const string OutputRoot = "artifacts/v5-p6s-candidate-authority/p6sf-global-association-score";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string EvaluationBasisPath = "eval/a99-closed-loop/gold-current/evaluation-basis.v1.json";
    internal static readonly (string Id, string Pdf, string GoldSha, int Count)[] Documents =
    [
        ("SRC-089", SourcePdfCorpus.Src089, "50d9e57225d9cf9dcf174e5b5b7422158c06d7327241d77fc4d019831b8a808d", 36),
        ("SRC-095", SourcePdfCorpus.Src095, "8c7cea0ada3d3af2a2fe9f7f48375575f40e987442a1d4586462ad7706f58ef5", 103),
    ];

    internal sealed record Part(string Alias, int Start, int End);
    internal sealed record Prediction(string DocumentId, string PackId, string CandidateId, string Identity, IReadOnlyList<Part> Parts);
    internal sealed record Gold(string DocumentId, int Ordinal, string Identity, IReadOnlyList<Part> Parts);
    internal sealed record Edge(int PredictionIndex, int GoldIndex, string MatchKind, int CoverageBps, long Reward);
    internal sealed record Matching(IReadOnlyList<Edge> Edges, IReadOnlyDictionary<int, Edge> ByGold);
    internal sealed record ExactMetric(int TruePositive, int FalsePositive, int FalseNegative, double Precision, double Recall, double F1);
    internal sealed record DiagnosticRow(string DocumentId, int GoldOrdinal, string GoldIdentity, string ModelOutcome,
        string FinalOutcome, object? Model, object? Final, IReadOnlyList<string> RepresentationCandidates);

    [Fact]
    public void P6SF_global_match_keeps_crossed_overlap_edges_that_greedy_order_would_lose()
    {
        var predictions = new[]
        {
            new Prediction("D", "P", "C0", "p0", [new Part("A", 0, 1), new Part("B", 0, 1)]),
            new Prediction("D", "P", "C1", "p1", [new Part("A", 1, 2)]),
        };
        var gold = new[]
        {
            new Gold("D", 1, "g0", [new Part("A", 0, 100)]),
            new Gold("D", 2, "g1", [new Part("B", 0, 100)]),
        };
        // A row-ordered greedy matcher takes C0→g0 first and loses C1→g0.  The global
        // cardinality objective instead assigns C0→g1 and C1→g0.
        var matched = GlobalAssociation(predictions, gold);
        Assert.Equal(2, matched.Edges.Count);
        Assert.Equal(1, matched.ByGold[0].PredictionIndex);
        Assert.Equal(0, matched.ByGold[1].PredictionIndex);
    }

    [Fact]
    public void P6SF_separates_strict_exact_from_global_gold_association_without_provider_calls()
    {
        var capturePath = TestRepository.Path($"{CaptureRoot}/{CaptureArtifact}");
        using var capture = JsonDocument.Parse(File.ReadAllText(capturePath));
        var captureRoot = capture.RootElement;
        Assert.Equal(31, captureRoot.GetProperty("providerCalls").GetInt32());
        Assert.False(captureRoot.GetProperty("goldRead").GetBoolean());
        Assert.Equal("NONE", captureRoot.GetProperty("goldMutation").GetString());

        var plans = Documents.ToDictionary(item => item.Id, item =>
        {
            var sha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(item.Pdf));
            return PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sha}.json"), item.Id);
        }, StringComparer.Ordinal);
        VerifyEvaluationBasis(plans);
        var gold = ReadGold(plans);
        var model = new List<Prediction>(); var final = new List<Prediction>(); var representation = new List<Prediction>();
        foreach (var row in captureRoot.GetProperty("rows").EnumerateArray())
        {
            var documentId = row.GetProperty("documentId").GetString()!;
            var packId = row.GetProperty("PackId").GetString()!;
            var plan = plans[documentId]; var pack = plan.Packs.Single(value => value.PackId == packId);
            Assert.True(row.GetProperty("transportAccepted").GetBoolean());
            Assert.Equal("stop", row.GetProperty("finishReason").GetString());
            Assert.Equal(0, row.GetProperty("retryCount").GetInt32());
            var raw = row.GetProperty("rawResponse").GetString()!;
            Assert.Equal(Hash(Encoding.UTF8.GetBytes(raw)), row.GetProperty("rawResponseSha256").GetString());
            var parsed = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(pack, raw);
            foreach (var decision in parsed.AcceptedBeforeOverlapQuarantine)
            {
                var binding = SemanticSourcePartBinder.Bind(plan.SourceAtoms, decision.Candidate.Parts); Assert.True(binding.IsBound, binding.Reason);
                var prediction = PredictionFrom(documentId, packId, decision.Candidate.Id, binding.Parts);
                if (decision.Kind == V5CandidateDecisionKind.HEADING) model.Add(prediction); else representation.Add(prediction);
            }
            foreach (var decision in parsed.Headings)
            {
                var binding = SemanticSourcePartBinder.Bind(plan.SourceAtoms, decision.Candidate.Parts); Assert.True(binding.IsBound, binding.Reason);
                final.Add(PredictionFrom(documentId, packId, decision.Candidate.Id, binding.Parts));
            }
        }
        Assert.Equal(model.Count, model.Select(value => $"{value.DocumentId}:{value.Identity}").Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(final.Count, final.Select(value => $"{value.DocumentId}:{value.Identity}").Distinct(StringComparer.Ordinal).Count());
        var allGold = gold.SelectMany(value => value.Value).OrderBy(value => value.DocumentId, StringComparer.Ordinal).ThenBy(value => value.Ordinal).ToArray();

        var modelAssociation = GlobalAssociation(model, allGold);
        var finalAssociation = GlobalAssociation(final, allGold);
        // These capture the independently verified current-cohort cardinalities.  They also prove
        // the global assignment is not silently a lower-cardinality replacement for greedy P6S-E.
        Assert.Equal(115, modelAssociation.Edges.Count);
        Assert.Equal(113, finalAssociation.Edges.Count);
        Assert.Equal(modelAssociation.Edges.Count, modelAssociation.Edges.Select(value => value.PredictionIndex).Distinct().Count());
        Assert.Equal(modelAssociation.Edges.Count, modelAssociation.Edges.Select(value => value.GoldIndex).Distinct().Count());

        var finalKeys = final.Select(CandidateKey).ToHashSet(StringComparer.Ordinal);
        var modelExact = ExactOccurrence(model, allGold);
        var finalExact = ExactOccurrence(final, allGold);
        Assert.Equal(103, modelExact.TruePositive);
        Assert.Equal(102, finalExact.TruePositive);
        var diagnostics = allGold.Select((item, index) => Diagnostic(index, item, model, final, representation, modelAssociation, finalAssociation, finalKeys)).ToArray();
        var conflictLosses = diagnostics.Count(value => value.FinalOutcome == "CONFLICT_LOSS");
        Assert.Equal(2, conflictLosses);

        var output = new
        {
            schemaVersion = "v5-p6sf-global-association-and-exact-score-v1",
            execution = new { providerCallsDuringScore = 0, frozenProviderCallsInCapture = 31, goldRead = true, goldMutation = "NONE", runtimeChanged = false,
                sourceArtifact = $"{CaptureRoot}/{CaptureArtifact}", sourceArtifactSha256 = Hash(File.ReadAllBytes(capturePath)) },
            scorer = new
            {
                exactOccurrence = "strict candidate identity equals Gold identity; this is the only TP/FP/FN/F1 occurrence metric",
                goldAssociation = "global one-to-one overlap assignment; association is diagnostic and is not semantic TP/FP/FN",
                objective = "maximum cardinality first; then exact identity count; then full Gold coverage; then coverage basis points; deterministic assignment tie-break",
                extentDiagnosis = "exact/excess/partial/no-model-proposal/role-error/conflict-loss",
            },
            metrics = new
            {
                modelSelection = new
                {
                    exactOccurrence = modelExact,
                    goldAssociation = Association(model, allGold, modelAssociation),
                    extent = Extent(diagnostics, modelStage: true),
                },
                finalFailClosedOutput = new
                {
                    exactOccurrence = finalExact,
                    goldAssociation = Association(final, allGold, finalAssociation),
                    extent = Extent(diagnostics, modelStage: false),
                },
                conflictProjection = new
                {
                    headingCandidatesBefore = model.Count, headingCandidatesAfter = final.Count,
                    candidatesQuarantined = model.Count(value => !finalKeys.Contains(CandidateKey(value))),
                    goldAssociationsLost = conflictLosses,
                    strictExactGoldLost = modelExact.TruePositive - finalExact.TruePositive,
                },
                representationDiagnostics = new
                {
                    total = representation.Count,
                    src095 = representation.Count(value => value.DocumentId == "SRC-095"),
                    relationAccuracy = "NOT_EVALUABLE_WITHOUT_RELATION_GOLD",
                },
                perDocument = Documents.Select(document => PerDocument(document.Id, model, final, allGold)).ToArray(),
            },
            goldOccurrenceDiagnostics = diagnostics,
        };
        FreezeArtifact.AssertJson(OutputRoot, "full31-global-association-and-exact-score.v1.json", output);
    }

    internal static object PerDocument(string documentId, IReadOnlyList<Prediction> model, IReadOnlyList<Prediction> final, IReadOnlyList<Gold> allGold)
    {
        var localGold = allGold.Where(value => value.DocumentId == documentId).ToArray();
        var localModel = model.Where(value => value.DocumentId == documentId).ToArray();
        var localFinal = final.Where(value => value.DocumentId == documentId).ToArray();
        return new { documentId, gold = localGold.Length,
            modelSelection = new { exactOccurrence = ExactOccurrence(localModel, localGold), goldAssociation = Association(localModel, localGold, GlobalAssociation(localModel, localGold)) },
            finalFailClosedOutput = new { exactOccurrence = ExactOccurrence(localFinal, localGold), goldAssociation = Association(localFinal, localGold, GlobalAssociation(localFinal, localGold)) } };
    }

    internal static DiagnosticRow Diagnostic(int goldIndex, Gold gold, IReadOnlyList<Prediction> model, IReadOnlyList<Prediction> final,
        IReadOnlyList<Prediction> representation, Matching modelAssociation, Matching finalAssociation, IReadOnlySet<string> finalKeys)
    {
        var pre = modelAssociation.ByGold.TryGetValue(goldIndex, out var modelEdge) ? modelEdge : null;
        var post = finalAssociation.ByGold.TryGetValue(goldIndex, out var finalEdge) ? finalEdge : null;
        var modelCandidate = pre is null ? null : model[pre.PredictionIndex];
        var finalCandidate = post is null ? null : final[post.PredictionIndex];
        var reps = representation.Where(value => value.DocumentId == gold.DocumentId && Overlap(value.Parts, gold.Parts)).Select(value => $"{value.PackId}:{value.CandidateId}").OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var modelOutcome = pre is not null ? Outcome(pre) : reps.Length > 0 ? "ROLE_ERROR" : "NO_MODEL_PROPOSAL";
        var finalOutcome = post is not null ? Outcome(post) : pre is not null && !finalKeys.Contains(CandidateKey(modelCandidate!)) ? "CONFLICT_LOSS" : modelOutcome;
        return new DiagnosticRow(gold.DocumentId, gold.Ordinal, gold.Identity, modelOutcome, finalOutcome,
            pre is null ? null : new { packId = modelCandidate!.PackId, candidateId = modelCandidate.CandidateId, identity = modelCandidate.Identity, pre.MatchKind, coverage = pre.CoverageBps / 10000d },
            post is null ? null : new { packId = finalCandidate!.PackId, candidateId = finalCandidate.CandidateId, identity = finalCandidate.Identity, post.MatchKind, coverage = post.CoverageBps / 10000d }, reps);
    }

    internal static string Outcome(Edge edge) => edge.MatchKind switch { "EXACT" => "EXACT", "FULL_GOLD_COVERAGE" => "EXCESS_EXTENT", _ => "PARTIAL" };
    internal static object Extent(IReadOnlyList<DiagnosticRow> diagnostics, bool modelStage)
    {
        var counts = diagnostics.GroupBy(value => modelStage ? value.ModelOutcome : value.FinalOutcome, StringComparer.Ordinal)
            .OrderBy(value => value.Key, StringComparer.Ordinal).ToDictionary(value => value.Key, value => value.Count());
        return new { stage = modelStage ? "PRE_PROJECTION" : "POST_PROJECTION", counts };
    }

    internal static object Association(IReadOnlyList<Prediction> predictions, IReadOnlyList<Gold> gold, Matching matching) => new
    {
        matched = matching.Edges.Count, unmatchedGold = gold.Count - matching.Edges.Count,
        unmatchedPredictions = predictions.Count - matching.Edges.Count,
        exact = matching.Edges.Count(value => value.MatchKind == "EXACT"), fullGoldCoverageExcess = matching.Edges.Count(value => value.MatchKind == "FULL_GOLD_COVERAGE"),
        partial = matching.Edges.Count(value => value.MatchKind == "OVERLAP"),
    };

    internal static ExactMetric ExactOccurrence(IReadOnlyList<Prediction> predictions, IReadOnlyList<Gold> gold)
    {
        var exactGold = gold.Where(item => predictions.Any(value => value.DocumentId == item.DocumentId && value.Identity == item.Identity)).Count();
        var exactPredictions = predictions.Count(value => gold.Any(item => item.DocumentId == value.DocumentId && item.Identity == value.Identity));
        var fp = predictions.Count - exactPredictions; var fn = gold.Count - exactGold;
        var precision = exactGold + fp == 0 ? 0d : (double)exactGold / (exactGold + fp);
        var recall = exactGold + fn == 0 ? 0d : (double)exactGold / (exactGold + fn);
        return new ExactMetric(exactGold, fp, fn, Math.Round(precision, 4), Math.Round(recall, 4), Math.Round(precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall), 4));
    }

    internal static Matching GlobalAssociation(IReadOnlyList<Prediction> predictions, IReadOnlyList<Gold> gold)
    {
        var rows = predictions.Count; var goldColumns = gold.Count; var columns = goldColumns + rows;
        var edges = new Dictionary<(int Row, int Column), Edge>();
        for (var row = 0; row < rows; row++) for (var column = 0; column < goldColumns; column++)
        {
            if (predictions[row].DocumentId != gold[column].DocumentId || !Overlap(predictions[row].Parts, gold[column].Parts)) continue;
            var coverage = (int)Math.Round(Coverage(gold[column].Parts, predictions[row].Parts) * 10000d, MidpointRounding.AwayFromZero);
            var exact = predictions[row].Identity == gold[column].Identity;
            var full = coverage == 10000;
            // Hierarchical weights: a further association outweighs all quality deltas; one exact
            // outweighs all lower-rank edges; one full-coverage edge outweighs all coverage deltas.
            var reward = 1_000_000_000_000_000L + (exact ? 1_000_000_000_000L : full ? 100_000_000L : 0L) + coverage;
            edges[(row, column)] = new Edge(row, column, exact ? "EXACT" : full ? "FULL_GOLD_COVERAGE" : "OVERLAP", coverage, reward);
        }
        var maxReward = edges.Count == 0 ? 0L : edges.Values.Max(value => value.Reward);
        var cost = new long[rows + 1, columns + 1];
        for (var row = 1; row <= rows; row++) for (var column = 1; column <= columns; column++)
            cost[row, column] = maxReward - (edges.TryGetValue((row - 1, column - 1), out var edge) ? edge.Reward : 0L);
        var assignment = Hungarian(cost, rows, columns);
        var matched = assignment.Select((column, row) => edges.TryGetValue((row, column - 1), out var edge) ? edge : null).Where(value => value is not null).Cast<Edge>().ToArray();
        return new Matching(matched, matched.ToDictionary(value => value.GoldIndex));
    }

    // Rectangular Hungarian minimization, deterministic due to row/column iteration order.
    private static int[] Hungarian(long[,] cost, int rows, int columns)
    {
        var u = new long[rows + 1]; var v = new long[columns + 1]; var p = new int[columns + 1]; var way = new int[columns + 1];
        for (var row = 1; row <= rows; row++)
        {
            p[0] = row; var column0 = 0; var min = Enumerable.Repeat(long.MaxValue / 4, columns + 1).ToArray(); var used = new bool[columns + 1];
            do
            {
                used[column0] = true; var row0 = p[column0]; var delta = long.MaxValue / 4; var next = 0;
                for (var column = 1; column <= columns; column++) if (!used[column])
                {
                    var current = cost[row0, column] - u[row0] - v[column];
                    if (current < min[column]) { min[column] = current; way[column] = column0; }
                    if (min[column] < delta) { delta = min[column]; next = column; }
                }
                for (var column = 0; column <= columns; column++) if (used[column]) { u[p[column]] += delta; v[column] -= delta; } else min[column] -= delta;
                column0 = next;
            } while (p[column0] != 0);
            do { var previous = way[column0]; p[column0] = p[previous]; column0 = previous; } while (column0 != 0);
        }
        var assignment = Enumerable.Repeat(0, rows).ToArray();
        for (var column = 1; column <= columns; column++) if (p[column] != 0) assignment[p[column] - 1] = column;
        return assignment;
    }

    internal static Dictionary<string, List<Gold>> ReadGold(IReadOnlyDictionary<string, PdfCandidateAuthorityDocumentPlan> plans) => Documents.ToDictionary(document => document.Id, document =>
    {
        var bytes = File.ReadAllBytes(TestRepository.Path($"eval/a99-closed-loop/gold-current/documents/{document.Id}.gold.v1.json")); Assert.Equal(document.GoldSha, Hash(bytes));
        using var json = JsonDocument.Parse(bytes); var ordinal = 0;
        return json.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().Select(claim =>
        {
            ordinal++; var sourceParts = JsonSerializer.Deserialize<List<SemanticSourcePart>>(claim.GetProperty("sourceParts").GetRawText())!;
            var bound = SemanticSourcePartBinder.Bind(plans[document.Id].SourceAtoms, sourceParts); Assert.True(bound.IsBound, bound.Reason);
            return new Gold(document.Id, ordinal, bound.Identity, bound.Parts.Select(value => new Part(value.Alias, value.Start, value.End)).ToArray());
        }).ToList();
    }, StringComparer.Ordinal);

    internal static void VerifyEvaluationBasis(IReadOnlyDictionary<string, PdfCandidateAuthorityDocumentPlan> plans)
    {
        using var basis = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(EvaluationBasisPath)));
        Assert.Equal("USER_RECONFIRMED_FROZEN", basis.RootElement.GetProperty("status").GetString());
        Assert.Equal(139, basis.RootElement.GetProperty("cohort").GetProperty("totalGoldOccurrences").GetInt32());
        foreach (var document in Documents)
        {
            var row = basis.RootElement.GetProperty("cohort").GetProperty("documentsById").GetProperty(document.Id);
            Assert.Equal(document.GoldSha, row.GetProperty("canonicalGoldSha256").GetString());
            Assert.Equal(document.Count, row.GetProperty("semanticHeadingTotal").GetInt32());
            Assert.Equal(plans[document.Id].SourceSha256, row.GetProperty("sourceSha256").GetString());
        }
    }

    internal static Prediction PredictionFrom(string documentId, string packId, string candidateId, IReadOnlyList<BoundSourcePart> parts)
    {
        var mapped = parts.Select(value => new Part(value.Alias, value.Start, value.End)).ToArray();
        return new Prediction(documentId, packId, candidateId, string.Join("|", mapped.Select(value => $"{value.Alias}:{value.Start}-{value.End}")), mapped);
    }

    internal static string CandidateKey(Prediction value) => $"{value.DocumentId}:{value.PackId}:{value.CandidateId}";
    internal static bool Overlap(IReadOnlyList<Part> left, IReadOnlyList<Part> right) => left.Any(a => right.Any(b => a.Alias == b.Alias && Math.Max(a.Start, b.Start) < Math.Min(a.End, b.End)));
    private static double Coverage(IReadOnlyList<Part> gold, IReadOnlyList<Part> predicted)
    {
        var total = gold.Sum(value => value.End - value.Start); var covered = 0;
        foreach (var expected in gold)
        {
            var intervals = predicted.Where(value => value.Alias == expected.Alias).Select(value => (Start: Math.Max(value.Start, expected.Start), End: Math.Min(value.End, expected.End))).Where(value => value.End > value.Start).OrderBy(value => value.Start).ToArray(); var start = -1; var end = -1;
            foreach (var interval in intervals) if (start < 0) { start = interval.Start; end = interval.End; } else if (interval.Start <= end) end = Math.Max(end, interval.End); else { covered += end - start; start = interval.Start; end = interval.End; }
            if (start >= 0) covered += end - start;
        }
        return total == 0 ? 0 : Math.Min(1d, (double)covered / total);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
