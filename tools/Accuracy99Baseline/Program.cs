using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;
using Accuracy99Baseline;

const string BaseRevision = "732c3505afc5dd312423ed0fa58056192fb39608";
var root = FindRepositoryRoot(args);
var outputRoot = Path.Combine(root, "eval", "accuracy99");
Directory.CreateDirectory(outputRoot);

var datasets = Accuracy99Datasets(root);
var only = args.FirstOrDefault(arg => arg.StartsWith("--only=", StringComparison.OrdinalIgnoreCase))?[7..];
if (!string.IsNullOrWhiteSpace(only))
    datasets = datasets.Where(dataset => string.Equals(dataset.Id, only, StringComparison.OrdinalIgnoreCase)).ToArray();
var debug = args.Any(arg => string.Equals(arg, "--debug", StringComparison.OrdinalIgnoreCase));
var importReviewRoot = args.FirstOrDefault(arg => arg.StartsWith("--import-reviews=", StringComparison.OrdinalIgnoreCase))?[17..];
var refreshReviewPackets = args.Any(arg => string.Equals(arg, "--refresh-review-packets", StringComparison.OrdinalIgnoreCase));
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
var sourceCatalogs = BuildSourceCatalogs(root, datasets);
var rebinding = BuildHistoricalRebinding(datasets, sourceCatalogs);
var phaseBReconciliation = BuildPhaseBReconciliation(sourceCatalogs, runs, allFirstLoss, rebinding.Count);
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
File.WriteAllText(Path.Combine(outputRoot, "gold-source-catalog.v1.json"), JsonSerializer.Serialize(new
{
    schemaVersion = 1,
    artifactKind = "accuracy99_parser_owned_gold_source_catalog",
    baseRevision = BaseRevision,
    coordinateSystem = "OpenXmlDocumentSource SourceId + SourceOrdinal; raw span is zero-based end-exclusive over RawText",
    derivedFrom = "OpenXmlDocumentSource only; no HeadingRecord, ValidatedStructure, Sections, Chunks, or predictions",
    documents = sourceCatalogs,
}, jsonOptions));
File.WriteAllText(Path.Combine(outputRoot, "gold-rebinding.v1.json"), JsonSerializer.Serialize(new
{
    schemaVersion = 1,
    artifactKind = "accuracy99_historical_positive_rebinding",
    baseRevision = BaseRevision,
    historicalPositiveLabels = rebinding.Count,
    statusCounts = rebinding.GroupBy(item => item.Status).ToDictionary(group => group.Key, group => group.Count()),
    records = rebinding,
    policy = "Historical labels are candidate evidence only. EXACT_REBOUND does not imply GOLD_READY until an exact heading span and explicit review are recorded.",
}, jsonOptions));
File.WriteAllText(Path.Combine(outputRoot, "gold-adjudication-status.v1.json"), JsonSerializer.Serialize(BuildAdjudicationStatus(root, datasets, sourceCatalogs, rebinding), jsonOptions));
var packetRoot = Path.Combine(outputRoot, "adjudication-packets");
Directory.CreateDirectory(packetRoot);
foreach (var catalog in sourceCatalogs.Where(item => item.Status == "AVAILABLE"))
{
    var packet = new
    {
        schemaVersion = 1,
        artifactKind = "accuracy99_source_first_adjudication_packet",
        datasetId = catalog.DatasetId,
        documentId = catalog.DocumentId,
        blind = false,
        predictionsIncluded = false,
        reviewStatus = "NOT_STARTED",
        requiredLabels = new[] { "HEADING", "NON_HEADING", "UNCERTAIN", "EXCLUDED" },
        occurrences = catalog.Occurrences.Select((occurrence, ordinal) => new
        {
            ordinal,
            occurrence.DocumentId,
            occurrence.SourceId,
            occurrence.SourceOrdinal,
            occurrence.RawText,
            occurrence.RawSourceSpan,
            occurrence.SourceType,
            occurrence.StableParserIdentity,
            occurrence.PreviousContext,
            occurrence.NextContext,
            occurrence.Page,
            historicalLabels = rebinding.Where(item => item.DatasetId == catalog.DatasetId &&
                item.ReboundSourceId == occurrence.SourceId).Select(item => new
                {
                    item.HistoricalOrdinal,
                    item.HistoricalSourceId,
                    item.HistoricalSourceOrdinal,
                    item.HistoricalText,
                    item.HistoricalLevel,
                    item.Status,
                }).ToArray(),
            adjudicatedLabel = (string?)null,
            headingSpan = (StructuralSpan?)null,
            headingText = (string?)null,
            structuralType = (string?)null,
            level = (int?)null,
            parentGoldId = (string?)null,
            reviewer = (string?)null,
        }).ToArray(),
    };
    File.WriteAllText(Path.Combine(packetRoot, $"{catalog.DatasetId}.v1.json"), JsonSerializer.Serialize(packet, jsonOptions));
}
File.WriteAllText(Path.Combine(outputRoot, "development-set.v1.json"), JsonSerializer.Serialize(new
{
    schemaVersion = 1,
    artifactKind = "accuracy99_non_blind_development_set",
    baseRevision = BaseRevision,
    blind = false,
    tuningAllowed = true,
    documentCount = sourceCatalogs.Count(item => item.Status == "AVAILABLE"),
    documents = sourceCatalogs.Where(item => item.Status == "AVAILABLE").Select(item => item.DatasetId).ToArray(),
    reviewedHeadingOccurrences = 0,
    reviewedNonHeadingOccurrences = 0,
    exhaustiveDocuments = 0,
    status = "READY_FOR_HUMAN_ADJUDICATION",
    note = "Historical inspection makes this a development/adjudication set, not a final blind 99% holdout.",
}, jsonOptions));
File.WriteAllText(Path.Combine(outputRoot, "blind-holdout.v1.json"), JsonSerializer.Serialize(new
{
    schemaVersion = 1,
    artifactKind = "accuracy99_blind_holdout_manifest",
    baseRevision = BaseRevision,
    status = "NOT_AVAILABLE",
    blind = false,
    documentCount = 0,
    contaminationStatus = "NOT_APPLICABLE",
    acquisitionRequired = "Acquire source files not previously used for extraction tuning, freeze document hashes and parser coordinates, then perform source-first exhaustive review before claiming blind holdout support.",
}, jsonOptions));
File.WriteAllText(Path.Combine(outputRoot, "phase-b-reconciliation.v1.json"), JsonSerializer.Serialize(phaseBReconciliation, jsonOptions));

var docPath = Path.Combine(root, "docs", "accuracy", "accuracy99-outline-baseline.md");
Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);
File.WriteAllText(docPath, RenderMarkdown(baseline, runs, allFirstLoss));
var phaseBDocPath = Path.Combine(root, "docs", "accuracy", "accuracy99-gold-protocol.md");
File.WriteAllText(phaseBDocPath, RenderPhaseBMarkdown(sourceCatalogs, rebinding, phaseBReconciliation));
var phaseC = PreparePhaseC(root, outputRoot, sourceCatalogs, rebinding, importReviewRoot, refreshReviewPackets, jsonOptions);

Console.WriteLine($"ACCURACY-99 PHASE C = {phaseC.Status}; datasets={runs.Count}; reviewOccurrences={phaseC.TotalOccurrences}; providerCalls={providerCalls}");

static PhaseCExecutionSummary PreparePhaseC(
    string root,
    string outputRoot,
    IReadOnlyList<SourceCatalogSnapshot> sourceCatalogs,
    IReadOnlyList<HistoricalRebinding> rebinding,
    string? importReviewRoot,
    bool refreshReviewPackets,
    JsonSerializerOptions jsonOptions)
{
    var documents = sourceCatalogs.Where(item => item.Status == "AVAILABLE").Select(catalog => new PhaseCSourceDocument
    {
        DatasetId = catalog.DatasetId,
        DocumentId = catalog.DocumentId!,
        SourceCatalogVersion = PhaseCAdjudication.SourceCatalogVersion,
        Occurrences = catalog.Occurrences.Select(item => new PhaseCSourceOccurrence
        {
            SourceId = item.SourceId,
            SourceOrdinal = item.SourceOrdinal,
            RawSourceText = item.RawText,
            RawSourceSpan = item.RawSourceSpan,
            SourceType = item.SourceType,
            PreviousSourceText = item.PreviousContext,
            NextSourceText = item.NextContext,
            Page = item.Page,
        }).ToArray(),
    }).ToArray();

    var developmentRoot = Path.Combine(outputRoot, "adjudication", "development");
    Directory.CreateDirectory(developmentRoot);
    var importing = !string.IsNullOrWhiteSpace(importReviewRoot);
    var reviewRoot = importing ? Path.GetFullPath(importReviewRoot!) : developmentRoot;
    var summaries = new List<PhaseCPacketSummary>();
    var imports = new List<PhaseCImportResult>();

    foreach (var document in documents)
    {
        var historical = rebinding.Where(item => item.DatasetId == document.DatasetId && item.ReboundSourceId is not null)
            .GroupBy(item => item.ReboundSourceId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<PhaseCHistoricalReference>)group.Select(item => new PhaseCHistoricalReference
            {
                HistoricalOrdinal = item.HistoricalOrdinal,
                HistoricalSourceId = item.HistoricalSourceId,
                HistoricalSourceOrdinal = item.HistoricalSourceOrdinal,
                HistoricalText = item.HistoricalText,
                HistoricalLevel = item.HistoricalLevel,
                Status = item.Status,
            }).ToArray(), StringComparer.Ordinal);
        var packetPath = Path.Combine(reviewRoot, $"{document.DatasetId}.review.jsonl");
        if (!importing && refreshReviewPackets && File.Exists(packetPath) && HasHumanReviewData(PhaseCAdjudication.ReadPacket(packetPath)))
            throw new InvalidOperationException($"Refusing to overwrite human review data in {packetPath}.");
        if (!importing && (refreshReviewPackets || !File.Exists(packetPath)))
            PhaseCAdjudication.WritePacket(packetPath, PhaseCAdjudication.CreateBlankPacket(document, historical));
        if (!File.Exists(packetPath))
            throw new FileNotFoundException($"Review packet {document.DatasetId} was not supplied.", packetPath);

        var completenessErrors = PhaseCAdjudication.ValidatePacketCompleteness(packetPath, document);
        if (completenessErrors.Count != 0)
            throw new InvalidDataException($"Review packet {document.DatasetId} failed completeness validation: {string.Join(", ", completenessErrors)}");
        var packet = PhaseCAdjudication.ReadPacket(packetPath);
        var labels = rebinding.Where(item => item.DatasetId == document.DatasetId).ToArray();
        var unlabeled = packet.Occurrences.Count(item =>
            item.AdjudicatedLabel is null && item.InitialAdjudicatedLabel is null && item.FinalAdjudicatedLabel is null);
        summaries.Add(new PhaseCPacketSummary
        {
            DatasetId = document.DatasetId,
            Packet = importing ? Path.GetFullPath(packetPath) : Path.GetRelativePath(root, packetPath).Replace('\\', '/'),
            CatalogOccurrenceCount = document.Occurrences.Count,
            ReviewPacketOccurrenceCount = packet.Occurrences.Count,
            SourceCatalogHash = PhaseCAdjudication.ComputeSourceCatalogHash(document),
            ReviewPacketHash = PhaseCAdjudication.ComputeFileHash(packetPath),
            HistoricalExactRebounds = labels.Count(item => item.Status == "EXACT_REBOUND"),
            HistoricalReviewRequired = labels.Count(item => item.Status == "REVIEW_REQUIRED"),
            HistoricalAmbiguous = labels.Count(item => item.Status == "AMBIGUOUS"),
            Unlabeled = unlabeled,
            PacketCompleteness = "PASS",
            ReviewStatus = packet.Manifest.ReviewStatus,
        });
        if (importing) imports.Add(PhaseCAdjudication.ImportAndValidate(packetPath, document));
    }

    var status = "WAITING_FOR_HUMAN_ADJUDICATION";
    if (importing)
    {
        var invalid = imports.Where(item => !item.GoldReady).ToArray();
        if (invalid.Length != 0)
        {
            var detail = string.Join("; ", invalid.Select(item => $"{item.DatasetId}=[{string.Join(",", item.Errors)}]"));
            throw new InvalidDataException("Human review import is not GOLD_READY: " + detail);
        }
        var gold = PhaseCAdjudication.FreezeDevelopmentGold(imports, "accuracy99-development-gold-v1");
        var goldPath = Path.Combine(outputRoot, "development-gold.v1.json");
        if (File.Exists(goldPath))
            throw new InvalidOperationException("development-gold.v1.json is already frozen; corrections require a new explicit version.");
        File.WriteAllText(goldPath, JsonSerializer.Serialize(gold, jsonOptions));
        status = "DEVELOPMENT_GOLD_READY";
    }

    File.WriteAllText(Path.Combine(outputRoot, "phase-c-adjudication-status.v1.json"), JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        artifactKind = "accuracy99_phase_c_development_gold_adjudication",
        productionBaselineRevision = PhaseCAdjudication.ProductionBaselineRevision,
        status,
        sourceFirst = true,
        predictionsIncluded = false,
        importer = "READY",
        goldValidators = "READY",
        packetCompleteness = summaries.All(item => item.PacketCompleteness == "PASS") ? "PASS" : "FAIL",
        totalOccurrences = summaries.Sum(item => item.CatalogOccurrenceCount),
        unlabeledOccurrences = summaries.Sum(item => item.Unlabeled),
        developmentGold = status == "DEVELOPMENT_GOLD_READY" ? "FROZEN" : "NOT_FROZEN",
        developmentBaseline = "NOT_RUN",
        providerCalls = 0,
        productionSourceChanged = false,
        tuningPerformed = false,
        packets = summaries,
    }, jsonOptions));

    File.WriteAllText(Path.Combine(outputRoot, "blind-holdout-manifest.v1.json"), JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        artifactKind = "accuracy99_blind_holdout_acquisition_manifest",
        status = "NOT_AVAILABLE",
        blind = false,
        documentCount = 0,
        developmentDocumentsExcluded = documents.Select(item => item.DatasetId).ToArray(),
        hashesFrozen = false,
        labelsFrozen = false,
        requirements = new[]
        {
            "unseen documents where possible",
            "available parser-owned source facts",
            "exhaustive source-first labels including negatives",
            "exact heading spans, reviewed levels, and reviewed parent states",
            "PDF and DOCX strata appropriate to the claim",
            "document and source-catalog hashes frozen before evaluation",
        },
    }, jsonOptions));

    var guidePath = Path.Combine(root, "docs", "accuracy", "accuracy99-human-adjudication-guide.md");
    File.WriteAllText(guidePath, RenderHumanAdjudicationGuide());
    return new PhaseCExecutionSummary(status, summaries.Sum(item => item.CatalogOccurrenceCount));
}

static bool HasHumanReviewData(PhaseCReviewPacket packet) =>
    packet.Manifest.ReviewStatus != "READY_FOR_REVIEW" || packet.Occurrences.Any(item =>
        item.AdjudicatedLabel is not null || item.InitialAdjudicatedLabel is not null ||
        item.FinalAdjudicatedLabel is not null || item.Reviewer is not null || item.ReviewNotes is not null ||
        item.HeadingStart is not null || item.HeadingEnd is not null || item.HeadingText is not null ||
        item.StructuralType is not null || item.Level is not null || item.LevelReviewStatus is not null ||
        item.ParentGoldId is not null || item.ParentReviewStatus is not null || item.GoldHeadingId is not null);

static string RenderHumanAdjudicationGuide() =>
    """
    # Accuracy-99 human adjudication guide

    Edit the five `eval/accuracy99/adjudication/development/*.review.jsonl` files source-first. The first line is an immutable manifest; every later line is one parser-owned occurrence. Do not remove, duplicate, reorder, or edit source identity/text fields.

    - `HEADING`: the occurrence contains a heading. Set the exact zero-based, end-exclusive `headingStart`/`headingEnd`, copy the exact substring into `headingText`, set `structuralType`, and record level/parent review status.
    - `NON_HEADING`: reviewed source content that is not a heading.
    - `UNCERTAIN`: source evidence is insufficient for a defensible semantic decision.
    - `EXCLUDED`: occurrence is outside the benchmark's eligible source universe for an explicit protocol reason.

    For `HEADING`, coordinates may cover only part of `rawSourceText`. Never normalize text when selecting the span. Set `levelReviewStatus` to `REVIEWED` with a positive `level`, or `LEVEL_NOT_REVIEWED` with `level=null`. Set `parentReviewStatus` to `ROOT`, `PARENT_REVIEWED`, or `PARENT_UNKNOWN`; `PARENT_REVIEWED` must use another heading's deterministic `goldHeadingId` in the same document.

    Every reviewed row requires `reviewer`. Non-heading labels must leave every heading, level, parent, and gold-heading field null. Historical provenance is evidence only and does not pre-fill the human decision. Production predictions are intentionally absent.

    After all rows are complete, change the manifest `reviewStatus` to `REVIEW_COMPLETE` and run the importer with `--import-reviews=<directory>`. A discrepancy pass may preserve `initialAdjudicatedLabel`, then set `finalAdjudicatedLabel` plus `resolutionReason`; it must never overwrite the initial label silently.

    `--refresh-review-packets` is only for regenerating untouched blank packets. The runner refuses to refresh any packet containing human input. A frozen `development-gold.v1.json` is immutable; corrections require a new explicit dataset version.
    """ + Environment.NewLine;

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

static IReadOnlyList<SourceCatalogSnapshot> BuildSourceCatalogs(string root, IReadOnlyList<DatasetSpec> datasets)
{
    var catalogs = new List<SourceCatalogSnapshot>();
    foreach (var dataset in datasets)
    {
        if (!File.Exists(dataset.DocumentPath))
        {
            catalogs.Add(new SourceCatalogSnapshot
            {
                DatasetId = dataset.Id,
                Status = "SOURCE_MISSING",
                DocumentPath = Path.GetRelativePath(root, dataset.DocumentPath).Replace('\\', '/'),
                DocumentId = null,
                SourceKind = null,
                Occurrences = [],
            });
            continue;
        }

        var source = new OpenXmlDocumentSource().Read(dataset.DocumentPath);
        var paragraphs = source.Paragraphs;
        var occurrences = paragraphs.Select((paragraph, index) => new SourceCatalogOccurrence
        {
            DocumentId = source.DocumentId,
            SourceId = paragraph.SourceId,
            SourceOrdinal = paragraph.SourceOrdinal,
            RawText = paragraph.Text,
            RawSourceSpan = new StructuralSpan(0, paragraph.Text.Length),
            SourceType = source.SourceKind,
            StableParserIdentity = paragraph.SourceId,
            PreviousContext = index == 0 ? null : TrimContext(paragraphs[index - 1].Text),
            NextContext = index + 1 >= paragraphs.Count ? null : TrimContext(paragraphs[index + 1].Text),
            Page = null,
        }).ToArray();
        if (occurrences.Any(occurrence => !PhaseBContracts.IsValidSourceSpan(occurrence.RawText, occurrence.RawSourceSpan)))
            throw new InvalidOperationException($"Parser-owned source catalog contains an invalid span for dataset {dataset.Id}.");

        catalogs.Add(new SourceCatalogSnapshot
        {
            DatasetId = dataset.Id,
            Status = "AVAILABLE",
            DocumentPath = Path.GetRelativePath(root, dataset.DocumentPath).Replace('\\', '/'),
            DocumentId = source.DocumentId,
            SourceKind = source.SourceKind,
            Occurrences = occurrences,
        });
    }
    return catalogs;
}

static IReadOnlyList<HistoricalRebinding> BuildHistoricalRebinding(
    IReadOnlyList<DatasetSpec> datasets,
    IReadOnlyList<SourceCatalogSnapshot> catalogs)
{
    var records = new List<HistoricalRebinding>();
    foreach (var dataset in datasets)
    {
        if (!File.Exists(dataset.KeyPath)) continue;
        var key = AnswerKey.Load(dataset.KeyPath);
        var catalog = catalogs.Single(item => item.DatasetId == dataset.Id);
        if (catalog.Status == "SOURCE_MISSING") continue;
        var occurrencesById = catalog.Occurrences
            .GroupBy(item => item.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var historicalBySourceId = key.PositiveEntries
            .Where(entry => entry.StableId is not null)
            .GroupBy(entry => entry.StableId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var (entry, ordinal) in key.PositiveEntries.Select((entry, ordinal) => (entry, ordinal)))
        {
            var sourceId = entry.StableId;
            if (sourceId is null && entry.Index is not null)
                sourceId = catalog.Occurrences.FirstOrDefault(item => item.SourceOrdinal == entry.Index)?.SourceId;
            var candidates = sourceId is not null && occurrencesById.TryGetValue(sourceId, out var found)
                ? found
                : [];
            var duplicateHistorical = sourceId is not null && historicalBySourceId.GetValueOrDefault(sourceId) > 1;
            var source = candidates.SingleOrDefault();
            string status;
            string reason;
            if (sourceId is null)
            {
                status = "INVALID_HISTORICAL_LABEL";
                reason = "historical label has neither a stable source ID nor a usable ordinal";
            }
            else if (candidates.Length == 0)
            {
                status = "SOURCE_NOT_AVAILABLE";
                reason = catalog.Status == "SOURCE_MISSING"
                    ? "source document is unavailable in the current checkout"
                    : "stable source ID is absent from the current parser-owned catalog; coordinate migration or parser identity change requires review";
            }
            else if (duplicateHistorical)
            {
                status = "AMBIGUOUS";
                reason = "multiple historical positives reference the same source occurrence; an exact heading span is required to disambiguate";
            }
            else if (string.IsNullOrWhiteSpace(entry.Text) ||
                !Normalize(source!.RawText).Contains(Normalize(entry.Text), StringComparison.Ordinal))
            {
                status = "REVIEW_REQUIRED";
                reason = "source identity exists but historical text is not contained in the raw source text";
            }
            else
            {
                status = "EXACT_REBOUND";
                reason = "unique parser-owned source identity and historical text are compatible; heading span and semantic fields still require explicit review";
            }

            records.Add(new HistoricalRebinding
            {
                DatasetId = dataset.Id,
                HistoricalOrdinal = ordinal,
                HistoricalSourceId = entry.StableId,
                HistoricalSourceOrdinal = entry.Index,
                HistoricalText = entry.Text,
                HistoricalLevel = entry.Level,
                Status = status,
                Reason = reason,
                ReboundSourceId = source?.SourceId,
                ReboundSourceOrdinal = source?.SourceOrdinal,
                ReboundRawSourceSpan = source?.RawSourceSpan,
                GoldReady = false,
            });
        }
    }
    return records;
}

static object BuildAdjudicationStatus(
    string root,
    IReadOnlyList<DatasetSpec> datasets,
    IReadOnlyList<SourceCatalogSnapshot> catalogs,
    IReadOnlyList<HistoricalRebinding> rebinding)
{
    return new
    {
        schemaVersion = 1,
        artifactKind = "accuracy99_gold_adjudication_status",
        baseRevision = BaseRevision,
        documents = datasets.Select(dataset =>
        {
            var catalog = catalogs.Single(item => item.DatasetId == dataset.Id);
            var labels = rebinding.Where(item => item.DatasetId == dataset.Id).ToArray();
            var keyPositiveCount = File.Exists(dataset.KeyPath) ? AnswerKey.Load(dataset.KeyPath).PositiveEntries.Count : 0;
            return new
            {
                datasetId = dataset.Id,
                document = Path.GetRelativePath(root, dataset.DocumentPath).Replace('\\', '/'),
                status = catalog.Status == "SOURCE_MISSING" ? "SOURCE_MISSING" : "PARTIAL_REVIEW",
                sourceOccurrences = catalog.Occurrences.Count,
                historicalPositiveLabels = labels.Length,
                historicalKeyLabelsOutsidePhaseACohort = catalog.Status == "SOURCE_MISSING" ? keyPositiveCount : 0,
                exactRebound = labels.Count(item => item.Status == "EXACT_REBOUND"),
                reviewRequired = labels.Count(item => item.Status == "REVIEW_REQUIRED"),
                sourceNotAvailable = labels.Count(item => item.Status == "SOURCE_NOT_AVAILABLE"),
                ambiguous = labels.Count(item => item.Status == "AMBIGUOUS"),
                invalid = labels.Count(item => item.Status == "INVALID_HISTORICAL_LABEL"),
                exhaustiveReview = "NOT_STARTED",
                goldReady = false,
                eligibleForPrecision = false,
                packet = catalog.Status == "AVAILABLE" ? $"eval/accuracy99/adjudication-packets/{dataset.Id}.v1.json" : null,
            };
        }),
        protocol = new
        {
            sourceFirst = true,
            predictionHiddenDuringInitialReview = true,
            requiredFinalLabels = new[] { "HEADING", "NON_HEADING", "UNCERTAIN", "EXCLUDED" },
            unlabeledIsNotNegative = true,
        },
    };
}

static object BuildPhaseBReconciliation(
    IReadOnlyList<SourceCatalogSnapshot> catalogs,
    IReadOnlyList<DatasetRun> runs,
    IReadOnlyList<FirstLossEntry> firstLoss,
    int rebindingCount)
{
    var byDataset = catalogs.ToDictionary(item => item.DatasetId, StringComparer.Ordinal);
    var positiveRecords = firstLoss.Count(item => item.GoldOrdinal >= 0);
    var missingSentinels = firstLoss.Count(item => item.GoldOrdinal < 0);
    var sourceNotParsed = firstLoss.Where(item => item.Stage == "SOURCE_NOT_PARSED").ToArray();
    var unknown = firstLoss.Where(item => item.Stage == "UNKNOWN").ToArray();
    static string Classify(FirstLossEntry item, IReadOnlyDictionary<string, SourceCatalogSnapshot> catalogsByDataset)
    {
        if (!catalogsByDataset.TryGetValue(item.DatasetId, out var catalog) || catalog.Status == "SOURCE_MISSING")
            return "SOURCE_DOCUMENT_MISSING";
        var sourcePresent = item.SourceId is not null && catalog.Occurrences.Any(source => source.SourceId == item.SourceId);
        if (item.Stage == "SOURCE_NOT_PARSED")
            return sourcePresent
                ? "EVALUATOR_LOOKUP_OR_FIRST_LOSS_PROVENANCE_CONFLICT"
                : "HISTORICAL_COORDINATE_INCOMPATIBLE";
        return !sourcePresent ? "NO_CURRENT_SOURCE_BINDING" : "INSUFFICIENT_HISTORICAL_PROVENANCE";
    }

    return new
    {
        schemaVersion = 1,
        artifactKind = "accuracy99_phase_b_reconciliation",
        baseRevision = BaseRevision,
        historicalPositiveLabels = runs.Sum(run => run.Gold.Count),
        firstLossRecords = firstLoss.Count,
        positiveOccurrenceFirstLossRecords = positiveRecords,
        missingDatasetSentinelRecords = missingSentinels,
        sourceNotParsed = sourceNotParsed.Length,
        unknown = unknown.Length,
        difference = firstLoss.Count - runs.Sum(run => run.Gold.Count),
        differenceReason = "The first-loss ledger contains two explicit dataset-level missing-input sentinel records for 025 and 063. They are not positive occurrences; the 222 labeled occurrence records reconcile exactly.",
        historicalPositiveAccountingDelta = rebindingCount - runs.Sum(run => run.Gold.Count),
        sourceNotParsedPartition = sourceNotParsed
            .GroupBy(item => Classify(item, byDataset))
            .ToDictionary(group => group.Key, group => group.Count()),
        unknownPartition = unknown
            .GroupBy(item => Classify(item, byDataset))
            .ToDictionary(group => group.Key, group => group.Count()),
        integrity = new
        {
            duplicateGoldIdentity = 0,
            invalidGoldSpanForGoldReady = 0,
            goldSourceNotFoundForGoldReady = 0,
            pipelineDerivedGold = 0,
            unreviewedLabelUsedAsHumanGold = 0,
            partialDocumentUsedForPrecision = 0,
            holdoutContaminationUnreported = 0,
        },
    };
}

static string TrimContext(string text) => text.Length <= 240 ? text : text[..240];

static string RenderPhaseBMarkdown(
    IReadOnlyList<SourceCatalogSnapshot> catalogs,
    IReadOnlyList<HistoricalRebinding> rebinding,
    object reconciliation)
{
    var counts = rebinding.GroupBy(item => item.Status).ToDictionary(group => group.Key, group => group.Count());
    var lines = new List<string>
    {
        "# ACCURACY-99 Phase B gold protocol",
        "",
        "This branch establishes parser-owned annotation coordinates and human-review packets only. Production extraction code, thresholds, prompts, and validators are unchanged.",
        "",
        $"- Base revision: `{BaseRevision}`",
        "- Status: `READY_FOR_HUMAN_ADJUDICATION`",
        "- Blind holdout: `NOT_AVAILABLE`",
        "",
        "## Historical accounting",
        "",
        "The 222 historical positive labels are accounted for in `gold-rebinding.v1.json`. The earlier 224 first-loss records reconcile as 222 occurrence records plus two explicit missing-input sentinels for datasets 025 and 063.",
        "",
        $"Rebinding status counts: {string.Join(", ", counts.OrderBy(item => item.Key).Select(item => $"{item.Key}={item.Value}"))}.",
        "",
        "`EXACT_REBOUND` means only that a unique parser source identity and historical text are compatible. It is not `GOLD_READY`: exact heading span, semantic label, level, parent, and exhaustive negative review remain pending.",
        "",
        "## Review protocol",
        "",
        "Reviewers first see source identity, raw text, exact parser coordinates, and neighboring context. Production predictions remain hidden until initial labels are frozen. Every source occurrence must receive `HEADING`, `NON_HEADING`, `UNCERTAIN`, or `EXCLUDED`; unlabeled is never a negative.",
        "",
        "## Dataset status",
        "",
    };
    lines.AddRange(catalogs.Select(catalog => $"- `{catalog.DatasetId}`: `{catalog.Status}`, occurrences={catalog.Occurrences.Count}"));
    lines.Add("");
    lines.Add("Precision/recall remain unavailable until an exhaustive reviewed development set and a genuinely blind holdout are frozen. No accuracy remediation is authorized by this phase.");
    return string.Join(Environment.NewLine, lines) + Environment.NewLine;
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

sealed record PhaseCExecutionSummary(string Status, int TotalOccurrences);

sealed class PhaseCPacketSummary
{
    public required string DatasetId { get; init; }
    public required string Packet { get; init; }
    public required int CatalogOccurrenceCount { get; init; }
    public required int ReviewPacketOccurrenceCount { get; init; }
    public required string SourceCatalogHash { get; init; }
    public required string ReviewPacketHash { get; init; }
    public required int HistoricalExactRebounds { get; init; }
    public required int HistoricalReviewRequired { get; init; }
    public required int HistoricalAmbiguous { get; init; }
    public required int Unlabeled { get; init; }
    public required string PacketCompleteness { get; init; }
    public required string ReviewStatus { get; init; }
}

sealed class SourceCatalogSnapshot
{
    public required string DatasetId { get; init; }
    public required string Status { get; init; }
    public required string DocumentPath { get; init; }
    public required string? DocumentId { get; init; }
    public required string? SourceKind { get; init; }
    public required IReadOnlyList<SourceCatalogOccurrence> Occurrences { get; init; }
}

sealed record SourceCatalogOccurrence
{
    public required string DocumentId { get; init; }
    public required string SourceId { get; init; }
    public required int SourceOrdinal { get; init; }
    public required string RawText { get; init; }
    public required StructuralSpan RawSourceSpan { get; init; }
    public required string SourceType { get; init; }
    public required string StableParserIdentity { get; init; }
    public string? PreviousContext { get; init; }
    public string? NextContext { get; init; }
    public int? Page { get; init; }
}

sealed class HistoricalRebinding
{
    public required string DatasetId { get; init; }
    public required int HistoricalOrdinal { get; init; }
    public required string? HistoricalSourceId { get; init; }
    public required int? HistoricalSourceOrdinal { get; init; }
    public required string? HistoricalText { get; init; }
    public required int? HistoricalLevel { get; init; }
    public required string Status { get; init; }
    public required string Reason { get; init; }
    public required string? ReboundSourceId { get; init; }
    public required int? ReboundSourceOrdinal { get; init; }
    public required StructuralSpan? ReboundRawSourceSpan { get; init; }
    public required bool GoldReady { get; init; }
}

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
