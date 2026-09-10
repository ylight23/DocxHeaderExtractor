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

    private static async Task<RepeatMetric> RunRepeatAsync(string repoRoot, string output, DocumentContext context, int repeat, OpenRouterCeilingReasoningModel model, string gitSha, CancellationToken ct)
    {
        var repeatName = $"r{repeat}";
        var dir = Path.Combine(output, context.DocumentId, repeatName);
        Directory.CreateDirectory(dir);
        var stopwatch = Stopwatch.StartNew();
        RequestPacketTelemetry? telemetry = null;
        try
        {
            var requestId = $"{SemanticTextExactBindingContract.ProtocolVersion}:{context.DocumentId}:{repeat}:{context.PacketHash}";
            var (rawContent, requestTelemetry) = await model.CompleteRawStructuredSemanticAsync(
                context.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId,
                context.Packet, context.SourceRows.Sum(x => x.RawText.Length), context.SourceRows.Count, context.SourceRows.Count,
                SemanticTextExactBindingContract.System,
                SemanticTextExactBindingContract.BuildUser(context.Packet, ReasoningRoute.ModelCapabilityCeiling.ToString()),
                SemanticTextExactBindingContract.Schema(), "semantic_text_exact_binding_v1", ct);
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
                providerAttempts = 1, inputTokens = telemetry.ReportedInputTokens, reasoningTokens = telemetry.ReportedReasoningTokens,
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
            var freeze = new { schemaVersion = "a99-semantic-text-generalization-freeze-v1", context.DocumentId, repeat = repeatName, gitSha, model = Model, actualProvider = last?.ProviderRoute, sourceSha256 = context.SourceSha256, semanticContractVersion = SemanticTextExactBindingContract.ProtocolVersion, promptHash = context.PromptHash, schemaHash = context.SchemaHash, packetHash = context.PacketHash, reasoningConfiguration = new { requested = true, enabled = true, excluded = true }, predictionSha256 = Sha256(predictionPath), resultSha256 = Sha256(resultPath), rawProposalCount = 0, boundProposalCount = 0, finalCount = 0, providerAttempts = last is null ? 0 : 1, inputTokens = last?.ReportedInputTokens, reasoningTokens = last?.ReportedReasoningTokens, outputTokens = last?.ReportedOutputTokens, finishReason = last?.FinishReason, executionMode = "FULL_CONTEXT", failureClass = ex.GetType().Name, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow };
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

    private static RepeatMetric LoadFrozenMetric(string output, string documentId, string repeat)
    {
        var dir = Path.Combine(output, documentId, repeat);
        using var scoreDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "score.v1.json")));
        using var firstDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "first-loss.v1.json")));
        using var predictionDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "prediction.v1.json")));
        using var freezeDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "freeze.v1.json")));
        var score = scoreDoc.RootElement;
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
        return new MicroMetric(group.First().Repeat, tp, fp, fn, p, r, f1, group.Sum(x => x.SystemLoss));
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
    private sealed record MicroMetric(string Repeat, int Tp, int Fp, int Fn, double Precision, double Recall, double F1, int SystemLoss);
    private sealed record RepeatMetric(string DocumentId, string Repeat, string Status, int Gold, int Tp, int Fp, int Fn, double Precision, double Recall, double F1, int SystemBindingLoss, int SystemValidatorLoss, int SystemProjectionLoss, int RawCount, int BoundCount, int ValidatedCount, int FinalCount, string? Provider, string? FinishReason, int? InputTokens, int? ReasoningTokens, int? OutputTokens, long WallTimeMs, HashSet<string> PredictionKeys, IReadOnlyList<LossRow> FirstLosses, IReadOnlyList<LossRow> FalsePositives, object? Score = null, IReadOnlyDictionary<string, int>? LossCounts = null)
    {
        [JsonIgnore] public int SystemLoss => SystemBindingLoss + SystemValidatorLoss + SystemProjectionLoss;
        [JsonIgnore] public IReadOnlyDictionary<string, int> FirstLossCounts { get; } = LossCounts ?? new Dictionary<string, int>();
    }
}
