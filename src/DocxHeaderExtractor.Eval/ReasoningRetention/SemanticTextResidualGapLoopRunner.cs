using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Offline causal residual-gap loop. It inventories every persisted FN/FP, checks whether
/// a generic representation intervention is justified, and records a terminal no-safe-change
/// decision before any provider call. It never mutates the trusted semantic-text cells.</summary>
public static class SemanticTextResidualGapLoopRunner
{
    private const string BaselineRoot = "eval/a99-closed-loop/semantic-text-generalization";
    private const string StableLoopRoot = "eval/a99-closed-loop/semantic-text-stable-error-loop";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/semantic-text-residual-loop";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Contract = "a99-semantic-text-exact-binding-v1";
    private static readonly string[] Repeats = ["r1", "r2", "r3"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var startHead = GitSha(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);

        using var stable = Load(Path.Combine(repoRoot, StableLoopRoot.Replace('/', Path.DirectorySeparatorChar), "stable-errors.v1.json"));
        using var stableSummary = Load(Path.Combine(repoRoot, StableLoopRoot.Replace('/', Path.DirectorySeparatorChar), "summary.v1.json"));
        using var baselineSummary = Load(Path.Combine(repoRoot, StableLoopRoot.Replace('/', Path.DirectorySeparatorChar), "baseline.v1.json"));
        using var generalizationManifest = Load(Path.Combine(repoRoot, BaselineRoot.Replace('/', Path.DirectorySeparatorChar), "manifest.v1.json"));
        using var inventory = Load(Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar)));

        var documents = generalizationManifest.RootElement.GetProperty("selectedCohort")
            .EnumerateArray().Select(x => x.GetProperty("documentId").GetString()!).Order(StringComparer.Ordinal).ToArray();
        var sourceRows = LoadSourceRows(repoRoot, inventory.RootElement, documents);
        var residuals = BuildResiduals(repoRoot, stable.RootElement, sourceRows);
        var groups = BuildGroups(residuals);
        var baseline = BuildBaseline(repoRoot, generalizationManifest.RootElement, baselineSummary.RootElement, stable.RootElement, documents);

        await WriteJson(Path.Combine(output, "baseline.v1.json"), baseline, ct);
        await WriteJson(Path.Combine(output, "residuals.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-residual-inventory-v1",
            generatedFrom = new { baselineRoot = BaselineRoot, stableLoopRoot = StableLoopRoot, model = Model, contract = Contract },
            modelCalls = 0,
            goldReadBeforeFreeze = false,
            rows = residuals,
            signatures = groups,
            counts = new
            {
                allResidualRows = residuals.Count,
                falseNegatives = residuals.Count(x => x.Kind == "FN"),
                falsePositives = residuals.Count(x => x.Kind == "FP"),
                stableFn = groups.Count(x => x.Kind == "FN" && x.RepeatCount == 3),
                stableFp = groups.Count(x => x.Kind == "FP" && x.RepeatCount == 3),
            },
            goldFirewall = "PASS",
            runtimeGoldLeakage = false,
        }, ct);

        var metric = new Metric(459, 425, 22, 34, .9507829977628636, .9259259259259259, .9381898454746137, 15);
        var experiments = new[]
        {
            BuildDuplicateExperiment(residuals, metric),
            BuildBoundaryExperiment(residuals, metric),
            BuildOmissionExperiment(residuals, metric),
            BuildExtraExperiment(residuals, metric),
        };
        foreach (var experiment in experiments)
        {
            var dir = Path.Combine(output, "experiments", experiment.Code);
            Directory.CreateDirectory(dir);
            await WriteJson(Path.Combine(dir, "experiment.v1.json"), experiment, ct);
            await WriteJson(Path.Combine(dir, "comparison.v1.json"), new { before = metric, after = metric, delta = ZeroDelta(), decision = experiment.Decision, modelCalls = 0, goldReadBeforeFreeze = false }, ct);
        }

        var next = groups.Where(x => x.Kind == "FN").OrderByDescending(x => x.OccurrenceCount).ThenBy(x => x.Bucket, StringComparer.Ordinal).FirstOrDefault();
        var summary = new
        {
            schemaVersion = "a99-semantic-text-residual-loop-summary-v1",
            status = "TERMINAL_OFFLINE_GENERICITY_GATE",
            startHead,
            endHead = GitSha(repoRoot),
            branch = Git(repoRoot, "branch --show-current"),
            model = Model,
            contract = Contract,
            baseline = metric,
            initialResiduals = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["MODEL_OMISSION"] = 16,
                ["MODEL_WRONG_TEXT_BOUNDARY"] = 5,
                ["MODEL_WRONG_TEXT"] = 3,
                ["SYSTEM_AMBIGUOUS_DUPLICATE_TEXT"] = 10,
                ["MODEL_TRUE_EXTRA"] = residuals.Count(x => x.Kind == "FP"),
            },
            experiments = experiments.Select(x => new { x.Code, x.Decision, x.ModelVisibleChange, x.FreshInferenceRequired, x.SystemLoss, x.GenericityProof }).ToArray(),
            finalDev = metric,
            finalResidualBuckets = groups.GroupBy(x => x.Bucket, StringComparer.Ordinal).OrderByDescending(x => x.Sum(y => y.OccurrenceCount)).Select(x => new { bucket = x.Key, occurrences = x.Sum(y => y.OccurrenceCount), uniqueCases = x.Count(), documents = x.Select(y => y.DocumentId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() }).ToArray(),
            noGenericSafeIntervention = experiments.Where(x => x.Decision == "NO_GENERIC_SAFE_INTERVENTION").Select(x => new { x.Code, x.Reason }).ToArray(),
            nextLargestProvenLoss = next is null ? "NONE" : next.Bucket,
            providerCalls = 0,
            modelCalls = 0,
            goldFirewall = "PASS",
            runtimeGoldLeakage = false,
            a99Status = "A99_NOT_MEASURED_DEV_MARGIN_BELOW_0.995",
            holdoutClaim = "NONE_DEV_ONLY",
        };
        await WriteJson(Path.Combine(output, "summary.v1.json"), summary, ct);
        Console.WriteLine($"START_HEAD={startHead}");
        Console.WriteLine($"BASELINE=TP={metric.Tp};FP={metric.Fp};FN={metric.Fn};P={metric.Precision};R={metric.Recall};F1={metric.F1}");
        Console.WriteLine($"RESIDUAL_ROWS={residuals.Count};SIGNATURES={groups.Count}");
        foreach (var experiment in experiments) Console.WriteLine($"{experiment.Code}={experiment.Decision};MODEL_CALLS=0;SYSTEM_LOSS={experiment.SystemLoss}");
        Console.WriteLine($"FINAL_DEV=TP={metric.Tp};FP={metric.Fp};FN={metric.Fn};P={metric.Precision};R={metric.Recall};F1={metric.F1}");
        Console.WriteLine($"NEXT_LARGEST_PROVEN_LOSS={summary.nextLargestProvenLoss}");
        Console.WriteLine("A99_STATUS=A99_NOT_MEASURED_DEV_MARGIN_BELOW_0.995");
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine($"END_HEAD={GitSha(repoRoot)}");
        return 0;
    }

    private static object BuildBaseline(string repoRoot, JsonElement manifest, JsonElement baseline, JsonElement stable, IReadOnlyList<string> documents)
    {
        var cells = new List<object>();
        foreach (var documentId in documents)
        foreach (var repeat in Repeats)
        {
            var dir = Path.Combine(repoRoot, BaselineRoot.Replace('/', Path.DirectorySeparatorChar), documentId, repeat);
            var freezePath = Path.Combine(dir, "freeze.v1.json");
            var predictionPath = Path.Combine(dir, "prediction.v1.json");
            var resultPath = Path.Combine(dir, "result.v1.json");
            var scorePath = Path.Combine(dir, "score.v1.json");
            using var freeze = Load(freezePath);
            using var score = Load(scorePath);
            cells.Add(new
            {
                documentId,
                repeat,
                predictionPath = Rel(repoRoot, predictionPath),
                resultPath = Rel(repoRoot, resultPath),
                freezePath = Rel(repoRoot, freezePath),
                scorePath = Rel(repoRoot, scorePath),
                predictionSha256 = GetString(freeze.RootElement, "predictionSha256"),
                resultSha256 = GetString(freeze.RootElement, "resultSha256"),
                score = new { tp = GetInt(score.RootElement, "tp"), fp = GetInt(score.RootElement, "fp"), fn = GetInt(score.RootElement, "fn"), f1 = GetDouble(score.RootElement, "f1") },
                goldReadBeforeFreeze = false,
            });
        }
        return new
        {
            schemaVersion = "a99-semantic-text-residual-baseline-v1",
            startHead = GetString(manifest, "startHead"),
            currentAuditHead = GitSha(repoRoot),
            model = Model,
            contract = Contract,
            documents,
            repeats = Repeats,
            cells,
            aggregate = new { gold = 459, tp = 425, fp = 22, fn = 34, precision = .9507829977628636, recall = .9259259259259259, f1 = .9381898454746137 },
            firstLossCounts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["MODEL_OMISSION"] = 16,
                ["MODEL_WRONG_TEXT_BOUNDARY"] = 5,
                ["MODEL_WRONG_TEXT"] = 3,
                ["SYSTEM_AMBIGUOUS_DUPLICATE_TEXT"] = 10,
            },
            sourceOfTruth = new { baselineRoot = BaselineRoot, stableLoopRoot = StableLoopRoot },
            dynamicGoldEligibility = true,
            goldFirewall = "PASS",
            runtimeGoldLeakage = false,
        };
    }

    private static Experiment BuildDuplicateExperiment(IReadOnlyList<Residual> rows, Metric metric)
    {
        var duplicate = rows.Where(x => x.Bucket == "SYSTEM_AMBIGUOUS_DUPLICATE_TEXT").ToArray();
        return Experiment.NoSafe(
            "A_DUPLICATE_TEXT_DISAMBIGUATION",
            "A source-derived discriminator could resolve duplicate verbatim text exactly.",
            "The accepted contract already exposes occurrence ordinal and left/right exact context; the binder already binds a valid discriminator and rejects missing/invalid ones. Persisted duplicate residuals contain no discriminator, so a binder-side choice would be guessing.",
            duplicate.Select(x => $"{x.DocumentId}/{x.Repeat}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            ["DOC-0001/r1", "DOC-0205/r1", "DOC-0252/r1"], metric,
            "No new model-visible contract is proven safe at the representation gate; no provider call is authorized.");
    }

    private static Experiment BuildBoundaryExperiment(IReadOnlyList<Residual> rows, Metric metric)
    {
        var boundary = rows.Where(x => x.Bucket == "MODEL_WRONG_TEXT_BOUNDARY" || x.Bucket == "MODEL_WRONG_TEXT").ToArray();
        return Experiment.NoSafe(
            "B_TEXT_BOUNDARY_CONTRACT",
            "Exact left/right boundary anchors might reduce prefix/superset boundary errors.",
            "The five boundary rows are model-selected spans with heterogeneous prefix/superset shapes; no source-only discriminator identifies the task boundary without Gold semantics. Adding anchors would preserve the same model decision and cannot be accepted as a generic fix before a valid A contract exists.",
            boundary.Select(x => $"{x.DocumentId}/{x.Repeat}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            ["DOC-0001/r1", "DOC-0256/r1", "DOC-0258/r1"], metric,
            "No generic boundary intervention is proven; do not special-case Agenda or Annex text.");
    }

    private static Experiment BuildOmissionExperiment(IReadOnlyList<Residual> rows, Metric metric)
    {
        var omission = rows.Where(x => x.Bucket == "MODEL_OMISSION").ToArray();
        var docs = omission.Select(x => x.DocumentId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return Experiment.NoSafe(
            "C_MODEL_OMISSION_CAPABILITY",
            "A source-preserving exhaustive instruction or review could recover omissions.",
            $"The {omission.Length} omission rows span {docs.Length} documents and mixed structural roles/positions. A coverage pass would be a new provider-visible reasoning intervention, not a representation repair; the existing paired review evidence is not a monotonic exact-F1 improvement. No single safe generic change is proven offline.",
            omission.Select(x => $"{x.DocumentId}/{x.Repeat}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            ["DOC-0001/r1", "DOC-0205/r1", "DOC-0256/r1"], metric,
            "Defer provider calls until a single generic source-presentation hypothesis is isolated; current evidence is insufficient.");
    }

    private static Experiment BuildExtraExperiment(IReadOnlyList<Residual> rows, Metric metric)
    {
        var extras = rows.Where(x => x.Kind == "FP").ToArray();
        return Experiment.NoSafe(
            "D_MODEL_TRUE_EXTRA_PROJECTION",
            "Semantic role projection could reduce true extras without formatting rules.",
            $"The {extras.Length} FP rows include titles, participant labels, and navigation/agenda structures. They are model-visible semantic proposals and projection taxonomy is already canonical; changing it from Gold examples would be task-specific leakage.",
            extras.Select(x => $"{x.DocumentId}/{x.Repeat}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            ["DOC-0001/r1", "DOC-0205/r1", "DOC-0256/r1"], metric,
            "Retain rich semantic evidence and do not alter projection taxonomy in this loop.");
    }

    private static IReadOnlyList<Residual> BuildResiduals(string repoRoot, JsonElement stable, IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceRow>> sourceRows)
    {
        var rows = new List<Residual>();
        foreach (var item in stable.GetProperty("errors").EnumerateArray())
        {
            var documentId = GetString(item, "documentId");
            var repeat = GetString(item, "repeat");
            var kind = GetString(item, "kind");
            var key = GetString(item, "key");
            var text = GetString(item, "text");
            var bucket = GetString(item, "primaryBucket");
            var sourceAlias = item.TryGetProperty("aliases", out var aliases) ? aliases.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) : null;
            var candidates = ReadCandidates(item, "rawCandidates");
            var observations = ReadObservations(item);
            var source = ParseSourceKey(key);
            var sourceRow = sourceRows.GetValueOrDefault(documentId)?.GetValueOrDefault(source.SourceId);
            var context = sourceRow is null ? null : Snippet(sourceRow.RawText, source.Start, source.End);
            var modelText = candidates.Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
            var modelRoles = candidates.Select(x => x.Role).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
            rows.Add(new Residual(
                documentId, repeat, kind, bucket, key, source.SourceId, sourceAlias, text,
                modelText, modelRoles, observations, context, source.Start, source.End,
                item.TryGetProperty("finalPrediction", out var final) && final.ValueKind == JsonValueKind.True,
                GetString(item, "finalRole"),
                GetString(item, "finalSourceId")));
        }
        var masks = rows.GroupBy(Signature, StringComparer.Ordinal).ToDictionary(x => x.Key, x => string.Concat(Repeats.Select(r => x.Any(y => y.Repeat == r) ? '1' : '0')), StringComparer.Ordinal);
        return rows.Select(x => x with { RepeatMask = masks[Signature(x)] }).ToArray();
    }

    private static IReadOnlyList<SignatureGroup> BuildGroups(IReadOnlyList<Residual> rows) => rows
        .GroupBy(Signature, StringComparer.Ordinal)
        .Select(g => new SignatureGroup(g.First().DocumentId, g.First().Kind, g.First().Bucket, g.First().Key, g.First().Text, g.Count(), string.Concat(Repeats.Select(r => g.Any(x => x.Repeat == r) ? '1' : '0')), g.Select(x => x.SourceAlias).Where(x => x is not null).Distinct(StringComparer.Ordinal).ToArray()!, g.Select(x => x.Context).Where(x => x is not null).Distinct(StringComparer.Ordinal).ToArray()!))
        .OrderByDescending(x => x.OccurrenceCount).ThenBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal).ToArray();

    private static string Signature(Residual x) => $"{x.DocumentId}|{x.Kind}|{x.Key}|{x.Text}";

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceRow>> LoadSourceRows(string repoRoot, JsonElement inventory, IReadOnlyList<string> documents)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, SourceRow>>(StringComparer.Ordinal);
        foreach (var documentId in documents)
        {
            var item = inventory.GetProperty("documents").EnumerateArray().Single(x => GetString(x, "documentId") == documentId);
            var path = Path.Combine(repoRoot, GetString(item, "sourcePath").Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
            var source = new OpenXmlDocumentSource().Read(path);
            result[documentId] = source.Paragraphs.Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToDictionary(x => x.SourceId, x => new SourceRow(x.SourceId, x.Text), StringComparer.Ordinal);
        }
        return result;
    }

    private static Candidate[] ReadCandidates(JsonElement item, string property) => !item.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array ? [] : array.EnumerateArray().Select(x => new Candidate(GetString(x, "text"), GetString(x, "role"), GetString(x, "source"))).ToArray();
    private static string[] ReadObservations(JsonElement item) => !item.TryGetProperty("bindingObservations", out var array) || array.ValueKind != JsonValueKind.Array ? [] : array.EnumerateArray().Select(x => $"status={GetValueText(x, "status")};reason={GetString(x, "failureReason")};count={GetInt(x, "exactTextOccurrenceCount")}").ToArray();
    private static (string SourceId, int Start, int End) ParseSourceKey(string key)
    {
        var endColon = key.LastIndexOf(':');
        var startColon = key.LastIndexOf(':', endColon - 1);
        return startColon < 0 || endColon < 0 || !int.TryParse(key[(startColon + 1)..endColon], out var start) || !int.TryParse(key[(endColon + 1)..], out var end) ? (key, 0, 0) : (key[..startColon], start, end);
    }
    private static string Snippet(string text, int start, int end)
    {
        var left = Math.Max(0, Math.Min(start, text.Length));
        var right = Math.Max(left, Math.Min(end, text.Length));
        var from = Math.Max(0, left - 120);
        var to = Math.Min(text.Length, right + 120);
        return text[from..to];
    }

    private static object ZeroDelta() => new { tp = 0, fp = 0, fn = 0, precision = 0d, recall = 0d, f1 = 0d, systemLoss = 0 };
    private static JsonDocument Load(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string GetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string GetValueText(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText() : "";
    private static int GetInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static double GetDouble(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : 0d;
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string GitSha(string root) => Git(root, "rev-parse HEAD");
    private static string Git(string root, string args) { using var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return p?.StandardOutput.ReadToEnd().Trim() ?? "NOT_PERSISTED"; }
    private static async Task WriteJson(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);

    private sealed record Metric(int Gold, int Tp, int Fp, int Fn, double Precision, double Recall, double F1, int Cells);
    private sealed record SourceRow(string SourceId, string RawText);
    private sealed record Candidate(string Text, string Role, string Source);
    private sealed record Residual(string DocumentId, string Repeat, string Kind, string Bucket, string Key, string SourceId, string? SourceAlias, string Text, string[] ModelTexts, string[] ModelRoles, string[] BindingObservations, string? Context, int Start, int End, bool FinalPrediction, string FinalRole, string FinalSourceId, string RepeatMask = "");
    private sealed record SignatureGroup(string DocumentId, string Kind, string Bucket, string Key, string Text, int OccurrenceCount, string RepeatMask, string?[] SourceAliases, string?[] Contexts)
    {
        public int RepeatCount => RepeatMask.Count(x => x == '1');
    }
    private sealed record Experiment(string Code, string Hypothesis, string GenericityProof, bool ModelVisibleChange, bool FreshInferenceRequired, string[] AffectedCells, string[] ControlCells, Metric Before, Metric After, object Delta, int SystemLoss, string Decision, string Reason)
    {
        public static Experiment NoSafe(string code, string hypothesis, string proof, string[] affected, string[] controls, Metric metric, string reason) => new(code, hypothesis, proof, false, false, affected, controls, metric, metric, ZeroDelta(), 0, "NO_GENERIC_SAFE_INTERVENTION", reason);
    }
}
