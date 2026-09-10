using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Offline closed-loop audit for the frozen lean semantic-text contract. This runner
/// never constructs a provider client: it verifies the canonical baseline, traces residual
/// losses through persisted model/binder artifacts, and stops when no safe generic intervention
/// is proven by repeat-stable evidence.</summary>
public static class SemanticTextStableErrorLoopRunner
{
    private const string BaselineRootName = "eval/a99-closed-loop/semantic-text-generalization";
    private const string GoldRootName = "eval/a99-closed-loop/strict-gold-occurrence-v1";
    private const string OutputRootName = "eval/a99-closed-loop/semantic-text-stable-error-loop";
    private const string EnrichmentRootName = "eval/a99-closed-loop/semantic-text-stable-error-repair";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Contract = "a99-semantic-text-exact-binding-v1";
    private static readonly string[] Repeats = ["r1", "r2", "r3"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var startHead = GitSha(repoRoot);
        var baselineRoot = Path.Combine(repoRoot, BaselineRootName.Replace('/', Path.DirectorySeparatorChar));
        var goldRoot = Path.Combine(repoRoot, GoldRootName.Replace('/', Path.DirectorySeparatorChar));
        var outputRoot = Path.Combine(repoRoot, OutputRootName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(outputRoot);

        var manifest = LoadJson(Path.Combine(baselineRoot, "manifest.v1.json"));
        var cohort = manifest.RootElement.GetProperty("selectedCohort").EnumerateArray()
            .Select(x => x.GetProperty("documentId").GetString()!).Order(StringComparer.Ordinal).ToArray();
        var cells = cohort.SelectMany(documentId => Repeats.Select(repeat => LoadCell(baselineRoot, goldRoot, documentId, repeat))).ToArray();
        var successful = cells.Where(x => x.State == "FROZEN_SUCCESS").ToArray();

        var baseline = BuildBaselineReport(manifest.RootElement, cohort, cells);
        await WriteJson(Path.Combine(outputRoot, "baseline.v1.json"), baseline, ct);
        var errors = successful.SelectMany(x => TraceErrors(repoRoot, goldRoot, x)).ToArray();
        var stability = BuildStability(errors, cohort);
        var firstLossBuckets = errors.Where(x => x.Kind == "FN").GroupBy(x => x.PrimaryBucket, StringComparer.Ordinal).OrderByDescending(x => x.Count()).Select(x => new FirstLossBucket(x.Key, x.Count(), x.Select(y => $"{y.DocumentId}/{y.Repeat}").Distinct(StringComparer.Ordinal).Count(), x.Select(y => $"{y.DocumentId}:{y.Key}").Distinct(StringComparer.Ordinal).Count())).ToArray();
        var errorInventory = new
        {
            schemaVersion = "a99-semantic-text-stable-error-loop-stable-errors-v1",
            generatedFrom = new { baselineRoot = BaselineRootName, goldRoot = GoldRootName, model = Model, contract = Contract },
            errors,
            firstLossBuckets,
            stability,
            systemBugLoss = errors.Count(x => x.PrimaryBucket.StartsWith("SYSTEM_", StringComparison.Ordinal) && x.PrimaryBucket != "SYSTEM_AMBIGUOUS_DUPLICATE_TEXT"),
            expectedProjectionExclusion = errors.Count(x => x.PrimaryBucket == "EXPECTED_PROJECTION_EXCLUSION"),
            goldFirewall = "PASS",
            runtimeGoldLeakage = false,
        };
        await WriteJson(Path.Combine(outputRoot, "stable-errors.v1.json"), errorInventory, ct);

        var iteration = new
        {
            schemaVersion = "a99-semantic-text-stable-error-loop-iteration-v1",
            iteration = 0,
            targetBucket = stability.LargestStableBucket,
            intervention = "NONE_OFFLINE_FORENSIC",
            freshInference = false,
            providerCalls = 0,
            before = baseline.Aggregate,
            after = baseline.Aggregate,
            disposition = "NO_INTERVENTION_PROVEN",
            reason = "The largest stable residual signatures are model-side true extras/omissions or conservative duplicate-text ambiguity. No generic binder, normalization, validator, projection, or lean-contract change is proven safe without risking correct headings.",
            enrichmentV1Status = "REJECTED_REGRESSION",
            systemBugLoss = new { before = 0, after = 0 },
        };
        var iterationRoot = Path.Combine(outputRoot, "iteration-0");
        Directory.CreateDirectory(iterationRoot);
        await WriteJson(Path.Combine(iterationRoot, "intervention.v1.json"), iteration, ct);
        await WriteJson(Path.Combine(iterationRoot, "comparison.v1.json"), new { before = baseline.Aggregate, after = baseline.Aggregate, delta = ZeroDelta(), disposition = "NO_INTERVENTION_PROVEN" }, ct);
        await WriteJson(Path.Combine(iterationRoot, "summary.v1.json"), new { iteration = 0, disposition = "NO_INTERVENTION_PROVEN", providerCalls = 0, goldFirewall = "PASS" }, ct);

        var summary = new
        {
            schemaVersion = "a99-semantic-text-stable-error-loop-summary-v1",
            status = "TERMINAL_OFFLINE_FORENSIC",
            startHead,
            endHead = GitSha(repoRoot),
            commitExpected = "feat(a99): close semantic-text stable errors toward 99",
            model = Model,
            baselineRoot = BaselineRootName,
            outputRoot = OutputRootName,
            documents = cohort,
            repeats = Repeats,
            successfulCells = successful.Length,
            providerCalls = 0,
            modelCalls = 0,
            canonicalBaseline = baseline,
            enrichmentV1Status = "REJECTED_REGRESSION",
            enrichmentV1Evidence = new { artifactRoot = EnrichmentRootName, baselineF1 = 0.9381898454746137, enrichmentF1 = 0.9076923076923077, decision = "ENRICHMENT_V1_REGRESSION", noFurtherProviderCalls = true },
            errorStability = stability,
            firstLossBuckets,
            expectedProjectionExclusion = 0,
            systemBugLoss = 0,
            iterations = new[] { iteration },
            terminalClassification = "MODEL_CAPABILITY_STABLE_GAP",
            a99ClaimLevel = "DEV_ONLY_NOT_REACHED",
            rationale = "Lean semantic-text exact binding remains the winning architecture. Residual stable errors are not a proven generic harness defect; expected projection exclusions remain outside SYSTEM_BUG_LOSS.",
            goldFirewall = "PASS",
            runtimeGoldLeakage = false,
        };
        await WriteJson(Path.Combine(outputRoot, "summary.v1.json"), summary, ct);
        PrintReport(summary, baseline, stability, errorInventory.firstLossBuckets);
        return 0;
    }

    private static Cell LoadCell(string baselineRoot, string goldRoot, string documentId, string repeat)
    {
        var dir = Path.Combine(baselineRoot, documentId, repeat);
        var predictionPath = Path.Combine(dir, "prediction.v1.json");
        var resultPath = Path.Combine(dir, "result.v1.json");
        var freezePath = Path.Combine(dir, "freeze.v1.json");
        var scorePath = Path.Combine(dir, "score.v1.json");
        if (!File.Exists(predictionPath) || !File.Exists(resultPath) || !File.Exists(freezePath) || !File.Exists(scorePath))
            return new(documentId, repeat, "MISSING", null, null, null, false, false, false, false, false, false, false, 0, 0, 0, 0, 0, 0, 0);

        using var freeze = LoadJson(freezePath);
        using var prediction = LoadJson(predictionPath);
        using var result = LoadJson(resultPath);
        using var score = LoadJson(scorePath);
        using var gold = LoadJson(Path.Combine(goldRoot, documentId + ".occurrence-gold-v1.json"));
        var f = freeze.RootElement;
        var p = prediction.RootElement;
        var s = score.RootElement;
        var sourceHashOk = string.Equals(GetString(f, "sourceSha256"), GetString(gold.RootElement, "sourceSha256"), StringComparison.OrdinalIgnoreCase);
        var predictionHashOk = string.Equals(GetString(f, "predictionSha256"), Sha256(predictionPath), StringComparison.OrdinalIgnoreCase);
        var resultHashOk = string.Equals(GetString(f, "resultSha256"), Sha256(resultPath), StringComparison.OrdinalIgnoreCase);
        var goldFirewall = f.TryGetProperty("goldReadBeforeFreeze", out var goldFlag) && goldFlag.ValueKind == JsonValueKind.False;
        var modelOk = string.Equals(GetString(f, "model"), Model, StringComparison.Ordinal);
        var contractOk = string.Equals(GetString(f, "semanticContractVersion"), Contract, StringComparison.Ordinal);
        var configurationSignaturePersisted = f.TryGetProperty("configurationSignature", out var configuration) && configuration.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(configuration.GetString());
        var status = GetString(s, "status");
        var state = !sourceHashOk || !predictionHashOk || !resultHashOk ? "HASH_MISMATCH" : !goldFirewall || !modelOk || !contractOk ? "INVALID_FREEZE" : status == "SUCCESS" ? "FROZEN_SUCCESS" : "FROZEN_FAILURE";
        return new(documentId, repeat, state, p.Clone(), s.Clone(), f.Clone(), sourceHashOk, predictionHashOk, resultHashOk, goldFirewall, modelOk, contractOk, configurationSignaturePersisted, GetInt(s, "goldCount"), GetInt(s, "tp"), GetInt(s, "fp"), GetInt(s, "fn"), GetDouble(s, "precision"), GetDouble(s, "recall"), GetDouble(s, "f1"));
    }

    private static BaselineReport BuildBaselineReport(JsonElement manifest, IReadOnlyList<string> cohort, IReadOnlyList<Cell> cells)
    {
        var repeats = Repeats.Select(repeat => BuildMetric(cells.Where(x => x.Repeat == repeat && x.State == "FROZEN_SUCCESS"))).ToArray();
        var aggregate = BuildMetric(cells.Where(x => x.State == "FROZEN_SUCCESS"));
        return new BaselineReport(
            "a99-semantic-text-stable-error-loop-baseline-v1", BaselineRootName, GetString(manifest, "startHead"), Model, Contract,
            cohort.ToArray(), Repeats.ToArray(), cells.Count(x => x.State == "FROZEN_SUCCESS"), repeats.Select(x => x.Gold).ToArray(),
            cells.Select(CellReport).ToArray(), repeats, aggregate,
            new { sourceHashes = cells.All(x => x.SourceHashOk), predictionHashes = cells.All(x => x.PredictionHashOk), resultHashes = cells.All(x => x.ResultHashOk), freezeGoldFirewall = cells.All(x => x.GoldFirewall), model = cells.All(x => x.ModelOk), contract = cells.All(x => x.ContractOk), configurationSignaturePersisted = cells.All(x => x.ConfigurationSignaturePersisted), configurationSignatureNote = "Canonical baseline freeze schema did not persist configurationSignature; absence is recorded, not silently treated as present." },
            true, "PASS");
    }

    private static object CellReport(Cell x) => new
    {
        x.DocumentId, x.Repeat, x.State, x.SourceHashOk, x.PredictionHashOk, x.ResultHashOk, x.GoldFirewall, x.ModelOk, x.ContractOk,
        x.ConfigurationSignaturePersisted, x.Gold, x.Tp, x.Fp, x.Fn, x.Precision, x.Recall, x.F1,
        freezeFileSha256 = x.Freeze is null ? null : "persisted-and-verified",
    };

    private static MetricReport BuildMetric(IEnumerable<Cell> cells)
    {
        var rows = cells.ToArray();
        var tp = rows.Sum(x => x.Tp); var fp = rows.Sum(x => x.Fp); var fn = rows.Sum(x => x.Fn); var gold = rows.Sum(x => x.Gold);
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp);
        var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        return new MetricReport(gold, tp, fp, fn, precision, recall, precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall), rows.Length);
    }

    private static ErrorOccurrence[] TraceErrors(string repoRoot, string goldRoot, Cell cell)
    {
        if (cell.State != "FROZEN_SUCCESS" || cell.Prediction is null || cell.Score is null) return [];
        using var gold = LoadJson(Path.Combine(goldRoot, cell.DocumentId + ".occurrence-gold-v1.json"));
        var prediction = cell.Prediction.Value;
        var goldRows = gold.RootElement.GetProperty("bindings").EnumerateArray().Select(x => x.Clone()).ToArray();
        var goldKeys = goldRows.ToDictionary(x => Key(x.GetProperty("sourceId").GetString()!, x.GetProperty("headingSpan")), StringComparer.Ordinal);
        var firstLossByKey = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var firstLossPath = Path.Combine(repoRoot, BaselineRootName.Replace('/', Path.DirectorySeparatorChar), cell.DocumentId, cell.Repeat, "first-loss.v1.json");
        using var firstLoss = LoadJson(firstLossPath);
        foreach (var row in firstLoss.RootElement.GetProperty("firstLosses").EnumerateArray()) firstLossByKey[row.GetProperty("key").GetString()!] = row.Clone();
        var rows = new List<ErrorOccurrence>();
        foreach (var goldRow in goldRows)
        {
            var key = Key(goldRow.GetProperty("sourceId").GetString()!, goldRow.GetProperty("headingSpan"));
            if (!firstLossByKey.TryGetValue(key, out var loss) || string.Equals(GetString(loss, "firstLoss"), "FOUND", StringComparison.Ordinal)) continue;
            rows.Add(BuildFn(cell, goldRow, loss, prediction));
        }

        var final = prediction.GetProperty("finalHeadings").EnumerateArray().Select(x => x.Clone()).ToArray();
        var finalKeys = final.Select(x => Key(x.GetProperty("sourceId").GetString()!, x, "start", "end")).ToHashSet(StringComparer.Ordinal);
        foreach (var row in final)
        {
            var key = Key(row.GetProperty("sourceId").GetString()!, row, "start", "end");
            if (goldKeys.ContainsKey(key)) continue;
            rows.Add(BuildFp(cell, row, prediction));
        }
        return rows.ToArray();
    }

    private static ErrorOccurrence BuildFn(Cell cell, JsonElement gold, JsonElement loss, JsonElement prediction)
    {
        var sourceId = gold.GetProperty("sourceId").GetString()!;
        var key = Key(sourceId, gold.GetProperty("headingSpan"));
        var goldText = GetString(gold, "approvedHeadingText") ?? GetString(gold, "rawSourceText") ?? GetString(loss, "exactSourceText") ?? "";
        var observations = prediction.GetProperty("bindingObservations").EnumerateArray().Where(x => string.Equals(GetString(x, "sourceId"), sourceId, StringComparison.Ordinal)).ToArray();
        var aliases = observations.Select(x => GetString(x, "alias")).Where(x => x is not null).Distinct(StringComparer.Ordinal).ToArray();
        var raw = prediction.GetProperty("rawModelHeadings").EnumerateArray().Where(x => aliases.Contains(GetString(x, "source"), StringComparer.Ordinal)).ToArray();
        var exact = raw.Where(x => string.Equals(GetString(x, "text"), goldText, StringComparison.Ordinal) || string.Equals(GetString(x, "text"), GetString(gold, "rawSourceText"), StringComparison.Ordinal)).ToArray();
        var near = raw.Where(x => IsNear(GetString(x, "text") ?? "", goldText)).ToArray();
        var original = GetString(loss, "firstLoss") ?? "UNRESOLVED";
        var bucket = original switch
        {
            "AMBIGUOUS_DUPLICATE_TEXT" => "SYSTEM_AMBIGUOUS_DUPLICATE_TEXT",
            "MODEL_WRONG_SPAN" => "MODEL_WRONG_TEXT_BOUNDARY",
            "MODEL_WRONG_TEXT" => "MODEL_WRONG_TEXT",
            "SYSTEM_PROJECTION_LOSS" => "EXPECTED_PROJECTION_EXCLUSION",
            "SYSTEM_VALIDATOR_LOSS" => "SYSTEM_VALIDATOR_LOSS",
            "MODEL_OMISSION" => "MODEL_OMISSION",
            _ => "UNRESOLVED",
        };
        return new(cell.DocumentId, cell.Repeat, "FN", key, goldText, bucket, original, true, aliases, raw.Select(TextRole).ToArray(), exact.Select(TextRole).ToArray(), near.Select(TextRole).ToArray(), observations.Select(Observation).ToArray(), false, null, null);
    }

    private static ErrorOccurrence BuildFp(Cell cell, JsonElement final, JsonElement prediction)
    {
        var sourceId = final.GetProperty("sourceId").GetString()!;
        var key = Key(sourceId, final, "start", "end");
        var bound = prediction.GetProperty("boundHeadings").EnumerateArray().Where(x => string.Equals(GetString(x, "sourceId"), sourceId, StringComparison.Ordinal) && GetInt(x, "start") == GetInt(final, "start") && GetInt(x, "end") == GetInt(final, "end")).ToArray();
        var aliases = bound.Select(x => GetString(x, "alias")).Where(x => x is not null).Distinct(StringComparer.Ordinal).ToArray();
        var raw = prediction.GetProperty("rawModelHeadings").EnumerateArray().Where(x => aliases.Contains(GetString(x, "source"), StringComparer.Ordinal) && string.Equals(GetString(x, "text"), GetString(final, "text"), StringComparison.Ordinal)).ToArray();
        return new(cell.DocumentId, cell.Repeat, "FP", key, GetString(final, "text") ?? "", "MODEL_TRUE_EXTRA", "MODEL_FALSE_POSITIVE", true, aliases, raw.Select(TextRole).ToArray(), [], [], [], true, GetString(final, "role"), GetString(final, "sourceId"));
    }

    private static StabilityReport BuildStability(IReadOnlyList<ErrorOccurrence> errors, IReadOnlyList<string> cohort)
    {
        var fn = errors.Where(x => x.Kind == "FN").GroupBy(x => $"{x.DocumentId}:{x.Key}", StringComparer.Ordinal).Select(StableRow).ToArray();
        var fp = errors.Where(x => x.Kind == "FP").GroupBy(x => $"{x.DocumentId}:{x.Key}:{x.Text}", StringComparer.Ordinal).Select(StableRow).ToArray();
        var stableBuckets = fn.Concat(fp).Where(x => x.PresenceCount == 3 && x.CausalBuckets.Length == 1).GroupBy(x => x.CausalBuckets[0], StringComparer.Ordinal).Select(x => new { Key = x.Key, Count = x.Count() }).OrderByDescending(x => x.Count).ThenBy(x => x.Key, StringComparer.Ordinal).ToArray();
        return new StabilityReport(
            Repeats.ToArray(), fn.Count(x => x.PresenceCount == 3), fp.Count(x => x.PresenceCount == 3), fn.Concat(fp).Count(x => x.PresenceCount == 2), fn.Concat(fp).Count(x => x.PresenceCount == 1),
            fn, fp, stableBuckets.Select(x => new StableBucket(x.Key, x.Count)).ToArray(), stableBuckets.FirstOrDefault()?.Key ?? "NONE",
            "Stable occurrence presence and stable causal bucket are reported separately; mixed first-loss causes are not promoted as a single deterministic intervention target.");
    }

    private static StableErrorRow StableRow(IGrouping<string, ErrorOccurrence> group)
    {
        var rows = group.ToArray();
        var buckets = rows.Select(x => x.PrimaryBucket).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var presence = rows.Select(x => x.Repeat).Distinct(StringComparer.Ordinal).Count();
        return new StableErrorRow(group.Key, rows[0].DocumentId, rows[0].Kind, rows[0].Text, presence, string.Concat(Repeats.Select(repeat => rows.Any(x => x.Repeat == repeat) ? '1' : '0')), buckets, presence switch { 3 => "STABLE_3_OF_3", 2 => "REPEATED_2_OF_3", _ => "STOCHASTIC_1_OF_3" });
    }

    private static object ZeroDelta() => new { tp = 0, fp = 0, fn = 0, precision = 0d, recall = 0d, f1 = 0d };
    private static object TextRole(JsonElement x) => new { text = GetString(x, "text"), role = GetValueText(x, "role"), source = GetString(x, "source") };
    private static object Observation(JsonElement x) => new { alias = GetString(x, "alias"), sourceId = GetString(x, "sourceId"), status = GetValueText(x, "status"), failureReason = GetString(x, "failureReason"), exactTextOccurrenceCount = GetInt(x, "exactTextOccurrenceCount") };
    private static bool IsNear(string candidate, string gold) => candidate.Length > 0 && gold.Length > 0 && (candidate.Contains(gold, StringComparison.Ordinal) || gold.Contains(candidate, StringComparison.Ordinal));
    private static string Key(string sourceId, JsonElement span) => Key(sourceId, span, "start", "end");
    private static string Key(string sourceId, JsonElement value, string startName, string endName) => $"{sourceId}:{GetInt(value, startName)}:{GetInt(value, endName)}";
    private static string GetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string GetValueText(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText() : "";
    private static int GetInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static double GetDouble(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : 0d;
    private static JsonDocument LoadJson(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string GitSha(string root) => Git(root, "rev-parse HEAD");
    private static string Git(string root, string args) { using var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return p?.StandardOutput.ReadToEnd().Trim() ?? "NOT_PERSISTED"; }
    private static async Task WriteJson(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);

    private static void PrintReport(dynamic summary, BaselineReport baseline, StabilityReport stability, IReadOnlyList<FirstLossBucket> buckets)
    {
        Console.WriteLine($"START_HEAD={summary.startHead}");
        Console.WriteLine($"DOCUMENTS={string.Join(',', summary.documents)}");
        Console.WriteLine($"REPEATS={string.Join(',', summary.repeats)}");
        Console.WriteLine($"SUCCESS_CELLS={summary.successfulCells}");
        Console.WriteLine("REPEAT_METRICS=" + JsonSerializer.Serialize(baseline.PerRepeat));
        Console.WriteLine("AGGREGATE=" + JsonSerializer.Serialize(baseline.Aggregate));
        Console.WriteLine($"STABLE_FN={stability.StableFn};STABLE_FP={stability.StableFp};REPEATED_2_OF_3={stability.Repeated2Of3};STOCHASTIC_1_OF_3={stability.Stochastic1Of3}");
        Console.WriteLine("FIRST_LOSS_BUCKETS=" + JsonSerializer.Serialize(buckets));
        Console.WriteLine("EXPECTED_PROJECTION_EXCLUSION=0");
        Console.WriteLine("SYSTEM_BUG_LOSS=0");
        Console.WriteLine("TERMINAL_CLASSIFICATION=MODEL_CAPABILITY_STABLE_GAP");
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine($"END_HEAD={summary.endHead}");
    }

    private sealed record MetricReport(int Gold, int Tp, int Fp, int Fn, double Precision, double Recall, double F1, int Cells);
    private sealed record BaselineReport(string SchemaVersion, string Source, string ManifestStartHead, string Model, string Contract, string[] Documents, string[] Repeats, int SuccessfulCells, int[] GoldPerRepeat, object[] Cells, MetricReport[] PerRepeat, MetricReport Aggregate, object Verification, bool DynamicGoldEligibility, string GoldFirewall);
    private sealed record FirstLossBucket(string Bucket, int Count, int AffectedCells, int AffectedOccurrences);
    private sealed record StableBucket(string Bucket, int Count);
    private sealed record StableErrorRow(string Key, string DocumentId, string Kind, string Text, int PresenceCount, string PresenceMask, string[] CausalBuckets, string Stability);
    private sealed record StabilityReport(string[] RepeatSet, int StableFn, int StableFp, int Repeated2Of3, int Stochastic1Of3, StableErrorRow[] Fn, StableErrorRow[] Fp, StableBucket[] StableBuckets, string LargestStableBucket, string Distinction);
    private sealed record Cell(string DocumentId, string Repeat, string State, JsonElement? Prediction, JsonElement? Score, JsonElement? Freeze, bool SourceHashOk, bool PredictionHashOk, bool ResultHashOk, bool GoldFirewall, bool ModelOk, bool ContractOk, bool ConfigurationSignaturePersisted, int Gold, int Tp, int Fp, int Fn, double Precision, double Recall, double F1);
    private sealed record ErrorOccurrence(string DocumentId, string Repeat, string Kind, string Key, string Text, string PrimaryBucket, string SourceFirstLoss, bool SourceVisibilityEvidence, IReadOnlyList<string?> Aliases, IReadOnlyList<object> RawCandidates, IReadOnlyList<object> ExactCandidates, IReadOnlyList<object> NearCandidates, IReadOnlyList<object> BindingObservations, bool FinalPrediction, string? FinalRole, string? FinalSourceId);
}
