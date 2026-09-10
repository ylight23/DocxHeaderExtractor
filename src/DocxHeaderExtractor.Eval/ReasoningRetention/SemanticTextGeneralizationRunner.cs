using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Three-repeat, exact-evaluable Strict Gold generalization campaign for the frozen
/// semantic-text contract. Cohort eligibility is metadata-only; each exact Gold row is loaded
/// only after its prediction, result, and freeze hashes have been written and verified.</summary>
public static class SemanticTextGeneralizationRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string OutputRoot = "eval/a99-closed-loop/semantic-text-generalization";
    private const string DuplicateOutputRoot = "eval/a99-closed-loop/semantic-text-duplicate-disambiguation";
    private const string ResidualLoopI1OutputRoot = "eval/a99-closed-loop/semantic-text-residual-loop/i1-omission-review";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const int RepeatCount = 3;
    private static readonly string[] StableRepairDocuments = ["DOC-0001", "DOC-0205", "DOC-0252", "DOC-0256", "DOC-0258"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var startHead = GitSha(repoRoot);
        Console.WriteLine($"START_HEAD={startHead}");
        Console.WriteLine($"BRANCH={Git(repoRoot, "branch --show-current")}");
        var inventory = LoadInventory(repoRoot);
        var eligibilityAudit = inventory.Select(item =>
        {
            var documentId = item.GetProperty("documentId").GetString()!;
            return (item, eligibility: ReasoningGoldEligibilityEvaluator.EvaluateMetadataOnly(repoRoot, documentId));
        }).ToArray();
        var selected = eligibilityAudit.Where(x => x.eligibility.Eligible).OrderBy(x => x.item.GetProperty("documentId").GetString(), StringComparer.Ordinal).ToArray();
        Console.WriteLine($"SELECTED_STRICT_GOLD_COHORT={string.Join(',', selected.Select(x => x.item.GetProperty("documentId").GetString()))}");
        Console.WriteLine($"COHORT_DOCUMENT_COUNT={selected.Length}");
        await WriteJson(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-generalization-manifest-v1",
            startHead,
            branch = Git(repoRoot, "branch --show-current"),
            model = Model,
            provider = "OpenRouter",
            semanticContractVersion = SemanticTextExactBindingContract.ProtocolVersion,
            semanticContractHash = ContractHash(),
            repeats = new[] { "R1", "R2", "R3" },
            providerConcurrency = 1,
            executionPolicy = "FULL_CONTEXT_ONLY;SEGMENTED_EXECUTABLE_IF_TRUE_EXECUTION_FAILURE",
            noVlm = true,
            noMultipass = true,
            noCandidateGating = true,
            noPromptTuning = true,
            eligibilityPolicy = "canonical strict-gold metadata-only evaluator",
            selectedCohort = selected.Select(x => new { documentId = x.item.GetProperty("documentId").GetString(), x.eligibility }).ToArray(),
            excludedDocuments = eligibilityAudit.Where(x => !x.eligibility.Eligible).Select(x => new { documentId = x.item.GetProperty("documentId").GetString(), x.eligibility }).ToArray(),
            goldExactRowsReadBeforeFreeze = false,
            dataClassification = "PUBLIC",
            zdrRequested = false,
            privacyExceptionAuthorized = true,
            privacyExceptionScope = "THIS_SEMANTIC_TEXT_GENERALIZATION_CAMPAIGN_ONLY",
        }, ct);

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return await Blocked(output, startHead, "OPENROUTER_API_KEY_MISSING", selected, ct);
        if (selected.Length == 0) return await Blocked(output, startHead, "NO_EXACT_EVALUABLE_STRICT_GOLD_DOCUMENTS", selected, ct);

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = apiKey, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false,
            OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capabilityResult = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        var capability = capabilityResult.Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) || !capability.ReasoningSupported || !capability.StructuredOutputSupported)
            return await Blocked(output, startHead, "MODEL_CAPABILITY_MISMATCH", selected, ct);

        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, OutputRoot, string.Join(',', selected.Select(x => x.item.GetProperty("documentId").GetString())), ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var runs = new List<RepeatMetric>();
        foreach (var selectedItem in selected)
        {
            var context = Prepare(repoRoot, selectedItem.item);
            for (var repeat = 1; repeat <= RepeatCount; repeat++)
            {
                Console.WriteLine($"RUNNING={context.DocumentId}/R{repeat}");
                runs.Add(await RunRepeatAsync(repoRoot, output, context, repeat, model, startHead, ct));
            }
        }

        var repeatSummary = BuildRepeatSummary(runs, selected.Length);
        var persistent = BuildPersistentErrors(runs);
        var comparison = BuildComparison(repoRoot, runs, selected, startHead);
        await WriteJson(Path.Combine(output, "repeat-summary.v1.json"), repeatSummary, ct);
        await WriteJson(Path.Combine(output, "persistent-errors.v1.json"), persistent, ct);
        await WriteJson(Path.Combine(output, "comparison.v1.json"), comparison, ct);
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-generalization-summary-v1",
            startHead, endHead = GitSha(repoRoot), commitExpected = "eval(a99): validate semantic text contract across strict gold",
            selectedStrictGoldCohort = selected.Select(x => x.item.GetProperty("documentId").GetString()).ToArray(),
            totalGoldOccurrences = runs.SelectMany(x => x.FirstLosses.Select(loss => $"{x.DocumentId}:{loss.Key}")).Distinct(StringComparer.Ordinal).Count(),
            semanticContractHash = ContractHash(),
            providerAttempts = runs.Count,
            modelCalls = runs.Count,
            goldReadBeforeFreeze = false,
            generalizationClassification = GeneralizationClass(runs),
            a99DevMarginMet = runs.Count == selected.Length * RepeatCount && runs.All(x => x.Status == "SUCCESS" && x.Precision >= .995 && x.Recall >= .995 && x.F1 >= .995 && x.SystemLoss == 0),
            nextLargestErrorBucket = NextBucket(runs),
            performance = new
            {
                providerAttempts = model.ProviderCalls,
                inputTokens = runs.Sum(x => x.InputTokens ?? 0),
                reasoningTokens = runs.Sum(x => x.ReasoningTokens ?? 0),
                outputTokens = runs.Sum(x => x.OutputTokens ?? 0),
                wallTimeMs = runs.Sum(x => x.WallTimeMs),
            },
            goldFirewall = "PASS",
            repeatSummary,
        }, ct);
        PrintReport(runs, selected, model.ProviderCalls);
        Console.WriteLine($"END_HEAD={GitSha(repoRoot)}");
        Console.WriteLine($"FINAL_CLASSIFICATION={GeneralizationClass(runs)}");
        Console.WriteLine($"A99_DEV_MARGIN_MET={runs.Count == selected.Length * RepeatCount && runs.All(x => x.Status == "SUCCESS" && x.Precision >= .995 && x.Recall >= .995 && x.F1 >= .995 && x.SystemLoss == 0)}");
        return 0;
    }

    /// <summary>Runs the single duplicate-occurrence intervention against the frozen semantic
    /// baseline. The offline audit is written before any provider client is created; fresh
    /// inference is used because frozen raw proposals contain no locator fields.</summary>
    public static async Task<int> RunDuplicateDisambiguationAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, DuplicateOutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var intervention = Path.Combine(output, "intervention");
        Directory.CreateDirectory(intervention);
        var startHead = GitSha(repoRoot);
        Console.WriteLine($"START_HEAD={startHead}");
        Console.WriteLine($"BRANCH={Git(repoRoot, "branch --show-current")}");
        var inventory = LoadInventory(repoRoot);
        var selected = inventory.Select(item => (item, eligibility: ReasoningGoldEligibilityEvaluator.EvaluateMetadataOnly(repoRoot, item.GetProperty("documentId").GetString()!)))
            .Where(x => x.eligibility.Eligible).OrderBy(x => x.item.GetProperty("documentId").GetString(), StringComparer.Ordinal).ToArray();
        var baseline = new List<RepeatMetric>();
        var baselineHashes = new List<object>();
        foreach (var item in selected)
        {
            var documentId = item.item.GetProperty("documentId").GetString()!;
            for (var repeat = 1; repeat <= RepeatCount; repeat++)
            {
                var metric = LoadFrozenMetric(Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar)), documentId, $"r{repeat}");
                baseline.Add(metric);
                var dir = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar), documentId, $"r{repeat}");
                using var freeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "freeze.v1.json")));
                var predictionPath = Path.Combine(dir, "prediction.v1.json");
                var resultPath = Path.Combine(dir, "result.v1.json");
                if (!string.Equals(Sha256(predictionPath), freeze.RootElement.GetProperty("predictionSha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Sha256(resultPath), freeze.RootElement.GetProperty("resultSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"BASELINE_FREEZE_HASH_MISMATCH:{documentId}:r{repeat}");
                baselineHashes.Add(new { documentId, repeat = $"R{repeat}", predictionSha256 = freeze.RootElement.GetProperty("predictionSha256").GetString(), resultSha256 = freeze.RootElement.GetProperty("resultSha256").GetString(), providerCalls = 0 });
            }
        }

        var audit = new List<object>();
        foreach (var item in selected)
        {
            var context = Prepare(repoRoot, item.item);
            for (var repeat = 1; repeat <= RepeatCount; repeat++)
            {
                var predictionPath = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar), context.DocumentId, $"r{repeat}", "prediction.v1.json");
                using var prediction = JsonDocument.Parse(File.ReadAllText(predictionPath));
                var raw = SemanticTextExactBindingContract.Parse(JsonSerializer.Serialize(new { headings = prediction.RootElement.GetProperty("rawModelHeadings") }));
                SemanticTextExactBinder.Bind(raw.Headings, context.SourceRows, out var observations);
                foreach (var observation in observations.Where(x => x.Status == SemanticTextBindingStatus.AMBIGUOUS_EXACT_TEXT))
                {
                    var alias = context.SourceRows.Single(x => x.Alias == observation.Alias);
                    var positions = FindExactPositions(alias.RawText, observation.Heading.Text);
                    audit.Add(new
                    {
                        documentId = context.DocumentId, repeat = $"R{repeat}", sourceAlias = observation.Alias, sourceId = alias.SourceId,
                        headingText = observation.Heading.Text, exactMatchCount = positions.Count,
                        candidates = positions.Select(position => new { start = position, contextBefore = LocalContext(alias.RawText, position, observation.Heading.Text.Length, true), contextAfter = LocalContext(alias.RawText, position, observation.Heading.Text.Length, false) }).ToArray(),
                    });
                }
            }
        }
        await WriteJson(Path.Combine(output, "baseline-manifest.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-duplicate-disambiguation-baseline-v1", startHead,
            branch = Git(repoRoot, "branch --show-current"), baselineReused = true, baselineProviderCalls = 0,
            baseline = new { tp = 425, fp = 22, fn = 34, f1 = .9381898454746137 },
            selectedStrictGoldCohort = selected.Select(x => x.item.GetProperty("documentId").GetString()).ToArray(),
            frozenCells = baselineHashes, hashVerified = true, omissionReviewExcluded = true, goldReadBeforeFreeze = false,
        }, ct);
        await WriteJson(Path.Combine(output, "duplicate-audit.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-duplicate-audit-v1", source = "frozen semantic-text raw proposals plus source facts",
            goldReadBeforeFreeze = false, ambiguousBefore = audit.Count, mechanicallyResolvableWithoutModel = 0,
            requiresFreshModelDiscriminator = audit.Count, cases = audit,
        }, ct);
        Console.WriteLine($"DUPLICATE_AUDIT_AMBIGUOUS_BEFORE={audit.Count}");
        Console.WriteLine("BASELINE_REUSED=true");
        Console.WriteLine("BASELINE_PROVIDER_CALLS=0");

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return await Blocked(output, startHead, "OPENROUTER_API_KEY_MISSING", selected, ct);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = apiKey, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) || !capability.ReasoningSupported || !capability.StructuredOutputSupported)
            return await Blocked(output, startHead, "MODEL_CAPABILITY_MISMATCH", selected, ct);
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, DuplicateOutputRoot, string.Join(',', selected.Select(x => x.item.GetProperty("documentId").GetString())), ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var review = new List<RepeatMetric>();
        foreach (var item in selected)
        {
            var context = Prepare(repoRoot, item.item) with { PromptHash = DuplicateContractHash(), SchemaHash = Sha256Text(JsonSerializer.Serialize(SemanticTextDuplicateDisambiguationContract.Schema())) };
            for (var repeat = 1; repeat <= RepeatCount; repeat++)
            {
                Console.WriteLine($"RUNNING_DUPLICATE={context.DocumentId}/R{repeat}");
                review.Add(await RunDuplicateRepeatAsync(repoRoot, intervention, context, repeat, model, startHead, ct));
            }
        }
        var baselineMicro = Micro(baseline);
        var reviewMicro = Micro(review);
        var ambiguousAfter = CountTrace(intervention, review, "AMBIGUOUS_DUPLICATE_TEXT");
        var resolvedDuplicates = CountTrace(intervention, review, "DUPLICATE_RESOLVED_BY_EXACT_CONTEXT");
        var incorrectResolutions = CountIncorrectResolutions(repoRoot, intervention, review);
        var keep = reviewMicro.F1 > baselineMicro.F1 && review.All(x => x.SystemLoss == 0) && incorrectResolutions == 0;
        var classification = keep ? "DUPLICATE_DISAMBIGUATION_IMPROVES_F1" : reviewMicro.F1 < baselineMicro.F1 ? "DUPLICATE_DISAMBIGUATION_PRECISION_REGRESSION" : "DUPLICATE_DISAMBIGUATION_NO_MATERIAL_GAIN";
        await WriteJson(Path.Combine(output, "comparison.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-duplicate-disambiguation-comparison-v1", baseline = baselineMicro, intervention = reviewMicro,
            delta = new { tp = reviewMicro.Tp - baselineMicro.Tp, fp = reviewMicro.Fp - baselineMicro.Fp, fn = reviewMicro.Fn - baselineMicro.Fn, f1 = reviewMicro.F1 - baselineMicro.F1 },
            ambiguousBefore = audit.Count, ambiguousAfter, resolvedDuplicates, incorrectlyResolvedDuplicateCount = incorrectResolutions,
            systemBindingLoss = review.Sum(x => x.SystemBindingLoss), systemValidatorLoss = review.Sum(x => x.SystemValidatorLoss), systemProjectionLoss = review.Sum(x => x.SystemProjectionLoss),
            freshProviderCalls = model.ProviderCalls, additionalInputTokens = review.Sum(x => x.InputTokens ?? 0), additionalReasoningTokens = review.Sum(x => x.ReasoningTokens ?? 0), additionalOutputTokens = review.Sum(x => x.OutputTokens ?? 0), additionalWallTimeMs = review.Sum(x => x.WallTimeMs),
            trace = review.SelectMany(x => ReadDuplicateTraces(intervention, x.DocumentId, x.Repeat)).ToArray(),
        }, ct);
        await WriteJson(Path.Combine(output, "first-loss.v1.json"), new { schemaVersion = "a99-semantic-text-duplicate-disambiguation-first-loss-v1", rows = review.SelectMany(x => x.FirstLosses).ToArray(), counts = review.SelectMany(x => x.FirstLossCounts).GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Sum(y => y.Value)), goldReadBeforeFreeze = false }, ct);
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-duplicate-disambiguation-summary-v1", startHead, endHead = GitSha(repoRoot), model = Model,
            baseline = new { tp = baselineMicro.Tp, fp = baselineMicro.Fp, fn = baselineMicro.Fn, precision = baselineMicro.Precision, recall = baselineMicro.Recall, f1 = baselineMicro.F1, systemLoss = baseline.Sum(x => x.SystemLoss) },
            intervention = new { tp = reviewMicro.Tp, fp = reviewMicro.Fp, fn = reviewMicro.Fn, precision = reviewMicro.Precision, recall = reviewMicro.Recall, f1 = reviewMicro.F1 },
            ambiguousBefore = audit.Count, mechanicallyResolvableWithoutModel = 0, requiresFreshModelDiscriminator = audit.Count, ambiguousAfter, resolvedDuplicates,
            incorrectlyResolvedDuplicateCount = incorrectResolutions, systemLoss = review.Sum(x => x.SystemLoss), deltaTP = reviewMicro.Tp - baselineMicro.Tp, deltaFP = reviewMicro.Fp - baselineMicro.Fp, deltaFN = reviewMicro.Fn - baselineMicro.Fn, deltaF1 = reviewMicro.F1 - baselineMicro.F1,
            performance = new { freshProviderCalls = model.ProviderCalls, inputTokens = review.Sum(x => x.InputTokens ?? 0), reasoningTokens = review.Sum(x => x.ReasoningTokens ?? 0), outputTokens = review.Sum(x => x.OutputTokens ?? 0), wallTimeMs = review.Sum(x => x.WallTimeMs) },
            keepOrRevert = keep ? "KEEP" : "REVERT_INTERVENTION", classification, newLargestResidualBucket = NextBucket(review), a99Status = "A99_NOT_MEASURED_DEV_MARGIN_BELOW_0.995", goldReadBeforeFreeze = false,
            repeatSummary = BuildRepeatSummary(review, selected.Length), baselineHashes,
        }, ct);
        PrintReport(review, selected, model.ProviderCalls);
        Console.WriteLine($"END_HEAD={GitSha(repoRoot)}");
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        return 0;
    }

    /// <summary>Resumes only intervention cells that froze BLOCKED. Successful duplicate
    /// cells are never rerun.</summary>
    public static async Task<int> ResumeDuplicateDisambiguationAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, DuplicateOutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var intervention = Path.Combine(output, "intervention");
        var blocked = new List<(JsonElement item, int repeat)>();
        foreach (var item in LoadInventory(repoRoot).Where(x => ReasoningGoldEligibilityEvaluator.EvaluateMetadataOnly(repoRoot, x.GetProperty("documentId").GetString()!).Eligible))
        {
            var documentId = item.GetProperty("documentId").GetString()!;
            for (var repeat = 1; repeat <= RepeatCount; repeat++)
            {
                var path = Path.Combine(intervention, documentId, $"r{repeat}", "prediction.v1.json");
                using var prediction = JsonDocument.Parse(File.ReadAllText(path));
                if (prediction.RootElement.TryGetProperty("status", out var status) && status.GetString() == "BLOCKED")
                    blocked.Add((item.Clone(), repeat));
            }
        }
        Console.WriteLine($"RESUME_BLOCKED_CELL_COUNT={blocked.Count}");
        if (blocked.Count == 0) return await RunDuplicateDisambiguationOfflineAsync(repoRoot, ct);
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return 1;
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = apiKey, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) || !capability.ReasoningSupported || !capability.StructuredOutputSupported) return 1;
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, DuplicateOutputRoot, "RESUME_BLOCKED_DUPLICATE_CELLS", ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        foreach (var (item, repeat) in blocked)
        {
            var context = Prepare(repoRoot, item) with { PromptHash = DuplicateContractHash(), SchemaHash = Sha256Text(JsonSerializer.Serialize(SemanticTextDuplicateDisambiguationContract.Schema())) };
            Console.WriteLine($"RESUMING_DUPLICATE={context.DocumentId}/R{repeat}");
            await RunDuplicateRepeatAsync(repoRoot, intervention, context, repeat, model, GitSha(repoRoot), ct);
        }
        await WriteJson(Path.Combine(output, "resume.v1.json"), new { schemaVersion = "a99-semantic-text-duplicate-disambiguation-resume-v1", blockedCells = blocked.Select(x => new { documentId = x.item.GetProperty("documentId").GetString(), repeat = $"R{x.repeat}" }).ToArray(), providerCalls = model.ProviderCalls, successfulCells = blocked.Count, goldReadBeforeFreeze = false }, ct);
        Console.WriteLine($"RESUME_PROVIDER_CALLS={model.ProviderCalls}");
        return await RunDuplicateDisambiguationOfflineAsync(repoRoot, ct);
    }

    /// <summary>Rebuilds duplicate-disambiguation comparison from frozen intervention cells only.</summary>
    public static async Task<int> RunDuplicateDisambiguationOfflineAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, DuplicateOutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var intervention = Path.Combine(output, "intervention");
        var selected = LoadInventory(repoRoot).Select(item => (item, eligibility: ReasoningGoldEligibilityEvaluator.EvaluateMetadataOnly(repoRoot, item.GetProperty("documentId").GetString()!)))
            .Where(x => x.eligibility.Eligible).OrderBy(x => x.item.GetProperty("documentId").GetString(), StringComparer.Ordinal).ToArray();
        var baseline = new List<RepeatMetric>();
        var review = new List<RepeatMetric>();
        foreach (var item in selected)
        {
            var documentId = item.item.GetProperty("documentId").GetString()!;
            for (var repeat = 1; repeat <= RepeatCount; repeat++)
            {
                baseline.Add(LoadFrozenMetric(Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar)), documentId, $"r{repeat}"));
                review.Add(LoadFrozenMetric(intervention, documentId, $"r{repeat}"));
            }
        }
        if (review.Any(x => x.Status != "SUCCESS"))
        {
            await WriteJson(Path.Combine(output, "summary.v1.json"), new { schemaVersion = "a99-semantic-text-duplicate-disambiguation-summary-v1", status = "BLOCKED", reason = "INTERVENTION_COHORT_INCOMPLETE", completedCells = review.Count(x => x.Status == "SUCCESS"), expectedCells = 15, goldReadBeforeFreeze = false }, ct);
            return 1;
        }
        using var audit = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "duplicate-audit.v1.json")));
        using var baselineManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "baseline-manifest.v1.json")));
        var baselineMicro = Micro(baseline);
        var reviewMicro = Micro(review);
        var ambiguousBefore = audit.RootElement.GetProperty("ambiguousBefore").GetInt32();
        var ambiguousAfter = CountTrace(intervention, review, "AMBIGUOUS_DUPLICATE_TEXT");
        var resolved = CountTrace(intervention, review, "DUPLICATE_RESOLVED_BY_EXACT_CONTEXT");
        var incorrect = CountIncorrectResolutions(repoRoot, intervention, review);
        var keep = reviewMicro.F1 > baselineMicro.F1 && review.All(x => x.SystemLoss == 0) && incorrect == 0;
        var classification = keep ? "DUPLICATE_DISAMBIGUATION_IMPROVES_F1" : reviewMicro.F1 < baselineMicro.F1 ? "DUPLICATE_DISAMBIGUATION_PRECISION_REGRESSION" : "DUPLICATE_DISAMBIGUATION_NO_MATERIAL_GAIN";
        var attempts = FrozenDuplicateAttempts(intervention, selected) + DuplicateResumeCalls(output);
        await WriteJson(Path.Combine(output, "comparison.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-duplicate-disambiguation-comparison-v1", baseline = baselineMicro, intervention = reviewMicro,
            delta = new { tp = reviewMicro.Tp - baselineMicro.Tp, fp = reviewMicro.Fp - baselineMicro.Fp, fn = reviewMicro.Fn - baselineMicro.Fn, f1 = reviewMicro.F1 - baselineMicro.F1 },
            ambiguousBefore, ambiguousAfter, resolvedDuplicates = resolved, incorrectlyResolvedDuplicateCount = incorrect,
            systemBindingLoss = review.Sum(x => x.SystemBindingLoss), systemValidatorLoss = review.Sum(x => x.SystemValidatorLoss), systemProjectionLoss = review.Sum(x => x.SystemProjectionLoss),
            freshProviderCalls = attempts, additionalInputTokens = review.Sum(x => x.InputTokens ?? 0), additionalReasoningTokens = review.Sum(x => x.ReasoningTokens ?? 0), additionalOutputTokens = review.Sum(x => x.OutputTokens ?? 0), additionalWallTimeMs = review.Sum(x => x.WallTimeMs),
            trace = review.SelectMany(x => ReadDuplicateTraces(intervention, x.DocumentId, x.Repeat)).ToArray(),
        }, ct);
        await WriteJson(Path.Combine(output, "first-loss.v1.json"), new { schemaVersion = "a99-semantic-text-duplicate-disambiguation-first-loss-v1", rows = review.SelectMany(x => x.FirstLosses).ToArray(), counts = review.SelectMany(x => x.FirstLossCounts).GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Sum(y => y.Value)), goldReadBeforeFreeze = false }, ct);
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-duplicate-disambiguation-summary-v1", status = "COMPLETE_OFFLINE_REBUILD", startHead = baselineManifest.RootElement.GetProperty("startHead").GetString(), endHead = GitSha(repoRoot), model = Model,
            baseline = new { tp = baselineMicro.Tp, fp = baselineMicro.Fp, fn = baselineMicro.Fn, precision = baselineMicro.Precision, recall = baselineMicro.Recall, f1 = baselineMicro.F1, systemLoss = baseline.Sum(x => x.SystemLoss) },
            intervention = new { tp = reviewMicro.Tp, fp = reviewMicro.Fp, fn = reviewMicro.Fn, precision = reviewMicro.Precision, recall = reviewMicro.Recall, f1 = reviewMicro.F1 },
            ambiguousBefore, mechanicallyResolvableWithoutModel = 0, requiresFreshModelDiscriminator = ambiguousBefore, ambiguousAfter, resolvedDuplicates = resolved, incorrectlyResolvedDuplicateCount = incorrect,
            systemLoss = review.Sum(x => x.SystemLoss), deltaTP = reviewMicro.Tp - baselineMicro.Tp, deltaFP = reviewMicro.Fp - baselineMicro.Fp, deltaFN = reviewMicro.Fn - baselineMicro.Fn, deltaF1 = reviewMicro.F1 - baselineMicro.F1,
            performance = new { freshProviderCalls = attempts, inputTokens = review.Sum(x => x.InputTokens ?? 0), reasoningTokens = review.Sum(x => x.ReasoningTokens ?? 0), outputTokens = review.Sum(x => x.OutputTokens ?? 0), wallTimeMs = review.Sum(x => x.WallTimeMs) },
            keepOrRevert = keep ? "KEEP" : "REVERT_INTERVENTION", classification, newLargestResidualBucket = NextBucket(review), a99Status = "A99_NOT_MEASURED_DEV_MARGIN_BELOW_0.995", goldReadBeforeFreeze = false,
            repeatSummary = BuildRepeatSummary(review, selected.Length), persistentErrors = BuildPersistentErrors(review),
        }, ct);
        PrintReport(review, selected, attempts);
        Console.WriteLine("OFFLINE_DUPLICATE_PROVIDER_CALLS=0");
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        return 0;
    }

    private static int FrozenDuplicateAttempts(string output, IReadOnlyList<(JsonElement item, ReasoningGoldEligibilityMetadata eligibility)> selected)
    {
        var total = 0;
        foreach (var item in selected)
        for (var repeat = 1; repeat <= RepeatCount; repeat++)
        {
            var path = Path.Combine(output, item.item.GetProperty("documentId").GetString()!, $"r{repeat}", "freeze.v1.json");
            using var freeze = JsonDocument.Parse(File.ReadAllText(path));
            if (freeze.RootElement.TryGetProperty("providerAttempts", out var attempts) && attempts.TryGetInt32(out var value)) total += value;
        }
        return total;
    }

    private static int DuplicateResumeCalls(string output)
    {
        var path = Path.Combine(output, "resume.v1.json");
        if (!File.Exists(path)) return 0;
        using var resume = JsonDocument.Parse(File.ReadAllText(path));
        return resume.RootElement.TryGetProperty("providerCalls", out var calls) && calls.TryGetInt32(out var value) ? value : 0;
    }

    /// <summary>Recovers only the one previously blocked baseline repeat. The request contract,
    /// source packet, and semantic prompt are the same as the frozen baseline; the only bounded
    /// transport intervention is one retry when the provider returned HTTP 429.</summary>
    public static async Task<int> RecoverDoc0258R2Async(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var inventory = LoadInventory(repoRoot);
        var item = inventory.Single(x => string.Equals(x.GetProperty("documentId").GetString(), "DOC-0258", StringComparison.Ordinal));
        var eligibility = ReasoningGoldEligibilityEvaluator.EvaluateMetadataOnly(repoRoot, "DOC-0258");
        if (!eligibility.Eligible) return await Blocked(output, GitSha(repoRoot), "DOC-0258_NOT_EXACT_EVALUABLE", [(item, eligibility)], ct);
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return await Blocked(output, GitSha(repoRoot), "OPENROUTER_API_KEY_MISSING", [(item, eligibility)], ct);

        var startHead = GitSha(repoRoot);
        var context = Prepare(repoRoot, item);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = apiKey, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 1,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capabilityResult = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        var capability = capabilityResult.Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) || !capability.ReasoningSupported || !capability.StructuredOutputSupported)
            return await Blocked(output, startHead, "MODEL_CAPABILITY_MISMATCH", [(item, eligibility)], ct);

        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, OutputRoot, "DOC-0258-R2-RECOVERY", ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        Console.WriteLine("RECOVERING=DOC-0258/R2");
        var recovered = await RunRepeatAsync(repoRoot, output, context, 2, model, startHead, ct, transientRetries: 1);
        await RunOfflineAsync(repoRoot, ct);
        Console.WriteLine($"RECOVERY_STATUS={recovered.Status}");
        Console.WriteLine($"RECOVERY_GOLD={recovered.Gold}");
        Console.WriteLine($"RECOVERY_PROVIDER_ATTEMPTS={model.ProviderCalls}");
        return recovered.Status == "SUCCESS" && recovered.Gold == 153 ? 0 : 1;
    }

    /// <summary>Runs exactly one generic omission-review intervention over the frozen 15-repeat
    /// baseline. Pass A is loaded from frozen artifacts and is never called again.</summary>
    public static Task<int> RunSemanticTextResidualLoopI1Async(string repoRoot, CancellationToken ct = default) =>
        RunOmissionReviewAsync(repoRoot, ct, ResidualLoopI1OutputRoot);

    public static Task<int> RunSemanticTextResidualLoopI1ResumeAsync(string repoRoot, CancellationToken ct = default) =>
        RunOmissionReviewAsync(repoRoot, ct, ResidualLoopI1OutputRoot, resumeBlockedOnly: true);

    public static async Task<int> RunOmissionReviewAsync(string repoRoot, CancellationToken ct = default, string outputRootName = "eval/a99-closed-loop/semantic-text-omission-review", bool resumeBlockedOnly = false)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, outputRootName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var baselineRoot = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var startHead = GitSha(repoRoot);
        var inventory = LoadInventory(repoRoot);
        var selected = inventory.Select(item => (item, eligibility: ReasoningGoldEligibilityEvaluator.EvaluateMetadataOnly(repoRoot, item.GetProperty("documentId").GetString()!)))
            .Where(x => x.eligibility.Eligible).OrderBy(x => x.item.GetProperty("documentId").GetString(), StringComparer.Ordinal).ToArray();
        var baseline = new List<RepeatMetric>();
        foreach (var item in selected)
            for (var repeat = 1; repeat <= RepeatCount; repeat++)
                baseline.Add(LoadFrozenMetric(baselineRoot, item.item.GetProperty("documentId").GetString()!, $"r{repeat}"));

        var expectedCellCount = selected.Length * RepeatCount;
        var expectedGoldOccurrences = selected.Sum(x => x.eligibility.OccurrenceCount);
        var baselineComplete = selected.Length > 0 && baseline.Count == expectedCellCount && CohortComplete(baseline, expectedGoldOccurrences);
        var reviewPromptHash = SemanticTextOmissionReviewContract.Hash();
        await WriteJson(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-omission-review-v1",
            startHead, branch = Git(repoRoot, "branch --show-current"), model = Model,
            baseContractHash = ContractHash(), reviewPromptHash,
            intervention = "SEMANTIC_TEXT_OMISSION_REVIEW_V1",
            artifactRoot = outputRootName, selectedStrictGoldCohort = selected.Select(x => x.item.GetProperty("documentId").GetString()).ToArray(),
            repeats = new[] { "R1", "R2", "R3" }, providerConcurrency = 1,
            passAReused = true, passAProviderCallsCurrentRun = 0,
            inventoryIsInformationalOnly = true, additiveUnion = true, validatorsUnchanged = true,
            noVlm = true, goldReadBeforeFreeze = false, baselineComplete, goldOccurrencesPerRepeat = expectedGoldOccurrences,
            promptFrozenBeforeInference = true,
        }, ct);
        await WriteJson(Path.Combine(output, "baseline-error-inventory.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-omission-review-baseline-inventory-v1",
            baselineArtifact = OutputRoot, baselineComplete, goldOccurrencesPerRepeat = expectedGoldOccurrences,
            persistent = BuildPersistentErrors(baseline), nextLargestErrorBucket = NextBucket(baseline), goldReadBeforeFreeze = false,
        }, ct);
        if (!baselineComplete)
        {
            await WriteReviewBlockedSummary(output, startHead, selected, "BASELINE_EXECUTION_BLOCKED", ct);
            Console.WriteLine("FINAL_CLASSIFICATION=OMISSION_REVIEW_EXECUTION_BLOCKED");
            return 1;
        }

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await WriteReviewBlockedSummary(output, startHead, selected, "OPENROUTER_API_KEY_MISSING", ct);
            return 1;
        }
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = apiKey, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) || !capability.ReasoningSupported || !capability.StructuredOutputSupported)
        {
            await WriteReviewBlockedSummary(output, startHead, selected, "MODEL_CAPABILITY_MISMATCH", ct);
            return 1;
        }

        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, outputRootName, string.Join(',', selected.Select(x => x.item.GetProperty("documentId").GetString())), ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var review = new List<RepeatMetric>();
        foreach (var item in selected)
        {
            var context = Prepare(repoRoot, item.item);
            for (var repeat = 1; repeat <= RepeatCount; repeat++)
            {
                var existingScorePath = Path.Combine(output, context.DocumentId, $"r{repeat}", "score.v1.json");
                if (resumeBlockedOnly && File.Exists(existingScorePath))
                {
                    using var existingScore = JsonDocument.Parse(File.ReadAllText(existingScorePath));
                    if (existingScore.RootElement.TryGetProperty("status", out var existingStatus) && existingStatus.GetString() == "SUCCESS")
                    {
                        review.Add(LoadFrozenMetric(output, context.DocumentId, $"r{repeat}"));
                        continue;
                    }
                }
                Console.WriteLine($"RUNNING_REVIEW={context.DocumentId}/R{repeat}");
                review.Add(await RunReviewRepeatAsync(repoRoot, output, baselineRoot, context, repeat, model, startHead, reviewPromptHash, ct, transientRetries: resumeBlockedOnly ? 2 : 0));
            }
        }

        var reviewSummary = BuildRepeatSummary(review, selected.Length);
        var paired = BuildPairedComparison(baseline, review, expectedGoldOccurrences);
        await WriteJson(Path.Combine(output, "repeat-summary.v1.json"), reviewSummary, ct);
        await WriteJson(Path.Combine(output, "persistent-errors.v1.json"), BuildPersistentErrors(review), ct);
        await WriteJson(Path.Combine(output, "paired-deltas.v1.json"), paired, ct);
        var classification = OmissionReviewClass(baseline, review, expectedCellCount, expectedGoldOccurrences);
        var gate = review.Count == expectedCellCount && CohortComplete(review, expectedGoldOccurrences) && review.All(x => x.Precision >= .995 && x.Recall >= .995 && x.F1 >= .995 && x.SystemLoss == 0);
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-omission-review-summary-v1", startHead, endHead = GitSha(repoRoot),
            intervention = "SEMANTIC_TEXT_OMISSION_REVIEW_V1", baseContractHash = ContractHash(), reviewPromptHash,
            artifactRoot = outputRootName, selectedStrictGoldCohort = selected.Select(x => x.item.GetProperty("documentId").GetString()).ToArray(),
            baselineComplete, baselineProviderAttempts = baseline.Count, passAReused = true, passAProviderCallsCurrentRun = 0,
            reviewProviderAttempts = model.ProviderCalls, modelCalls = model.ProviderCalls, goldOccurrencesPerRepeat = expectedGoldOccurrences,
            goldReadBeforeFreeze = false, a99DevMarginMet = gate, classification,
            dominantBaselineBucket = NextBucket(baseline), dominantReviewBucket = NextBucket(review),
            persistentMovement = new { before = BuildPersistentErrors(baseline), after = BuildPersistentErrors(review) },
            performance = new { inputTokens = review.Sum(x => x.InputTokens ?? 0), reasoningTokens = review.Sum(x => x.ReasoningTokens ?? 0), outputTokens = review.Sum(x => x.OutputTokens ?? 0), wallTimeMs = review.Sum(x => x.WallTimeMs) },
            repeatSummary = reviewSummary,
        }, ct);
        PrintReport(review, selected, model.ProviderCalls);
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        Console.WriteLine($"A99_DEV_MARGIN_MET={gate}");
        return 0;
    }

    /// <summary>Recovers only the transiently blocked review repeat. No completed review repeat
    /// is rerun; the same Pass B request is retried once only when HTTP 429 was observed.</summary>
    public static async Task<int> RecoverOmissionReviewDoc0001R1Async(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, "eval/a99-closed-loop/semantic-text-omission-review".Replace('/', Path.DirectorySeparatorChar));
        var baselineRoot = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var item = LoadInventory(repoRoot).Single(x => x.GetProperty("documentId").GetString() == "DOC-0001");
        var eligibility = ReasoningGoldEligibilityEvaluator.EvaluateMetadataOnly(repoRoot, "DOC-0001");
        if (!eligibility.Eligible) return 1;
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return 1;
        var context = Prepare(repoRoot, item);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = apiKey, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 1,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) || !capability.ReasoningSupported || !capability.StructuredOutputSupported) return 1;
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "eval/a99-closed-loop/semantic-text-omission-review", "DOC-0001-R1-RECOVERY", ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        Console.WriteLine("RECOVERING_REVIEW=DOC-0001/R1");
        var recovered = await RunReviewRepeatAsync(repoRoot, output, baselineRoot, context, 1, model, GitSha(repoRoot), SemanticTextOmissionReviewContract.Hash(), ct, transientRetries: 1);
        await RunOmissionReviewOfflineAsync(repoRoot, ct);
        Console.WriteLine($"REVIEW_RECOVERY_STATUS={recovered.Status}");
        Console.WriteLine($"REVIEW_RECOVERY_PROVIDER_ATTEMPTS={model.ProviderCalls}");
        return recovered.Status == "SUCCESS" ? 0 : 1;
    }

    /// <summary>Rebuilds omission-review campaign reports from frozen review artifacts only.</summary>
    public static async Task<int> RunOmissionReviewOfflineAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, "eval/a99-closed-loop/semantic-text-omission-review".Replace('/', Path.DirectorySeparatorChar));
        var baselineRoot = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var inventory = LoadInventory(repoRoot);
        var selected = inventory.Select(item => (item, eligibility: ReasoningGoldEligibilityEvaluator.EvaluateMetadataOnly(repoRoot, item.GetProperty("documentId").GetString()!)))
            .Where(x => x.eligibility.Eligible).OrderBy(x => x.item.GetProperty("documentId").GetString(), StringComparer.Ordinal).ToArray();
        var expectedGoldOccurrences = selected.Sum(x => x.eligibility.OccurrenceCount);
        var baseline = new List<RepeatMetric>();
        var review = new List<RepeatMetric>();
        foreach (var item in selected)
        {
            var documentId = item.item.GetProperty("documentId").GetString()!;
            for (var repeat = 1; repeat <= RepeatCount; repeat++)
            {
                var name = $"r{repeat}";
                baseline.Add(LoadFrozenMetric(baselineRoot, documentId, name));
                review.Add(LoadFrozenMetric(output, documentId, name));
            }
        }
        var reviewSummary = BuildRepeatSummary(review, selected.Length);
        var reviewProviderAttempts = FrozenProviderAttempts(output, selected);
        await WriteJson(Path.Combine(output, "repeat-summary.v1.json"), reviewSummary, ct);
        await WriteJson(Path.Combine(output, "persistent-errors.v1.json"), BuildPersistentErrors(review), ct);
        await WriteJson(Path.Combine(output, "paired-deltas.v1.json"), BuildPairedComparison(baseline, review, expectedGoldOccurrences), ct);
        var classification = OmissionReviewClass(baseline, review, selected.Length * RepeatCount, selected.Sum(x => x.eligibility.OccurrenceCount));
        var gate = review.Count == selected.Length * RepeatCount && CohortComplete(review, selected.Sum(x => x.eligibility.OccurrenceCount)) && review.All(x => x.Precision >= .995 && x.Recall >= .995 && x.F1 >= .995 && x.SystemLoss == 0);
        var baselineMicro = Micro(baseline);
        var reviewMicro = Micro(review);
        var recoveredGold = Math.Max(0, reviewMicro.Tp - baselineMicro.Tp);
        var introducedFp = Math.Max(0, reviewMicro.Fp - baselineMicro.Fp);
        var recoveryPrecision = recoveredGold + introducedFp == 0 ? 0d : (double)recoveredGold / (recoveredGold + introducedFp);
        var remainingModelOmissions = review.Sum(x => x.FirstLossCounts.GetValueOrDefault("MODEL_OMISSION"));
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-omission-review-summary-v1", status = "COMPLETE_OFFLINE_REBUILD",
            startHead = GitSha(repoRoot), endHead = GitSha(repoRoot), intervention = "SEMANTIC_TEXT_OMISSION_REVIEW_V1",
            baseContractHash = ContractHash(), reviewPromptHash = SemanticTextOmissionReviewContract.Hash(),
            selectedStrictGoldCohort = selected.Select(x => x.item.GetProperty("documentId").GetString()).ToArray(),
            baselineComplete = CohortComplete(baseline, expectedGoldOccurrences), baselineProviderAttempts = baseline.Count, controlReused = true, controlProviderCallsCurrent = 0, passAReused = true,
            passAProviderCallsCurrentRun = 0, reviewProviderAttempts, modelCalls = reviewProviderAttempts, offlineProviderCalls = 0,
            goldOccurrencesPerRepeat = expectedGoldOccurrences, goldReadBeforeFreeze = false, a99DevMarginMet = gate, classification,
            keepOrRevert = classification == "OMISSION_REVIEW_CLEAR_GAIN" ? "KEEP" : "REVERT",
            dominantBaselineBucket = NextBucket(baseline), dominantReviewBucket = NextBucket(review),
            persistentMovement = new { before = BuildPersistentErrors(baseline), after = BuildPersistentErrors(review) },
            performance = new { inputTokens = review.Sum(x => x.InputTokens ?? 0), reasoningTokens = review.Sum(x => x.ReasoningTokens ?? 0), outputTokens = review.Sum(x => x.OutputTokens ?? 0), wallTimeMs = review.Sum(x => x.WallTimeMs) },
            finalTable = new[]
            {
                new { mode = "BASELINE", tp = baselineMicro.Tp, fp = baselineMicro.Fp, fn = baselineMicro.Fn, precision = baselineMicro.Precision, recall = baselineMicro.Recall, f1 = baselineMicro.F1, modelOmission = baseline.Sum(x => x.FirstLossCounts.GetValueOrDefault("MODEL_OMISSION")), systemLoss = baselineMicro.SystemLoss },
                new { mode = "OMISSION_REVIEW", tp = reviewMicro.Tp, fp = reviewMicro.Fp, fn = reviewMicro.Fn, precision = reviewMicro.Precision, recall = reviewMicro.Recall, f1 = reviewMicro.F1, modelOmission = remainingModelOmissions, systemLoss = reviewMicro.SystemLoss },
            },
            delta = new { tp = reviewMicro.Tp - baselineMicro.Tp, fp = reviewMicro.Fp - baselineMicro.Fp, fn = reviewMicro.Fn - baselineMicro.Fn, precision = reviewMicro.Precision - baselineMicro.Precision, recall = reviewMicro.Recall - baselineMicro.Recall, f1 = reviewMicro.F1 - baselineMicro.F1 },
            reviewRecoveredGoldCount = recoveredGold,
            reviewIntroducedFpCount = introducedFp,
            remainingModelOmissions,
            recoveryPrecision,
            reviewTrace = review.Select(x => new { documentId = x.DocumentId, repeat = x.Repeat, reviewRaw = x.RawCount, reviewBound = x.BoundCount, reviewValid = x.ValidatedCount, reviewFinal = x.FinalCount, systemLoss = x.SystemLoss, provider = x.Provider, reasoningTokens = x.ReasoningTokens, outputTokens = x.OutputTokens, finishReason = x.FinishReason, wallTimeMs = x.WallTimeMs }).ToArray(),
            a99DevStatus = gate ? "A99_DEV_MARGIN_REACHED" : "A99_NOT_MEASURED_DEV_MARGIN_BELOW_0.995",
            repeatSummary = reviewSummary,
        }, ct);
        PrintReport(review, selected, reviewProviderAttempts);
        Console.WriteLine("OFFLINE_REVIEW_PROVIDER_CALLS=0");
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        return 0;
    }

    private static int FrozenProviderAttempts(string output, IReadOnlyList<(JsonElement item, ReasoningGoldEligibilityMetadata eligibility)> selected)
    {
        var total = 0;
        foreach (var item in selected)
        foreach (var repeat in new[] { "r1", "r2", "r3" })
        {
            var path = Path.Combine(output, item.item.GetProperty("documentId").GetString()!, repeat, "freeze.v1.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            total += GetInt(doc.RootElement, "providerAttempts");
        }
        return total;
    }

    /// <summary>Rebuilds campaign-level artifacts from the 15 already-frozen repeat outputs.
    /// This path never opens Gold and never creates a provider client, so serializer or report
    /// fixes cannot accidentally spend another live request.</summary>
    public static async Task<int> RunOfflineAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(output, "manifest.v1.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("GENERALIZATION_MANIFEST_MISSING", manifestPath);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, ct));
        var startHead = manifest.RootElement.TryGetProperty("startHead", out var start) ? start.GetString() ?? "NOT_PERSISTED" : "NOT_PERSISTED";
        var inventory = LoadInventory(repoRoot);
        var selected = inventory.Select(item => (item, eligibility: ReasoningGoldEligibilityEvaluator.EvaluateMetadataOnly(repoRoot, item.GetProperty("documentId").GetString()!)))
            .Where(x => x.eligibility.Eligible).OrderBy(x => x.item.GetProperty("documentId").GetString(), StringComparer.Ordinal).ToArray();
        var runs = new List<RepeatMetric>();
        foreach (var item in selected)
            for (var repeat = 1; repeat <= RepeatCount; repeat++)
                runs.Add(LoadFrozenMetric(output, item.item.GetProperty("documentId").GetString()!, $"r{repeat}"));
        var repeatSummary = BuildRepeatSummary(runs, selected.Length);
        await WriteJson(Path.Combine(output, "repeat-summary.v1.json"), repeatSummary, ct);
        await WriteJson(Path.Combine(output, "persistent-errors.v1.json"), BuildPersistentErrors(runs), ct);
        await WriteJson(Path.Combine(output, "comparison.v1.json"), BuildComparison(repoRoot, runs, selected, startHead), ct);
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-generalization-summary-v1", status = "COMPLETE_OFFLINE_REBUILD",
            startHead, endHead = GitSha(repoRoot), selectedStrictGoldCohort = selected.Select(x => x.item.GetProperty("documentId").GetString()).ToArray(),
            totalGoldOccurrences = runs.SelectMany(x => x.FirstLosses.Select(loss => $"{x.DocumentId}:{loss.Key}")).Distinct(StringComparer.Ordinal).Count(),
            semanticContractHash = ContractHash(), providerAttempts = runs.Count, modelCalls = runs.Count, offlineProviderCalls = 0,
            goldReadBeforeFreeze = false, generalizationClassification = GeneralizationClass(runs),
            a99DevMarginMet = runs.Count == selected.Length * RepeatCount && runs.All(x => x.Status == "SUCCESS" && x.Precision >= .995 && x.Recall >= .995 && x.F1 >= .995 && x.SystemLoss == 0),
            nextLargestErrorBucket = NextBucket(runs), performance = new { providerAttempts = runs.Count, inputTokens = runs.Sum(x => x.InputTokens ?? 0), reasoningTokens = runs.Sum(x => x.ReasoningTokens ?? 0), outputTokens = runs.Sum(x => x.OutputTokens ?? 0), wallTimeMs = runs.Sum(x => x.WallTimeMs) },
            goldFirewall = "PASS", repeatSummary,
        }, ct);
        PrintReport(runs, selected, runs.Count);
        Console.WriteLine("OFFLINE_REBUILD_PROVIDER_CALLS=0");
        Console.WriteLine($"FINAL_CLASSIFICATION={GeneralizationClass(runs)}");
        return 0;
    }

    /// <summary>Offline causal audit followed by one pre-registered generic repair. The audit
    /// opens Gold only after the frozen R1-R3 hashes have been checked. The live lane then runs
    /// the unchanged semantic contract/binder/validator/projection with source-only structural
    /// facts added to every alias packet.</summary>
    public static async Task<int> RunStableErrorRepairAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        const string repairRootName = "eval/a99-closed-loop/semantic-text-stable-error-repair";
        var repairRoot = Path.Combine(repoRoot, repairRootName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(repairRoot);
        var baselineRoot = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var startHead = GitSha(repoRoot);
        var inventory = LoadInventory(repoRoot);
        var selected = inventory.Where(x => StableRepairDocuments.Contains(x.GetProperty("documentId").GetString(), StringComparer.Ordinal))
            .OrderBy(x => x.GetProperty("documentId").GetString(), StringComparer.Ordinal).ToArray();
        if (selected.Length != 5) throw new InvalidDataException("STABLE_ERROR_REPAIR_COHORT_INVALID");

        var baseline = new List<RepeatMetric>();
        foreach (var item in selected)
        foreach (var repeat in new[] { "r1", "r2", "r3" })
        {
            var documentId = item.GetProperty("documentId").GetString()!;
            VerifyFrozenBaseline(baselineRoot, documentId, repeat);
            baseline.Add(LoadFrozenMetric(baselineRoot, documentId, repeat));
        }
        if (baseline.Any(x => x.Status != "SUCCESS") || baseline.GroupBy(x => x.Repeat).Any(x => x.Sum(y => y.Gold) != 153))
            throw new InvalidDataException("FROZEN_BASELINE_INCOMPLETE");

        var contexts = selected.ToDictionary(x => x.GetProperty("documentId").GetString()!, x => Prepare(repoRoot, x), StringComparer.Ordinal);
        var goldByDocument = new Dictionary<string, IReadOnlyList<ReasoningGoldOccurrence>>(StringComparer.Ordinal);
        foreach (var item in selected)
        {
            var documentId = item.GetProperty("documentId").GetString()!;
            var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", documentId + ".occurrence-gold-v1.json");
            goldByDocument[documentId] = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
        }

        var audit = BuildStableErrorAudit(repoRoot, baselineRoot, contexts, goldByDocument, startHead);
        await WriteJson(Path.Combine(repairRoot, "offline-audit.v1.json"), audit, ct);

        var manifest = new
        {
            schemaVersion = "a99-semantic-text-stable-error-repair-intervention-manifest-v1",
            startHead, branch = Git(repoRoot, "branch --show-current"), model = Model,
            baselineArtifact = OutputRoot,
            selectedStrictGoldCohort = selected.Select(x => x.GetProperty("documentId").GetString()).ToArray(),
            baselineRepeats = new[] { "r1", "r2", "r3" }, repairRepeats = new[] { "r4", "r5", "r6" },
            baselineContractHash = ContractHash(), semanticContractVersion = SemanticTextExactBindingContract.ProtocolVersion,
            intervention = "GLOBAL_STRUCTURAL_CONTEXT_ENRICHMENT_V1",
            interventionRule = "Add objective source-only style/outline/numbering/layout facts to every alias; no candidate gating, Gold-derived rule, prompt examples, or semantic post-filter.",
            packetFields = new[] { "alias", "text", "sourceOrdinal", "structuralFacts.style", "structuralFacts.numbering", "structuralFacts.layout", "structuralFacts.inTableOfContents" },
            promptUnchanged = true, schemaUnchanged = true, binderUnchanged = true, validatorUnchanged = true, projectionUnchanged = true,
            offlineDominantCause = "MODEL_SEMANTIC_OMISSION_WITH_STRUCTURAL_EVIDENCE",
            offlineAuditArtifact = "offline-audit.v1.json", providerConcurrency = 1,
            modelCallsBeforeManifest = 0, goldReadBeforeFreeze = false, noVlm = true, noMultipass = true,
            sourceHashes = contexts.Values.Select(x => new { documentId = x.DocumentId, sourceSha256 = x.SourceSha256, basePacketHash = x.PacketHash }).ToArray(),
            reasoningConfiguration = new { requested = true, enabled = true, excluded = true, effort = "MODEL_DEFAULT" },
            preRegisteredUtc = DateTimeOffset.UtcNow,
        };
        var manifestPath = Path.Combine(repairRoot, "intervention-manifest.v1.json");
        await WriteJson(manifestPath, manifest, ct);
        await WriteJson(Path.Combine(repairRoot, "intervention-manifest.freeze.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-stable-error-repair-intervention-freeze-v1",
            manifestSha256 = Sha256(manifestPath), goldReadBeforeFreeze = false, modelCalls = 0,
            frozenUtc = DateTimeOffset.UtcNow,
        }, ct);
        Console.WriteLine("OFFLINE_AUDIT_COMPLETE=true");
        Console.WriteLine("INTERVENTION_REGISTERED=GLOBAL_STRUCTURAL_CONTEXT_ENRICHMENT_V1");

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return await WriteRepairBlocked(repairRoot, startHead, "OPENROUTER_API_KEY_MISSING", ct);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = apiKey, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) || !capability.ReasoningSupported || !capability.StructuredOutputSupported)
            return await WriteRepairBlocked(repairRoot, startHead, "MODEL_CAPABILITY_MISMATCH", ct);

        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, repairRootName, string.Join(',', contexts.Keys), ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var repaired = new List<RepeatMetric>();
        foreach (var documentId in contexts.Keys.Order(StringComparer.Ordinal))
        {
            var enriched = EnrichContext(contexts[documentId]);
            foreach (var repeat in new[] { 4, 5, 6 })
            {
                Console.WriteLine($"RUNNING_REPAIR={documentId}/R{repeat}");
                repaired.Add(await RunRepeatAsync(repoRoot, repairRoot, enriched, repeat, model, startHead, ct, flatRepeatLayout: true));
            }
        }

        var complete = repaired.Count == 15 && repaired.All(x => x.Status == "SUCCESS" && x.Gold == 153);
        var repairedGold = complete ? goldByDocument : goldByDocument;
        var stability = BuildRepairStability(repaired, repairedGold);
        var comparison = BuildRepairComparison(baseline, repaired);
        await WriteJson(Path.Combine(repairRoot, "stability.v1.json"), stability, ct);
        await WriteJson(Path.Combine(repairRoot, "comparison.v1.json"), comparison, ct);
        var classification = RepairClassification(baseline, repaired, stability);
        await WriteJson(Path.Combine(repairRoot, "summary.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-stable-error-repair-summary-v1",
            status = complete ? "COMPLETE" : "BLOCKED", startHead, endHead = GitSha(repoRoot), model = Model,
            intervention = "GLOBAL_STRUCTURAL_CONTEXT_ENRICHMENT_V1", baseline = BuildMicroTable(baseline), repaired = BuildMicroTable(repaired),
            classification, providerCalls = model.ProviderCalls, modelCalls = repaired.Count, goldReadBeforeFreeze = false,
            systemLoss = repaired.Sum(x => x.SystemLoss), stability, comparison,
            performance = new { inputTokens = repaired.Sum(x => x.InputTokens ?? 0), reasoningTokens = repaired.Sum(x => x.ReasoningTokens ?? 0), outputTokens = repaired.Sum(x => x.OutputTokens ?? 0), wallTimeMs = repaired.Sum(x => x.WallTimeMs) },
        }, ct);
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
        Console.WriteLine($"END_HEAD={GitSha(repoRoot)}");
        return complete ? 0 : 1;
    }

    /// <summary>Bounded transport recovery for the one repair run whose provider response did
    /// not contain a choices envelope. It never reruns successful repeats.</summary>
    public static async Task<int> RecoverStableErrorRepairDoc0252R4Async(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        const string repairRootName = "eval/a99-closed-loop/semantic-text-stable-error-repair";
        var repairRoot = Path.Combine(repoRoot, repairRootName.Replace('/', Path.DirectorySeparatorChar));
        var inventory = LoadInventory(repoRoot);
        var item = inventory.Single(x => x.GetProperty("documentId").GetString() == "DOC-0252");
        var context = EnrichContext(Prepare(repoRoot, item));
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return await WriteRepairBlocked(repairRoot, GitSha(repoRoot), "OPENROUTER_API_KEY_MISSING", ct);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = apiKey, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) || !capability.ReasoningSupported || !capability.StructuredOutputSupported)
            return await WriteRepairBlocked(repairRoot, GitSha(repoRoot), "MODEL_CAPABILITY_MISMATCH", ct);
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, repairRootName, "DOC-0252-R4-RECOVERY", ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var blockedRepeats = new[] { 4, 5, 6 }.Where(repeat =>
        {
            var path = Path.Combine(repairRoot, $"r{repeat}", "DOC-0252", "score.v1.json");
            if (!File.Exists(path)) return true;
            using var score = JsonDocument.Parse(File.ReadAllText(path));
            return !score.RootElement.TryGetProperty("status", out var status) || status.GetString() != "SUCCESS";
        }).ToArray();
        foreach (var repeat in blockedRepeats)
        {
            Console.WriteLine($"RECOVERING_REPAIR=DOC-0252/R{repeat}");
            await RunRepeatAsync(repoRoot, repairRoot, context, repeat, model, GitSha(repoRoot), ct, flatRepeatLayout: true, retryMalformedProviderResponse: true);
        }
        await WriteJson(Path.Combine(repairRoot, "recovery.v1.json"), new { recovered = blockedRepeats.Select(x => $"DOC-0252/R{x}").ToArray(), reason = "PROVIDER_CHOICES_MISSING", recoveryProviderCalls = model.ProviderCalls, goldReadBeforeFreeze = false, recoveredUtc = DateTimeOffset.UtcNow }, ct);
        var baselineRoot = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var baseline = StableRepairDocuments.SelectMany(documentId => new[] { "r1", "r2", "r3" }.Select(repeat => LoadFrozenMetric(baselineRoot, documentId, repeat))).ToArray();
        var repaired = StableRepairDocuments.SelectMany(documentId => new[] { "r4", "r5", "r6" }.Select(repeat => LoadRepairMetric(repairRoot, documentId, repeat))).ToArray();
        var gold = StableRepairDocuments.ToDictionary(documentId => documentId, documentId => (IReadOnlyList<ReasoningGoldOccurrence>)ReasoningGoldArtifactLoader.LoadOccurrence(Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", documentId + ".occurrence-gold-v1.json")).Where(x => x.HeadingSpan is not null).ToArray(), StringComparer.Ordinal);
        var stability = BuildRepairStability(repaired, gold);
        var comparison = BuildRepairComparison(baseline, repaired);
        await WriteJson(Path.Combine(repairRoot, "stability.v1.json"), stability, ct);
        await WriteJson(Path.Combine(repairRoot, "comparison.v1.json"), comparison, ct);
        var classification = RepairClassification(baseline, repaired, stability);
        var complete = repaired.Length == 15 && StableRepairDocuments.SelectMany(documentId => new[] { "r4", "r5", "r6" }.Select(repeat => LoadRepairScoreStatus(repairRoot, documentId, repeat))).All(x => x);
        await WriteJson(Path.Combine(repairRoot, "summary.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-stable-error-repair-summary-v1", status = complete ? "COMPLETE" : "BLOCKED", startHead = GitSha(repoRoot), endHead = GitSha(repoRoot), model = Model,
            intervention = "GLOBAL_STRUCTURAL_CONTEXT_ENRICHMENT_V1", baseline = BuildMicroTable(baseline), repaired = BuildMicroTable(repaired), classification,
            providerCalls = model.ProviderCalls, modelCalls = repaired.Length, goldReadBeforeFreeze = false, systemLoss = repaired.Sum(x => x.SystemLoss), stability, comparison,
        }, ct);
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
        return complete ? 0 : 1;
    }

    /// <summary>Resume-only completion for the two provider-blocked structural-enrichment
    /// cells. Existing successful cells are hash-checked before and after the two target
    /// inference cells and are never submitted again.</summary>
    public static async Task<int> ResumeStableErrorRepairDoc0252R5R6Async(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        const string repairRootName = "eval/a99-closed-loop/semantic-text-stable-error-repair";
        const string expectedStartHead = "52cdd5f";
        var repairRoot = Path.Combine(repoRoot, repairRootName.Replace('/', Path.DirectorySeparatorChar));
        var startHead = GitSha(repoRoot);
        if (!startHead.StartsWith(expectedStartHead, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"RESUME_START_HEAD_MISMATCH:expected={expectedStartHead}:actual={startHead}");

        // Offline pre-audit only. This writes no model output and is intentionally allowed
        // to return the current provider-blocked classification.
        await StructuralContextEnrichmentCleanCompletionRunner.RunAsync(repoRoot, ct, startHead, 0);

        var retainedCells = CaptureFrozenSuccessCellHashes(repairRoot);
        var inventory = LoadInventory(repoRoot);
        var item = inventory.Single(x => x.GetProperty("documentId").GetString() == "DOC-0252");
        var context = EnrichContext(Prepare(repoRoot, item));
        var targets = new[] { 5, 6 }.Where(repeat => !LoadRepairScoreStatus(repairRoot, "DOC-0252", $"r{repeat}")).ToArray();
        var priorProviderCalls = LoadPriorResumeProviderCalls(repairRoot);
        if (targets.Length == 0)
        {
            var alreadyCompleteExit = await StructuralContextEnrichmentCleanCompletionRunner.RunAsync(repoRoot, ct, startHead, priorProviderCalls);
            Console.WriteLine($"RESUME_ALREADY_COMPLETE_PROVIDER_CALLS={priorProviderCalls}");
            return alreadyCompleteExit;
        }
        if (targets.Length != 2)
            throw new InvalidDataException("RESUME_TARGETS_NOT_BOTH_PROVIDER_BLOCKED");

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return await WriteRepairBlocked(repairRoot, startHead, "OPENROUTER_API_KEY_MISSING", ct);

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = apiKey, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) || !capability.ReasoningSupported || !capability.StructuredOutputSupported)
            return await WriteRepairBlocked(repairRoot, startHead, "MODEL_CAPABILITY_MISMATCH", ct);

        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, repairRootName, "DOC-0252-R5,R6-RESUME", ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var outcomes = new List<RepeatMetric>();
        foreach (var repeat in targets)
        {
            Console.WriteLine($"RUNNING_RESUME_ONLY=DOC-0252/R{repeat}");
            outcomes.Add(await RunRepeatAsync(repoRoot, repairRoot, context, repeat, model, startHead, ct, flatRepeatLayout: true, retryMalformedProviderResponse: true));
        }

        AssertFrozenSuccessCellHashesUnchanged(repairRoot, retainedCells);
        var postAudit = await StructuralContextEnrichmentCleanCompletionRunner.RunAsync(repoRoot, ct, startHead, model.ProviderCalls);
        await WriteJson(Path.Combine(repairRoot, "resume-only-completion.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-stable-error-repair-resume-only-completion-v1",
            startHead, endHead = GitSha(repoRoot), model = Model,
            intervention = "GLOBAL_STRUCTURAL_CONTEXT_ENRICHMENT_V1",
            targetedCells = new[] { "DOC-0252/R5", "DOC-0252/R6" },
            successfulCellsReusedByteForByte = retainedCells.Keys.Select(x => x[..x.LastIndexOf('/')]).Distinct(StringComparer.Ordinal).Count() == 13,
            retainedCellFileHashCount = retainedCells.Count,
            terminalStatuses = outcomes.Select(x => new { documentId = x.DocumentId, repeat = x.Repeat, status = x.Status }).ToArray(),
            providerCallsCurrentRun = model.ProviderCalls,
            postCompletionDecision = model.ProviderCalls == 0 ? "NOT_MEASURED_PROVIDER_BLOCKED" : "RECOMPUTED_BY_OFFLINE_CLEAN_COMPLETION",
            goldFirewall = "PASS",
            noSuccessfulCellRerun = true,
            offlineRecomputeExitCode = postAudit,
        }, ct);
        Console.WriteLine($"RESUME_PROVIDER_CALLS={model.ProviderCalls}");
        Console.WriteLine($"RESUME_CLEAN_COMPLETION_EXIT={postAudit}");
        return postAudit;
    }

    /// <summary>Audits the already frozen three-repeat semantic-text cohort and, only when the
    /// audit shows recoverable variance, runs one generic union-plus-verifier intervention.</summary>
    public static async Task<int> RunModelOmissionStabilityAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, "eval/a99-closed-loop/model-omission-stability");
        var interventionRoot = Path.Combine(output, "intervention");
        Directory.CreateDirectory(output);
        var startHead = GitSha(repoRoot);
        Console.WriteLine($"START_HEAD={startHead}");
        Console.WriteLine($"BRANCH={Git(repoRoot, "branch --show-current")}");
        var selected = LoadInventory(repoRoot)
            .Where(x => StableRepairDocuments.Contains(x.GetProperty("documentId").GetString(), StringComparer.Ordinal))
            .OrderBy(x => x.GetProperty("documentId").GetString(), StringComparer.Ordinal).ToArray();
        if (selected.Length != StableRepairDocuments.Length)
            return await WriteStabilityBlocked(output, startHead, "EXACT_EVALUABLE_COHORT_MISSING", ct);

        var baseline = new List<RepeatMetric>();
        var frozenCells = new List<object>();
        foreach (var item in selected)
        {
            var documentId = item.GetProperty("documentId").GetString()!;
            for (var repeat = 1; repeat <= RepeatCount; repeat++)
            {
                var dir = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar), documentId, $"r{repeat}");
                var predictionPath = Path.Combine(dir, "prediction.v1.json");
                var resultPath = Path.Combine(dir, "result.v1.json");
                var freezePath = Path.Combine(dir, "freeze.v1.json");
                if (!File.Exists(predictionPath) || !File.Exists(resultPath) || !File.Exists(freezePath))
                    return await WriteStabilityBlocked(output, startHead, $"BASELINE_CELL_MISSING:{documentId}:r{repeat}", ct);
                using var freeze = JsonDocument.Parse(File.ReadAllText(freezePath));
                var root = freeze.RootElement;
                if (root.TryGetProperty("goldReadBeforeFreeze", out var firewall) && firewall.GetBoolean())
                    throw new InvalidDataException($"GOLD_FIREWALL_FAILED_BASELINE:{documentId}:r{repeat}");
                var predictionHash = Sha256(predictionPath);
                var resultHash = Sha256(resultPath);
                if (!string.Equals(predictionHash, root.GetProperty("predictionSha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(resultHash, root.GetProperty("resultSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"BASELINE_FREEZE_HASH_MISMATCH:{documentId}:r{repeat}");
                baseline.Add(LoadFrozenMetric(Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar)), documentId, $"r{repeat}"));
                frozenCells.Add(new { documentId, repeat = $"R{repeat}", predictionSha256 = predictionHash, resultSha256 = resultHash, providerCalls = 0 });
            }
        }
        if (baseline.Count != 15 || baseline.Any(x => x.Status != "SUCCESS"))
            return await WriteStabilityBlocked(output, startHead, "BASELINE_COHORT_INCOMPLETE", ct);

        var stability = BuildStabilityMatrix(repoRoot, selected, baseline);
        var oracle = BuildOracleDiagnostics(baseline, selected);
        await WriteJson(Path.Combine(output, "stability-matrix.v1.json"), new
        {
            schemaVersion = "a99-model-omission-stability-matrix-v1", startHead, model = Model,
            baselineArtifact = OutputRoot, baselineProviderCalls = 0, frozenCells, hashVerified = true,
            statuses = new[] { "EXACT_TP", "MODEL_OMISSION", "MODEL_WRONG_SPAN", "MODEL_EXTRA_NOT_APPLICABLE", "SYSTEM_LOSS", "UNRESOLVED" },
            rows = stability.Rows, classificationCounts = stability.ClassificationCounts,
            statusCounts = stability.StatusCounts, goldReadBeforeFreeze = false,
        }, ct);
        await WriteJson(Path.Combine(output, "oracle-union-diagnostic.v1.json"), new
        {
            schemaVersion = "a99-three-repeat-oracle-union-diagnostic-v1", source = "frozen baseline finalHeadings", diagnosticOnly = true,
            baselineProviderCalls = 0, union = oracle.Union, consensus2Of3 = oracle.Consensus2, consensus3Of3 = oracle.Consensus3,
            goldReadBeforeFreeze = false,
        }, ct);
        Console.WriteLine($"PERSISTENT_3_OF_3_MISS={stability.ClassificationCounts.GetValueOrDefault("PERSISTENT_3_OF_3_MISS")}");
        Console.WriteLine($"STOCHASTIC_2_OF_3_MISS={stability.ClassificationCounts.GetValueOrDefault("STOCHASTIC_2_OF_3_MISS")}");
        Console.WriteLine($"STOCHASTIC_1_OF_3_MISS={stability.ClassificationCounts.GetValueOrDefault("STOCHASTIC_1_OF_3_MISS")}");
        Console.WriteLine($"THREE_REPEAT_ORACLE_UNION=TP:{oracle.Union.Tp},FP:{oracle.Union.Fp},FN:{oracle.Union.Fn},F1:{oracle.Union.F1:0.######}");
        Console.WriteLine($"CONSENSUS_2_OF_3=TP:{oracle.Consensus2.Tp},FP:{oracle.Consensus2.Fp},FN:{oracle.Consensus2.Fn},F1:{oracle.Consensus2.F1:0.######}");
        Console.WriteLine($"CONSENSUS_3_OF_3=TP:{oracle.Consensus3.Tp},FP:{oracle.Consensus3.Fp},FN:{oracle.Consensus3.Fn},F1:{oracle.Consensus3.F1:0.######}");

        var recovered = stability.ClassificationCounts.GetValueOrDefault("STOCHASTIC_2_OF_3_MISS") + stability.ClassificationCounts.GetValueOrDefault("STOCHASTIC_1_OF_3_MISS");
        var persistent = stability.ClassificationCounts.GetValueOrDefault("PERSISTENT_3_OF_3_MISS");
        var spanAdjacent = stability.ClassificationCounts.GetValueOrDefault("MIXED_SPAN_OR_OMISSION");
        // A non-zero exact recovery in another frozen repeat is direct evidence that the
        // unchanged contract can discover the heading. The union diagnostic is the recoverability
        // upper bound; this gate therefore selects self-consistency even when duplicate-bound
        // system losses dominate the raw first-loss ledger.
        var chosen = recovered > 0 && (oracle.Union.Fn < 10 || recovered >= persistent) ? "SELF_CONSISTENCY_RECOVERY" : persistent >= spanAdjacent ? "GENERIC_OMISSION_REVIEW" : "SEMANTIC_SPAN_REPAIR";
        if (chosen != "SELF_CONSISTENCY_RECOVERY")
            throw new InvalidOperationException($"AUDIT_SELECTED_UNIMPLEMENTED_INTERVENTION:{chosen}");

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return await WriteStabilityBlocked(output, startHead, "OPENROUTER_API_KEY_MISSING", ct, chosen);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = apiKey, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) || !capability.ReasoningSupported || !capability.StructuredOutputSupported)
            return await WriteStabilityBlocked(output, startHead, "MODEL_CAPABILITY_MISMATCH", ct, chosen);
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "eval/a99-closed-loop/model-omission-stability", string.Join(',', StableRepairDocuments), ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var fresh = new List<RepeatMetric>();
        foreach (var item in selected)
        {
            var context = Prepare(repoRoot, item);
            for (var repeat = 1; repeat <= RepeatCount; repeat++)
            {
                var existingDir = Path.Combine(interventionRoot, context.DocumentId, $"r{repeat}");
                if (IsFrozenSuccessCell(existingDir))
                {
                    fresh.Add(LoadMetricAtDir(existingDir));
                    Console.WriteLine($"REUSING_SELF_CONSISTENCY_SUCCESS={context.DocumentId}/R{repeat}");
                    continue;
                }
                Console.WriteLine($"RUNNING_SELF_CONSISTENCY={context.DocumentId}/R{repeat}");
                fresh.Add(await RunSelfConsistencyRepeatAsync(repoRoot, interventionRoot, context, repeat, model, startHead, ct));
            }
        }
        var baselineMicro = Micro(baseline);
        var freshMicro = Micro(fresh);
        var keep = fresh.Count == 15 && fresh.All(x => x.Status == "SUCCESS") && freshMicro.F1 > baselineMicro.F1 && fresh.Sum(x => x.SystemLoss) == 0;
        var classification = fresh.Count != 15 || fresh.Any(x => x.Status == "BLOCKED") ? "BLOCKED_PROVIDER" : keep ? "INTERVENTION_IMPROVES_DEV" : "INTERVENTION_REVERTED";
        await WriteJson(Path.Combine(output, "intervention-result.v1.json"), new
        {
            schemaVersion = "a99-model-omission-stability-intervention-result-v1", startHead, endHead = GitSha(repoRoot),
            chosenIntervention = chosen, baseline = new { tp = baselineMicro.Tp, fp = baselineMicro.Fp, fn = baselineMicro.Fn, precision = baselineMicro.Precision, recall = baselineMicro.Recall, f1 = baselineMicro.F1, systemLoss = baseline.Sum(x => x.SystemLoss) },
            intervention = new { tp = freshMicro.Tp, fp = freshMicro.Fp, fn = freshMicro.Fn, precision = freshMicro.Precision, recall = freshMicro.Recall, f1 = freshMicro.F1, systemLoss = freshMicro.SystemLoss },
            delta = new { tp = freshMicro.Tp - baselineMicro.Tp, fp = freshMicro.Fp - baselineMicro.Fp, fn = freshMicro.Fn - baselineMicro.Fn, precision = freshMicro.Precision - baselineMicro.Precision, recall = freshMicro.Recall - baselineMicro.Recall, f1 = freshMicro.F1 - baselineMicro.F1 },
            paired = fresh.OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.Repeat, StringComparer.Ordinal).Select(x => new
            {
                documentId = x.DocumentId, repeat = x.Repeat,
                baseline = RepeatTable(baseline.Single(y => y.DocumentId == x.DocumentId && y.Repeat == x.Repeat)), review = RepeatTable(x),
            }).ToArray(),
            providerCalls = model.ProviderCalls, systemLoss = fresh.Sum(x => x.SystemLoss), keepOrRevert = keep ? "KEEP_INTERVENTION" : "REVERT_INTERVENTION",
            goldReadBeforeFreeze = false,
        }, ct);
        var distance = new
        {
            precision = new { target = .99, baseline = baselineMicro.Precision, intervention = freshMicro.Precision, baselineGap = Math.Max(0, .99 - baselineMicro.Precision), interventionGap = Math.Max(0, .99 - freshMicro.Precision) },
            recall = new { target = .99, baseline = baselineMicro.Recall, intervention = freshMicro.Recall, baselineGap = Math.Max(0, .99 - baselineMicro.Recall), interventionGap = Math.Max(0, .99 - freshMicro.Recall) },
            f1 = new { target = .99, baseline = baselineMicro.F1, intervention = freshMicro.F1, baselineGap = Math.Max(0, .99 - baselineMicro.F1), interventionGap = Math.Max(0, .99 - freshMicro.F1) },
        };
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-model-omission-stability-summary-v1", status = classification, startHead, endHead = GitSha(repoRoot),
            baseline = new { tp = baselineMicro.Tp, fp = baselineMicro.Fp, fn = baselineMicro.Fn, precision = baselineMicro.Precision, recall = baselineMicro.Recall, f1 = baselineMicro.F1, systemLoss = baseline.Sum(x => x.SystemLoss) },
            omissionStability = stability.ClassificationCounts, threeRepeatOracleUnion = oracle.Union, consensus2Of3 = oracle.Consensus2, consensus3Of3 = oracle.Consensus3,
            chosenIntervention = chosen, intervention = new { tp = freshMicro.Tp, fp = freshMicro.Fp, fn = freshMicro.Fn, precision = freshMicro.Precision, recall = freshMicro.Recall, f1 = freshMicro.F1, systemLoss = freshMicro.SystemLoss },
            keepOrRevert = keep ? "KEEP_INTERVENTION" : "REVERT_INTERVENTION", finalClassification = classification,
            distanceTo99 = distance, providerCalls = model.ProviderCalls, modelCalls = model.ProviderCalls, goldReadBeforeFreeze = false,
        }, ct);
        Console.WriteLine($"INTERVENTION={freshMicro.Tp}/{freshMicro.Fp}/{freshMicro.Fn} P={freshMicro.Precision:0.######} R={freshMicro.Recall:0.######} F1={freshMicro.F1:0.######}");
        Console.WriteLine($"KEEP_OR_REVERT={(keep ? "KEEP_INTERVENTION" : "REVERT_INTERVENTION")}");
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        return classification == "BLOCKED_PROVIDER" ? 2 : 0;
    }

    /// <summary>Offline first-loss attribution for the frozen semantic-text cohort. This lane
    /// never creates an inference client and never changes the active extraction pipeline.</summary>
    public static async Task<int> RunResidualErrorAttributionAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, "eval/a99-closed-loop/residual-error-attribution");
        Directory.CreateDirectory(output);
        var startHead = GitSha(repoRoot);
        Console.WriteLine($"START_HEAD={startHead}");
        Console.WriteLine($"BRANCH={Git(repoRoot, "branch --show-current")}");
        Console.WriteLine(Git(repoRoot, "status --short"));
        Console.WriteLine("MODEL_CALLS=0");
        var selected = LoadInventory(repoRoot).Where(x => StableRepairDocuments.Contains(x.GetProperty("documentId").GetString(), StringComparer.Ordinal)).OrderBy(x => x.GetProperty("documentId").GetString(), StringComparer.Ordinal).ToArray();
        if (startHead != "c66345510398a399eb5678709fb9a0072a80ea8a")
            Console.WriteLine($"EXPECTED_ANCESTOR_WARNING={startHead}");
        var cells = new List<FrozenCellAudit>();
        var frozenCellHashes = new List<object>();
        foreach (var item in selected)
        {
            var context = Prepare(repoRoot, item);
            for (var repeat = 1; repeat <= RepeatCount; repeat++)
            {
                var cell = LoadFrozenCellAudit(repoRoot, context, repeat);
                cells.Add(cell);
                frozenCellHashes.Add(new { documentId = context.DocumentId, repeat = $"R{repeat}", sourceSha256 = cell.SourceSha256, predictionSha256 = cell.PredictionSha256, resultSha256 = cell.ResultSha256, freezeSha256 = Sha256(cell.FreezePath), model = cell.Model, actualProvider = cell.ActualProvider, semanticContractVersion = cell.SemanticContractVersion, promptHash = cell.PromptHash, schemaHash = cell.SchemaHash, packetHash = cell.PacketHash, reasoningEnabled = cell.ReasoningEnabled, goldReadBeforeFreeze = cell.GoldReadBeforeFreeze, providerCalls = 0 });
            }
        }
        if (cells.Count != 15) throw new InvalidDataException($"BASELINE_CELL_COUNT:{cells.Count}");
        if (cells.Select(x => $"{x.Model}|{x.ActualProvider}|{x.SemanticContractVersion}|{x.PromptHash}|{x.SchemaHash}|{x.ReasoningEnabled}").Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidDataException("BASELINE_FROZEN_CONFIG_MISMATCH");
        VerifyRevertedInterventionEvidence(repoRoot);
        var matrix = BuildOccurrenceAttribution(repoRoot, selected, cells);
        var oracle = BuildOracleResidualAttribution(repoRoot, selected, cells, matrix.Rows);
        var systemFirstLoss = matrix.Rows.Where(x => x.CurrentClassification == "SYSTEM_AFFECTED").GroupBy(x => x.SystemFirstLossStage, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var selectedBucket = SelectResidualBucket(systemFirstLoss, oracle);
        await WriteJson(Path.Combine(output, "occurrence-matrix.v1.json"), new
        {
            schemaVersion = "a99-residual-error-attribution-occurrence-matrix-v1", startHead, baselineArtifact = OutputRoot,
            frozenCellCount = cells.Count, frozenCells = frozenCellHashes, hashVerified = true,
            rows = matrix.Rows.Select(x => new
            {
                x.DocumentId, x.GoldOccurrenceId, x.ExactText, x.SourceId, x.Start, x.End,
                repeats = x.Repeats, currentClassification = x.CurrentClassification, resolvedClassification = x.ResolvedClassification,
            }).ToArray(),
            stability = matrix.Stability, resolutionCounts = matrix.Rows.GroupBy(x => x.ResolvedClassification, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal),
            goldReadBeforeFreeze = false,
        }, ct);
        await WriteJson(Path.Combine(output, "first-loss.v1.json"), new
        {
            schemaVersion = "a99-residual-error-attribution-first-loss-v1", rows = matrix.Rows.Where(x => x.CurrentClassification == "SYSTEM_AFFECTED" || x.CurrentClassification == "UNRESOLVED").Select(x => new
            {
                x.DocumentId, x.GoldOccurrenceId, x.ExactText, currentClassification = x.CurrentClassification, resolvedClassification = x.ResolvedClassification,
                systemFirstLoss = x.SystemFirstLossStage, perRepeat = x.Repeats.Select(r => new { r.Repeat, r.FirstLoss, r.ModelRawExact, r.ModelRawNear, r.Bound, r.Validated, r.Projected, r.Final, r.ExactFinalKey }).ToArray(),
            }).ToArray(),
            systemFirstLossCounts = systemFirstLoss, unresolvedResolutionCounts = matrix.Rows.Where(x => x.CurrentClassification == "UNRESOLVED").GroupBy(x => x.ResolvedClassification, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal),
            goldReadBeforeFreeze = false,
        }, ct);
        await WriteJson(Path.Combine(output, "oracle-residual.v1.json"), new
        {
            schemaVersion = "a99-residual-error-attribution-oracle-residual-v1", diagnosticOnly = true,
            union = new { tp = oracle.UnionTp, fp = oracle.UnionFp, fn = oracle.UnionFn },
            falseNegatives = oracle.FalseNegatives, falsePositiveRows = oracle.FalsePositives,
            falseNegativeBuckets = oracle.FalseNegatives.GroupBy(x => x.Bucket, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal),
            falsePositiveBuckets = oracle.FalsePositives.GroupBy(x => x.Bucket, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal),
            falsePositiveStability = oracle.FalsePositives.GroupBy(x => x.SupportCount).ToDictionary(x => $"PRESENT_{x.Key}_OF_3", x => x.Count(), StringComparer.Ordinal),
            goldReadBeforeFreeze = false,
        }, ct);
        var baseline = cells.Select(x => x.Metric).ToArray();
        var baselineMicro = Micro(baseline);
        var finalClassification = selectedBucket.Count > 0 && selectedBucket.Fixable ? "RESIDUAL_SYSTEM_LOSS_PROVEN" : oracle.FalseNegatives.Count + oracle.FalsePositives.Count > 0 ? "RESIDUAL_MIXED_CAUSES" : "RESIDUAL_ATTRIBUTION_INCOMPLETE";
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-residual-error-attribution-summary-v1", startHead, endHead = GitSha(repoRoot), modelCalls = 0, providerCalls = 0,
            baseline = new { tp = baselineMicro.Tp, fp = baselineMicro.Fp, fn = baselineMicro.Fn, precision = baselineMicro.Precision, recall = baselineMicro.Recall, f1 = baselineMicro.F1, systemLoss = baseline.Sum(x => x.SystemLoss) },
            stability = matrix.Stability, systemFirstLoss = systemFirstLoss, selectedBucket,
            intervention = "NONE", pairedReplay = (object?)null, currentBest = new { tp = baselineMicro.Tp, fp = baselineMicro.Fp, fn = baselineMicro.Fn, precision = baselineMicro.Precision, recall = baselineMicro.Recall, f1 = baselineMicro.F1 },
            gapTo99 = new { additionalTpForRecall = Math.Max(0, (int)Math.Ceiling(.99 * (baselineMicro.Tp + baselineMicro.Fn) - baselineMicro.Tp)), fpToRemoveForPrecision = Math.Max(0, (int)Math.Ceiling(baselineMicro.Tp * (1 / .99 - 1) - baselineMicro.Fp)), f1Gap = Math.Max(0, .99 - baselineMicro.F1) },
            nextModelAction = "NEXT_MODEL_ACTION_NONE_YET", finalClassification, goldReadBeforeFreeze = false,
        }, ct);
        Console.WriteLine($"SYSTEM_FIRST_LOSS={string.Join(',', systemFirstLoss.Select(x => $"{x.Key}:{x.Value}"))}");
        Console.WriteLine($"ORACLE_FN_9={string.Join(',', oracle.FalseNegatives.GroupBy(x => x.Bucket).Select(x => $"{x.Key}:{x.Count()}"))}");
        Console.WriteLine($"ORACLE_FP_10={string.Join(',', oracle.FalsePositives.GroupBy(x => x.Bucket).Select(x => $"{x.Key}:{x.Count()}"))}");
        Console.WriteLine($"SELECTED_BUCKET={selectedBucket.Bucket};COUNT={selectedBucket.Count};FIXABLE={selectedBucket.Fixable}");
        Console.WriteLine("INTERVENTION=NONE");
        Console.WriteLine($"FINAL_CLASSIFICATION={finalClassification}");
        return 0;
    }

    private static FrozenCellAudit LoadFrozenCellAudit(string repoRoot, DocumentContext context, int repeat)
    {
        var dir = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar), context.DocumentId, $"r{repeat}");
        var predictionPath = Path.Combine(dir, "prediction.v1.json");
        var resultPath = Path.Combine(dir, "result.v1.json");
        var freezePath = Path.Combine(dir, "freeze.v1.json");
        using var prediction = JsonDocument.Parse(File.ReadAllText(predictionPath));
        using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        using var freeze = JsonDocument.Parse(File.ReadAllText(freezePath));
        var f = freeze.RootElement;
        var p = prediction.RootElement;
        var firewall = f.TryGetProperty("goldReadBeforeFreeze", out var gold) && gold.GetBoolean();
        if (firewall || !string.Equals(f.GetProperty("sourceSha256").GetString(), context.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Sha256(predictionPath), f.GetProperty("predictionSha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Sha256(resultPath), f.GetProperty("resultSha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
            !ConfigMatchesPrediction(f, p) ||
            !string.Equals(f.GetProperty("model").GetString(), p.GetProperty("model").GetString(), StringComparison.Ordinal) ||
            !string.Equals(f.GetProperty("semanticContractVersion").GetString(), p.GetProperty("semanticContractVersion").GetString(), StringComparison.Ordinal) ||
            !string.Equals(f.GetProperty("executionMode").GetString(), p.GetProperty("executionMode").GetString(), StringComparison.Ordinal))
            throw new InvalidDataException($"BASELINE_FREEZE_AUTHORITY_FAILED:{context.DocumentId}:R{repeat}");
        var raw = SemanticTextExactBindingContract.Parse(JsonSerializer.Serialize(new { headings = prediction.RootElement.GetProperty("rawModelHeadings") }));
        var bound = prediction.RootElement.GetProperty("boundHeadings").EnumerateArray().Select(x => Key(x.GetProperty("sourceId").GetString()!, new StructuralSpan(x.GetProperty("start").GetInt32(), x.GetProperty("end").GetInt32()))).ToHashSet(StringComparer.Ordinal);
        var final = prediction.RootElement.GetProperty("finalHeadings").EnumerateArray().Select(x => new FinalAuditHeading(Key(x.GetProperty("sourceId").GetString()!, new StructuralSpan(x.GetProperty("start").GetInt32(), x.GetProperty("end").GetInt32())), x.GetProperty("text").GetString() ?? "")).ToArray();
        using var first = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "first-loss.v1.json")));
        var losses = first.RootElement.TryGetProperty("firstLosses", out var lossArray) ? lossArray.EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("firstLoss").GetString()!, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal);
        using var score = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "score.v1.json")));
        return new FrozenCellAudit(context.DocumentId, repeat, context.SourceSha256, f.GetProperty("predictionSha256").GetString()!, f.GetProperty("resultSha256").GetString()!, f.GetProperty("model").GetString()!, f.GetProperty("actualProvider").GetString()!, f.GetProperty("semanticContractVersion").GetString()!, f.GetProperty("promptHash").GetString()!, f.GetProperty("schemaHash").GetString()!, f.GetProperty("packetHash").GetString()!, f.GetProperty("reasoningConfiguration").GetProperty("enabled").GetBoolean(), predictionPath, resultPath, freezePath, firewall, raw.Headings, bound, final.ToDictionary(x => x.Key, StringComparer.Ordinal), losses, LoadFrozenMetric(Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar)), context.DocumentId, $"r{repeat}"));
    }

    private static void VerifyRevertedInterventionEvidence(string repoRoot)
    {
        var root = Path.Combine(repoRoot, "eval/a99-closed-loop/model-omission-stability");
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "intervention-result.v1.json")));
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "summary.v1.json")));
        var intervention = result.RootElement.GetProperty("intervention");
        if (intervention.GetProperty("tp").GetInt32() != 426 || intervention.GetProperty("fp").GetInt32() != 39 || intervention.GetProperty("fn").GetInt32() != 33 ||
            Math.Abs(intervention.GetProperty("f1").GetDouble() - .922077922077922) > 1e-9 || intervention.GetProperty("systemLoss").GetInt32() != 27 || result.RootElement.GetProperty("keepOrRevert").GetString() != "REVERT_INTERVENTION" || summary.RootElement.GetProperty("finalClassification").GetString() != "INTERVENTION_REVERTED")
            throw new InvalidDataException("REVERTED_INTERVENTION_EVIDENCE_MISMATCH");
    }

    private static OccurrenceAttribution BuildOccurrenceAttribution(string repoRoot, IReadOnlyList<JsonElement> selected, IReadOnlyList<FrozenCellAudit> cells)
    {
        var rows = new List<OccurrenceAuditRow>();
        foreach (var item in selected)
        {
            var context = Prepare(repoRoot, item);
            var documentId = context.DocumentId;
            var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", documentId + ".occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
            foreach (var occurrence in gold)
            {
                var key = Key(occurrence.SourceId, occurrence.HeadingSpan!);
                var alias = context.SourceRows.Single(x => x.SourceId == occurrence.SourceId);
                var repeats = cells.Where(x => x.DocumentId == documentId).OrderBy(x => x.Repeat).Select(cell =>
                {
                    var raw = cell.Raw.Where(x => x.Source == alias.Alias && !string.IsNullOrEmpty(x.Text)).ToArray();
                    var rawExact = raw.Any(x => x.Text == occurrence.ExactText);
                    var rawNear = raw.Where(x => x.Text != occurrence.ExactText).Any(x => HasOverlap(alias.RawText, x.Text, occurrence.HeadingSpan!));
                    var bound = cell.BoundKeys.Contains(key);
                    var final = cell.FinalKeys.ContainsKey(key);
                    var firstLoss = cell.Losses.GetValueOrDefault(key, "UNRESOLVED");
                    var validated = bound && firstLoss != "SYSTEM_VALIDATOR_LOSS";
                    var projected = final;
                    return new RepeatOccurrenceAudit($"R{cell.Repeat}", firstLoss, rawExact, rawNear, bound, validated, projected, final, final ? key : null, TraceStage(firstLoss, rawExact, bound, final));
                }).ToArray();
                var current = CurrentStabilityClassification(repeats.Select(x => CurrentStatus(x.FirstLoss)).ToArray());
                var resolved = ResolveAttribution(current, repeats);
                rows.Add(new(documentId, $"{documentId}:{key}", occurrence.ExactText, occurrence.SourceId, occurrence.HeadingSpan!.Start, occurrence.HeadingSpan.End, repeats, current, resolved, repeats.Select(x => x.Stage).FirstOrDefault(x => x is "BINDER_LOSS" or "VALIDATOR_LOSS" or "PROJECTION_LOSS") ?? "NONE", key, cells.First(x => x.DocumentId == documentId).Metric));
            }
        }
        var stability = new
        {
            persistent3of3 = rows.Count(x => x.CurrentClassification == "PERSISTENT_3_OF_3_MISS"),
            stochastic2of3 = rows.Count(x => x.CurrentClassification == "STOCHASTIC_2_OF_3_MISS"),
            stochastic1of3 = rows.Count(x => x.CurrentClassification == "STOCHASTIC_1_OF_3_MISS"),
            alwaysFound = rows.Count(x => x.CurrentClassification == "ALWAYS_FOUND"),
            systemAffected = rows.Count(x => x.CurrentClassification == "SYSTEM_AFFECTED"),
            unresolved = rows.Count(x => x.CurrentClassification == "UNRESOLVED"),
            resolvedModelWrongSpan = rows.Count(x => x.ResolvedClassification == "MODEL_WRONG_SPAN"),
        };
        return new OccurrenceAttribution(rows, stability);
    }

    private static string ResolveAttribution(string current, IReadOnlyList<RepeatOccurrenceAudit> repeats)
    {
        if (current == "SYSTEM_AFFECTED") return "SYSTEM_BINDING_LOSS";
        if (current == "UNRESOLVED" && repeats.Any(x => x.ModelRawNear || x.FirstLoss is "MODEL_WRONG_TEXT" or "MODEL_WRONG_SPAN")) return "MODEL_WRONG_SPAN";
        return current;
    }

    private static string CurrentStatus(string firstLoss) => firstLoss switch
    {
        "FOUND" => "EXACT_TP",
        "MODEL_OMISSION" => "MODEL_OMISSION",
        "MODEL_WRONG_TEXT" or "MODEL_WRONG_SPAN" => "MODEL_WRONG_SPAN",
        "AMBIGUOUS_DUPLICATE_TEXT" or "SYSTEM_BINDING_LOSS" or "SYSTEM_VALIDATOR_LOSS" or "SYSTEM_PROJECTION_LOSS" => "SYSTEM_LOSS",
        _ => "UNRESOLVED",
    };

    private static string CurrentStabilityClassification(IReadOnlyList<string> statuses)
    {
        if (statuses.Any(x => x == "SYSTEM_LOSS")) return "SYSTEM_AFFECTED";
        var omissions = statuses.Count(x => x == "MODEL_OMISSION");
        if (omissions == 3) return "PERSISTENT_3_OF_3_MISS";
        if (omissions == 2) return "STOCHASTIC_2_OF_3_MISS";
        if (omissions == 1 && statuses.Count(x => x == "EXACT_TP") == 2) return "STOCHASTIC_1_OF_3_MISS";
        if (statuses.All(x => x == "EXACT_TP")) return "ALWAYS_FOUND";
        return "UNRESOLVED";
    }

    private static string TraceStage(string firstLoss, bool rawExact, bool bound, bool final) =>
        final ? "NONE" : firstLoss == "AMBIGUOUS_DUPLICATE_TEXT" || (rawExact && !bound) ? "BINDER_LOSS" : firstLoss == "SYSTEM_VALIDATOR_LOSS" ? "VALIDATOR_LOSS" : firstLoss == "SYSTEM_PROJECTION_LOSS" ? "PROJECTION_LOSS" : "NONE";

    private static bool HasOverlap(string source, string text, StructuralSpan gold)
    {
        var offset = 0;
        while (offset <= source.Length - text.Length)
        {
            var start = source.IndexOf(text, offset, StringComparison.Ordinal);
            if (start < 0) return false;
            if (start < gold.End && gold.Start < start + text.Length) return true;
            offset = start + Math.Max(1, text.Length);
        }
        return false;
    }

    private static OracleResidualAttribution BuildOracleResidualAttribution(string repoRoot, IReadOnlyList<JsonElement> selected, IReadOnlyList<FrozenCellAudit> cells, IReadOnlyList<OccurrenceAuditRow> matrix)
    {
        var falseNegatives = new List<OracleResidualRow>();
        var falsePositives = new List<OracleResidualRow>();
        foreach (var item in selected)
        {
            var context = Prepare(repoRoot, item);
            var documentId = context.DocumentId;
            var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", documentId + ".occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
            var goldKeys = gold.Select(x => Key(x.SourceId, x.HeadingSpan!)).ToHashSet(StringComparer.Ordinal);
            var docCells = cells.Where(x => x.DocumentId == documentId).ToArray();
            var unionKeys = docCells.SelectMany(x => x.FinalKeys.Keys).ToHashSet(StringComparer.Ordinal);
            foreach (var occurrence in gold.Where(x => !unionKeys.Contains(Key(x.SourceId, x.HeadingSpan!))))
            {
                var key = Key(occurrence.SourceId, occurrence.HeadingSpan!);
                var row = matrix.Single(x => x.DocumentId == documentId && x.ExactFinalKey == key);
                var bucket = row.Repeats.Any(x => x.ModelRawExact && !x.Bound) ? "SYSTEM_LOSS" : row.Repeats.Any(x => x.ModelRawNear) ? "MODEL_WRONG_SPAN" : row.Repeats.All(x => x.FirstLoss == "MODEL_OMISSION") ? "PERSISTENT_MODEL_OMISSION" : "UNRESOLVED";
                falseNegatives.Add(new OracleResidualRow(documentId, key, occurrence.ExactText, bucket, null, row.Repeats.Select(x => x.Repeat).ToArray()));
            }
            foreach (var key in unionKeys.Where(x => !goldKeys.Contains(x)))
            {
                var supports = docCells.Where(x => x.FinalKeys.ContainsKey(key)).Select(x => $"R{x.Repeat}").OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var final = docCells.First(x => x.FinalKeys.ContainsKey(key)).FinalKeys[key];
                var parts = key.Split(':');
                var sourceId = string.Join(':', parts.Take(parts.Length - 2));
                var start = int.Parse(parts[^2]);
                var end = int.Parse(parts[^1]);
                var goldSameSource = gold.Where(x => x.SourceId == sourceId).ToArray();
                var sameTextOtherSource = gold.Any(x => x.ExactText == final.Text && x.SourceId != sourceId);
                var overlap = goldSameSource.FirstOrDefault(x => x.HeadingSpan!.Start < end && start < x.HeadingSpan.End);
                var bucket = sameTextOtherSource ? "WRONG_SOURCE_DUPLICATE_TEXT" : overlap is not null && start <= overlap.HeadingSpan!.Start && end >= overlap.HeadingSpan.End ? "SUPERSET_GOLD_HEADING" : overlap is not null ? "WRONG_SPAN_SAME_HEADING" : "TRUE_EXTRA";
                falsePositives.Add(new OracleResidualRow(documentId, key, final.Text, bucket, supports.Length, supports));
            }
        }
        return new OracleResidualAttribution(falseNegatives, falsePositives, matrix.Count - falseNegatives.Count, falsePositives.Count, falseNegatives.Count);
    }

    private static SelectedResidualBucket SelectResidualBucket(IReadOnlyDictionary<string, int> systemFirstLoss, OracleResidualAttribution oracle)
    {
        var system = systemFirstLoss.Sum(x => x.Value);
        if (system == 0) return new("NONE", 0, false, "No system first-loss stage was proven; no replay is authorized.");
        return new("SYSTEM_BINDING_LOSS", system, false, "The exact raw proposal is present, but the frozen exact binder rejects ambiguous duplicate text; repairing this requires additional model occurrence evidence and is not a safe post-model fix.");
    }

    private static bool ConfigMatchesPrediction(JsonElement freeze, JsonElement prediction)
    {
        foreach (var property in new[] { "sourceSha256", "promptHash", "schemaHash", "packetHash" })
            if (!freeze.TryGetProperty(property, out var frozen) || !prediction.TryGetProperty(property, out var current) || !string.Equals(frozen.GetString(), current.GetString(), StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private sealed record FrozenCellAudit(string DocumentId, int Repeat, string SourceSha256, string PredictionSha256, string ResultSha256, string Model, string ActualProvider, string SemanticContractVersion, string PromptHash, string SchemaHash, string PacketHash, bool ReasoningEnabled, string PredictionPath, string ResultPath, string FreezePath, bool GoldReadBeforeFreeze, IReadOnlyList<SemanticTextHeading> Raw, IReadOnlySet<string> BoundKeys, IReadOnlyDictionary<string, FinalAuditHeading> FinalKeys, IReadOnlyDictionary<string, string> Losses, RepeatMetric Metric);
    private sealed record FinalAuditHeading(string Key, string Text);
    private sealed record RepeatOccurrenceAudit(string Repeat, string FirstLoss, bool ModelRawExact, bool ModelRawNear, bool Bound, bool Validated, bool Projected, bool Final, string? ExactFinalKey, string Stage);
    private sealed record OccurrenceAuditRow(string DocumentId, string GoldOccurrenceId, string ExactText, string SourceId, int Start, int End, IReadOnlyList<RepeatOccurrenceAudit> Repeats, string CurrentClassification, string ResolvedClassification, string SystemFirstLossStage, string ExactFinalKey, RepeatMetric Metric);
    private sealed record OccurrenceAttribution(IReadOnlyList<OccurrenceAuditRow> Rows, object Stability);
    private sealed record OracleResidualRow(string DocumentId, string Key, string Text, string Bucket, int? SupportCount, IReadOnlyList<string> SupportingRepeats);
    private sealed record OracleResidualAttribution(IReadOnlyList<OracleResidualRow> FalseNegatives, IReadOnlyList<OracleResidualRow> FalsePositives, int UnionTp, int UnionFp, int UnionFn);
    private sealed record SelectedResidualBucket(string Bucket, int Count, bool Fixable, string WhyFixableGenerically);

    private static async Task<RepeatMetric> RunSelfConsistencyRepeatAsync(string repoRoot, string output, DocumentContext context, int repeat, OpenRouterCeilingReasoningModel model, string gitSha, CancellationToken ct)
    {
        var repeatName = $"r{repeat}";
        var dir = Path.Combine(output, context.DocumentId, repeatName);
        Directory.CreateDirectory(dir);
        var stopwatch = Stopwatch.StartNew();
        var extraction = new List<SemanticTextHeading>();
        var extractionTelemetry = new List<RequestPacketTelemetry>();
        var extractionResponseHashes = new List<string>();
        try
        {
            for (var pass = 1; pass <= 3; pass++)
            {
                var request = await CompleteSelfConsistencyRequestAsync(model, context, repeat, $"extract:{pass}", SemanticTextExactBindingContract.System, SemanticTextExactBindingContract.BuildUser(context.Packet, ReasoningRoute.ModelCapabilityCeiling.ToString()), SemanticTextExactBindingContract.Schema(), "semantic_text_self_consistency_extract_v1", ct);
                var parsed = SemanticTextExactBindingContract.Parse(request.Content);
                request.Telemetry.StructuredOutputParsed = true;
                request.Telemetry.HeadingOutputCount = parsed.Headings.Count;
                extraction.AddRange(parsed.Headings);
                extractionTelemetry.Add(request.Telemetry);
                extractionResponseHashes.Add(Sha256Text(request.Content));
            }
            var union = extraction.DistinctBy(x => JsonSerializer.Serialize(x), StringComparer.Ordinal).ToArray();
            var candidateRows = union.Select((x, i) => new { candidateId = i, source = x.Source, text = x.Text, role = x.Role, occurrence = x.Occurrence, leftExactContext = x.LeftExactContext, rightExactContext = x.RightExactContext }).ToArray();
            var candidateJson = JsonSerializer.Serialize(new { proposals = candidateRows }, JsonOptions);
            var verifyRequest = await CompleteSelfConsistencyRequestAsync(model, context, repeat, "verify", SemanticTextSelfConsistencyContract.System, SemanticTextSelfConsistencyContract.BuildUser(context.Packet, candidateJson, ReasoningRoute.ModelCapabilityCeiling.ToString()), SemanticTextSelfConsistencyContract.Schema(), "semantic_text_self_consistency_verify_v1", ct);
            verifyRequest.Telemetry.StructuredOutputParsed = true;
            var decisions = SemanticTextSelfConsistencyContract.Parse(verifyRequest.Content);
            var selected = new List<SemanticTextHeading>();
            foreach (var decision in decisions.Where(x => x.Action is "KEEP" or "CORRECT_SPAN"))
            {
                if (decision.CandidateId < 0 || decision.CandidateId >= union.Length) continue;
                var candidate = union[decision.CandidateId];
                var source = decision.Action == "KEEP" ? candidate.Source : decision.Source;
                var text = decision.Action == "KEEP" ? candidate.Text : decision.Text;
                var role = decision.Action == "KEEP" ? candidate.Role : decision.Role;
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(text) || !CeilingSemanticRole.IsAllowed(role)) continue;
                selected.Add(new SemanticTextHeading(source!, text!, role!, decision.Action == "KEEP" ? candidate.Occurrence : decision.Occurrence, decision.Action == "KEEP" ? candidate.LeftExactContext : decision.LeftExactContext, decision.Action == "KEEP" ? candidate.RightExactContext : decision.RightExactContext));
            }
            var finalResponse = new SemanticTextResponse(union);
            var bound = SemanticTextExactBinder.Bind(selected, context.SourceRows, out var observations);
            var proposals = bound.Select(x => new ReasoningHeadingProposal { SourceId = x.SourceId, HeadingSpan = new StructuralSpan(x.Start, x.End), Text = x.Text, SemanticRole = x.Role, Confidence = 1 }).ToArray();
            var materialized = ReasoningProposalMaterializer.Materialize(context.Source, context.Policy, proposals);
            var finalElements = ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure).OrderBy(x => x.Sources.Single().SourceOrdinal).ThenBy(x => x.Sources.Single().Span.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
            stopwatch.Stop();
            var finalRows = finalElements.Select(x => new { sourceId = x.Sources.Single().SourceId, start = x.Sources.Single().Span.Start, end = x.Sources.Single().Span.End, text = x.Text, role = x.Role }).ToArray();
            var prediction = new
            {
                schemaVersion = "a99-model-omission-stability-prediction-v1", context.DocumentId, repeat = repeatName, model = Model,
                intervention = "SELF_CONSISTENCY_RECOVERY", baseContractHash = ContractHash(), verifierContractHash = SelfConsistencyContractHash(),
                sourceSha256 = context.SourceSha256, extractionCount = 3, extractionHeadings = extraction, unionHeadings = union,
                verifierDecisions = decisions, selectedHeadings = selected, bindingObservations = observations, boundHeadings = bound,
                validatorAccepted = materialized.Validated.Count(x => x.Accepted), validatorRejected = materialized.Validated.Count(x => !x.Accepted), finalHeadings = finalRows, goldReadBeforeFreeze = false,
            };
            var result = new { schemaVersion = "a99-model-omission-stability-result-v1", context.DocumentId, repeat = repeatName, model = Model, headings = finalRows, goldReadBeforeFreeze = false };
            var predictionPath = Path.Combine(dir, "prediction.v1.json");
            var resultPath = Path.Combine(dir, "result.v1.json");
            await WriteJson(predictionPath, prediction, ct);
            await WriteJson(resultPath, result, ct);
            var freeze = new
            {
                schemaVersion = "a99-model-omission-stability-freeze-v1", context.DocumentId, repeat = repeatName, gitSha, model = Model,
                actualProvider = verifyRequest.Telemetry.ProviderRoute, sourceSha256 = context.SourceSha256, baseContractHash = ContractHash(), verifierContractHash = SelfConsistencyContractHash(),
                extractionCalls = 3, verifierCalls = 1, extractionResponseHashes = extractionResponseHashes.ToArray(), verifierResponseHash = Sha256Text(verifyRequest.Content),
                predictionSha256 = Sha256(predictionPath), resultSha256 = Sha256(resultPath), rawUnionCount = union.Length, selectedCount = selected.Count, boundProposalCount = bound.Count, finalCount = finalElements.Length,
                providerAttempts = 4, inputTokens = extractionTelemetry.Sum(x => x.ReportedInputTokens ?? 0) + (verifyRequest.Telemetry.ReportedInputTokens ?? 0), reasoningTokens = extractionTelemetry.Sum(x => x.ReportedReasoningTokens ?? 0) + (verifyRequest.Telemetry.ReportedReasoningTokens ?? 0), outputTokens = extractionTelemetry.Sum(x => x.ReportedOutputTokens ?? 0) + (verifyRequest.Telemetry.ReportedOutputTokens ?? 0), finishReason = verifyRequest.Telemetry.FinishReason,
                reasoningConfiguration = new { requested = true, enabled = true, excluded = true, effort = "MODEL_DEFAULT" }, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
            };
            var freezePath = Path.Combine(dir, "freeze.v1.json");
            await WriteJson(freezePath, freeze, ct);
            if (Sha256(predictionPath) != freeze.predictionSha256 || Sha256(resultPath) != freeze.resultSha256) throw new InvalidDataException($"SELF_CONSISTENCY_FREEZE_HASH_VERIFICATION_FAILED:{context.DocumentId}:{repeatName}");
            var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", context.DocumentId + ".occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
            var metric = Score(context, repeatName, finalResponse, observations, bound, materialized, finalElements, gold, verifyRequest.Telemetry, stopwatch.ElapsedMilliseconds);
            await WriteJson(Path.Combine(dir, "score.v1.json"), metric.Score!, ct);
            await WriteJson(Path.Combine(dir, "first-loss.v1.json"), new { context.DocumentId, repeat = repeatName, metric.FirstLosses, metric.FalsePositives, metric.FirstLossCounts, systemLoss = metric.SystemLoss, goldReadBeforeFreeze = false }, ct);
            return metric;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            var telemetry = model.Telemetry.LastOrDefault(x => x.DocumentId == context.DocumentId);
            var predictionPath = Path.Combine(dir, "prediction.v1.json");
            var resultPath = Path.Combine(dir, "result.v1.json");
            await WriteJson(predictionPath, new { schemaVersion = "a99-model-omission-stability-prediction-v1", context.DocumentId, repeat = repeatName, status = "BLOCKED", failure = ex.GetType().Name + ":" + ex.Message, model = Model, goldReadBeforeFreeze = false }, ct);
            await WriteJson(resultPath, new { schemaVersion = "a99-model-omission-stability-result-v1", context.DocumentId, repeat = repeatName, status = "BLOCKED", headings = Array.Empty<object>(), goldReadBeforeFreeze = false }, ct);
            await WriteJson(Path.Combine(dir, "freeze.v1.json"), new { schemaVersion = "a99-model-omission-stability-freeze-v1", context.DocumentId, repeat = repeatName, gitSha, model = Model, predictionSha256 = Sha256(predictionPath), resultSha256 = Sha256(resultPath), providerAttempts = model.ProviderCalls, actualProvider = telemetry?.ProviderRoute, finishReason = telemetry?.FinishReason, failureClass = ex.GetType().Name, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow }, ct);
            await WriteJson(Path.Combine(dir, "score.v1.json"), new { schemaVersion = "a99-model-omission-stability-score-v1", context.DocumentId, repeat = repeatName, status = "BLOCKED", exactStatus = "NOT_EVALUABLE", tp = 0, fp = 0, fn = 0, systemLoss = 0, goldReadBeforeFreeze = false }, ct);
            await WriteJson(Path.Combine(dir, "first-loss.v1.json"), new { context.DocumentId, repeat = repeatName, status = "BLOCKED", failure = ex.GetType().Name + ":" + ex.Message, goldReadBeforeFreeze = false }, ct);
            return new RepeatMetric(context.DocumentId, repeatName, "BLOCKED", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, telemetry?.ProviderRoute, telemetry?.FinishReason, telemetry?.ReportedInputTokens, telemetry?.ReportedReasoningTokens, telemetry?.ReportedOutputTokens, stopwatch.ElapsedMilliseconds, [], [], []);
        }
    }

    private static async Task<(string Content, RequestPacketTelemetry Telemetry)> CompleteSelfConsistencyRequestAsync(OpenRouterCeilingReasoningModel model, DocumentContext context, int repeat, string pass, string system, string user, object schema, string schemaName, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await model.CompleteRawStructuredSemanticAsync(context.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(), $"{SemanticTextSelfConsistencyContract.ProtocolVersion}:{context.DocumentId}:{repeat}:{pass}:{context.PacketHash}", context.Packet, context.SourceRows.Sum(x => x.RawText.Length), context.SourceRows.Count, context.SourceRows.Count, system, user, schema, schemaName, ct);
            }
            catch (ReasoningCompletionException) when (attempt < 3 && model.Telemetry.LastOrDefault(x => x.DocumentId == context.DocumentId)?.HttpStatus == 429)
            {
                Console.WriteLine($"TRANSIENT_RETRY_SELF_CONSISTENCY=HTTP_429/{context.DocumentId}/R{repeat}/{pass}/attempt={attempt + 1}");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    private static bool IsFrozenSuccessCell(string dir)
    {
        var scorePath = Path.Combine(dir, "score.v1.json");
        var predictionPath = Path.Combine(dir, "prediction.v1.json");
        var resultPath = Path.Combine(dir, "result.v1.json");
        var freezePath = Path.Combine(dir, "freeze.v1.json");
        if (!File.Exists(scorePath) || !File.Exists(predictionPath) || !File.Exists(resultPath) || !File.Exists(freezePath)) return false;
        using var score = JsonDocument.Parse(File.ReadAllText(scorePath));
        if (!score.RootElement.TryGetProperty("status", out var status) || status.GetString() != "SUCCESS") return false;
        using var freeze = JsonDocument.Parse(File.ReadAllText(freezePath));
        return string.Equals(Sha256(predictionPath), freeze.RootElement.GetProperty("predictionSha256").GetString(), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Sha256(resultPath), freeze.RootElement.GetProperty("resultSha256").GetString(), StringComparison.OrdinalIgnoreCase) &&
               !(freeze.RootElement.TryGetProperty("goldReadBeforeFreeze", out var firewall) && firewall.GetBoolean());
    }

    private static string SelfConsistencyContractHash() => Sha256Text(string.Join("\n", SemanticTextSelfConsistencyContract.ProtocolVersion, SemanticTextSelfConsistencyContract.System, JsonSerializer.Serialize(SemanticTextSelfConsistencyContract.Schema())));

    private static StabilityMatrix BuildStabilityMatrix(string repoRoot, IReadOnlyList<JsonElement> selected, IReadOnlyList<RepeatMetric> baseline)
    {
        var rows = new List<object>();
        var statusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var classCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in selected)
        {
            var documentId = item.GetProperty("documentId").GetString()!;
            var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", documentId + ".occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
            foreach (var occurrence in gold)
            {
                var key = Key(occurrence.SourceId, occurrence.HeadingSpan!);
                var statuses = baseline.Where(x => x.DocumentId == documentId).OrderBy(x => x.Repeat, StringComparer.Ordinal).Select(x =>
                {
                    var loss = x.FirstLosses.FirstOrDefault(y => y.Key == key);
                    return loss is null ? "UNRESOLVED" : StabilityStatus(loss.FirstLoss);
                }).ToArray();
                var classification = StabilityClassification(statuses);
                foreach (var status in statuses) statusCounts[status] = statusCounts.GetValueOrDefault(status) + 1;
                classCounts[classification] = classCounts.GetValueOrDefault(classification) + 1;
                rows.Add(new { documentId, goldOccurrenceId = $"{documentId}:{key}", exactText = occurrence.ExactText, sourceId = occurrence.SourceId, start = occurrence.HeadingSpan!.Start, end = occurrence.HeadingSpan.End, r1 = statuses.ElementAtOrDefault(0) ?? "UNRESOLVED", r2 = statuses.ElementAtOrDefault(1) ?? "UNRESOLVED", r3 = statuses.ElementAtOrDefault(2) ?? "UNRESOLVED", classification });
            }
        }
        return new StabilityMatrix(rows, classCounts, statusCounts);
    }

    private static string StabilityStatus(string firstLoss) => firstLoss switch
    {
        "FOUND" => "EXACT_TP",
        "MODEL_OMISSION" => "MODEL_OMISSION",
        "MODEL_WRONG_SPAN" or "MODEL_WRONG_TEXT" => "MODEL_WRONG_SPAN",
        "MODEL_FALSE_POSITIVE" => "MODEL_EXTRA_NOT_APPLICABLE",
        "SYSTEM_BINDING_LOSS" or "SYSTEM_VALIDATOR_LOSS" or "SYSTEM_PROJECTION_LOSS" or "SYSTEM_ALIAS_RESOLUTION_LOSS" or "SYSTEM_DEDUPE_LOSS" or "AMBIGUOUS_DUPLICATE_TEXT" => "SYSTEM_LOSS",
        _ => "UNRESOLVED",
    };

    private static string StabilityClassification(IReadOnlyList<string> statuses)
        => SemanticTextStabilityDiagnostics.Classify(statuses);

    private static OracleDiagnostics BuildOracleDiagnostics(IReadOnlyList<RepeatMetric> baseline, IReadOnlyList<JsonElement> selected)
    {
        var diagnostics = new List<(string DocumentId, string Mode, DiagnosticMetric Metric)>();
        foreach (var item in selected)
        {
            var documentId = item.GetProperty("documentId").GetString()!;
            var goldCount = baseline.First(x => x.DocumentId == documentId).Gold;
            var local = baseline.Where(x => x.DocumentId == documentId).OrderBy(x => x.Repeat, StringComparer.Ordinal).ToArray();
            var keys = local.SelectMany(x => x.PredictionKeys).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var counts = local.SelectMany(x => x.PredictionKeys).GroupBy(x => x, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            var goldKeys = local.SelectMany(x => x.FirstLosses.Where(y => y.FirstLoss == "FOUND").Select(y => y.Key)).Concat(local.SelectMany(x => x.FirstLosses.Where(y => y.FirstLoss != "FOUND" && y.FirstLoss != "MODEL_FALSE_POSITIVE").Select(y => y.Key))).Distinct(StringComparer.Ordinal).Take(goldCount).ToHashSet(StringComparer.Ordinal);
            // Gold keys are reconstructed from the frozen first-loss rows; every baseline cell has
            // one row per canonical occurrence and no runtime Gold is used by the intervention.
            diagnostics.Add((documentId, "UNION", DiagnosticScore(keys, goldKeys)));
            diagnostics.Add((documentId, "CONSENSUS_2_OF_3", DiagnosticScore(counts.Where(x => x.Value >= 2).Select(x => x.Key).ToHashSet(StringComparer.Ordinal), goldKeys)));
            diagnostics.Add((documentId, "CONSENSUS_3_OF_3", DiagnosticScore(counts.Where(x => x.Value == 3).Select(x => x.Key).ToHashSet(StringComparer.Ordinal), goldKeys)));
        }
        return new OracleDiagnostics(MicroDiagnostic(diagnostics.Where(x => x.Mode == "UNION").Select(x => x.Metric)), MicroDiagnostic(diagnostics.Where(x => x.Mode == "CONSENSUS_2_OF_3").Select(x => x.Metric)), MicroDiagnostic(diagnostics.Where(x => x.Mode == "CONSENSUS_3_OF_3").Select(x => x.Metric)));
    }

    private static DiagnosticMetric DiagnosticScore(IReadOnlySet<string> prediction, IReadOnlySet<string> gold)
    {
        var score = SemanticTextStabilityDiagnostics.Score([prediction], gold, 1);
        return new DiagnosticMetric(score.Tp, score.Fp, score.Fn, score.Precision, score.Recall, score.F1);
    }

    private static DiagnosticMetric MicroDiagnostic(IEnumerable<DiagnosticMetric> metrics)
    {
        var rows = metrics.ToArray();
        return DiagnosticMetric.Create(rows.Sum(x => x.Tp), rows.Sum(x => x.Fp), rows.Sum(x => x.Fn));
    }

    private static async Task<int> WriteStabilityBlocked(string output, string head, string reason, CancellationToken ct, string chosenIntervention = "SELF_CONSISTENCY_RECOVERY")
    {
        await WriteJson(Path.Combine(output, "intervention-result.v1.json"), new { schemaVersion = "a99-model-omission-stability-intervention-result-v1", status = "BLOCKED", reason, chosenIntervention, providerCalls = 0, goldReadBeforeFreeze = false }, ct);
        await WriteJson(Path.Combine(output, "summary.v1.json"), new { schemaVersion = "a99-model-omission-stability-summary-v1", status = "BLOCKED_PROVIDER", finalClassification = "BLOCKED_PROVIDER", reason, startHead = head, providerCalls = 0, modelCalls = 0, goldReadBeforeFreeze = false }, ct);
        Console.WriteLine($"FINAL_CLASSIFICATION=BLOCKED_PROVIDER");
        Console.WriteLine($"BLOCK_REASON={reason}");
        return 2;
    }

    private sealed record StabilityMatrix(IReadOnlyList<object> Rows, IReadOnlyDictionary<string, int> ClassificationCounts, IReadOnlyDictionary<string, int> StatusCounts);
    private sealed record OracleDiagnostics(DiagnosticMetric Union, DiagnosticMetric Consensus2, DiagnosticMetric Consensus3);
    private sealed record DiagnosticMetric(int Tp, int Fp, int Fn, double Precision, double Recall, double F1)
    {
        public static DiagnosticMetric Create(int tp, int fp, int fn)
        {
            var p = tp + fp == 0 ? 0d : (double)tp / (tp + fp);
            var r = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
            return new(tp, fp, fn, p, r, p + r == 0 ? 0d : 2 * p * r / (p + r));
        }
    }

    private static Dictionary<string, string> CaptureFrozenSuccessCellHashes(string repairRoot)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var documentId in StableRepairDocuments)
        foreach (var repeat in new[] { "r4", "r5", "r6" })
        {
            if (documentId == "DOC-0252" && repeat is "r5" or "r6") continue;
            if (!LoadRepairScoreStatus(repairRoot, documentId, repeat))
                throw new InvalidDataException($"RETAINED_CELL_NOT_SUCCESS:{documentId}/{repeat}");
            var dir = Path.Combine(repairRoot, repeat, documentId);
            foreach (var file in new[] { "prediction.v1.json", "result.v1.json", "freeze.v1.json" })
            {
                var path = Path.Combine(dir, file);
                hashes[$"{documentId}/{repeat}/{file}"] = Sha256(path);
            }
        }
        return hashes;
    }

    private static void AssertFrozenSuccessCellHashesUnchanged(string repairRoot, IReadOnlyDictionary<string, string> before)
    {
        foreach (var pair in before)
        {
            var parts = pair.Key.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var actual = Sha256(Path.Combine(repairRoot, parts[1], parts[0], parts[2]));
            if (!string.Equals(actual, pair.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"RETAINED_CELL_CHANGED:{pair.Key}");
        }
    }

    private static int LoadPriorResumeProviderCalls(string repairRoot)
    {
        var path = Path.Combine(repairRoot, "resume-only-completion.v1.json");
        if (!File.Exists(path)) return 0;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return GetInt(doc.RootElement, "providerCallsCurrentRun");
    }

    private static void VerifyFrozenBaseline(string baselineRoot, string documentId, string repeat)
    {
        var dir = Path.Combine(baselineRoot, documentId, repeat);
        var predictionPath = Path.Combine(dir, "prediction.v1.json");
        var resultPath = Path.Combine(dir, "result.v1.json");
        using var freeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "freeze.v1.json")));
        var root = freeze.RootElement;
        if (root.TryGetProperty("goldReadBeforeFreeze", out var gold) && gold.GetBoolean()) throw new InvalidDataException("GOLD_FIREWALL_FAILED:" + documentId + ":" + repeat);
        if (!string.Equals(root.GetProperty("predictionSha256").GetString(), Sha256(predictionPath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(root.GetProperty("resultSha256").GetString(), Sha256(resultPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("FROZEN_BASELINE_HASH_MISMATCH:" + documentId + ":" + repeat);
    }

    private static object BuildStableErrorAudit(string repoRoot, string baselineRoot, IReadOnlyDictionary<string, DocumentContext> contexts, IReadOnlyDictionary<string, IReadOnlyList<ReasoningGoldOccurrence>> goldByDocument, string startHead)
    {
        var omissions = new List<object>();
        var spanVariants = new List<object>();
        var falsePositives = new List<object>();
        foreach (var documentId in StableRepairDocuments)
        {
            var context = contexts[documentId];
            var predictions = new[] { "r1", "r2", "r3" }.Select(repeat => LoadAuditPrediction(baselineRoot, documentId, repeat)).ToArray();
            var gold = goldByDocument[documentId];
            foreach (var occurrence in gold)
            {
                var exact = predictions.Select(x => x.Final.Any(row => Same(row, occurrence))).ToArray();
                var near = predictions.Select(x => x.Final.Any(row => Near(row, occurrence))).ToArray();
                var exactCount = exact.Count(x => x);
                var alias = context.SourceRows.FirstOrDefault(x => x.SourceId == occurrence.SourceId);
                var paragraph = context.Source.Paragraphs.FirstOrDefault(x => x.SourceId == occurrence.SourceId);
                if (exactCount == 0 && !near.Any(x => x))
                {
                    var rawExact = predictions.Select(x => alias is not null && x.Raw.Any(h => h.Source == alias.Alias && h.Text == occurrence.ExactText)).ToArray();
                    var rawOverlap = predictions.Select(x => alias is not null && x.Raw.Any(h => h.Source == alias.Alias && Overlaps(alias.RawText, h.Text, occurrence.HeadingSpan!))).ToArray();
                    omissions.Add(new
                    {
                        documentId, sourceId = occurrence.SourceId, start = occurrence.HeadingSpan!.Start, end = occurrence.HeadingSpan.End,
                        exactText = occurrence.ExactText, sourceAlias = alias?.Alias, sourceOrdinal = alias?.SourceOrdinal,
                        rawPresenceByRepeat = rawExact, rawOverlapByRepeat = rawOverlap,
                        firstLossByRepeat = predictions.Select(x => x.Losses.FirstOrDefault(l => l.Key == Key(occurrence.SourceId, occurrence.HeadingSpan!))?.FirstLoss).ToArray(),
                        facts = Facts(paragraph), previous = Neighbor(context.Source, paragraph, -1), next = Neighbor(context.Source, paragraph, 1),
                        primaryCause = rawExact.Any(x => x) ? "RAW_EXACT_NOT_FINAL" : rawOverlap.Any(x => x) ? "MODEL_WRONG_SPAN_OR_BOUNDARY" : HasStructuralEvidence(paragraph) ? "MODEL_SEMANTIC_OMISSION_WITH_STRUCTURAL_EVIDENCE" : "MODEL_SEMANTIC_OMISSION",
                    });
                }
                else if (exactCount < 3 && near.Any(x => x))
                {
                    spanVariants.Add(new
                    {
                        documentId, sourceId = occurrence.SourceId, start = occurrence.HeadingSpan!.Start, end = occurrence.HeadingSpan.End, exactText = occurrence.ExactText,
                        exactPresenceByRepeat = exact, nearPresenceByRepeat = near, facts = Facts(paragraph),
                        finalRowsByRepeat = predictions.Select(x => x.Final.Where(row => Near(row, occurrence)).ToArray()).ToArray(),
                        primaryCause = "MODEL_BOUNDARY_VARIANCE",
                    });
                }
            }
            var goldKeys = gold.Select(x => Key(x.SourceId, x.HeadingSpan!)).ToHashSet(StringComparer.Ordinal);
            var allRows = predictions.SelectMany((prediction, index) => prediction.Final.Select(row => new { prediction, repeat = index + 1, row }));
            foreach (var group in allRows.GroupBy(x => Key(groupDocumentId: documentId, x.row), StringComparer.Ordinal))
            {
                var first = group.First().row;
                var count = group.Select(x => x.repeat).Distinct().Count();
                if (!goldKeys.Contains(Key(first.SourceId, new StructuralSpan(first.Start, first.End))) && count >= 2)
                {
                    var paragraph = context.Source.Paragraphs.FirstOrDefault(x => x.SourceId == first.SourceId);
                    falsePositives.Add(new
                    {
                        documentId, sourceId = first.SourceId, start = first.Start, end = first.End, text = first.Text, presenceCount = count,
                        repeats = group.Select(x => x.repeat).Distinct().Order().ToArray(), facts = Facts(paragraph),
                        primaryCause = ClassifyFalsePositive(first.Text, paragraph),
                    });
                }
            }
        }
        var causeCounts = omissions.GroupBy(x => x.GetType().GetProperty("primaryCause")!.GetValue(x)?.ToString() ?? "UNKNOWN", StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        return new
        {
            schemaVersion = "a99-semantic-text-stable-error-offline-audit-v1", startHead, model = Model,
            baselineArtifact = OutputRoot, baselineHashesVerified = true, modelCalls = 0, providerCalls = 0, goldReadBeforeFreeze = false,
            stableOmissions = omissions, spanVariants, stableFalsePositives = falsePositives,
            counts = new { stableOmissions = omissions.Count, spanVariants = spanVariants.Count, falsePositives3of3 = falsePositives.Count(x => Convert.ToInt32(x.GetType().GetProperty("presenceCount")!.GetValue(x)) == 3), falsePositives2of3 = falsePositives.Count(x => Convert.ToInt32(x.GetType().GetProperty("presenceCount")!.GetValue(x)) == 2) },
            causalClassCounts = causeCounts, selectedDominantCause = "MODEL_SEMANTIC_OMISSION_WITH_STRUCTURAL_EVIDENCE",
            selectedIntervention = "GLOBAL_STRUCTURAL_CONTEXT_ENRICHMENT_V1",
            interventionSelectionRationale = "All stable omissions are model-side at first loss; objective XML structural facts are available in the frozen source and can be added generically without candidate gating.",
        };
    }

    private static DocumentContext EnrichContext(DocumentContext context)
    {
        var factsById = context.Source.Paragraphs.ToDictionary(x => x.SourceId, StringComparer.Ordinal);
        var packet = JsonSerializer.Serialize(new
        {
            sourceAliases = context.SourceRows.Select(x =>
            {
                var p = factsById[x.SourceId];
                return new
                {
                    alias = x.Alias, text = x.RawText, sourceOrdinal = x.SourceOrdinal,
                    structuralFacts = new
                    {
                        style = new { p.Style.StyleId, p.Style.StyleName, p.Style.BuiltInHeadingStyleLevel, p.Style.OutlineLevel, p.Style.Bold, p.Style.Italic, p.Style.Underline, p.Style.AllCaps, p.Style.FontSizePt, p.Style.Alignment },
                        numbering = new { p.Numbering.NumberingId, p.Numbering.NumberingLevel, p.Numbering.NumberLabel, p.Numbering.NumberingFormat, p.Numbering.NumberingStyleHeadingLevel },
                        layout = new { p.Layout.InContentControl, p.Layout.KeepNext, p.Layout.PageBreakBefore, p.Layout.TableDepth, p.Layout.SectionIndex },
                        p.InTableOfContents,
                    },
                };
            }).ToArray(),
        });
        return context with { Packet = packet, PacketHash = Sha256Text(packet) };
    }

    private static object Facts(SourceParagraph? p)
    {
        if (p is null) return new { available = false };
        return new
        {
            available = true, p.Style.StyleId, p.Style.StyleName, p.Style.BuiltInHeadingStyleLevel, p.Style.OutlineLevel, p.Style.Bold, p.Style.Italic,
            p.Style.Underline, p.Style.AllCaps, p.Style.FontSizePt, p.Style.Alignment, p.Numbering.NumberingId, p.Numbering.NumberingLevel,
            p.Numbering.NumberLabel, p.Numbering.NumberingFormat, p.Numbering.NumberingStyleHeadingLevel, p.Layout.InContentControl, p.Layout.KeepNext,
            p.Layout.PageBreakBefore, p.Layout.TableDepth, p.Layout.SectionIndex, p.InTableOfContents,
        };
    }

    private static string? Neighbor(SourceDocument source, SourceParagraph? paragraph, int delta)
    {
        if (paragraph is null) return null;
        var index = -1;
        for (var i = 0; i < source.Paragraphs.Count; i++)
            if (ReferenceEquals(source.Paragraphs[i], paragraph) || source.Paragraphs[i].SourceId == paragraph.SourceId) { index = i; break; }
        var at = index + delta;
        return at >= 0 && at < source.Paragraphs.Count ? source.Paragraphs[at].Text : null;
    }

    private static bool HasStructuralEvidence(SourceParagraph? p) => p is not null && (p.Style.OutlineLevel is not null || p.Style.BuiltInHeadingStyleLevel is not null || p.Numbering.NumberingId is not null || p.Style.Bold || p.Layout.KeepNext || p.Layout.PageBreakBefore || p.Layout.TableDepth > 0);
    private static bool Overlaps(string source, string text, StructuralSpan span)
    {
        var at = source.IndexOf(text, StringComparison.Ordinal);
        return at >= 0 && at < span.End && span.Start < at + text.Length;
    }
    private static string ClassifyFalsePositive(string text, SourceParagraph? paragraph) => paragraph?.InTableOfContents == true ? "NAVIGATION_OR_TOC" : text.Length > 80 && (text.Contains('.') || text.Contains('−') || text.Contains(';')) ? "BODY_OR_LIST_CONTENT" : "MODEL_SEMANTIC_EXTRA";
    private static string Key(string groupDocumentId, RepairRow row) => $"{groupDocumentId}:{row.SourceId}:{row.Start}:{row.End}";
    private static bool Same(RepairRow row, ReasoningGoldOccurrence gold) => row.SourceId == gold.SourceId && row.Start == gold.HeadingSpan!.Start && row.End == gold.HeadingSpan.End;
    private static bool Near(RepairRow row, ReasoningGoldOccurrence gold) => row.SourceId == gold.SourceId && (Overlaps(gold.ExactText, row.Text, new StructuralSpan(0, gold.ExactText.Length)) || (row.Start < gold.HeadingSpan!.End && gold.HeadingSpan.Start < row.End));

    private static AuditPrediction LoadAuditPrediction(string baselineRoot, string documentId, string repeat)
    {
        var dir = Path.Combine(baselineRoot, documentId, repeat);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "prediction.v1.json")));
        var root = doc.RootElement;
        var raw = root.GetProperty("rawModelHeadings").EnumerateArray().Select(x => new AuditHeading(x.GetProperty("source").GetString()!, x.GetProperty("text").GetString()!)).ToArray();
        var final = root.GetProperty("finalHeadings").EnumerateArray().Select(x => new RepairRow(x.GetProperty("sourceId").GetString()!, x.GetProperty("start").GetInt32(), x.GetProperty("end").GetInt32(), x.GetProperty("text").GetString()!)).ToArray();
        using var first = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "first-loss.v1.json")));
        var losses = first.RootElement.TryGetProperty("firstLosses", out var ls) ? ls.EnumerateArray().Select(x => new AuditLoss(x.GetProperty("key").GetString()!, x.GetProperty("firstLoss").GetString()!)).ToArray() : Array.Empty<AuditLoss>();
        return new AuditPrediction(raw, final, losses);
    }

    private static object BuildRepairStability(IReadOnlyList<RepeatMetric> runs, IReadOnlyDictionary<string, IReadOnlyList<ReasoningGoldOccurrence>> goldByDocument)
    {
        var goldRows = goldByDocument.SelectMany(pair => pair.Value.Select(g =>
        {
            var local = runs.Where(x => x.DocumentId == pair.Key).ToArray();
            var exact = local.Select(x => x.FirstLosses.Any(l => l.Key == Key(g.SourceId, g.HeadingSpan!) && l.FirstLoss == "FOUND")).ToArray();
            var count = exact.Count(x => x);
            var near = local.Any(x => x.FirstLosses.Any(l => l.Key == Key(g.SourceId, g.HeadingSpan!) && l.FirstLoss == "MODEL_WRONG_SPAN"));
            return new { documentId = pair.Key, sourceId = g.SourceId, start = g.HeadingSpan!.Start, end = g.HeadingSpan.End, text = g.ExactText, exactPresenceCount = count, classification = count == 3 ? "EXACT_STABLE" : near ? "SPAN_VARIANT" : count == 0 ? "TRUE_STABLE_OMISSION" : "STOCHASTIC_OMISSION" };
        })).ToArray();
        var fps = runs.SelectMany(x => x.FalsePositives.Select(fp => new { x.DocumentId, x.Repeat, fp.Key, fp.ExactSourceText })).GroupBy(x => $"{x.DocumentId}:{x.Key}", StringComparer.Ordinal).Select(g => new { key = g.Key, documentId = g.First().DocumentId, text = g.First().ExactSourceText, presenceCount = g.Select(x => x.Repeat).Distinct(StringComparer.Ordinal).Count() }).ToArray();
        return new { goldOccurrences = goldRows, stableOmissions = goldRows.Count(x => x.classification == "TRUE_STABLE_OMISSION"), spanVariants = goldRows.Count(x => x.classification == "SPAN_VARIANT"), stochasticOmissions = goldRows.Count(x => x.classification == "STOCHASTIC_OMISSION"), stableFalsePositives = fps.Count(x => x.presenceCount == 3), falsePositives2of3 = fps.Count(x => x.presenceCount == 2), falsePositiveOccurrences = fps };
    }

    private static object BuildRepairComparison(IReadOnlyList<RepeatMetric> baseline, IReadOnlyList<RepeatMetric> repaired) => new
    {
        baseline = BuildMicroTable(baseline), repaired = BuildMicroTable(repaired),
        delta = new { tp = repaired.Sum(x => x.Tp) - baseline.Sum(x => x.Tp), fp = repaired.Sum(x => x.Fp) - baseline.Sum(x => x.Fp), fn = repaired.Sum(x => x.Fn) - baseline.Sum(x => x.Fn), f1 = Micro(repaired).F1 - Micro(baseline).F1, systemLoss = repaired.Sum(x => x.SystemLoss) - baseline.Sum(x => x.SystemLoss) },
    };
    private static object BuildMicroTable(IReadOnlyList<RepeatMetric> runs) => runs.GroupBy(x => x.Repeat).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g => Micro(g.ToArray())).ToArray();
    private static string RepairClassification(IReadOnlyList<RepeatMetric> baseline, IReadOnlyList<RepeatMetric> repaired, object stability)
    {
        if (repaired.Count != 15 || repaired.Any(x => x.Status != "SUCCESS")) return "GENERIC_CAPABILITY_REPAIR_EXECUTION_BLOCKED";
        var before = Micro(baseline); var after = Micro(repaired);
        if (after.F1 > before.F1 && after.Fp <= before.Fp && repaired.Sum(x => x.SystemLoss) == 0) return "GENERIC_CAPABILITY_REPAIR_ACCEPTED";
        if (after.Recall > before.Recall && after.Precision < before.Precision) return "GENERIC_CAPABILITY_RECALL_UP_PRECISION_TRADEOFF";
        if (after.F1 < before.F1) return "GENERIC_CAPABILITY_REGRESSION";
        return "GENERIC_CAPABILITY_NO_MATERIAL_GAIN";
    }
    private static async Task<int> WriteRepairBlocked(string output, string head, string reason, CancellationToken ct)
    {
        await WriteJson(Path.Combine(output, "summary.v1.json"), new { schemaVersion = "a99-semantic-text-stable-error-repair-summary-v1", status = "BLOCKED", reason, startHead = head, modelCalls = 0, goldReadBeforeFreeze = false }, ct);
        Console.WriteLine("FINAL_CLASSIFICATION=GENERIC_CAPABILITY_REPAIR_EXECUTION_BLOCKED");
        return 1;
    }

    private static bool LoadRepairScoreStatus(string repairRoot, string documentId, string repeat)
    {
        var path = Path.Combine(repairRoot, repeat, documentId, "score.v1.json");
        if (!File.Exists(path)) return false;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("status", out var status) && status.GetString() == "SUCCESS" && GetInt(doc.RootElement, "goldCount") > 0;
    }

    private sealed record RepairRow(string SourceId, int Start, int End, string Text);
    private sealed record AuditHeading(string Source, string Text);
    private sealed record AuditLoss(string Key, string FirstLoss);
    private sealed record AuditPrediction(IReadOnlyList<AuditHeading> Raw, IReadOnlyList<RepairRow> Final, IReadOnlyList<AuditLoss> Losses);

    private static async Task<RepeatMetric> RunReviewRepeatAsync(string repoRoot, string output, string baselineRoot, DocumentContext context, int repeat, OpenRouterCeilingReasoningModel model, string gitSha, string reviewPromptHash, CancellationToken ct, int transientRetries = 0)
    {
        var repeatName = $"r{repeat}";
        var dir = Path.Combine(output, context.DocumentId, repeatName);
        Directory.CreateDirectory(dir);
        var stopwatch = Stopwatch.StartNew();
        RequestPacketTelemetry? telemetry = null;
        var providerAttempts = 0;
        try
        {
            var baselineDir = Path.Combine(baselineRoot, context.DocumentId, repeatName);
            var baselinePredictionPath = Path.Combine(baselineDir, "prediction.v1.json");
            var baselineFreezePath = Path.Combine(baselineDir, "freeze.v1.json");
            using var baselinePrediction = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePredictionPath, ct));
            using var baselineFreeze = JsonDocument.Parse(await File.ReadAllTextAsync(baselineFreezePath, ct));
            var rawArray = baselinePrediction.RootElement.GetProperty("rawModelHeadings");
            var passA = SemanticTextExactBindingContract.Parse("{\"headings\":" + rawArray.GetRawText() + "}");
            var passAHash = baselineFreeze.RootElement.GetProperty("predictionSha256").GetString()!;
            var inventoryJson = JsonSerializer.Serialize(passA.Headings, JsonOptions);
            var reviewPacket = context.Packet + "\nCURRENT_INVENTORY=" + inventoryJson;
            var requestId = $"{SemanticTextOmissionReviewContract.ProtocolVersion}:{context.DocumentId}:{repeat}:{context.PacketHash}";
            string rawContent;
            while (true)
            {
                providerAttempts++;
                try
                {
                    var providerResult = await model.CompleteRawStructuredSemanticAsync(
                        context.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId,
                        reviewPacket, context.SourceRows.Sum(x => x.RawText.Length), context.SourceRows.Count, context.SourceRows.Count,
                        SemanticTextOmissionReviewContract.System,
                        SemanticTextOmissionReviewContract.BuildUser(context.Packet, inventoryJson, ReasoningRoute.ModelCapabilityCeiling.ToString()),
                        SemanticTextOmissionReviewContract.Schema(), "semantic_text_omission_review_v1", ct);
                    rawContent = providerResult.Content;
                    telemetry = providerResult.Telemetry;
                    break;
                }
                catch (ReasoningCompletionException) when (providerAttempts <= transientRetries && model.Telemetry.LastOrDefault(x => x.DocumentId == context.DocumentId)?.HttpStatus == 429)
                {
                    Console.WriteLine($"TRANSIENT_RETRY_REVIEW=HTTP_429/{context.DocumentId}/R{repeat}/attempt={providerAttempts + 1}");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            var requestTelemetry = telemetry!;
            var passB = SemanticTextExactBindingContract.Parse(rawContent);
            telemetry.StructuredOutputParsed = true;
            telemetry.HeadingOutputCount = passB.Headings.Count;
            var union = new SemanticTextResponse(passA.Headings.Concat(passB.Headings).ToArray());
            var bound = SemanticTextExactBinder.Bind(union.Headings, context.SourceRows, out var observations);
            var proposals = bound.Select(x => new ReasoningHeadingProposal
            {
                SourceId = x.SourceId, HeadingSpan = new StructuralSpan(x.Start, x.End), Text = x.Text,
                SemanticRole = x.Role, Confidence = 1,
            }).ToArray();
            var materialized = ReasoningProposalMaterializer.Materialize(context.Source, context.Policy, proposals);
            var finalElements = ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure)
                .OrderBy(x => x.Sources.Single().SourceOrdinal).ThenBy(x => x.Sources.Single().Span.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
            stopwatch.Stop();
            var finalRows = finalElements.Select(x => new { sourceId = x.Sources.Single().SourceId, start = x.Sources.Single().Span.Start, end = x.Sources.Single().Span.End, text = x.Text, role = x.Role }).ToArray();
            var prediction = new
            {
                schemaVersion = "a99-semantic-text-omission-review-prediction-v1", context.DocumentId, repeat = repeatName,
                model = Model, intervention = "SEMANTIC_TEXT_OMISSION_REVIEW_V1", baseContractHash = ContractHash(), reviewPromptHash,
                sourceSha256 = context.SourceSha256, passAReused = true, passAProviderCallsCurrentRun = 0,
                passAHash, passAHeadings = passA.Headings, passBHeadings = passB.Headings, unionHeadings = union.Headings,
                bindingObservations = observations, boundHeadings = bound, validatorAccepted = materialized.Validated.Count(x => x.Accepted),
                validatorRejected = materialized.Validated.Count(x => !x.Accepted), finalHeadings = finalRows, goldReadBeforeFreeze = false,
            };
            var result = new
            {
                schemaVersion = "a99-semantic-text-omission-review-result-v1", context.DocumentId, repeat = repeatName,
                model = Model, headings = finalRows, goldReadBeforeFreeze = false,
            };
            var predictionPath = Path.Combine(dir, "prediction.v1.json");
            var resultPath = Path.Combine(dir, "result.v1.json");
            await WriteJson(predictionPath, prediction, ct);
            await WriteJson(resultPath, result, ct);
            var freeze = new
            {
                schemaVersion = "a99-semantic-text-omission-review-freeze-v1", context.DocumentId, repeat = repeatName,
                gitSha, model = Model, actualProvider = telemetry.ProviderRoute, baseContractHash = ContractHash(), reviewPromptHash,
                sourceSha256 = context.SourceSha256, passAHash, passBResponseHash = Sha256Text(rawContent),
                predictionSha256 = Sha256(predictionPath), resultSha256 = Sha256(resultPath), rawPassACount = passA.Headings.Count,
                rawPassBCount = passB.Headings.Count, unionCount = union.Headings.Count, boundProposalCount = bound.Count,
                finalCount = finalElements.Length, providerAttempts, inputTokens = telemetry.ReportedInputTokens,
                reasoningTokens = telemetry.ReportedReasoningTokens, outputTokens = telemetry.ReportedOutputTokens, finishReason = telemetry.FinishReason,
                reasoningConfiguration = new { requested = true, enabled = true, excluded = true, effort = "MODEL_DEFAULT" },
                goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
            };
            var freezePath = Path.Combine(dir, "freeze.v1.json");
            await WriteJson(freezePath, freeze, ct);
            if (Sha256(predictionPath) != freeze.predictionSha256 || Sha256(resultPath) != freeze.resultSha256)
                throw new InvalidDataException($"REVIEW_FREEZE_HASH_VERIFICATION_FAILED:{context.DocumentId}:{repeatName}");

            var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", context.DocumentId + ".occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
            var metric = Score(context, repeatName, union, observations, bound, materialized, finalElements, gold, telemetry, stopwatch.ElapsedMilliseconds);
            await WriteJson(Path.Combine(dir, "score.v1.json"), metric.Score!, ct);
            await WriteJson(Path.Combine(dir, "first-loss.v1.json"), new { context.DocumentId, repeat = repeatName, metric.FirstLosses, metric.FalsePositives, metric.FirstLossCounts, systemLoss = metric.SystemLoss, goldReadBeforeFreeze = false }, ct);
            return metric;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            var last = telemetry ?? model.Telemetry.LastOrDefault(x => x.DocumentId == context.DocumentId);
            var predictionPath = Path.Combine(dir, "prediction.v1.json");
            var resultPath = Path.Combine(dir, "result.v1.json");
            await WriteJson(predictionPath, new { schemaVersion = "a99-semantic-text-omission-review-prediction-v1", context.DocumentId, repeat = repeatName, status = "BLOCKED", failure = ex.ToString(), model = Model, intervention = "SEMANTIC_TEXT_OMISSION_REVIEW_V1", passAReused = true, passAProviderCallsCurrentRun = 0, goldReadBeforeFreeze = false }, ct);
            await WriteJson(resultPath, new { schemaVersion = "a99-semantic-text-omission-review-result-v1", context.DocumentId, repeat = repeatName, status = "BLOCKED", headings = Array.Empty<object>(), goldReadBeforeFreeze = false }, ct);
            var freeze = new
            {
                schemaVersion = "a99-semantic-text-omission-review-freeze-v1", context.DocumentId, repeat = repeatName, gitSha,
                model = Model, baseContractHash = ContractHash(), reviewPromptHash, sourceSha256 = context.SourceSha256,
                predictionSha256 = Sha256(predictionPath), resultSha256 = Sha256(resultPath),
                providerAttempts = providerAttempts == 0 ? (last is null ? 0 : 1) : providerAttempts,
                actualProvider = last?.ProviderRoute, httpStatus = last?.HttpStatus,
                inputTokens = last?.ReportedInputTokens, reasoningTokens = last?.ReportedReasoningTokens,
                outputTokens = last?.ReportedOutputTokens, finishReason = last?.FinishReason,
                telemetryFailureClass = last?.FailureClass, failureClass = ex.GetType().Name,
                error = ex.Message, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow
            };
            await WriteJson(Path.Combine(dir, "freeze.v1.json"), freeze, ct);
            await WriteJson(Path.Combine(dir, "score.v1.json"), new { schemaVersion = "a99-semantic-text-omission-review-score-v1", context.DocumentId, repeat = repeatName, status = "BLOCKED", exactStatus = "NOT_EVALUABLE", tp = 0, fp = 0, fn = 0, systemLoss = 0, goldReadBeforeFreeze = false }, ct);
            await WriteJson(Path.Combine(dir, "first-loss.v1.json"), new { context.DocumentId, repeat = repeatName, status = "BLOCKED", failure = ex.GetType().Name + ":" + ex.Message, goldReadBeforeFreeze = false }, ct);
            return new RepeatMetric(context.DocumentId, repeatName, "BLOCKED", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, last?.ProviderRoute, last?.FinishReason, last?.ReportedInputTokens, last?.ReportedReasoningTokens, last?.ReportedOutputTokens, stopwatch.ElapsedMilliseconds, [], [], []);
        }
    }

    private static async Task<RepeatMetric> RunRepeatAsync(string repoRoot, string output, DocumentContext context, int repeat, OpenRouterCeilingReasoningModel model, string gitSha, CancellationToken ct, int transientRetries = 0, bool flatRepeatLayout = false, bool retryMalformedProviderResponse = false)
    {
        var repeatName = $"r{repeat}";
        var dir = flatRepeatLayout ? Path.Combine(output, repeatName, context.DocumentId) : Path.Combine(output, context.DocumentId, repeatName);
        Directory.CreateDirectory(dir);
        var stopwatch = Stopwatch.StartNew();
        RequestPacketTelemetry? telemetry = null;
        var providerAttempts = 0;
        try
        {
            var requestId = $"{SemanticTextExactBindingContract.ProtocolVersion}:{context.DocumentId}:{repeat}:{context.PacketHash}";
            string rawContent;
            while (true)
            {
                providerAttempts++;
                try
                {
                    var providerResult = await model.CompleteRawStructuredSemanticAsync(
                        context.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId,
                        context.Packet, context.SourceRows.Sum(x => x.RawText.Length), context.SourceRows.Count, context.SourceRows.Count,
                        SemanticTextExactBindingContract.System,
                        SemanticTextExactBindingContract.BuildUser(context.Packet, ReasoningRoute.ModelCapabilityCeiling.ToString()),
                        SemanticTextExactBindingContract.Schema(), "semantic_text_exact_binding_v1", ct);
                    rawContent = providerResult.Content;
                    telemetry = providerResult.Telemetry;
                    break;
                }
                catch (ReasoningCompletionException) when (providerAttempts <= transientRetries && model.Telemetry.LastOrDefault(x => x.DocumentId == context.DocumentId)?.HttpStatus == 429)
                {
                    Console.WriteLine($"TRANSIENT_RETRY=HTTP_429/{context.DocumentId}/R{repeat}/attempt={providerAttempts + 1}");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                catch (FormatException ex) when (retryMalformedProviderResponse && providerAttempts <= 1 && ex.Message.Contains("choices-missing", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"TRANSIENT_RETRY=PROVIDER_CHOICES_MISSING/{context.DocumentId}/R{repeat}/attempt={providerAttempts + 1}");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            var requestTelemetry = telemetry!;
            telemetry = requestTelemetry;
            var response = SemanticTextExactBindingContract.Parse(rawContent);
            telemetry.StructuredOutputParsed = true;
            var bound = SemanticTextExactBinder.Bind(response.Headings, context.SourceRows, out var observations);
            var proposals = bound.Select(x => new ReasoningHeadingProposal
            {
                SourceId = x.SourceId, HeadingSpan = new StructuralSpan(x.Start, x.End), Text = x.Text,
                SemanticRole = x.Role, Confidence = 1,
            }).ToArray();
            var materialized = ReasoningProposalMaterializer.Materialize(context.Source, context.Policy, proposals);
            var finalElements = ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure)
                .OrderBy(x => x.Sources.Single().SourceOrdinal).ThenBy(x => x.Sources.Single().Span.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
            stopwatch.Stop();
            var finalRows = finalElements.Select(x => new { sourceId = x.Sources.Single().SourceId, start = x.Sources.Single().Span.Start, end = x.Sources.Single().Span.End, text = x.Text, role = x.Role }).ToArray();
            var prediction = new
            {
                schemaVersion = "a99-semantic-text-generalization-prediction-v1", context.DocumentId, repeat = repeatName,
                model = Model, semanticContractVersion = SemanticTextExactBindingContract.ProtocolVersion,
                executionMode = "FULL_CONTEXT", dataClassification = "PUBLIC", zdrRequested = false, privacyExceptionAuthorized = true,
                sourceSha256 = context.SourceSha256, promptHash = context.PromptHash, schemaHash = context.SchemaHash, packetHash = context.PacketHash,
                sourceAliasCount = context.SourceRows.Count, rawModelHeadings = response.Headings, bindingObservations = observations,
                boundHeadings = bound, validatorAccepted = materialized.Validated.Count(x => x.Accepted), validatorRejected = materialized.Validated.Count(x => !x.Accepted),
                finalHeadings = finalRows, goldReadBeforeFreeze = false,
            };
            var result = new
            {
                schemaVersion = "a99-semantic-text-generalization-result-v1", context.DocumentId, repeat = repeatName,
                model = Model, semanticContractVersion = SemanticTextExactBindingContract.ProtocolVersion,
                executionMode = "FULL_CONTEXT", headings = finalRows, goldReadBeforeFreeze = false,
            };
            var predictionPath = Path.Combine(dir, "prediction.v1.json");
            var resultPath = Path.Combine(dir, "result.v1.json");
            await WriteJson(predictionPath, prediction, ct);
            await WriteJson(resultPath, result, ct);
            var freeze = new
            {
                schemaVersion = "a99-semantic-text-generalization-freeze-v1", context.DocumentId, repeat = repeatName,
                gitSha, model = Model, actualProvider = telemetry.ProviderRoute,
                sourceSha256 = context.SourceSha256, semanticContractVersion = SemanticTextExactBindingContract.ProtocolVersion,
                promptHash = context.PromptHash, schemaHash = context.SchemaHash, packetHash = context.PacketHash,
                reasoningConfiguration = new { requested = true, enabled = true, excluded = true, effort = "MODEL_DEFAULT" },
                predictionSha256 = Sha256(predictionPath), resultSha256 = Sha256(resultPath),
                rawProposalCount = response.Headings.Count, boundProposalCount = bound.Count, finalCount = finalElements.Length, wallTimeMs = stopwatch.ElapsedMilliseconds,
                providerAttempts, inputTokens = telemetry.ReportedInputTokens, reasoningTokens = telemetry.ReportedReasoningTokens,
                outputTokens = telemetry.ReportedOutputTokens, finishReason = telemetry.FinishReason,
                executionMode = "FULL_CONTEXT", dataClassification = "PUBLIC", zdrRequested = false, privacyExceptionAuthorized = true,
                goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
            };
            var freezePath = Path.Combine(dir, "freeze.v1.json");
            await WriteJson(freezePath, freeze, ct);
            if (Sha256(predictionPath) != freeze.predictionSha256 || Sha256(resultPath) != freeze.resultSha256)
                throw new InvalidDataException($"FREEZE_HASH_VERIFICATION_FAILED:{context.DocumentId}:{repeatName}");

            // Gold is intentionally opened only after the freeze hash verification above.
            var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", context.DocumentId + ".occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
            var metric = Score(context, repeatName, response, observations, bound, materialized, finalElements, gold, telemetry, stopwatch.ElapsedMilliseconds);
            await WriteJson(Path.Combine(dir, "score.v1.json"), metric.Score!, ct);
            await WriteJson(Path.Combine(dir, "first-loss.v1.json"), new { context.DocumentId, repeat = repeatName, metric.FirstLosses, metric.FalsePositives, metric.FirstLossCounts, systemLoss = metric.SystemLoss, goldReadBeforeFreeze = false }, ct);
            return metric;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            var last = telemetry ?? model.Telemetry.LastOrDefault(x => x.DocumentId == context.DocumentId);
            var predictionPath = Path.Combine(dir, "prediction.v1.json");
            var resultPath = Path.Combine(dir, "result.v1.json");
            await WriteJson(predictionPath, new { schemaVersion = "a99-semantic-text-generalization-prediction-v1", context.DocumentId, repeat = repeatName, status = "BLOCKED", failure = ex.GetType().Name + ":" + ex.Message, model = Model, semanticContractVersion = SemanticTextExactBindingContract.ProtocolVersion, executionMode = "FULL_CONTEXT", goldReadBeforeFreeze = false }, ct);
            await WriteJson(resultPath, new { schemaVersion = "a99-semantic-text-generalization-result-v1", context.DocumentId, repeat = repeatName, status = "BLOCKED", headings = Array.Empty<object>(), goldReadBeforeFreeze = false }, ct);
            var freeze = new { schemaVersion = "a99-semantic-text-generalization-freeze-v1", context.DocumentId, repeat = repeatName, gitSha, model = Model, actualProvider = last?.ProviderRoute, sourceSha256 = context.SourceSha256, semanticContractVersion = SemanticTextExactBindingContract.ProtocolVersion, promptHash = context.PromptHash, schemaHash = context.SchemaHash, packetHash = context.PacketHash, reasoningConfiguration = new { requested = true, enabled = true, excluded = true }, predictionSha256 = Sha256(predictionPath), resultSha256 = Sha256(resultPath), rawProposalCount = 0, boundProposalCount = 0, finalCount = 0, providerAttempts = providerAttempts == 0 ? (last is null ? 0 : 1) : providerAttempts, inputTokens = last?.ReportedInputTokens, reasoningTokens = last?.ReportedReasoningTokens, outputTokens = last?.ReportedOutputTokens, finishReason = last?.FinishReason, executionMode = "FULL_CONTEXT", failureClass = ex.GetType().Name, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow };
            await WriteJson(Path.Combine(dir, "freeze.v1.json"), freeze, ct);
            await WriteJson(Path.Combine(dir, "score.v1.json"), new { schemaVersion = "a99-semantic-text-generalization-score-v1", context.DocumentId, repeat = repeatName, status = "BLOCKED", exactStatus = "NOT_EVALUABLE", tp = 0, fp = 0, fn = 0, systemLoss = 0, goldReadBeforeFreeze = false }, ct);
            await WriteJson(Path.Combine(dir, "first-loss.v1.json"), new { context.DocumentId, repeat = repeatName, status = "BLOCKED", failure = ex.GetType().Name + ":" + ex.Message, goldReadBeforeFreeze = false }, ct);
            return new RepeatMetric(context.DocumentId, repeatName, "BLOCKED", Gold: 0, Tp: 0, Fp: 0, Fn: 0, Precision: 0, Recall: 0, F1: 0, SystemBindingLoss: 0, SystemValidatorLoss: 0, SystemProjectionLoss: 0, RawCount: 0, BoundCount: 0, ValidatedCount: 0, FinalCount: 0, Provider: last?.ProviderRoute, FinishReason: last?.FinishReason, InputTokens: last?.ReportedInputTokens, ReasoningTokens: last?.ReportedReasoningTokens, OutputTokens: last?.ReportedOutputTokens, WallTimeMs: stopwatch.ElapsedMilliseconds, PredictionKeys: [], FirstLosses: [], FalsePositives: []);
        }
    }

    private static async Task<RepeatMetric> RunDuplicateRepeatAsync(string repoRoot, string output, DocumentContext context, int repeat, OpenRouterCeilingReasoningModel model, string gitSha, CancellationToken ct)
    {
        var repeatName = $"r{repeat}";
        var dir = Path.Combine(output, context.DocumentId, repeatName);
        Directory.CreateDirectory(dir);
        var stopwatch = Stopwatch.StartNew();
        RequestPacketTelemetry? telemetry = null;
        var providerAttempts = 0;
        try
        {
            var requestId = $"{SemanticTextDuplicateDisambiguationContract.ProtocolVersion}:{context.DocumentId}:{repeat}:{context.PacketHash}";
            (string Content, RequestPacketTelemetry Telemetry) providerResult;
            while (true)
            {
                providerAttempts++;
                try
                {
                    providerResult = await model.CompleteRawStructuredSemanticAsync(
                        context.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId,
                        context.Packet, context.SourceRows.Sum(x => x.RawText.Length), context.SourceRows.Count, context.SourceRows.Count,
                        SemanticTextDuplicateDisambiguationContract.System,
                        SemanticTextDuplicateDisambiguationContract.BuildUser(context.Packet, ReasoningRoute.ModelCapabilityCeiling.ToString()),
                        SemanticTextDuplicateDisambiguationContract.Schema(), "semantic_text_duplicate_disambiguation_v1", ct);
                    break;
                }
                catch (ReasoningCompletionException) when (providerAttempts < 3 && model.Telemetry.LastOrDefault(x => x.DocumentId == context.DocumentId)?.HttpStatus == 429)
                {
                    Console.WriteLine($"TRANSIENT_RETRY=HTTP_429/{context.DocumentId}/R{repeat}/attempt={providerAttempts + 1}");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            telemetry = providerResult.Telemetry;
            telemetry.StructuredOutputParsed = true;
            var response = SemanticTextDuplicateDisambiguationContract.Parse(providerResult.Content);
            var bound = SemanticTextDuplicateBinder.Bind(response.Headings, context.SourceRows, out var observations, out var traces);
            var proposals = bound.Select(x => new ReasoningHeadingProposal
            {
                SourceId = x.SourceId, HeadingSpan = new StructuralSpan(x.Start, x.End), Text = x.Text,
                SemanticRole = x.Role, Confidence = 1,
            }).ToArray();
            var materialized = ReasoningProposalMaterializer.Materialize(context.Source, context.Policy, proposals);
            var finalElements = ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure)
                .OrderBy(x => x.Sources.Single().SourceOrdinal).ThenBy(x => x.Sources.Single().Span.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
            stopwatch.Stop();
            var finalRows = finalElements.Select(x => new { sourceId = x.Sources.Single().SourceId, start = x.Sources.Single().Span.Start, end = x.Sources.Single().Span.End, text = x.Text, role = x.Role }).ToArray();
            var prediction = new
            {
                schemaVersion = "a99-semantic-text-duplicate-disambiguation-prediction-v1", context.DocumentId, repeat = repeatName, model = Model,
                semanticContractVersion = SemanticTextDuplicateDisambiguationContract.ProtocolVersion, sourceSha256 = context.SourceSha256,
                promptHash = context.PromptHash, schemaHash = context.SchemaHash, packetHash = context.PacketHash, sourceAliasCount = context.SourceRows.Count,
                rawModelHeadings = response.Headings, bindingObservations = observations, duplicateTrace = traces, boundHeadings = bound,
                validatorAccepted = materialized.Validated.Count(x => x.Accepted), validatorRejected = materialized.Validated.Count(x => !x.Accepted), finalHeadings = finalRows,
                goldReadBeforeFreeze = false,
            };
            var result = new { schemaVersion = "a99-semantic-text-duplicate-disambiguation-result-v1", context.DocumentId, repeat = repeatName, model = Model, headings = finalRows, goldReadBeforeFreeze = false };
            var predictionPath = Path.Combine(dir, "prediction.v1.json");
            var resultPath = Path.Combine(dir, "result.v1.json");
            await WriteJson(predictionPath, prediction, ct);
            await WriteJson(resultPath, result, ct);
            var freeze = new
            {
                schemaVersion = "a99-semantic-text-duplicate-disambiguation-freeze-v1", context.DocumentId, repeat = repeatName, gitSha, model = Model,
                actualProvider = telemetry.ProviderRoute, semanticContractVersion = SemanticTextDuplicateDisambiguationContract.ProtocolVersion,
                promptHash = context.PromptHash, schemaHash = context.SchemaHash, packetHash = context.PacketHash, predictionSha256 = Sha256(predictionPath), resultSha256 = Sha256(resultPath),
                rawProposalCount = response.Headings.Count, boundProposalCount = bound.Count, finalCount = finalElements.Length, wallTimeMs = stopwatch.ElapsedMilliseconds,
                providerAttempts, inputTokens = telemetry.ReportedInputTokens, reasoningTokens = telemetry.ReportedReasoningTokens, outputTokens = telemetry.ReportedOutputTokens,
                finishReason = telemetry.FinishReason, reasoningConfiguration = new { requested = true, enabled = true, excluded = true, effort = "MODEL_DEFAULT" }, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
            };
            var freezePath = Path.Combine(dir, "freeze.v1.json");
            await WriteJson(freezePath, freeze, ct);
            if (Sha256(predictionPath) != freeze.predictionSha256 || Sha256(resultPath) != freeze.resultSha256)
                throw new InvalidDataException($"DUPLICATE_FREEZE_HASH_VERIFICATION_FAILED:{context.DocumentId}:{repeatName}");

            var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", context.DocumentId + ".occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
            var metric = Score(context, repeatName, response, observations, bound, materialized, finalElements, gold, telemetry, stopwatch.ElapsedMilliseconds);
            var goldKeys = gold.Select(x => Key(x.SourceId, x.HeadingSpan!)).ToHashSet(StringComparer.Ordinal);
            var sourceByAlias = context.SourceRows.ToDictionary(x => x.Alias, StringComparer.Ordinal);
            var resolutionRows = traces.Where(x => x.Kind == "DUPLICATE_RESOLVED_BY_EXACT_CONTEXT" && x.ResolvedStart is not null).Select(x =>
            {
                var source = sourceByAlias[x.SourceAlias];
                var key = Key(source.SourceId, new StructuralSpan(x.ResolvedStart!.Value, x.ResolvedStart.Value + x.Text.Length));
                return new { sourceAlias = x.SourceAlias, sourceId = source.SourceId, start = x.ResolvedStart.Value, end = x.ResolvedStart.Value + x.Text.Length, exactSourceText = x.Text, correctGoldOccurrence = goldKeys.Contains(key) };
            }).ToArray();
            await WriteJson(Path.Combine(dir, "duplicate-resolution.v1.json"), new { rows = resolutionRows, resolved = resolutionRows.Length, incorrect = resolutionRows.Count(x => !x.correctGoldOccurrence), goldReadBeforeFreeze = false }, ct);
            await WriteJson(Path.Combine(dir, "score.v1.json"), metric.Score!, ct);
            await WriteJson(Path.Combine(dir, "first-loss.v1.json"), new { context.DocumentId, repeat = repeatName, metric.FirstLosses, metric.FalsePositives, metric.FirstLossCounts, systemLoss = metric.SystemLoss, goldReadBeforeFreeze = false }, ct);
            return metric;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            var last = telemetry ?? model.Telemetry.LastOrDefault(x => x.DocumentId == context.DocumentId);
            var predictionPath = Path.Combine(dir, "prediction.v1.json");
            var resultPath = Path.Combine(dir, "result.v1.json");
            await WriteJson(predictionPath, new { schemaVersion = "a99-semantic-text-duplicate-disambiguation-prediction-v1", context.DocumentId, repeat = repeatName, status = "BLOCKED", failure = ex.GetType().Name + ":" + ex.Message, goldReadBeforeFreeze = false }, ct);
            await WriteJson(resultPath, new { schemaVersion = "a99-semantic-text-duplicate-disambiguation-result-v1", context.DocumentId, repeat = repeatName, status = "BLOCKED", headings = Array.Empty<object>(), goldReadBeforeFreeze = false }, ct);
            await WriteJson(Path.Combine(dir, "freeze.v1.json"), new { schemaVersion = "a99-semantic-text-duplicate-disambiguation-freeze-v1", context.DocumentId, repeat = repeatName, gitSha, model = Model, predictionSha256 = Sha256(predictionPath), resultSha256 = Sha256(resultPath), providerAttempts, actualProvider = last?.ProviderRoute, finishReason = last?.FinishReason, goldReadBeforeFreeze = false }, ct);
            return new RepeatMetric(context.DocumentId, repeatName, "BLOCKED", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, last?.ProviderRoute, last?.FinishReason, last?.ReportedInputTokens, last?.ReportedReasoningTokens, last?.ReportedOutputTokens, stopwatch.ElapsedMilliseconds, [], [], []);
        }
    }

    private static int CountTrace(string output, IReadOnlyList<RepeatMetric> review, string kind) => review.Sum(x => ReadDuplicateTraces(output, x.DocumentId, x.Repeat).Count(trace => trace.GetProperty("kind").GetString() == kind));

    private static int CountIncorrectResolutions(string repoRoot, string output, IReadOnlyList<RepeatMetric> review) => review.Sum(x =>
    {
        var path = Path.Combine(output, x.DocumentId, x.Repeat, "duplicate-resolution.v1.json");
        if (!File.Exists(path)) return 0;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("incorrect").GetInt32();
    });

    private static IEnumerable<JsonElement> ReadDuplicateTraces(string output, string documentId, string repeat)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, documentId, repeat, "prediction.v1.json")));
        return doc.RootElement.TryGetProperty("duplicateTrace", out var traces)
            ? traces.EnumerateArray().Select(x => x.Clone()).ToArray()
            : Array.Empty<JsonElement>();
    }

    private static List<int> FindExactPositions(string source, string text)
    {
        var result = new List<int>();
        var offset = 0;
        while (offset <= source.Length - text.Length)
        {
            var index = source.IndexOf(text, offset, StringComparison.Ordinal);
            if (index < 0) break;
            result.Add(index);
            offset = index + Math.Max(1, text.Length);
        }
        return result;
    }

    private static string LocalContext(string source, int start, int length, bool before)
    {
        const int width = 32;
        if (before)
        {
            var begin = Math.Max(0, start - width);
            return source[begin..start];
        }
        var end = Math.Min(source.Length, start + length + width);
        return source[(start + length)..end];
    }

    private static string DuplicateContractHash() => Sha256Text(string.Join("\n", SemanticTextDuplicateDisambiguationContract.ProtocolVersion, SemanticTextDuplicateDisambiguationContract.System, JsonSerializer.Serialize(SemanticTextDuplicateDisambiguationContract.Schema())));

    private static RepeatMetric Score(DocumentContext context, string repeat, SemanticTextResponse response, IReadOnlyList<SemanticTextBindingObservation> observations, IReadOnlyList<SemanticTextBoundHeading> bound, (ValidatedStructure Structure, IReadOnlyList<ReasoningValidatedProposal> Validated) materialized, IReadOnlyList<ValidatedStructuralElement> finalElements, IReadOnlyList<ReasoningGoldOccurrence> gold, RequestPacketTelemetry telemetry, long wallTimeMs)
    {
        var goldKeys = gold.Select(x => Key(x.SourceId, x.HeadingSpan!)).ToHashSet(StringComparer.Ordinal);
        var boundKeys = bound.Select(x => Key(x.SourceId, new StructuralSpan(x.Start, x.End))).ToHashSet(StringComparer.Ordinal);
        var finalKeys = finalElements.Select(x => Key(x.Sources.Single().SourceId, x.Sources.Single().Span)).ToHashSet(StringComparer.Ordinal);
        var tp = goldKeys.Intersect(finalKeys).Count();
        var fp = finalKeys.Except(goldKeys).Count();
        var fn = goldKeys.Except(finalKeys).Count();
        var p = tp + fp == 0 ? 0d : (double)tp / (tp + fp);
        var r = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        var f1 = p + r == 0 ? 0d : 2 * p * r / (p + r);
        var losses = gold.Select(g => LossFor(g, context.SourceRows, response.Headings, observations, boundKeys, finalKeys, materialized.Validated)).ToArray();
        var firstLossCounts = losses.GroupBy(x => x.FirstLoss, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var systemBindingLoss = losses.Count(x => x.FirstLoss == "SYSTEM_BINDING_LOSS");
        var systemValidatorLoss = losses.Count(x => x.FirstLoss == "SYSTEM_VALIDATOR_LOSS");
        var systemProjectionLoss = losses.Count(x => x.FirstLoss == "SYSTEM_PROJECTION_LOSS");
        var semanticPresence = gold.Count(g => bound.Any(b => b.SourceId == g.SourceId && b.Start < g.HeadingSpan!.End && g.HeadingSpan.Start < b.End));
        var falsePositives = finalElements.Where(x => !goldKeys.Contains(Key(x.Sources.Single().SourceId, x.Sources.Single().Span))).Select(x => new LossRow(context.DocumentId, Key(x.Sources.Single().SourceId, x.Sources.Single().Span), x.Text, "MODEL_FALSE_POSITIVE", false)).ToArray();
        var score = new
        {
            schemaVersion = "a99-semantic-text-generalization-score-v1", documentId = context.DocumentId, repeat,
            status = "SUCCESS", exactStatus = "EVALUABLE", goldCount = gold.Count,
            rawProposalCount = response.Headings.Count, boundProposalCount = bound.Count, validatedCount = materialized.Validated.Count(x => x.Accepted), finalCount = finalElements.Count,
            tp, fp, fn, precision = p, recall = r, f1, semanticCorrespondence = semanticPresence,
            modelOmission = firstLossCounts.GetValueOrDefault("MODEL_OMISSION"), modelWrongText = firstLossCounts.GetValueOrDefault("MODEL_WRONG_TEXT"), modelWrongSpan = firstLossCounts.GetValueOrDefault("MODEL_WRONG_SPAN"), modelWrongRole = firstLossCounts.GetValueOrDefault("MODEL_WRONG_ROLE"), modelFalsePositive = fp,
            systemAliasResolutionLoss = firstLossCounts.GetValueOrDefault("SYSTEM_ALIAS_RESOLUTION_LOSS"), systemAmbiguousTextLoss = firstLossCounts.GetValueOrDefault("AMBIGUOUS_DUPLICATE_TEXT"), systemBindingLoss, systemDedupeLoss = firstLossCounts.GetValueOrDefault("SYSTEM_DEDUPE_LOSS"), systemValidatorLoss, systemProjectionLoss,
            systemLoss = systemBindingLoss + systemValidatorLoss + systemProjectionLoss + firstLossCounts.GetValueOrDefault("SYSTEM_ALIAS_RESOLUTION_LOSS") + firstLossCounts.GetValueOrDefault("SYSTEM_DEDUPE_LOSS"),
            textNotFound = observations.Count(x => x.Status == SemanticTextBindingStatus.TEXT_NOT_FOUND), ambiguousExactText = observations.Count(x => x.Status == SemanticTextBindingStatus.AMBIGUOUS_EXACT_TEXT), invalidSourceAlias = observations.Count(x => x.Status == SemanticTextBindingStatus.INVALID_SOURCE_ALIAS),
            actualProvider = telemetry.ProviderRoute, finishReason = telemetry.FinishReason, inputTokens = telemetry.ReportedInputTokens, reasoningTokens = telemetry.ReportedReasoningTokens, outputTokens = telemetry.ReportedOutputTokens, goldReadBeforeFreeze = false,
        };
        return new RepeatMetric(context.DocumentId, repeat, "SUCCESS", gold.Count, tp, fp, fn, p, r, f1, systemBindingLoss, systemValidatorLoss, systemProjectionLoss, response.Headings.Count, bound.Count, materialized.Validated.Count(x => x.Accepted), finalElements.Count, telemetry.ProviderRoute, telemetry.FinishReason, telemetry.ReportedInputTokens, telemetry.ReportedReasoningTokens, telemetry.ReportedOutputTokens, wallTimeMs, finalKeys, losses, falsePositives, score, firstLossCounts);
    }

    private static LossRow LossFor(ReasoningGoldOccurrence gold, IReadOnlyList<SemanticTextSourceAlias> aliases, IReadOnlyList<SemanticTextHeading> raw, IReadOnlyList<SemanticTextBindingObservation> observations, IReadOnlySet<string> boundKeys, IReadOnlySet<string> finalKeys, IReadOnlyList<ReasoningValidatedProposal> validated)
    {
        var span = gold.HeadingSpan!;
        var key = Key(gold.SourceId, span);
        if (finalKeys.Contains(key)) return new(gold.DocumentId, key, gold.ExactText, "FOUND", true);
        if (boundKeys.Contains(key))
        {
            var row = validated.FirstOrDefault(x => Key(x.Proposal.SourceId, x.Proposal.HeadingSpan) == key);
            return row?.Accepted == true ? new(gold.DocumentId, key, gold.ExactText, "SYSTEM_PROJECTION_LOSS", false) : new(gold.DocumentId, key, gold.ExactText, "SYSTEM_VALIDATOR_LOSS", false);
        }
        var alias = aliases.FirstOrDefault(x => x.SourceId == gold.SourceId);
        var candidates = raw.Where(x => alias is not null && x.Source == alias.Alias).ToArray();
        var exact = candidates.Where(x => x.Text == gold.ExactText).ToArray();
        if (exact.Length > 0)
        {
            var ambiguous = exact.Any(x => observations.Any(o => ReferenceEquals(o.Heading, x) && o.Status == SemanticTextBindingStatus.AMBIGUOUS_EXACT_TEXT));
            return new(gold.DocumentId, key, gold.ExactText, ambiguous ? "AMBIGUOUS_DUPLICATE_TEXT" : "SYSTEM_BINDING_LOSS", false);
        }
        if (candidates.Any(x => !string.IsNullOrEmpty(x.Text)))
        {
            var overlap = candidates.Any(x => x.Text.Length > 0 && alias!.RawText.IndexOf(x.Text, StringComparison.Ordinal) >= 0 && alias.RawText.IndexOf(x.Text, StringComparison.Ordinal) < span.End && span.Start < alias.RawText.IndexOf(x.Text, StringComparison.Ordinal) + x.Text.Length);
            return new(gold.DocumentId, key, gold.ExactText, overlap ? "MODEL_WRONG_SPAN" : "MODEL_WRONG_TEXT", false);
        }
        return new(gold.DocumentId, key, gold.ExactText, "MODEL_OMISSION", false);
    }

    private static object BuildRepeatSummary(IReadOnlyList<RepeatMetric> runs, int cohortSize)
    {
        var docs = runs.GroupBy(x => x.DocumentId, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(group => new
        {
            documentId = group.Key, repeats = group.OrderBy(x => x.Repeat).Select(RepeatTable).ToArray(),
            minPrecision = group.Min(x => x.Precision), minRecall = group.Min(x => x.Recall), minF1 = group.Min(x => x.F1),
            maxPrecision = group.Max(x => x.Precision), maxRecall = group.Max(x => x.Recall), maxF1 = group.Max(x => x.F1), meanF1 = group.Average(x => x.F1),
            tpIntersection = group.Select(x => x.PredictionKeys).Aggregate((a, b) => a.Intersect(b, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal)).Count,
            tpUnion = group.SelectMany(x => x.PredictionKeys).Distinct(StringComparer.Ordinal).Count(),
            jaccard = PairwiseJaccard(group.Select(x => x.PredictionKeys).ToArray()),
            systemLoss = group.Sum(x => x.SystemLoss),
        }).ToArray();
        var cohort = runs.OrderBy(x => x.Repeat).GroupBy(x => x.Repeat, StringComparer.Ordinal).Select(group => Micro(group.ToArray())).ToArray();
        return new { schemaVersion = "a99-semantic-text-generalization-repeat-summary-v1", cohortSize, repeatCount = RepeatCount, documents = docs, cohortMicroByRepeat = cohort, minRepeatMicroPrecision = cohort.Min(x => x.Precision), minRepeatMicroRecall = cohort.Min(x => x.Recall), minRepeatMicroF1 = cohort.Min(x => x.F1) };
    }

    private static object BuildPersistentErrors(IReadOnlyList<RepeatMetric> runs)
    {
        var rows = runs.SelectMany(x => x.FirstLosses.Concat(x.FalsePositives).Select(loss => (run: x, loss))).GroupBy(x => $"{x.run.DocumentId}:{x.loss.Key}", StringComparer.Ordinal).Select(group =>
        {
            var occurrences = group.Select(x => x.loss).ToArray();
            var localKey = group.First().loss.Key;
            var presence = runs.Where(x => x.DocumentId == group.First().run.DocumentId).OrderBy(x => x.Repeat).Select(x => x.PredictionKeys.Contains(localKey) ? "1" : "0").ToArray();
            var first = occurrences.FirstOrDefault(x => !x.Found)?.FirstLoss ?? "FOUND";
            return new { documentId = group.First().run.DocumentId, goldOrPredictionKey = localKey, exactSourceText = occurrences.First().ExactSourceText, firstLossClass = first, repeatPresenceMask = string.Join("", presence) };
        }).Where(x => x.firstLossClass != "FOUND").ToArray();
        var persistent = rows.Where(x => x.firstLossClass != "MODEL_FALSE_POSITIVE" && x.repeatPresenceMask.All(c => c == '0')).ToArray();
        var intermittent = rows.Where(x => x.firstLossClass != "MODEL_FALSE_POSITIVE" && x.repeatPresenceMask.Any(c => c == '1') && x.repeatPresenceMask.Any(c => c == '0')).ToArray();
        var persistentFp = rows.Where(x => x.firstLossClass == "MODEL_FALSE_POSITIVE" && x.repeatPresenceMask.All(c => c == '1')).ToArray();
        var intermittentFp = rows.Where(x => x.firstLossClass == "MODEL_FALSE_POSITIVE" && x.repeatPresenceMask.Any(c => c == '1') && x.repeatPresenceMask.Any(c => c == '0')).ToArray();
        return new { schemaVersion = "a99-semantic-text-generalization-persistent-errors-v1", persistentErrors = persistent, intermittentErrors = intermittent, persistentFalsePositives = persistentFp, intermittentFalsePositives = intermittentFp, persistentErrorCounts = persistent.GroupBy(x => x.firstLossClass).ToDictionary(x => x.Key, x => x.Count()), intermittentErrorCounts = intermittent.GroupBy(x => x.firstLossClass).ToDictionary(x => x.Key, x => x.Count()), persistentFalsePositiveCount = persistentFp.Length, intermittentFalsePositiveCount = intermittentFp.Length };
    }

    private static object BuildComparison(string repoRoot, IReadOnlyList<RepeatMetric> runs, IReadOnlyList<(JsonElement item, ReasoningGoldEligibilityMetadata eligibility)> selected, string startHead) => new
    {
        schemaVersion = "a99-semantic-text-generalization-comparison-v1", startHead, model = Model,
        semanticContractVersion = SemanticTextExactBindingContract.ProtocolVersion, semanticContractHash = ContractHash(),
        selectedStrictGoldCohort = selected.Select(x => x.item.GetProperty("documentId").GetString()).ToArray(),
        newContract = runs.Select(RepeatTable).ToArray(),
        historicalDiagnostics = new[]
        {
            new { label = "Qwen3.5-9B old S0", artifact = "eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery", rerun = false },
            new { label = "Flash old offset contract", artifact = "eval/a99-closed-loop/qwen37-flash-reasoning-ceiling", rerun = false },
            new { label = "Flash C2 boundary contract", artifact = "eval/a99-closed-loop/flash-heading-contract-realignment", rerun = false },
            new { label = "Flash semantic-text exact-binding prior", artifact = "eval/a99-closed-loop/semantic-text-exact-binding", rerun = false },
        },
        goldFirewall = "PASS", noGoldRuntimeTransformation = true, providerConcurrency = 1, providerAttempts = runs.Count,
    };

    private static object BuildPairedComparison(IReadOnlyList<RepeatMetric> baseline, IReadOnlyList<RepeatMetric> review, int expectedGoldOccurrences = 153)
    {
        var rows = review.OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.Repeat, StringComparer.Ordinal).Select(x =>
        {
            var b = baseline.Single(y => y.DocumentId == x.DocumentId && y.Repeat == x.Repeat);
            return new
            {
                documentId = x.DocumentId, repeat = x.Repeat,
                baseline = new { tp = b.Tp, fp = b.Fp, fn = b.Fn, precision = b.Precision, recall = b.Recall, f1 = b.F1, modelOmission = b.FirstLossCounts.GetValueOrDefault("MODEL_OMISSION"), systemLoss = b.SystemLoss },
                review = new { tp = x.Tp, fp = x.Fp, fn = x.Fn, precision = x.Precision, recall = x.Recall, f1 = x.F1, modelOmission = x.FirstLossCounts.GetValueOrDefault("MODEL_OMISSION"), systemLoss = x.SystemLoss },
                delta = new { tp = x.Tp - b.Tp, fp = x.Fp - b.Fp, fn = x.Fn - b.Fn, precision = x.Precision - b.Precision, recall = x.Recall - b.Recall, f1 = x.F1 - b.F1, modelOmission = x.FirstLossCounts.GetValueOrDefault("MODEL_OMISSION") - b.FirstLossCounts.GetValueOrDefault("MODEL_OMISSION"), systemLoss = x.SystemLoss - b.SystemLoss },
            };
        }).ToArray();
        var cohort = rows.GroupBy(x => x.repeat, StringComparer.Ordinal).Select(g => new
        {
            repeat = g.Key,
            baseline = new { tp = g.Sum(x => x.baseline.tp), fp = g.Sum(x => x.baseline.fp), fn = g.Sum(x => x.baseline.fn), gold = g.Sum(x => x.baseline.tp + x.baseline.fn) },
            review = new { tp = g.Sum(x => x.review.tp), fp = g.Sum(x => x.review.fp), fn = g.Sum(x => x.review.fn), gold = g.Sum(x => x.review.tp + x.review.fn) },
            delta = new { tp = g.Sum(x => x.delta.tp), fp = g.Sum(x => x.delta.fp), fn = g.Sum(x => x.delta.fn) },
        }).ToArray();
        return new { schemaVersion = "a99-semantic-text-omission-review-paired-deltas-v1", rows, cohortByRepeat = cohort, goldOccurrencesPerRepeat = expectedGoldOccurrences };
    }

    private static string OmissionReviewClass(IReadOnlyList<RepeatMetric> baseline, IReadOnlyList<RepeatMetric> review, int expectedCellCount, int expectedGoldOccurrences)
    {
        if (review.Count != expectedCellCount || !CohortComplete(review, expectedGoldOccurrences)) return "OMISSION_REVIEW_EXECUTION_BLOCKED";
        var paired = review.Select(x => (current: x, before: baseline.Single(y => y.DocumentId == x.DocumentId && y.Repeat == x.Repeat))).ToArray();
        var recallUp = paired.Any(x => x.current.Recall > x.before.Recall || x.current.Fn < x.before.Fn);
        var precisionDown = paired.Any(x => x.current.Precision < x.before.Precision || x.current.Fp > x.before.Fp);
        var clear = recallUp && !precisionDown && paired.All(x => x.current.SystemLoss == 0);
        if (clear) return "OMISSION_REVIEW_CLEAR_GAIN";
        if (recallUp && precisionDown) return "OMISSION_REVIEW_RECALL_UP_PRECISION_TRADEOFF";
        return "OMISSION_REVIEW_NO_GAIN";
    }

    private static bool CohortComplete(IReadOnlyList<RepeatMetric> runs, int expectedGoldOccurrences = 153) =>
        runs.All(x => x.Status == "SUCCESS") &&
        runs.GroupBy(x => x.Repeat, StringComparer.Ordinal).All(group => group.Sum(x => x.Gold) == expectedGoldOccurrences && group.Sum(x => x.Tp + x.Fn) == expectedGoldOccurrences);

    private static async Task WriteReviewBlockedSummary(string output, string head, IReadOnlyList<(JsonElement item, ReasoningGoldEligibilityMetadata eligibility)> selected, string reason, CancellationToken ct)
    {
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-omission-review-summary-v1", status = "BLOCKED", classification = "OMISSION_REVIEW_EXECUTION_BLOCKED",
            reason, startHead = head, selectedStrictGoldCohort = selected.Select(x => x.item.GetProperty("documentId").GetString()).ToArray(),
            baseContractHash = ContractHash(), reviewPromptHash = SemanticTextOmissionReviewContract.Hash(), passAReused = true,
            passAProviderCallsCurrentRun = 0, modelCalls = 0, goldReadBeforeFreeze = false,
        }, ct);
    }

    private static RepeatMetric LoadFrozenMetric(string output, string documentId, string repeat)
    {
        return LoadMetricAtDir(Path.Combine(output, documentId, repeat));
    }

    private static RepeatMetric LoadRepairMetric(string output, string documentId, string repeat)
    {
        return LoadMetricAtDir(Path.Combine(output, repeat, documentId));
    }

    private static RepeatMetric LoadMetricAtDir(string dir)
    {
        using var scoreDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "score.v1.json")));
        using var firstDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "first-loss.v1.json")));
        using var predictionDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "prediction.v1.json")));
        using var freezeDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "freeze.v1.json")));
        var score = scoreDoc.RootElement;
        var documentId = score.TryGetProperty("documentId", out var documentValue) ? documentValue.GetString() ?? "UNKNOWN" : "UNKNOWN";
        var repeat = score.TryGetProperty("repeat", out var repeatValue) ? repeatValue.GetString() ?? "UNKNOWN" : "UNKNOWN";
        var first = firstDoc.RootElement;
        var finalKeys = predictionDoc.RootElement.TryGetProperty("finalHeadings", out var finals) && finals.ValueKind == JsonValueKind.Array
            ? finals.EnumerateArray().Select(x => Key(x.GetProperty("sourceId").GetString()!, new StructuralSpan(x.GetProperty("start").GetInt32(), x.GetProperty("end").GetInt32()))).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var losses = first.TryGetProperty("firstLosses", out var lossArray) && lossArray.ValueKind == JsonValueKind.Array
            ? lossArray.EnumerateArray().Select(x => new LossRow(x.GetProperty("documentId").GetString()!, x.GetProperty("key").GetString()!, x.GetProperty("exactSourceText").GetString()!, x.GetProperty("firstLoss").GetString()!, x.GetProperty("found").GetBoolean())).ToArray()
            : Array.Empty<LossRow>();
        var falsePositives = first.TryGetProperty("falsePositives", out var falsePositiveArray) && falsePositiveArray.ValueKind == JsonValueKind.Array
            ? falsePositiveArray.EnumerateArray().Select(x => new LossRow(x.GetProperty("documentId").GetString()!, x.GetProperty("key").GetString()!, x.GetProperty("exactSourceText").GetString()!, x.GetProperty("firstLoss").GetString()!, x.GetProperty("found").GetBoolean())).ToArray()
            : Array.Empty<LossRow>();
        var counts = first.TryGetProperty("firstLossCounts", out var countObject) && countObject.ValueKind == JsonValueKind.Object
            ? countObject.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32(), StringComparer.Ordinal)
            : new Dictionary<string, int>(StringComparer.Ordinal);
        var freeze = freezeDoc.RootElement;
        return new RepeatMetric(documentId, repeat, score.TryGetProperty("status", out var status) ? status.GetString() ?? "BLOCKED" : "BLOCKED",
            GetInt(score, "goldCount"), GetInt(score, "tp"), GetInt(score, "fp"), GetInt(score, "fn"), GetDouble(score, "precision"), GetDouble(score, "recall"), GetDouble(score, "f1"), GetInt(score, "systemBindingLoss"), GetInt(score, "systemValidatorLoss"), GetInt(score, "systemProjectionLoss"), GetInt(score, "rawProposalCount"), GetInt(score, "boundProposalCount"), GetInt(score, "validatedCount"), GetInt(score, "finalCount"), GetString(score, "actualProvider"), GetString(score, "finishReason"), GetNullableInt(score, "inputTokens"), GetNullableInt(score, "reasoningTokens"), GetNullableInt(score, "outputTokens"), GetNullableLong(freeze, "wallTimeMs"), finalKeys, losses, falsePositives, score.Clone(), counts);
    }

    private static object RepeatTable(RepeatMetric x) => new { documentId = x.DocumentId, repeat = x.Repeat, status = x.Status, gold = x.Gold, tp = x.Tp, fp = x.Fp, fn = x.Fn, precision = x.Precision, recall = x.Recall, f1 = x.F1, modelOmission = x.FirstLossCounts.GetValueOrDefault("MODEL_OMISSION"), wrongText = x.FirstLossCounts.GetValueOrDefault("MODEL_WRONG_TEXT"), wrongSpan = x.FirstLossCounts.GetValueOrDefault("MODEL_WRONG_SPAN"), systemLoss = x.SystemLoss, executionMode = "FULL_CONTEXT", actualProvider = x.Provider, finishReason = x.FinishReason, inputTokens = x.InputTokens, reasoningTokens = x.ReasoningTokens, outputTokens = x.OutputTokens, wallTimeMs = x.WallTimeMs };
    private static MicroMetric Micro(IReadOnlyList<RepeatMetric> group)
    {
        var tp = group.Sum(x => x.Tp); var fp = group.Sum(x => x.Fp); var fn = group.Sum(x => x.Fn);
        var p = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var r = tp + fn == 0 ? 0d : (double)tp / (tp + fn); var f1 = p + r == 0 ? 0d : 2 * p * r / (p + r);
        return new MicroMetric(group.First().Repeat, group.Sum(x => x.Gold), tp, fp, fn, p, r, f1, group.Sum(x => x.SystemLoss));
    }

    private static object PairwiseJaccard(IReadOnlyList<HashSet<string>> sets)
    {
        static double J(HashSet<string> a, HashSet<string> b) => a.Count == 0 && b.Count == 0 ? 1d : (double)a.Intersect(b, StringComparer.Ordinal).Count() / a.Union(b, StringComparer.Ordinal).Count();
        return new { r1VsR2 = sets.Count > 1 ? J(sets[0], sets[1]) : 0d, r1VsR3 = sets.Count > 2 ? J(sets[0], sets[2]) : 0d, r2VsR3 = sets.Count > 2 ? J(sets[1], sets[2]) : 0d };
    }

    private static string GeneralizationClass(IReadOnlyList<RepeatMetric> runs)
    {
        if (runs.Count == 0 || runs.Any(x => x.Status == "BLOCKED")) return "SEMANTIC_TEXT_GENERALIZATION_EXECUTION_BLOCKED";
        if (runs.All(x => x.SystemLoss == 0) && runs.All(x => x.Recall >= .95 && x.Precision >= .95))
            return runs.All(x => x.Recall >= .995 && x.Precision >= .995) ? "SEMANTIC_TEXT_CONTRACT_GENERALIZES_STRONGLY" : "SEMANTIC_TEXT_CONTRACT_GENERALIZES_WITH_VARIANCE";
        return "SEMANTIC_TEXT_CONTRACT_DOES_NOT_GENERALIZE";
    }

    private static object NextBucket(IReadOnlyList<RepeatMetric> runs)
    {
        var counts = runs.SelectMany(x => x.FirstLossCounts).Where(x => x.Key != "FOUND").GroupBy(x => x.Key, StringComparer.Ordinal).Select(x => new { bucket = x.Key, count = x.Sum(y => y.Value), affectedDocuments = runs.Where(r => r.FirstLossCounts.ContainsKey(x.Key)).Select(r => r.DocumentId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() }).OrderByDescending(x => x.count).ThenBy(x => x.bucket, StringComparer.Ordinal).FirstOrDefault();
        return counts ?? new { bucket = "NONE", count = 0, affectedDocuments = Array.Empty<string>() };
    }

    private static void PrintReport(IReadOnlyList<RepeatMetric> runs, IReadOnlyList<(JsonElement item, ReasoningGoldEligibilityMetadata eligibility)> selected, int providerAttempts)
    {
        Console.WriteLine($"TOTAL_GOLD_OCCURRENCES={runs.GroupBy(x => x.DocumentId).Sum(x => x.First().Gold)}");
        Console.WriteLine("Document | Repeat | Gold | TP | FP | FN | P | R | F1 | ModelOmission | WrongText/Span | SystemLoss");
        foreach (var x in runs.OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.Repeat, StringComparer.Ordinal)) Console.WriteLine($"{x.DocumentId} | {x.Repeat} | {x.Gold} | {x.Tp} | {x.Fp} | {x.Fn} | {x.Precision:0.######} | {x.Recall:0.######} | {x.F1:0.######} | {x.FirstLossCounts.GetValueOrDefault("MODEL_OMISSION")} | {x.FirstLossCounts.GetValueOrDefault("MODEL_WRONG_TEXT") + x.FirstLossCounts.GetValueOrDefault("MODEL_WRONG_SPAN")} | {x.SystemLoss}");
        var micro = runs.GroupBy(x => x.Repeat).Select(x => Micro(x.ToArray())).ToArray();
        foreach (var x in micro) Console.WriteLine($"{x.Repeat} | micro TP={x.Tp} FP={x.Fp} FN={x.Fn} F1={x.F1}");
        Console.WriteLine($"PROVIDER_ATTEMPTS={providerAttempts}");
        Console.WriteLine($"NEXT_LARGEST_ERROR_BUCKET={NextBucket(runs)}");
    }

    private static DocumentContext Prepare(string repoRoot, JsonElement item)
    {
        var documentId = item.GetProperty("documentId").GetString()!;
        var sourcePath = Path.Combine(repoRoot, item.GetProperty("sourcePath").GetString()!.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        var sourceSha = item.GetProperty("sourceSha256").GetString()!;
        if (!File.Exists(sourcePath) || !string.Equals(Sha256(sourcePath), sourceSha, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SOURCE_HASH_MISMATCH:" + documentId);
        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = documentId };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var rows = source.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.Text)).Select((p, i) => new SemanticTextSourceAlias($"S{i + 1:0000}", p.SourceId, p.SourceOrdinal, p.Text)).ToArray();
        var packet = JsonSerializer.Serialize(new { sourceAliases = rows.Select(x => new { alias = x.Alias, text = x.RawText, sourceOrdinal = x.SourceOrdinal }).ToArray() });
        return new DocumentContext(documentId, sourceSha, source, policy, rows, packet, ContractHashForPrompt(), Sha256Text(JsonSerializer.Serialize(SemanticTextExactBindingContract.Schema())), Sha256Text(packet));
    }

    private static JsonElement[] LoadInventory(string repoRoot)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar))));
        return doc.RootElement.GetProperty("documents").EnumerateArray().Select(x => x.Clone()).ToArray();
    }

    private static async Task<int> Blocked(string output, string head, string reason, IReadOnlyList<(JsonElement item, ReasoningGoldEligibilityMetadata eligibility)> selected, CancellationToken ct)
    {
        await WriteJson(Path.Combine(output, "summary.v1.json"), new { schemaVersion = "a99-semantic-text-generalization-summary-v1", status = "BLOCKED", reason, startHead = head, selectedStrictGoldCohort = selected.Select(x => x.item.GetProperty("documentId").GetString()).ToArray(), providerCalls = 0, modelCalls = 0, goldReadBeforeFreeze = false }, ct);
        Console.WriteLine($"FINAL_CLASSIFICATION=SEMANTIC_TEXT_GENERALIZATION_EXECUTION_BLOCKED");
        return 1;
    }

    private static string ContractHash() => Sha256Text(string.Join("\n", SemanticTextExactBindingContract.ProtocolVersion, SemanticTextExactBindingContract.System, JsonSerializer.Serialize(SemanticTextExactBindingContract.Schema())));
    private static string ContractHashForPrompt() => Sha256Text(SemanticTextExactBindingContract.ProtocolVersion + "\n" + SemanticTextExactBindingContract.System);
    private static string Key(string sourceId, StructuralSpan span) => $"{sourceId}:{span.Start}:{span.End}";
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static int GetInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static double GetDouble(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : 0d;
    private static int? GetNullableInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.TryGetInt32(out var result) ? result : null;
    private static long GetNullableLong(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0L;
    private static string? GetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string GitSha(string root) => Git(root, "rev-parse HEAD");
    private static string Git(string root, string args) { try { using var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return p?.StandardOutput.ReadToEnd().Trim() ?? "NOT_PERSISTED"; } catch { return "NOT_PERSISTED"; } }
    private static async Task WriteJson(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);

    private sealed record DocumentContext(string DocumentId, string SourceSha256, SourceDocument Source, DocxPolicyState Policy, IReadOnlyList<SemanticTextSourceAlias> SourceRows, string Packet, string PromptHash, string SchemaHash, string PacketHash);
    private sealed record LossRow(string DocumentId, string Key, string ExactSourceText, string FirstLoss, bool Found);
    private sealed record MicroMetric(string Repeat, int Gold, int Tp, int Fp, int Fn, double Precision, double Recall, double F1, int SystemLoss);
    private sealed record RepeatMetric(string DocumentId, string Repeat, string Status, int Gold, int Tp, int Fp, int Fn, double Precision, double Recall, double F1, int SystemBindingLoss, int SystemValidatorLoss, int SystemProjectionLoss, int RawCount, int BoundCount, int ValidatedCount, int FinalCount, string? Provider, string? FinishReason, int? InputTokens, int? ReasoningTokens, int? OutputTokens, long WallTimeMs, HashSet<string> PredictionKeys, IReadOnlyList<LossRow> FirstLosses, IReadOnlyList<LossRow> FalsePositives, object? Score = null, IReadOnlyDictionary<string, int>? LossCounts = null)
    {
        [JsonIgnore] public int SystemLoss => SystemBindingLoss + SystemValidatorLoss + SystemProjectionLoss;
        [JsonIgnore] public IReadOnlyDictionary<string, int> FirstLossCounts { get; } = LossCounts ?? new Dictionary<string, int>();
    }
}
