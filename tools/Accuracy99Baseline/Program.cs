using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

const string BaseRevision = "732c3505afc5dd312423ed0fa58056192fb39608";
var root = FindRepositoryRoot(args);
var outputRoot = Path.Combine(root, "eval", "accuracy99");
Directory.CreateDirectory(outputRoot);

var datasets = Accuracy99Datasets(root);
var only = args.FirstOrDefault(arg => arg.StartsWith("--only=", StringComparison.OrdinalIgnoreCase))?[7..];
if (!string.IsNullOrWhiteSpace(only))
    datasets = datasets.Where(dataset => string.Equals(dataset.Id, only, StringComparison.OrdinalIgnoreCase)).ToArray();
var debug = args.Any(arg => string.Equals(arg, "--debug", StringComparison.OrdinalIgnoreCase));
var inventory = BuildInventory(root, datasets);
var runs = new List<DatasetRun>();

foreach (var dataset in datasets)
{
    if (!File.Exists(dataset.DocumentPath) || !File.Exists(dataset.KeyPath))
    {
        runs.Add(DatasetRun.Missing(dataset));
        continue;
    }

    Console.WriteLine($"Running {dataset.Id} ...");
    var key = AnswerKey.Load(dataset.KeyPath);
    using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions
    {
        DisableLlm = true,
        TrustStyles = true,
    });
    var result = await pipeline.RunDocumentAsync(dataset.DocumentPath);
    runs.Add(Evaluate(dataset, key, result, debug));
}

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

var allGold = runs.SelectMany(run => run.Gold).ToArray();
var allFirstLoss = runs.SelectMany(run => run.FirstLoss).ToArray();
var providerCalls = runs.Sum(run => run.ProviderCalls);
var joined = runs.Sum(run => run.SourceIdentityJoined);
var sourceIdentityDenominator = runs.Sum(run => run.Gold.Count(gold => gold.SourceId is not null));
var levelCompared = runs.Sum(run => run.LevelCompared);
var levelCorrect = runs.Sum(run => run.LevelCorrect);

var baseline = new
{
    schemaVersion = 1,
    artifactKind = "accuracy99_outline_quality_baseline",
    phase = "A_trustworthy_outline_quality_baseline",
    status = "NOT_YET_MEASURABLE",
    baseRevision = BaseRevision,
    accuracyRevision = BaseRevision,
    branch = "accuracy/outline-99-baseline",
    productionSourceChanged = false,
    architectureChanged = false,
    tuningPerformed = false,
    deterministic = true,
    executionOptions = new
    {
        disableLlm = true,
        trustStyles = true,
        sourceOfTruth = "AuthorityExtractionPipeline.RunDocumentAsync",
        matching = "parser-owned SourceId + exact span; text-only joins are diagnostic only",
    },
    providerCalls,
    blindHoldout = "NOT_AVAILABLE",
    labelPolicy = new
    {
        positiveAndNegativeLabelsRequired = true,
        unlabeledOccurrencesAreNotNegative = true,
        precisionEligibility = "requires reviewed exhaustive negatives",
        exactQualityEligibility = "requires parser-owned SourceId and exact span",
    },
    datasetCount = runs.Count,
    eligibleDatasetCount = runs.Count(run => run.EligibleForSourceIdentityDiagnostics),
    eligibleForPrecisionCount = runs.Count(run => run.EligibleForPrecision),
    eligibleForExactSpanCount = runs.Count(run => run.HasExactSpanGold),
    metrics = new
    {
        sourceIdentityJoinRateDiagnostic = Metric.Diagnostic(joined, sourceIdentityDenominator),
        levelAccuracyOnJoinedOccurrences = Metric.Measured(levelCorrect, levelCompared),
        falsePositiveLedger = Metric.NotMeasurable("no reviewed exhaustive negative denominator"),
        precision = Metric.NotMeasurable("no reviewed exhaustive negative denominator"),
        recall = Metric.NotMeasurable("gold exact parser-owned spans are unavailable"),
        exactSpan = Metric.NotMeasurable("gold exact parser-owned spans are unavailable"),
        parentAccuracy = Metric.NotMeasurable("gold parent relations are unavailable"),
        hierarchy = Metric.NotMeasurable("gold parent relations are unavailable"),
    },
    datasets = runs,
    firstLossPartition = allFirstLoss
        .GroupBy(item => item.Stage)
        .OrderBy(group => group.Key)
        .ToDictionary(group => group.Key, group => group.Count()),
    unjoinedOccurrences = runs.Sum(run => run.UnjoinedOccurrences),
    nextRemediationOwner = "dataset/adjudication: freeze parser-owned spans and reviewed negatives before production tuning",
};

var firstLossArtifact = new
{
    schemaVersion = 1,
    artifactKind = "accuracy99_outline_first_loss_ledger",
    phase = "A_trustworthy_outline_quality_baseline",
    baseRevision = BaseRevision,
    accuracyRevision = BaseRevision,
    providerCalls,
    productionSourceChanged = false,
    entries = allFirstLoss,
    stageDefinitions = new[]
    {
        "SOURCE_NOT_PARSED", "CANDIDATE_NOT_GENERATED", "CANDIDATE_FILTERED",
        "SEMANTIC_ROLE_REJECTED", "SPAN_UNRESOLVED", "SPAN_INVALID",
        "VALIDATOR_REJECTED", "RELATION/HIERARCHY_LOSS", "OUTPUT_PROJECTION_LOSS", "UNKNOWN",
    },
    policy = "UNKNOWN is retained when current production provenance cannot identify a deeper first loss; no stage is inferred from text alone.",
};

var gaps = new
{
    schemaVersion = 1,
    artifactKind = "accuracy99_outline_adjudication_gaps",
    baseRevision = BaseRevision,
    blindHoldout = "NOT_AVAILABLE",
    gaps = new[]
    {
        new { gap = "reviewed_exhaustive_negative_labels", status = "MISSING", impact = "precision_not_measurable" },
        new { gap = "parser_owned_exact_gold_spans", status = "MISSING", impact = "exact_span_and_strict_recall_not_measurable" },
        new { gap = "gold_parent_relations", status = "MISSING", impact = "parent_and_hierarchy_not_measurable" },
        new { gap = "blind_holdout", status = "NOT_AVAILABLE", impact = "holdout_generalization_not_measurable" },
    },
    selectedNegativeAdjudication = new
    {
        artifact = "eval/accuracy-round6/selected-cohort-negative-adjudication.v1.json",
        occurrenceCount = 560,
        labelsFrozen = false,
        usableForPrecision = false,
    },
};

File.WriteAllText(Path.Combine(outputRoot, "outline-baseline.v1.json"), JsonSerializer.Serialize(baseline, jsonOptions));
File.WriteAllText(Path.Combine(outputRoot, "outline-first-loss-ledger.v1.json"), JsonSerializer.Serialize(firstLossArtifact, jsonOptions));
File.WriteAllText(Path.Combine(outputRoot, "outline-dataset-inventory.v1.json"), JsonSerializer.Serialize(inventory, jsonOptions));
File.WriteAllText(Path.Combine(outputRoot, "outline-adjudication-gaps.v1.json"), JsonSerializer.Serialize(gaps, jsonOptions));

var docPath = Path.Combine(root, "docs", "accuracy", "accuracy99-outline-baseline.md");
Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);
File.WriteAllText(docPath, RenderMarkdown(baseline, runs, allFirstLoss));

Console.WriteLine($"ACCURACY-99 BASELINE = NOT_YET_MEASURABLE; datasets={runs.Count}; providerCalls={providerCalls}; unjoined={baseline.unjoinedOccurrences}");

static string FindRepositoryRoot(string[] args)
{
    var requested = args.FirstOrDefault(arg => arg.StartsWith("--root=", StringComparison.OrdinalIgnoreCase))?[7..];
    var candidate = string.IsNullOrWhiteSpace(requested) ? Directory.GetCurrentDirectory() : requested;
    var directory = new DirectoryInfo(Path.GetFullPath(candidate));
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln"))) return directory.FullName;
        directory = directory.Parent;
    }
    throw new DirectoryNotFoundException("Could not locate DocxHeaderExtractor repository root.");
}

static IReadOnlyList<DatasetSpec> Accuracy99Datasets(string root) =>
[
    Dataset(root, "010", "HUMAN_GOLD", "todo10_8/generated-docx/01_phap_quy/010_Luat_An_ninh_mang_24-2018-QH14.docx", "keys/rebased/010_Luat_An_ninh_mang_24-2018-QH14.v2-regenerated-docx.key", true, "full human legal key; rebased to this DOCX"),
    Dataset(root, "025", "HUMAN_GOLD", "todo10_8/generated-docx/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.docx", "keys/legal-human/025_ND_47-2020_Chia_se_du_lieu_so.key", true, "full human legal key; duplicate paragraph anchors require text disambiguation"),
    Dataset(root, "051", "HUMAN_GOLD", "todo10_8/generated-docx/03_tai_chinh_ke_toan/051_WBG_Trust_Fund_FIS_June_2024.docx", "keys/partial-human/051_WBG_Trust_Fund_FIS_June_2024.key", true, "user-confirmed full page-title key; exact source spans not recorded"),
    Dataset(root, "052", "HUMAN_GOLD", "todo10_8/generated-docx/03_tai_chinh_ke_toan/052_WBG_Trust_Fund_FIS_December_2025.docx", "keys/partial-human/052_WBG_Trust_Fund_FIS_December_2025.key", true, "user-confirmed full page-title key; exact source spans not recorded"),
    Dataset(root, "056", "HUMAN_GOLD", "todo10_8/generated-docx/04_giao_trinh/056_OpenStax_Business_Law_I_Essentials.docx", "keys/typed-human/056_OpenStax_Business_Law_I_Essentials.key", true, "reviewed typed-numbering key; exact source spans not recorded"),
    Dataset(root, "063", "HUMAN_GOLD", "todo10_8/generated-docx/04_giao_trinh/063_Advanced_Linear_Algebra.docx", "keys/partial-human/063_Advanced_Linear_Algebra.key", true, "user-confirmed full TOC key; exact source spans not recorded"),
    Dataset(root, "092", "HUMAN_GOLD", "todo10_8/generated-docx/07_system_generated/092_RFC9111_HTTP_Caching.docx", "keys/rebased/092_RFC9111_HTTP_Caching.v2-regenerated-docx.key", true, "reviewed RFC key rebased to this DOCX"),
];

static DatasetSpec Dataset(string root, string id, string labelClass, string relativeDocumentPath, string relativeKeyPath, bool complete, string notes) =>
    new(id, labelClass,
        Path.Combine(root, relativeDocumentPath.Replace('/', Path.DirectorySeparatorChar)),
        Path.Combine(root, relativeKeyPath.Replace('/', Path.DirectorySeparatorChar)),
        complete, notes);

static object BuildInventory(string root, IReadOnlyList<DatasetSpec> selected)
{
    var files = Directory.EnumerateFiles(Path.Combine(root, "keys"), "*", SearchOption.AllDirectories)
        .Where(path => Path.GetExtension(path) is ".key" or ".json" or ".outline")
        .Select(path => new
        {
            path = Path.GetRelativePath(root, path).Replace('\\', '/'),
            labelClass = ClassifyKey(Path.GetRelativePath(root, path)),
        })
        .OrderBy(item => item.path, StringComparer.Ordinal)
        .ToArray();
    var classCounts = files.GroupBy(item => item.labelClass).ToDictionary(group => group.Key, group => group.Count());
    return new
    {
        schemaVersion = 1,
        artifactKind = "accuracy99_outline_dataset_inventory",
        baseRevision = BaseRevision,
        selectedDatasets = selected.Select(dataset => new
        {
            dataset.Id,
            dataset.LabelClass,
            document = Path.GetRelativePath(root, dataset.DocumentPath).Replace('\\', '/'),
            key = Path.GetRelativePath(root, dataset.KeyPath).Replace('\\', '/'),
            dataset.Complete,
            dataset.Notes,
        }),
        trackedKeyAndLabelArtifacts = new { total = files.Length, classCounts, files },
        evalArtifacts = InventoryFiles(root, "eval"),
        testArtifacts = new
        {
            totalCsFiles = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories).Count(),
            accuracyRelatedCsFiles = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains("Accuracy", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Contains("FirstLoss", StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray(),
        },
        sourceLabelRoots = new
        {
            gold = Directory.Exists(Path.Combine(root, "gold")),
            silver = Directory.Exists(Path.Combine(root, "silver")),
        },
        blindHoldout = "NOT_AVAILABLE",
        policy = "Only explicit reviewed labels are eligible; pipeline-derived and unlabeled silver artifacts are retained as inventory, never promoted to gold.",
    };
}

static object InventoryFiles(string root, string directoryName)
{
    var directory = Path.Combine(root, directoryName);
    if (!Directory.Exists(directory))
        return new { exists = false, total = 0, byTopLevelDirectory = new Dictionary<string, int>() };
    var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
    var byTopLevelDirectory = files
        .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
        .Select(path => path.Split('/')[0])
        .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    return new { exists = true, total = files.Length, byTopLevelDirectory };
}

static string ClassifyKey(string relativePath)
{
    var normalized = relativePath.Replace('\\', '/');
    if (normalized.Contains("/toc-derived/", StringComparison.OrdinalIgnoreCase) ||
        normalized.Contains("/tagged-pdf-coverage/", StringComparison.OrdinalIgnoreCase)) return "PIPELINE_DERIVED";
    if (normalized.Contains("/partial-human/", StringComparison.OrdinalIgnoreCase))
    {
        if (normalized.Contains("051_WBG_Trust_Fund_FIS_June_2024", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("052_WBG_Trust_Fund_FIS_December_2025", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("063_Advanced_Linear_Algebra", StringComparison.OrdinalIgnoreCase)) return "HUMAN_GOLD";
        return "REVIEWED_SILVER";
    }
    if (normalized.Contains("/rebased/", StringComparison.OrdinalIgnoreCase)) return "HUMAN_GOLD";
    if (normalized.Contains("/typed-human/054_", StringComparison.OrdinalIgnoreCase)) return "REVIEWED_SILVER";
    if (normalized.Contains("/legal-human/", StringComparison.OrdinalIgnoreCase) ||
        normalized.Contains("/typed-human/", StringComparison.OrdinalIgnoreCase) ||
        normalized.Contains("/format-driven-human/", StringComparison.OrdinalIgnoreCase) ||
        normalized is "keys/kltn-style.key" or "keys/bao-cao-thuc-tap.key") return "HUMAN_GOLD";
    return "UNKNOWN";
}

static DatasetRun Evaluate(DatasetSpec dataset, AnswerKey key, DocumentExtractionResult result, bool debug)
{
    var elements = result.Structure.OutlineElements;
    if (debug)
    {
        Console.WriteLine($"{dataset.Id}: catalog={result.SourceCatalog.Units.Count}, elements={elements.Count}");
        Console.WriteLine("catalog: " + string.Join(" | ", result.SourceCatalog.Units.Take(5).Select(unit => $"{unit.SourceId}@{unit.SourceOrdinal}")));
        Console.WriteLine("elements: " + string.Join(" | ", elements.Take(5).Select(element => $"{element.Id}:{string.Join(',', element.Sources.Select(source => source.SourceId))}")));
    }
    var gold = key.PositiveEntries.Select((entry, index) => GoldOccurrence.From(dataset.Id, index, entry, result.SourceCatalog)).ToArray();
    var used = new HashSet<string>(StringComparer.Ordinal);
    var firstLoss = new List<FirstLossEntry>();
    var joined = 0;
    var unjoined = 0;
    var levelCompared = 0;
    var levelCorrect = 0;
    foreach (var occurrence in gold)
    {
        var matches = elements.Where(element => occurrence.SourceId is not null &&
            element.Sources.Any(source => string.Equals(source.SourceId, occurrence.SourceId, StringComparison.Ordinal)))
            .Where(element => !used.Contains(element.Id))
            .OrderByDescending(element => string.Equals(Normalize(element.Text), Normalize(occurrence.Text), StringComparison.Ordinal))
            .ThenBy(element => element.Id, StringComparer.Ordinal)
            .ToArray();
        var match = matches.FirstOrDefault();
        if (match is null)
        {
            unjoined++;
            var sourcePresent = occurrence.SourceId is not null && result.SourceCatalog.Units.Any(unit => unit.SourceId == occurrence.SourceId);
            firstLoss.Add(new FirstLossEntry(dataset.Id, occurrence.Ordinal, occurrence.SourceId, occurrence.Text, sourcePresent ? "UNKNOWN" : "SOURCE_NOT_PARSED", "current generic provenance does not expose a deeper candidate-stage first loss"));
            continue;
        }
        used.Add(match.Id);
        joined++;
        if (occurrence.Level is not null && match.Level is not null)
        {
            levelCompared++;
            if (occurrence.Level == match.Level) levelCorrect++;
        }
    }

    return new DatasetRun
    {
        Id = dataset.Id,
        LabelClass = dataset.LabelClass,
        DocumentPath = dataset.DocumentPath,
        KeyPath = dataset.KeyPath,
        Document = Path.GetFileName(dataset.DocumentPath),
        Key = Path.GetFileName(dataset.KeyPath),
        Complete = dataset.Complete,
        Notes = dataset.Notes,
        PositiveLabelCount = key.PositiveEntries.Count,
        NegativeLabelCount = key.NegativeEntries.Count,
        Gold = gold,
        PredictedOutlineCount = elements.Count,
        SourceIdentityJoined = joined,
        UnjoinedOccurrences = unjoined,
        LevelCompared = levelCompared,
        LevelCorrect = levelCorrect,
        ProviderCalls = result.Provenance.ProviderCalls,
        HasExactSpanGold = gold.Any(item => item.Span is not null),
        EligibleForPrecision = false,
        EligibleForSourceIdentityDiagnostics = gold.Length > 0,
        FirstLoss = firstLoss,
        Status = "MEASURED_PARTIAL_CONTRACT",
    };
}

static string Normalize(string? value) => string.IsNullOrWhiteSpace(value)
    ? string.Empty
    : string.Join(' ', value.Normalize(NormalizationForm.FormKC).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

static string RenderMarkdown(object baseline, IReadOnlyList<DatasetRun> runs, IReadOnlyList<FirstLossEntry> firstLoss)
{
    var lines = new List<string>
    {
        "# ACCURACY-99 outline baseline",
        "",
        "This is a measurement-only baseline on the frozen architecture revision. It does not tune production behavior.",
        "",
        "- Status: `NOT_YET_MEASURABLE`",
        $"- Base and accuracy revision: `{BaseRevision}`",
        "- Branch: `accuracy/outline-99-baseline`",
        "- Blind holdout: `NOT_AVAILABLE`",
        "- Provider calls: `0` (deterministic `DisableLlm=true` run)",
        "",
        "## Contract status",
        "",
        "The selected reviewed keys provide positive occurrence labels, but do not provide parser-owned exact spans or reviewed exhaustive negatives. Therefore strict precision, exact-span recall, parent accuracy, and hierarchy accuracy are `NOT_MEASURABLE`; unlabeled occurrences are not treated as negatives.",
        "",
        "| Dataset | Class | Predicted outline | Gold occurrences | Source-joined | Level compared/correct | Unjoined |",
        "|---|---|---:|---:|---:|---:|---:|",
    };
    lines.AddRange(runs.Select(run => $"| {run.Id} | {run.LabelClass} | {run.PredictedOutlineCount} | {run.Gold.Count} | {run.SourceIdentityJoined} | {run.LevelCompared}/{run.LevelCorrect} | {run.UnjoinedOccurrences} |"));
    lines.Add("");
    lines.Add("## First-loss ledger");
    lines.Add("");
    var missedOccurrenceCount = firstLoss.Count(item => item.GoldOrdinal >= 0);
    lines.Add(firstLoss.Count == 0 ? "No joined-source misses were observed in the selected cohort." : $"{missedOccurrenceCount} labeled occurrences were not joined to the generic outline ({firstLoss.Count - missedOccurrenceCount} missing-input ledger entries). The current result envelope cannot attribute a deeper candidate-stage loss, so these remain `UNKNOWN` unless the source itself was absent (`SOURCE_NOT_PARSED`).");
    lines.Add("");
    lines.Add("## Historical reconciliation");
    lines.Add("");
    lines.Add("Historical accuracy and architecture artifacts were not overwritten. Pipeline-derived TOC keys, silver packets, and the unfrozen 560-occurrence negative adjudication packet remain inventory/evidence only.");
    lines.Add("");
    lines.Add("## Next remediation owner");
    lines.Add("");
    lines.Add("Dataset/adjudication: freeze parser-owned `SourceId + exact span + parent relation` gold occurrences and reviewed exhaustive negatives, then rerun this evaluator before any production tuning.");
    return string.Join(Environment.NewLine, lines) + Environment.NewLine;
}

sealed record DatasetSpec(string Id, string LabelClass, string DocumentPath, string KeyPath, bool Complete, string Notes);

sealed record FirstLossEntry(string DatasetId, int GoldOrdinal, string? SourceId, string? Text, string Stage, string Owner);

sealed record GoldOccurrence(string DatasetId, int Ordinal, string? SourceId, int? SourceOrdinal, StructuralSpan? Span, string? Text, string Type, int? Level, string LabelClass, string ReviewStatus, string Provenance)
{
    public static GoldOccurrence From(string datasetId, int ordinal, AnswerKeyEntry entry, DocumentSourceCatalog catalog)
    {
        var sourceId = entry.StableId;
        var sourceOrdinal = entry.Index;
        if (sourceId is null && sourceOrdinal is not null)
            sourceId = catalog.Units.FirstOrDefault(unit => unit.SourceOrdinal == sourceOrdinal)?.SourceId;
        if (sourceOrdinal is null && sourceId is not null)
            sourceOrdinal = catalog.Units.FirstOrDefault(unit => unit.SourceId == sourceId)?.SourceOrdinal;
        return new GoldOccurrence(datasetId, ordinal, sourceId, sourceOrdinal, null, entry.Text, "Heading", entry.Level, "HUMAN_GOLD", "reviewed", "external-human-key");
    }
}

sealed class DatasetRun
{
    public required string Id { get; init; }
    public required string LabelClass { get; init; }
    [JsonIgnore]
    public string DocumentPath { get; init; } = string.Empty;
    [JsonIgnore]
    public string KeyPath { get; init; } = string.Empty;
    public required string Document { get; init; }
    public required string Key { get; init; }
    public required bool Complete { get; init; }
    public required string Notes { get; init; }
    public required int PositiveLabelCount { get; init; }
    public required int NegativeLabelCount { get; init; }
    public required IReadOnlyList<GoldOccurrence> Gold { get; init; }
    public required IReadOnlyList<FirstLossEntry> FirstLoss { get; init; }
    public required int PredictedOutlineCount { get; init; }
    public required int SourceIdentityJoined { get; init; }
    public required int UnjoinedOccurrences { get; init; }
    public required int LevelCompared { get; init; }
    public required int LevelCorrect { get; init; }
    public required int ProviderCalls { get; init; }
    public required bool HasExactSpanGold { get; init; }
    public required bool EligibleForPrecision { get; init; }
    public required bool EligibleForSourceIdentityDiagnostics { get; init; }
    public required string Status { get; init; }

    public static DatasetRun Missing(DatasetSpec dataset) => new()
    {
        Id = dataset.Id,
        LabelClass = dataset.LabelClass,
        DocumentPath = dataset.DocumentPath,
        KeyPath = dataset.KeyPath,
        Document = Path.GetFileName(dataset.DocumentPath),
        Key = Path.GetFileName(dataset.KeyPath),
        Complete = dataset.Complete,
        Notes = dataset.Notes,
        PositiveLabelCount = 0,
        NegativeLabelCount = 0,
        Gold = [],
        FirstLoss = [new FirstLossEntry(dataset.Id, -1, null, null, "UNKNOWN", "dataset document or key missing")],
        PredictedOutlineCount = 0,
        SourceIdentityJoined = 0,
        UnjoinedOccurrences = 0,
        LevelCompared = 0,
        LevelCorrect = 0,
        ProviderCalls = 0,
        HasExactSpanGold = false,
        EligibleForPrecision = false,
        EligibleForSourceIdentityDiagnostics = false,
        Status = "MISSING_INPUT",
    };
}

static class Metric
{
    public static object Measured(int numerator, int denominator) => new { status = denominator == 0 ? "NOT_MEASURABLE" : "MEASURED", numerator, denominator, value = denominator == 0 ? (double?)null : (double)numerator / denominator };
    public static object Diagnostic(int numerator, int denominator) => new { status = denominator == 0 ? "NOT_MEASURABLE" : "DIAGNOSTIC_ONLY", numerator, denominator, value = denominator == 0 ? (double?)null : (double)numerator / denominator };
    public static object NotMeasurable(string reason) => new { status = "NOT_MEASURABLE", numerator = (int?)null, denominator = (int?)null, value = (double?)null, reason };
}
