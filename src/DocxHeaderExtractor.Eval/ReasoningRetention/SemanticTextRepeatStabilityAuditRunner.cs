using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Offline repeat-stability audit for the already frozen semantic-text baseline. This
/// runner never creates a provider client. It freezes the cross-repeat matrix and predefined
/// consensus predictions before opening Strict Gold for scoring.</summary>
public static class SemanticTextRepeatStabilityAuditRunner
{
    private const string BaselineRoot = "eval/a99-closed-loop/semantic-text-generalization";
    private const string OutputRoot = "eval/a99-closed-loop/semantic-text-repeat-stability";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string Model = "qwen/qwen3.7-flash";
    private static readonly string[] Documents = ["DOC-0001", "DOC-0205", "DOC-0252", "DOC-0256", "DOC-0258"];
    private static readonly string[] Repeats = ["r1", "r2", "r3"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var baselineRoot = Path.Combine(repoRoot, BaselineRoot.Replace('/', Path.DirectorySeparatorChar));
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var startHead = GitSha(repoRoot);
        Console.WriteLine($"START_HEAD={startHead}");
        Console.WriteLine($"BRANCH={Git(repoRoot, "branch --show-current")}");
        Console.WriteLine("MODEL_CALLS=0");

        var runs = LoadAndVerifyBaseline(baselineRoot);
        var matrix = BuildMatrix(runs);
        await WriteJson(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-repeat-stability-manifest-v1",
            startHead, branch = Git(repoRoot, "branch --show-current"), model = Model,
            baselineArtifact = BaselineRoot, cohort = Documents, repeats = Repeats,
            canonicalIdentity = "documentId + sourceId + exact start + exact end",
            operatorsDefinedBeforeGold = new[] { "INTERSECTION_3_OF_3", "MAJORITY_2_OF_3", "UNION_1_OF_3" },
            rolePolicy = "preserve semantic existence; role disagreement is reported separately",
            spanPolicy = "exact canonical identities only; no fuzzy merge or fabricated span",
            providerCalls = 0, modelCalls = 0, goldReadBeforeFreeze = false,
        }, ct);
        await WriteJson(Path.Combine(output, "baseline-matrix.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-repeat-stability-matrix-v1",
            canonicalIdentity = "documentId + sourceId + exact start + exact end",
            runs = runs.Select(RunManifestRow).ToArray(), occurrences = matrix,
            goldReadBeforeFreeze = false,
        }, ct);

        var operators = new[]
        {
            (Name: "intersection-3of3", Label: "INTERSECTION_3_OF_3", MinimumPresence: 3),
            (Name: "majority-2of3", Label: "MAJORITY_2_OF_3", MinimumPresence: 2),
            (Name: "union-1of3", Label: "UNION_1_OF_3", MinimumPresence: 1),
        };
        foreach (var op in operators)
        {
            var opDir = Path.Combine(output, "consensus", op.Name);
            Directory.CreateDirectory(opDir);
            var rows = matrix.Where(x => x.PresenceCount >= op.MinimumPresence)
                .Select(x => new ConsensusRow(x.DocumentId, x.SourceId, x.Start, x.End, x.Text, x.RoleR1 ?? x.RoleR2 ?? x.RoleR3!))
                .OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.SourceId, StringComparer.Ordinal)
                .ThenBy(x => x.Start).ThenBy(x => x.End).ToArray();
            var prediction = new
            {
                schemaVersion = "a99-semantic-text-repeat-stability-consensus-prediction-v1",
                operatorName = op.Label, threshold = op.MinimumPresence, rows, goldReadBeforeFreeze = false,
            };
            var predictionPath = Path.Combine(opDir, "prediction.v1.json");
            await WriteJson(predictionPath, prediction, ct);
            var freeze = new
            {
                schemaVersion = "a99-semantic-text-repeat-stability-consensus-freeze-v1",
                operatorName = op.Label, threshold = op.MinimumPresence, model = Model,
                predictionSha256 = Sha256(predictionPath), rowCount = rows.Length,
                providerCalls = 0, modelCalls = 0, goldReadBeforeFreeze = false,
                frozenUtc = DateTimeOffset.UtcNow,
            };
            await WriteJson(Path.Combine(opDir, "freeze.v1.json"), freeze, ct);
        }

        // Gold is opened only after the matrix and all predefined consensus predictions have
        // been persisted and their freeze hashes verified.
        var goldByDocument = LoadGold(repoRoot);
        var baselineMetrics = Repeats.Select(repeat =>
        {
            var predicted = runs.Where(run => run.Repeat == repeat).SelectMany(run => run.Rows.Select(row => Key(run.DocumentId, row.SourceId, row.Start, row.End))).ToHashSet(StringComparer.Ordinal);
            var gold = goldByDocument.SelectMany(pair => pair.Value.Select(row => Key(pair.Key, row.SourceId, row.Start, row.End))).ToHashSet(StringComparer.Ordinal);
            return Metrics(repeat.ToUpperInvariant(), gold, predicted, runs.Where(run => run.Repeat == repeat).Sum(ReadSystemLoss));
        }).ToArray();
        var consensusMetrics = new List<Metric>();
        foreach (var op in operators)
        {
            var opDir = Path.Combine(output, "consensus", op.Name);
            using var predictionDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(opDir, "prediction.v1.json"), ct));
            var rows = predictionDoc.RootElement.GetProperty("rows").EnumerateArray().Select(ParseConsensusRow).ToArray();
            var metric = ScoreConsensus(op.Label, rows, goldByDocument);
            consensusMetrics.Add(metric);
            await WriteJson(Path.Combine(opDir, "score.v1.json"), metric, ct);
        }

        var stability = BuildStability(runs, goldByDocument);
        var documentReport = BuildDocumentReport(runs, goldByDocument, stability);
        var table = baselineMetrics.Concat(consensusMetrics).ToArray();
        var largest = LargestBucket(stability, documentReport);
        var classification = FinalClassification(stability, consensusMetrics, baselineMetrics);
        await WriteJson(Path.Combine(output, "gold-stability.v1.json"), stability, ct);
        await WriteJson(Path.Combine(output, "document-report.v1.json"), documentReport, ct);
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-repeat-stability-summary-v1",
            startHead, endHead = GitSha(repoRoot), model = Model,
            baseline = baselineMetrics, consensus = consensusMetrics,
            goldTotal = 153, systemLoss = baselineMetrics.Sum(x => x.SystemLoss),
            stability, documentReport, largestResidualBucket = largest,
            nextSingleIntervention = NextIntervention(largest), finalClassification = classification,
            providerCalls = 0, modelCalls = 0, goldReadBeforeFreeze = false,
        }, ct);
        PrintReport(baselineMetrics, consensusMetrics, stability, largest, classification);
        Console.WriteLine($"END_HEAD={GitSha(repoRoot)}");
        return 0;
    }

    private static IReadOnlyList<BaselineRun> LoadAndVerifyBaseline(string baselineRoot)
    {
        var result = new List<BaselineRun>();
        foreach (var documentId in Documents)
        foreach (var repeat in Repeats)
        {
            var dir = Path.Combine(baselineRoot, documentId, repeat);
            var predictionPath = Path.Combine(dir, "prediction.v1.json");
            var resultPath = Path.Combine(dir, "result.v1.json");
            var freezePath = Path.Combine(dir, "freeze.v1.json");
            using var freeze = JsonDocument.Parse(File.ReadAllText(freezePath));
            var frozen = freeze.RootElement;
            if (frozen.GetProperty("goldReadBeforeFreeze").GetBoolean()) throw new InvalidDataException($"GOLD_FIREWALL_FAILED:{documentId}:{repeat}");
            if (!string.Equals(frozen.GetProperty("predictionSha256").GetString(), Sha256(predictionPath), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(frozen.GetProperty("resultSha256").GetString(), Sha256(resultPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"BASELINE_FREEZE_HASH_MISMATCH:{documentId}:{repeat}");
            using var prediction = JsonDocument.Parse(File.ReadAllText(predictionPath));
            var rows = prediction.RootElement.GetProperty("finalHeadings").EnumerateArray().Select(ParsePredictionRow).ToArray();
            result.Add(new BaselineRun(documentId, repeat, dir, rows));
        }
        return result;
    }

    private static IReadOnlyList<MatrixRow> BuildMatrix(IReadOnlyList<BaselineRun> runs)
    {
        var all = runs.SelectMany(run => run.Rows.Select(row => (run, row))).GroupBy(x => Key(x.run.DocumentId, x.row.SourceId, x.row.Start, x.row.End), StringComparer.Ordinal);
        return all.Select(group =>
        {
            var rows = group.ToArray();
            var byRepeat = Repeats.ToDictionary(repeat => repeat, repeat => rows.Where(x => x.run.Repeat == repeat).Select(x => x.row).SingleOrDefault(), StringComparer.Ordinal);
            return new MatrixRow(group.First().run.DocumentId, group.First().row.SourceId, group.First().row.Start, group.First().row.End,
                rows.Select(x => x.row.Text).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "",
                byRepeat["r1"]?.Role, byRepeat["r2"]?.Role, byRepeat["r3"]?.Role,
                byRepeat.Values.Count(x => x is not null));
        }).OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.SourceId, StringComparer.Ordinal).ThenBy(x => x.Start).ThenBy(x => x.End).ToArray();
    }

    private static object BuildStability(IReadOnlyList<BaselineRun> runs, IReadOnlyDictionary<string, IReadOnlyList<GoldRow>> goldByDocument)
    {
        var rows = goldByDocument.SelectMany(pair => pair.Value.Select(gold =>
        {
            var local = runs.Where(x => x.DocumentId == pair.Key).OrderBy(x => x.Repeat, StringComparer.Ordinal).ToArray();
            var exact = local.Select(run => run.Rows.Any(row => Same(row, gold))).ToArray();
            var near = local.Select(run => run.Rows.Any(row => Near(row, gold))).ToArray();
            var exactCount = exact.Count(x => x);
            var roleVariant = exact.All(x => x) && local.SelectMany(run => run.Rows.Where(row => Same(row, gold)).Select(row => row.Role)).Distinct(StringComparer.Ordinal).Count() > 1;
            var spanVariant = exactCount < 3 && near.Any(x => x);
            var classification = exactCount == 3 ? "EXACT_STABLE" : spanVariant ? "SPAN_VARIANT" : exactCount == 0 ? "TRUE_STABLE_OMISSION" : "STOCHASTIC_OMISSION";
            return new GoldStabilityRow(pair.Key, gold.SourceId, gold.Start, gold.End, gold.Text, exact.Count(x => x), classification, roleVariant);
        })).ToArray();
        return new
        {
            goldOccurrences = rows,
            found3of3 = rows.Count(x => x.ExactPresenceCount == 3),
            found2of3 = rows.Count(x => x.ExactPresenceCount == 2),
            found1of3 = rows.Count(x => x.ExactPresenceCount == 1),
            found0of3 = rows.Count(x => x.ExactPresenceCount == 0),
            stableExact = rows.Count(x => x.Classification == "EXACT_STABLE"),
            spanVariant = rows.Count(x => x.Classification == "SPAN_VARIANT"),
            stableOmission = rows.Count(x => x.Classification == "TRUE_STABLE_OMISSION"),
            stochasticOmission = rows.Count(x => x.Classification == "STOCHASTIC_OMISSION"),
            roleVariant = rows.Count(x => x.RoleVariant),
            fpStability = BuildFalsePositiveStability(runs, goldByDocument),
        };
    }

    private static object BuildFalsePositiveStability(IReadOnlyList<BaselineRun> runs, IReadOnlyDictionary<string, IReadOnlyList<GoldRow>> goldByDocument)
    {
        var rows = runs.SelectMany(run => run.Rows.Select(row => (run, row))).GroupBy(x => Key(x.run.DocumentId, x.row.SourceId, x.row.Start, x.row.End), StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First().row;
                var gold = goldByDocument[group.First().run.DocumentId].Any(x => Same(first, x));
                var count = Repeats.Count(repeat => group.Any(x => x.run.Repeat == repeat));
                return new FalsePositiveStabilityRow(group.First().run.DocumentId, first.SourceId, first.Start, first.End, first.Text, count, gold);
            }).Where(x => !x.IsGold).ToArray();
        return new
        {
            falsePositiveOccurrences = rows,
            fp3of3 = rows.Count(x => x.PresenceCount == 3),
            fp2of3 = rows.Count(x => x.PresenceCount == 2),
            fp1of3 = rows.Count(x => x.PresenceCount == 1),
            stableFalsePositives = rows.Count(x => x.PresenceCount == 3),
            stochasticFalsePositives = rows.Count(x => x.PresenceCount < 3),
        };
    }

    private static object BuildDocumentReport(IReadOnlyList<BaselineRun> runs, IReadOnlyDictionary<string, IReadOnlyList<GoldRow>> goldByDocument, object stabilityObject)
    {
        // The anonymous stability object is intentionally serialized for the artifact. Recompute
        // the compact per-document view from the same frozen rows to avoid Gold-sensitive state
        // leaking into the pre-Gold matrix construction.
        var rows = new List<object>();
        foreach (var documentId in Documents)
        {
            var localRuns = runs.Where(x => x.DocumentId == documentId).ToArray();
            var gold = goldByDocument[documentId];
            var classifications = gold.Select(g =>
            {
                var exact = localRuns.Count(run => run.Rows.Any(row => Same(row, g)));
                var near = localRuns.Any(run => run.Rows.Any(row => Near(row, g)));
                return exact == 3 ? "EXACT_STABLE" : near ? "SPAN_VARIANT" : exact == 0 ? "TRUE_STABLE_OMISSION" : "STOCHASTIC_OMISSION";
            }).ToArray();
            var metrics = localRuns.Select(run => Score(documentId, run.Rows, gold, 0)).ToArray();
            var fps = localRuns.Select(run => run.Rows.Count(row => !gold.Any(g => Same(row, g)))).ToArray();
            rows.Add(new
            {
                documentId, gold = gold.Count,
                tpMedian = Median(metrics.Select(x => x.Tp)), fpMedian = Median(fps), fnMedian = Median(metrics.Select(x => x.Fn)),
                stableOmissions = classifications.Count(x => x == "TRUE_STABLE_OMISSION"),
                stochasticOmissions = classifications.Count(x => x == "STOCHASTIC_OMISSION"),
                spanVariants = classifications.Count(x => x == "SPAN_VARIANT"),
                stableFalsePositives = localRuns.SelectMany(run => run.Rows).GroupBy(row => Key(documentId, row.SourceId, row.Start, row.End), StringComparer.Ordinal).Count(group => group.Count() == 3 && !gold.Any(g => Same(group.First(), g))),
                stochasticFalsePositives = localRuns.SelectMany(run => run.Rows).GroupBy(row => Key(documentId, row.SourceId, row.Start, row.End), StringComparer.Ordinal).Count(group => group.Count() < 3 && !gold.Any(g => Same(group.First(), g))),
                roleVariants = gold.Count(g => localRuns.All(run => run.Rows.Any(row => Same(row, g))) && localRuns.SelectMany(run => run.Rows.Where(row => Same(row, g)).Select(row => row.Role)).Distinct(StringComparer.Ordinal).Count() > 1),
            });
        }
        return rows;
    }

    private static Metric Score(string label, IReadOnlyList<PredictionRow> rows, IReadOnlyList<GoldRow> gold, int systemLoss)
    {
        var predictionKeys = rows.Select(x => Key(label, x.SourceId, x.Start, x.End)).ToHashSet(StringComparer.Ordinal);
        var goldKeys = gold.Select(x => Key(label, x.SourceId, x.Start, x.End)).ToHashSet(StringComparer.Ordinal);
        return Metrics(label, goldKeys, predictionKeys, systemLoss);
    }

    private static Metric ScoreConsensus(string label, IReadOnlyList<ConsensusRow> rows, IReadOnlyDictionary<string, IReadOnlyList<GoldRow>> goldByDocument)
    {
        var predicted = rows.Select(x => Key(x.DocumentId, x.SourceId, x.Start, x.End)).ToHashSet(StringComparer.Ordinal);
        var gold = goldByDocument.SelectMany(pair => pair.Value.Select(x => Key(pair.Key, x.SourceId, x.Start, x.End))).ToHashSet(StringComparer.Ordinal);
        return Metrics(label, gold, predicted, 0);
    }

    private static Metric Metrics(string label, IReadOnlySet<string> gold, IReadOnlySet<string> predictions, int systemLoss)
    {
        var tp = predictions.Intersect(gold, StringComparer.Ordinal).Count();
        var fp = predictions.Except(gold, StringComparer.Ordinal).Count();
        var fn = gold.Except(predictions, StringComparer.Ordinal).Count();
        var p = tp + fp == 0 ? 0d : (double)tp / (tp + fp);
        var r = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        var f1 = p + r == 0 ? 0d : 2 * p * r / (p + r);
        return new Metric(label, tp, fp, fn, p, r, f1, systemLoss);
    }

    private static string LargestBucket(object stabilityObject, object documentReport)
    {
        var stabilityJson = JsonSerializer.SerializeToElement(stabilityObject, JsonOptions);
        var candidates = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["STABLE_OMISSION"] = stabilityJson.GetProperty("stableOmission").GetInt32(),
            ["STOCHASTIC_OMISSION"] = stabilityJson.GetProperty("stochasticOmission").GetInt32(),
            ["SPAN_VARIANT"] = stabilityJson.GetProperty("spanVariant").GetInt32(),
            ["STABLE_FP"] = stabilityJson.GetProperty("fpStability").GetProperty("stableFalsePositives").GetInt32(),
            ["STOCHASTIC_FP"] = stabilityJson.GetProperty("fpStability").GetProperty("stochasticFalsePositives").GetInt32(),
        };
        return candidates.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key;
    }

    private static string NextIntervention(string bucket) => bucket switch
    {
        "STABLE_FP" => "VERIFIER_ONLY_PASS",
        "STOCHASTIC_FP" => "MAJORITY_CONSISTENCY_HARNESS",
        "SPAN_VARIANT" => "SEMANTIC_BOUNDARY_CONTRACT_AUDIT",
        _ => "STRONGER_SEMANTIC_EVIDENCE_OR_MODEL_CAPABILITY_INVESTIGATION",
    };

    private static string FinalClassification(object stabilityObject, IReadOnlyList<Metric> consensus, IReadOnlyList<Metric> baseline)
    {
        var stability = JsonSerializer.SerializeToElement(stabilityObject, JsonOptions);
        var stableOmission = stability.GetProperty("stableOmission").GetInt32();
        var span = stability.GetProperty("spanVariant").GetInt32();
        var fp = stability.GetProperty("fpStability");
        var stableFp = fp.GetProperty("stableFalsePositives").GetInt32();
        var stochasticFp = fp.GetProperty("stochasticFalsePositives").GetInt32();
        var majority = consensus.Single(x => x.Label == "MAJORITY_2_OF_3");
        var best = baseline.Max(x => x.F1);
        if (majority.F1 > best + .01) return "REPEAT_CONSISTENCY_RECOVERS_ACCURACY";
        var largest = new[]
        {
            (Name: "STABLE_MODEL_ERRORS_DOMINATE", Count: stableOmission),
            (Name: "SPAN_INSTABILITY_DOMINATES", Count: span),
            (Name: "MIXED_RESIDUALS_NO_CLEAR_WINNER", Count: stableFp),
            (Name: "MIXED_RESIDUALS_NO_CLEAR_WINNER", Count: stochasticFp),
        }.OrderByDescending(x => x.Count).First();
        if (largest.Count > 0 && largest.Name != "MIXED_RESIDUALS_NO_CLEAR_WINNER") return largest.Name;
        return "MIXED_RESIDUALS_NO_CLEAR_WINNER";
    }

    private static void PrintReport(IReadOnlyList<Metric> baseline, IReadOnlyList<Metric> consensus, object stability, string largest, string classification)
    {
        var s = JsonSerializer.SerializeToElement(stability, JsonOptions);
        Console.WriteLine("BASELINE / CONSENSUS | TP | FP | FN | P | R | F1");
        foreach (var metric in baseline.Concat(consensus)) Console.WriteLine($"{metric.Label} | {metric.Tp} | {metric.Fp} | {metric.Fn} | {metric.Precision:0.######} | {metric.Recall:0.######} | {metric.F1:0.######}");
        Console.WriteLine($"GOLD_STABILITY=3/3:{s.GetProperty("found3of3").GetInt32()} 2/3:{s.GetProperty("found2of3").GetInt32()} 1/3:{s.GetProperty("found1of3").GetInt32()} 0/3:{s.GetProperty("found0of3").GetInt32()}");
        var fp = s.GetProperty("fpStability");
        Console.WriteLine($"FP_STABILITY=3/3:{fp.GetProperty("fp3of3").GetInt32()} 2/3:{fp.GetProperty("fp2of3").GetInt32()} 1/3:{fp.GetProperty("fp1of3").GetInt32()}");
        Console.WriteLine($"SPAN=stableExact:{s.GetProperty("stableExact").GetInt32()} spanVariant:{s.GetProperty("spanVariant").GetInt32()} stableOmission:{s.GetProperty("stableOmission").GetInt32()} stochasticOmission:{s.GetProperty("stochasticOmission").GetInt32()}");
        Console.WriteLine($"LARGEST_RESIDUAL_BUCKET={largest}");
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
    }

    private static object RunManifestRow(BaselineRun run) => new { documentId = run.DocumentId, repeat = run.Repeat, predictionCount = run.Rows.Count };
    private static PredictionRow ParsePredictionRow(JsonElement x) => new(x.GetProperty("sourceId").GetString()!, x.GetProperty("start").GetInt32(), x.GetProperty("end").GetInt32(), x.GetProperty("text").GetString() ?? "", RoleText(x.GetProperty("role")));
    private static ConsensusRow ParseConsensusRow(JsonElement x) => new(x.GetProperty("documentId").GetString()!, x.GetProperty("sourceId").GetString()!, x.GetProperty("start").GetInt32(), x.GetProperty("end").GetInt32(), x.GetProperty("text").GetString() ?? "", x.GetProperty("role").GetString() ?? "");
    private static string RoleText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        _ => value.GetRawText(),
    };
    private static double Median(IEnumerable<int> values) { var a = values.OrderBy(x => x).ToArray(); return a.Length == 0 ? 0 : a[a.Length / 2]; }
    private static bool Same(PredictionRow row, GoldRow gold) => row.SourceId == gold.SourceId && row.Start == gold.Start && row.End == gold.End;
    private static bool Near(PredictionRow row, GoldRow gold) => row.SourceId == gold.SourceId &&
        (row.Start < gold.End && gold.Start < row.End || row.Text.Contains(gold.Text, StringComparison.Ordinal) || gold.Text.Contains(row.Text, StringComparison.Ordinal));
    private static string Key(string documentId, string sourceId, int start, int end) => $"{documentId}:{sourceId}:{start}:{end}";
    private static int ReadSystemLoss(BaselineRun run) { using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.Directory, "score.v1.json"))); return doc.RootElement.TryGetProperty("systemLoss", out var value) && value.TryGetInt32(out var loss) ? loss : 0; }
    private static IReadOnlyDictionary<string, IReadOnlyList<GoldRow>> LoadGold(string repoRoot)
    {
        var result = new Dictionary<string, IReadOnlyList<GoldRow>>(StringComparer.Ordinal);
        foreach (var documentId in Documents)
        {
            var path = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", documentId + ".occurrence-gold-v1.json");
            result[documentId] = ReasoningGoldArtifactLoader.LoadOccurrence(path).Where(x => x.HeadingSpan is not null).Select(x => new GoldRow(x.SourceId, x.HeadingSpan!.Start, x.HeadingSpan.End, x.ExactText)).ToArray();
        }
        return result;
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string GitSha(string root) => Git(root, "rev-parse HEAD");
    private static string Git(string root, string args) { try { using var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return p?.StandardOutput.ReadToEnd().Trim() ?? "NOT_PERSISTED"; } catch { return "NOT_PERSISTED"; } }
    private static async Task WriteJson(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);

    private sealed record BaselineRun(string DocumentId, string Repeat, string Directory, IReadOnlyList<PredictionRow> Rows);
    private sealed record PredictionRow(string SourceId, int Start, int End, string Text, string Role);
    private sealed record ConsensusRow(string DocumentId, string SourceId, int Start, int End, string Text, string Role);
    private sealed record MatrixRow(string DocumentId, string SourceId, int Start, int End, string Text, string? RoleR1, string? RoleR2, string? RoleR3, int PresenceCount);
    private sealed record GoldRow(string SourceId, int Start, int End, string Text);
    private sealed record GoldStabilityRow(string DocumentId, string SourceId, int Start, int End, string Text, int ExactPresenceCount, string Classification, bool RoleVariant);
    private sealed record FalsePositiveStabilityRow(string DocumentId, string SourceId, int Start, int End, string Text, int PresenceCount, bool IsGold);
    private sealed record Metric(string Label, int Tp, int Fp, int Fn, double Precision, double Recall, double F1, int SystemLoss);

    private sealed class GoldComparer : IEqualityComparer<GoldRow>
    {
        public static readonly GoldComparer Instance = new();
        public bool Equals(GoldRow? x, GoldRow? y) => x is not null && y is not null && x.SourceId == y.SourceId && x.Start == y.Start && x.End == y.End;
        public int GetHashCode(GoldRow obj) => HashCode.Combine(obj.SourceId, obj.Start, obj.End);
    }
}
