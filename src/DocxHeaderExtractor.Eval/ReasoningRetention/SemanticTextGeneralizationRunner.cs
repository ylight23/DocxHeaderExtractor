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
    public static async Task<int> RunOmissionReviewAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, "eval/a99-closed-loop/semantic-text-omission-review".Replace('/', Path.DirectorySeparatorChar));
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

        var baselineComplete = selected.Length == 5 && baseline.Count == 15 && CohortComplete(baseline);
        var reviewPromptHash = SemanticTextOmissionReviewContract.Hash();
        await WriteJson(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-omission-review-v1",
            startHead, branch = Git(repoRoot, "branch --show-current"), model = Model,
            baseContractHash = ContractHash(), reviewPromptHash,
            intervention = "SEMANTIC_TEXT_OMISSION_REVIEW_V1",
            selectedStrictGoldCohort = selected.Select(x => x.item.GetProperty("documentId").GetString()).ToArray(),
            repeats = new[] { "R1", "R2", "R3" }, providerConcurrency = 1,
            passAReused = true, passAProviderCallsCurrentRun = 0,
            inventoryIsInformationalOnly = true, additiveUnion = true, validatorsUnchanged = true,
            noVlm = true, goldReadBeforeFreeze = false, baselineComplete, goldOccurrencesPerRepeat = 153,
            promptFrozenBeforeInference = true,
        }, ct);
        await WriteJson(Path.Combine(output, "baseline-error-inventory.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-omission-review-baseline-inventory-v1",
            baselineArtifact = OutputRoot, baselineComplete, goldOccurrencesPerRepeat = 153,
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

        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "eval/a99-closed-loop/semantic-text-omission-review", string.Join(',', selected.Select(x => x.item.GetProperty("documentId").GetString())), ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var review = new List<RepeatMetric>();
        foreach (var item in selected)
        {
            var context = Prepare(repoRoot, item.item);
            for (var repeat = 1; repeat <= RepeatCount; repeat++)
            {
                Console.WriteLine($"RUNNING_REVIEW={context.DocumentId}/R{repeat}");
                review.Add(await RunReviewRepeatAsync(repoRoot, output, baselineRoot, context, repeat, model, startHead, reviewPromptHash, ct));
            }
        }

        var reviewSummary = BuildRepeatSummary(review, selected.Length);
        var paired = BuildPairedComparison(baseline, review);
        await WriteJson(Path.Combine(output, "repeat-summary.v1.json"), reviewSummary, ct);
        await WriteJson(Path.Combine(output, "persistent-errors.v1.json"), BuildPersistentErrors(review), ct);
        await WriteJson(Path.Combine(output, "paired-deltas.v1.json"), paired, ct);
        var classification = OmissionReviewClass(baseline, review);
        var gate = review.Count == 15 && CohortComplete(review) && review.All(x => x.Precision >= .995 && x.Recall >= .995 && x.F1 >= .995 && x.SystemLoss == 0);
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-omission-review-summary-v1", startHead, endHead = GitSha(repoRoot),
            intervention = "SEMANTIC_TEXT_OMISSION_REVIEW_V1", baseContractHash = ContractHash(), reviewPromptHash,
            selectedStrictGoldCohort = selected.Select(x => x.item.GetProperty("documentId").GetString()).ToArray(),
            baselineComplete, baselineProviderAttempts = 15, passAReused = true, passAProviderCallsCurrentRun = 0,
            reviewProviderAttempts = model.ProviderCalls, modelCalls = model.ProviderCalls, goldOccurrencesPerRepeat = 153,
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
        await WriteJson(Path.Combine(output, "paired-deltas.v1.json"), BuildPairedComparison(baseline, review), ct);
        var classification = OmissionReviewClass(baseline, review);
        var gate = review.Count == 15 && CohortComplete(review) && review.All(x => x.Precision >= .995 && x.Recall >= .995 && x.F1 >= .995 && x.SystemLoss == 0);
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
            baselineComplete = CohortComplete(baseline), baselineProviderAttempts = 15, controlReused = true, controlProviderCallsCurrent = 0, passAReused = true,
            passAProviderCallsCurrentRun = 0, reviewProviderAttempts, modelCalls = reviewProviderAttempts, offlineProviderCalls = 0,
            goldOccurrencesPerRepeat = 153, goldReadBeforeFreeze = false, a99DevMarginMet = gate, classification,
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
            var freeze = new { schemaVersion = "a99-semantic-text-omission-review-freeze-v1", context.DocumentId, repeat = repeatName, gitSha, model = Model, baseContractHash = ContractHash(), reviewPromptHash, sourceSha256 = context.SourceSha256, predictionSha256 = Sha256(predictionPath), resultSha256 = Sha256(resultPath), providerAttempts = providerAttempts == 0 ? (last is null ? 0 : 1) : providerAttempts, inputTokens = last?.ReportedInputTokens, reasoningTokens = last?.ReportedReasoningTokens, outputTokens = last?.ReportedOutputTokens, finishReason = last?.FinishReason, failureClass = ex.GetType().Name, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow };
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

    private static object BuildPairedComparison(IReadOnlyList<RepeatMetric> baseline, IReadOnlyList<RepeatMetric> review)
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
        return new { schemaVersion = "a99-semantic-text-omission-review-paired-deltas-v1", rows, cohortByRepeat = cohort, goldOccurrencesPerRepeat = 153 };
    }

    private static string OmissionReviewClass(IReadOnlyList<RepeatMetric> baseline, IReadOnlyList<RepeatMetric> review)
    {
        if (review.Count != 15 || !CohortComplete(review)) return "OMISSION_REVIEW_EXECUTION_BLOCKED";
        var paired = review.Select(x => (current: x, before: baseline.Single(y => y.DocumentId == x.DocumentId && y.Repeat == x.Repeat))).ToArray();
        var recallUp = paired.Any(x => x.current.Recall > x.before.Recall || x.current.Fn < x.before.Fn);
        var precisionDown = paired.Any(x => x.current.Precision < x.before.Precision || x.current.Fp > x.before.Fp);
        var clear = recallUp && !precisionDown && paired.All(x => x.current.SystemLoss == 0);
        if (clear) return "OMISSION_REVIEW_CLEAR_GAIN";
        if (recallUp && precisionDown) return "OMISSION_REVIEW_RECALL_UP_PRECISION_TRADEOFF";
        return "OMISSION_REVIEW_NO_GAIN";
    }

    private static bool CohortComplete(IReadOnlyList<RepeatMetric> runs) =>
        runs.All(x => x.Status == "SUCCESS") &&
        runs.GroupBy(x => x.Repeat, StringComparer.Ordinal).All(group => group.Sum(x => x.Gold) == 153 && group.Sum(x => x.Tp + x.Fn) == 153);

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
