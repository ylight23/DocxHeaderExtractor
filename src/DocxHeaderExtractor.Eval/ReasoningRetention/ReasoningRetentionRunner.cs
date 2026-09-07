using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.Eval.StrictGoldOccurrence;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Runs the evaluation-only R1-B paired diagnostic after exact Gold occurrence materialization.
/// Gold is loaded and joined only after all three route outputs have been produced.
/// </summary>
public static class ReasoningRetentionRunner
{
    private const string Population = "DIAGNOSTIC_DEV_SUBSET_V1";
    private const string OccurrenceManifest = "eval/a99-closed-loop/strict-gold-occurrence-materialization.v1.json";
    private const string RetentionRoot = "eval/a99-closed-loop/reasoning-retention";
    private const int DefaultReasoningMaxOutputTokens = 8_192;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(
        string repoRoot,
        RemoteInferenceOptions remote,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(remote);

        remote.Validate();
        var reasoningRemote = BuildReasoningRemoteOptions(remote);
        var timeoutOptions = ReasoningProviderTimeoutOptions.FromEnvironment();
        timeoutOptions.Validate();
        var gold = LoadAndValidateGold(repoRoot);
        var manifestPath = Path.Combine(repoRoot, OccurrenceManifest.Replace('/', Path.DirectorySeparatorChar));
        var executionRevision = GitRevision(repoRoot) ?? "UNRESOLVED";
        var outputRoot = Path.Combine(repoRoot, RetentionRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(outputRoot);
        await WriteJsonAsync(Path.Combine(outputRoot, "capability-retention-manifest.v2.json"), new
        {
            artifactKind = "a99_reasoning_capability_retention_manifest",
            schemaVersion = "a99-reasoning-capability-retention-v2",
            status = "READY",
            population = Population,
            executionRevision,
            occurrenceMaterialization = new
            {
                path = OccurrenceManifest,
                sha256 = FileSha256(manifestPath),
                documents = gold.Select(item => item.DocumentId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                expectedOccurrences = gold.Count,
            },
            routes = new[] { "MODEL_CAPABILITY_CEILING", "PRODUCTION_SYSTEM", "REASONING_PRESERVING_SHADOW" },
            repeats = 3,
            goldFirewall = new
            {
                goldSuppliedToContextBuilder = false,
                goldSuppliedToProvider = false,
                goldSuppliedToCandidateBuilder = false,
                goldSuppliedToValidator = false,
                goldJoinedAfterRoutes = true,
            },
            provider = new
            {
                name = "OpenRouter",
                model = remote.Model,
                endpoint = remote.Endpoint.ToString(),
                temperature = 0,
                reasoning = "none",
                contextSize = remote.ContextSize,
                maxOutputTokens = reasoningRemote.MaxOutputTokens,
                seed = (int?)null,
                timeouts = TimeoutConfiguration(timeoutOptions),
            },
            providerCalls = 0,
            holdoutTouched = false,
        }, cancellationToken);

        var repeats = new List<RepeatResult>();
        try
        {
            for (var repeat = 1; repeat <= 3; repeat++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Console.WriteLine($"R1-B repeat {repeat}/3: A -> B -> C");
                repeats.Add(await RunRepeatAsync(
                    repoRoot,
                    gold,
                    remote,
                    reasoningRemote,
                    timeoutOptions,
                    repeat,
                    cancellationToken));
            }
        }
        catch (Exception ex)
        {
            var completionFailure = ex as ReasoningCompletionException;
            var executionFailure = ex as ReasoningRetentionExecutionException;
            var failureClass = completionFailure?.FailureClass ?? executionFailure?.FailureClass ??
                (ex.Message.Contains("PROVIDER_AUTH_FAILURE", StringComparison.Ordinal)
                    ? ReasoningCompletionFailureClass.ProviderAuthFailure
                    : ex is InvalidDataException
                        ? ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid
                        : ReasoningCompletionFailureClass.ProviderUnavailable);
            var attempts = completionFailure?.AttemptTelemetry ?? executionFailure?.CompletionTelemetry ?? [];
            var observedProviderCalls = executionFailure?.ProviderCalls ?? attempts.Count;
            var completionStats = executionFailure?.CompletionStats ?? [];
            await WriteJsonAsync(Path.Combine(outputRoot, "provider-completion-integrity.v1.json"), new
            {
                artifactKind = "a99_provider_completion_integrity",
                schemaVersion = "a99-provider-completion-integrity-v1",
                status = "BLOCKED",
                attemptCount = observedProviderCalls,
                completionAttemptCount = attempts.Count,
                successfulCompletionCount = attempts.Count(item => item.JsonParseSucceeded && item.CompletionEnvelopeComplete && item.FailureClass is null),
                failedCompletionCount = attempts.Count(item => item.FailureClass is not null),
                failureClasses = attempts.Where(item => item.FailureClass is not null)
                    .Select(item => item.FailureClass!).Distinct(StringComparer.Ordinal).ToArray(),
                blockerClass = executionFailure?.FailureClass,
                blockerDocumentId = executionFailure?.DocumentId,
                blockerMessage = executionFailure?.Message,
                rangeSplitCount = completionStats.Sum(item => item.Completion.RangeSplitCount),
                retryCount = completionStats.Sum(item => item.Completion.RetryCount),
                maxObservedOutputTokens = attempts.Where(item => item.ReportedOutputTokens is not null)
                    .Select(item => item.ReportedOutputTokens!.Value).DefaultIfEmpty().Max(),
                maxObservedResponseBytes = attempts.Select(item => item.ReceivedContentBytes).DefaultIfEmpty().Max(),
                allSemanticPassesComplete = attempts.Count > 0 && attempts.All(item => item.FailureClass is null),
                partialResponsesScored = false,
                providerTimeouts = TimeoutConfiguration(timeoutOptions),
                attempts,
            }, cancellationToken);
            await WriteJsonAsync(
                Path.Combine(outputRoot, "provider-liveness.v1.json"),
                BuildProviderLivenessArtifact("BLOCKED", timeoutOptions, attempts, completionStats, observedProviderCalls),
                cancellationToken);
            await WriteJsonAsync(
                Path.Combine(outputRoot, "source-visibility-contract.v1.json"),
                BuildSourceVisibilityArtifact("BLOCKED", timeoutOptions, attempts, completionStats, observedProviderCalls),
                cancellationToken);
            await WriteCompletionMarkdownAsync(
                repoRoot,
                "BLOCKED",
                attempts.Where(item => item.FailureClass is not null)
                    .Select(item => item.FailureClass!).Distinct(StringComparer.Ordinal).ToArray(),
                attempts,
                completionStats.Sum(item => item.Completion.RangeSplitCount),
                completionStats.Sum(item => item.Completion.RetryCount),
                executionFailure?.FailureClass,
                cancellationToken);
            await WriteJsonAsync(Path.Combine(outputRoot, "final-diagnosis.v1.json"), new
            {
                artifactKind = "a99_reasoning_capability_vs_system_retention",
                schemaVersion = "a99-reasoning-retention-diagnosis-v1",
                status = "BLOCKED",
                population = Population,
                executionRevision,
                failureClass,
                blockerDocumentId = executionFailure?.DocumentId,
                message = ex.Message,
                materialization = "PASS_311_OF_311",
                providerCalls = observedProviderCalls,
                providerTimeouts = TimeoutConfiguration(timeoutOptions),
                partialResponsesScored = false,
                holdoutTouched = false,
            }, cancellationToken);
            Console.Error.WriteLine($"R1-B blocked: {failureClass}: {ex.Message}");
            return 1;
        }

        var allLedger = repeats.SelectMany(repeat => repeat.Documents.SelectMany(document =>
            document.Evaluation.Ledger.Select(entry => new { repeat = repeat.Repeat, entry }))).ToArray();
        var firstLoss = allLedger
            .Where(item => item.entry.Classification == RetentionClassification.SystemInducedLoss)
            .GroupBy(item => item.entry.FirstSystemLossStage)
            .OrderBy(group => group.Key)
            .ToDictionary(group => group.Key.ToString(), group => group.Count(), StringComparer.Ordinal);
        var classificationCounts = allLedger
            .GroupBy(item => item.entry.Classification)
            .ToDictionary(group => group.Key.ToString(), group => group.Count(), StringComparer.Ordinal);
        var providerCalls = repeats.Sum(repeat => repeat.ProviderCalls);
        var coverage = repeats.SelectMany(repeat => repeat.Documents).Select(document => document.ContextCoverage).ToArray();
        var semanticCoverage = repeats.SelectMany(repeat => repeat.Documents).Select(document => document.SemanticPassCoverage).ToArray();
        var ownershipCoverage = repeats.SelectMany(repeat => repeat.Documents).Select(document => document.OutputOwnershipCoverage).ToArray();
        var completionTelemetry = repeats.SelectMany(repeat => repeat.CompletionTelemetry).ToArray();
        await WriteJsonAsync(Path.Combine(outputRoot, "provider-completion-integrity.v1.json"), new
        {
            artifactKind = "a99_provider_completion_integrity",
            schemaVersion = "a99-provider-completion-integrity-v1",
            status = "PASS",
            attemptCount = completionTelemetry.Length,
            successfulCompletionCount = completionTelemetry.Count(item => item.JsonParseSucceeded && item.CompletionEnvelopeComplete && item.FailureClass is null),
            failedCompletionCount = completionTelemetry.Count(item => item.FailureClass is not null),
            failureClasses = completionTelemetry.Where(item => item.FailureClass is not null)
                .Select(item => item.FailureClass!).Distinct(StringComparer.Ordinal).ToArray(),
            rangeSplitCount = repeats.SelectMany(item => item.CompletionStats()).Sum(item => item.Completion.RangeSplitCount),
            retryCount = repeats.SelectMany(item => item.CompletionStats()).Sum(item => item.Completion.RetryCount),
            outOfScopeProposalCount = repeats.SelectMany(item => item.CompletionStats()).Sum(item => item.Completion.OutOfScopeProposalCount),
            maxObservedOutputTokens = completionTelemetry.Where(item => item.ReportedOutputTokens is not null)
                .Select(item => item.ReportedOutputTokens!.Value).DefaultIfEmpty().Max(),
            maxObservedResponseBytes = completionTelemetry.Select(item => item.ReceivedContentBytes).DefaultIfEmpty().Max(),
            allSemanticPassesComplete = completionTelemetry.Length > 0 && completionTelemetry.All(item => item.FailureClass is null),
            partialResponsesScored = false,
            ownershipViolationCount = repeats.SelectMany(item => item.CompletionStats()).Sum(item => item.Completion.OwnershipViolationCount),
            providerTimeouts = TimeoutConfiguration(timeoutOptions),
            attempts = completionTelemetry,
        }, cancellationToken);
        await WriteJsonAsync(
            Path.Combine(outputRoot, "provider-liveness.v1.json"),
            BuildProviderLivenessArtifact(
                "PASS",
                timeoutOptions,
                completionTelemetry,
                repeats.SelectMany(item => item.CompletionStats()).ToArray(),
                providerCalls),
            cancellationToken);
        await WriteJsonAsync(
            Path.Combine(outputRoot, "source-visibility-contract.v1.json"),
            BuildSourceVisibilityArtifact(
                "PASS",
                timeoutOptions,
                completionTelemetry,
                repeats.SelectMany(item => item.CompletionStats()).ToArray(),
                providerCalls),
            cancellationToken);
        await WriteCompletionMarkdownAsync(
            repoRoot,
            "PASS",
            completionTelemetry.Where(item => item.FailureClass is not null)
                .Select(item => item.FailureClass!).Distinct(StringComparer.Ordinal).ToArray(),
            completionTelemetry,
            repeats.SelectMany(item => item.CompletionStats()).Sum(item => item.Completion.RangeSplitCount),
            repeats.SelectMany(item => item.CompletionStats()).Sum(item => item.Completion.RetryCount),
            null,
            cancellationToken);
        var aggregate = new
        {
            artifactKind = "a99_reasoning_capability_vs_system_retention",
            schemaVersion = "a99-reasoning-retention-diagnosis-v1",
            status = "PASS",
            population = Population,
            executionRevision,
            model = remote.Model,
            provider = "OpenRouter",
            temperature = 0,
            seed = (int?)null,
            contextStrategy = "SINGLE_FULL_CONTEXT_OR_HIERARCHICAL_FULL_COVERAGE",
            sourceToModelContextCoverage = coverage.Length == 0 ? 0 : coverage.Min(),
            sourceToSemanticPassCoverage = semanticCoverage.Length == 0 ? 0 : semanticCoverage.Min(),
            sourceOutputOwnershipCoverage = ownershipCoverage.Length == 0 ? 0 : ownershipCoverage.Min(),
            expectedGoldOccurrences = gold.Count,
            goldJoinedAfterRoutes = true,
            repeats = repeats.Select(item => item.Summary).ToArray(),
            classificationCounts,
            firstLoss,
            providerCalls,
            providerTimeouts = TimeoutConfiguration(timeoutOptions),
            holdoutTouched = false,
            rawPromptsStored = false,
            rawCompletionsStored = false,
            partialResponsesScored = false,
            nextRecommendedOwner = RecommendOwner(repeats),
            nextRecommendedIntervention = RecommendIntervention(repeats),
        };

        await WriteJsonAsync(Path.Combine(outputRoot, "diagnostic-repeat-1.v1.json"), repeats[0].Artifact, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "diagnostic-repeat-2.v1.json"), repeats[1].Artifact, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "diagnostic-repeat-3.v1.json"), repeats[2].Artifact, cancellationToken);
        await WriteJsonLinesAsync(Path.Combine(outputRoot, "retention-ledger.v1.jsonl"), allLedger.Select(item => new
        {
            repeat = item.repeat,
            item.entry,
        }), cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "system-first-loss.v1.json"), new
        {
            artifactKind = "a99_system_first_loss",
            schemaVersion = "a99-system-first-loss-v1",
            status = "PASS",
            population = Population,
            executionRevision,
            systemInducedLosses = firstLoss.Values.Sum(),
            byStage = firstLoss,
            providerCalls,
        }, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "capability-gap.v1.json"), new
        {
            artifactKind = "a99_model_capability_gap",
            schemaVersion = "a99-capability-gap-v1",
            status = "PASS",
            population = Population,
            executionRevision,
            classificationCounts,
            providerCalls,
        }, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "final-diagnosis.v1.json"), aggregate, cancellationToken);
        await WriteMarkdownAsync(Path.Combine(repoRoot, "docs", "accuracy", "accuracy99-model-capability-vs-system-retention-v1.md"), aggregate, repeats, gold.Count, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "capability-retention-manifest.v2.json"), new
        {
            artifactKind = "a99_reasoning_capability_retention_manifest",
            schemaVersion = "a99-reasoning-capability-retention-v2",
            status = "PASS",
            population = Population,
            executionRevision,
            occurrenceMaterialization = new
            {
                path = OccurrenceManifest,
                sha256 = FileSha256(manifestPath),
                documents = gold.Select(item => item.DocumentId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                expectedOccurrences = gold.Count,
            },
            routes = new[] { "MODEL_CAPABILITY_CEILING", "PRODUCTION_SYSTEM", "REASONING_PRESERVING_SHADOW" },
            repeats = 3,
            goldFirewall = new { goldSuppliedToRoutes = false, goldJoinedAfterRoutes = true },
            provider = new
            {
                name = "OpenRouter",
                model = reasoningRemote.Model,
                temperature = 0,
                contextSize = reasoningRemote.ContextSize,
                maxOutputTokens = reasoningRemote.MaxOutputTokens,
                timeouts = TimeoutConfiguration(timeoutOptions),
            },
            providerCalls,
            partialResponsesScored = false,
            holdoutTouched = false,
            statusReason = "three paired repeats completed",
        }, cancellationToken);

        Console.WriteLine($"R1-B complete: gold={gold.Count}; providerCalls={providerCalls}; owner={RecommendOwner(repeats)}");
        return 0;
    }

    private static RemoteInferenceOptions BuildReasoningRemoteOptions(RemoteInferenceOptions remote)
    {
        var configured = Environment.GetEnvironmentVariable("A99_REASONING_MAX_OUTPUT_TOKENS");
        var maxOutputTokens = string.IsNullOrWhiteSpace(configured)
            ? Math.Max(remote.MaxOutputTokens, DefaultReasoningMaxOutputTokens)
            : int.TryParse(configured, out var parsed) ? parsed : throw new InvalidOperationException(
                "A99_REASONING_MAX_OUTPUT_TOKENS must be an integer.");
        if (maxOutputTokens is < 1_024 or > 16_384)
            throw new InvalidOperationException("A99_REASONING_MAX_OUTPUT_TOKENS must be between 1024 and 16384.");

        var reasoningRemote = new RemoteInferenceOptions
        {
            Endpoint = remote.Endpoint,
            ApiKey = remote.ApiKey,
            Model = remote.Model,
            ContextSize = remote.ContextSize,
            MaxOutputTokens = maxOutputTokens,
            MissingIdRetries = remote.MissingIdRetries,
            RequestTimeoutSeconds = remote.RequestTimeoutSeconds,
            TransientRequestRetries = remote.TransientRequestRetries,
            MaxParallelRequests = remote.MaxParallelRequests,
            SendChatTemplateKwargs = remote.SendChatTemplateKwargs,
            RequireJsonObjectResponse = remote.RequireJsonObjectResponse,
            DebugLog = remote.DebugLog,
        };
        reasoningRemote.Validate();
        return reasoningRemote;
    }

    private static async Task<RepeatResult> RunRepeatAsync(
        string repoRoot,
        IReadOnlyList<ReasoningGoldOccurrence> gold,
        RemoteInferenceOptions remote,
        RemoteInferenceOptions reasoningRemote,
        ReasoningProviderTimeoutOptions timeoutOptions,
        int repeat,
        CancellationToken ct)
    {
        var documents = new List<DocumentRunResult>();
        var providerCalls = 0;
        var completionTelemetry = new List<ReasoningCompletionTelemetry>();
        var completionStats = new List<ReasoningCompletionStats>();
        foreach (var documentId in StrictGoldOccurrenceMaterializer.DocumentIds)
        {
            ct.ThrowIfCancellationRequested();
            var inputPath = Path.Combine(repoRoot, StrictGoldOccurrenceMaterializer.SourcePaths[documentId]
                .Replace('/', Path.DirectorySeparatorChar));
            var documentGold = gold.Where(item => item.DocumentId == documentId).ToArray();
            var source = new OpenXmlDocumentSource().Read(inputPath);
            var features = NumberingStyleFeatures.FromSourceDocument(source);
            var derived = new DocumentFeatureDeriver().Derive(source);
            var pipelineOptions = new PipelineOptions { DisableLlm = false };
            var policyState = DocxPolicyStateBuilder.Build(source, features, derived, pipelineOptions.Extraction);

            using var ceilingModel = new OpenRouterReasoningSemanticModel(reasoningRemote, timeoutOptions: timeoutOptions);
            var ceilingObservation = await new ReasoningPreservingHeadingHarness(ceilingModel)
                .RunAsync(source, policyState, ReasoningRoute.ModelCapabilityCeiling, ct);
            providerCalls += ceilingModel.ProviderCalls;
            completionTelemetry.AddRange(ceilingModel.CompletionTelemetry);
            completionStats.Add(ceilingObservation.CompletionStats);

            using var shadowModel = new OpenRouterReasoningSemanticModel(reasoningRemote, timeoutOptions: timeoutOptions);
            var shadowObservation = await new ReasoningPreservingHeadingHarness(shadowModel)
                .RunAsync(source, policyState, ReasoningRoute.ReasoningPreservingShadow, ct);
            providerCalls += shadowModel.ProviderCalls;
            completionTelemetry.AddRange(shadowModel.CompletionTelemetry);
            completionStats.Add(shadowObservation.CompletionStats);

            var selection = new InferenceProviderSelection
            {
                Backend = InferenceBackend.OpenRouter,
                Remote = remote,
            };
            using var pipeline = new AuthorityExtractionPipeline(
                pipelineOptions,
                new HeaderClassifierFactory(selection));
            AuthorityPipelineExecutionResult execution;
            try
            {
                execution = await pipeline.RunDocumentExecutionAsync(inputPath, ct: ct);
            }
            catch (Exception ex)
            {
                throw new ReasoningRetentionExecutionException(
                    documentId,
                    "PRODUCTION_PIPELINE_FAILURE",
                    ex.Message,
                    providerCalls,
                    completionTelemetry.ToArray(),
                    completionStats.ToArray(),
                    ex);
            }
            var productionCalls = execution.Result.Provenance.ProviderCalls;
            providerCalls += productionCalls;

            var ceiling = BuildModelFinals(source, ceilingObservation);
            var shadow = BuildModelFinals(source, shadowObservation);
            var production = BuildProductionFinals(source, execution.CompatibilityOutline, documentGold);
            var evaluation = RetentionEvaluation.Evaluate(documentGold, ceiling, production, shadow);
            var routeMetrics = new
            {
                ceiling = MeasureRoute(documentGold, ceilingObservation.Proposed, ceiling),
                production = MeasureRoute(documentGold, [], production),
                shadow = MeasureRoute(documentGold, shadowObservation.Proposed, shadow),
            };
            var contextCoverage = ContextCoverage(source, ceilingObservation, shadowObservation);
            var semanticCoverage = Math.Min(
                ceilingObservation.SourceToSemanticPassCoverage,
                shadowObservation.SourceToSemanticPassCoverage);
            var ownershipCoverage = Math.Min(
                ceilingObservation.SourceOutputOwnershipCoverage,
                shadowObservation.SourceOutputOwnershipCoverage);
            documents.Add(new DocumentRunResult(
                documentId,
                documentGold.Length,
                evaluation,
                routeMetrics,
                contextCoverage,
                ceilingObservation.ProviderCalls,
                productionCalls,
                shadowObservation.ProviderCalls,
                semanticCoverage,
                ownershipCoverage,
                [ceilingObservation.CompletionStats, shadowObservation.CompletionStats]));
        }

        return new RepeatResult(repeat, providerCalls, documents, completionTelemetry);
    }

    private static IReadOnlyDictionary<string, ReasoningRouteFinal> BuildModelFinals(
        SourceDocument source,
        ReasoningRouteObservation observation)
    {
        var sourceById = source.Paragraphs.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var validated = observation.Validated.ToDictionary(item => item.ElementId, StringComparer.Ordinal);
        var finals = new Dictionary<string, ReasoningRouteFinal>(StringComparer.Ordinal);
        foreach (var proposal in observation.Proposed
                     .GroupBy(item => RetentionEvaluation.OccurrenceKey(item.SourceId, item.HeadingSpan), StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            sourceById.TryGetValue(proposal.SourceId, out var paragraph);
            var sourcePresent = paragraph is not null && proposal.HeadingSpan.IsValidFor(paragraph.Text);
            var contextVisible = sourcePresent && observation.ContextVisible.Contains(ContextId(source, paragraph!));
            var elementId = ReasoningProposalMaterializer.ElementId(proposal);
            var isValidated = validated.TryGetValue(elementId, out var row);
            finals[RetentionEvaluation.OccurrenceKey(proposal.SourceId, proposal.HeadingSpan)] = new ReasoningRouteFinal(
                SourcePresent: sourcePresent,
                RepresentationPresent: sourcePresent,
                CandidatePresent: sourcePresent,
                ModelRequestPresent: contextVisible,
                ModelProposed: true,
                PostValidatorPresent: isValidated && row!.Accepted,
                PostResolverPresent: isValidated && row!.Accepted,
                FinalIncluded: observation.FinalIncluded.Contains(elementId),
                Role: proposal.SemanticRole,
                Level: proposal.ProposedLevel,
                Parent: proposal.ProposedParent,
                SourceId: proposal.SourceId,
                Span: proposal.HeadingSpan);
        }
        return finals;
    }

    private static IReadOnlyDictionary<string, ReasoningRouteFinal> BuildProductionFinals(
        SourceDocument source,
        DocumentOutline outline,
        IReadOnlyList<ReasoningGoldOccurrence> gold)
    {
        var traces = outline.RouteAudit?.OccurrenceTraces ?? [];
        var sourceById = source.Paragraphs.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var results = new Dictionary<string, ReasoningRouteFinal>(StringComparer.Ordinal);
        foreach (var reference in gold)
        {
            if (reference.HeadingSpan is not { } span) continue;
            sourceById.TryGetValue(reference.SourceId, out var paragraph);
            var trace = traces.FirstOrDefault(item => item.SourceId == reference.SourceId &&
                item.FinalSpan is { } finalSpan && finalSpan.Start == span.Start && finalSpan.End == span.End)
                ?? traces.FirstOrDefault(item => item.SourceId == reference.SourceId);
            var heading = outline.Headings.FirstOrDefault(item => item.SourceId == reference.SourceId &&
                item.HeadingSpan is { } headingSpan && headingSpan.Start == span.Start && headingSpan.End == span.End);
            results[RetentionEvaluation.OccurrenceKey(reference.SourceId, span)] = new ReasoningRouteFinal(
                SourcePresent: paragraph is not null && span.IsValidFor(paragraph.Text),
                RepresentationPresent: trace?.RepresentationId is not null,
                CandidatePresent: trace?.CandidateConstructed == true,
                ModelRequestPresent: trace is not null && trace.ModelRequestMembership != "UNKNOWN",
                ModelProposed: trace?.ModelProposalPresent == true,
                PostValidatorPresent: trace?.ValidationStatus is not null && trace.ValidationIssues.Count == 0,
                PostResolverPresent: trace?.StructuralAfter is not null || heading is not null,
                FinalIncluded: heading is not null,
                Role: trace?.FinalRole,
                Level: heading?.Level ?? trace?.FinalLevel,
                Parent: trace?.FinalParent,
                SourceId: reference.SourceId,
                Span: heading?.HeadingSpan is { } outputSpan
                    ? new StructuralSpan(outputSpan.Start, outputSpan.End)
                    : trace?.FinalSpan is { } traceSpan
                        ? new StructuralSpan(traceSpan.Start, traceSpan.End)
                        : null);
        }
        return results;
    }

    private static RouteMetric MeasureRoute(
        IReadOnlyList<ReasoningGoldOccurrence> gold,
        IEnumerable<ReasoningHeadingProposal> proposals,
        IReadOnlyDictionary<string, ReasoningRouteFinal> finals)
    {
        var goldKeys = gold.Where(item => item.HeadingSpan is not null)
            .Select(item => RetentionEvaluation.OccurrenceKey(item.SourceId, item.HeadingSpan!))
            .ToHashSet(StringComparer.Ordinal);
        var predicted = finals.Where(item => item.Value.FinalIncluded)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        var tp = predicted.Intersect(goldKeys, StringComparer.Ordinal).Count();
        var fp = predicted.Except(goldKeys, StringComparer.Ordinal).Count();
        var fn = goldKeys.Except(predicted, StringComparer.Ordinal).Count();
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp);
        var recall = goldKeys.Count == 0 ? 0d : (double)tp / goldKeys.Count;
        var f1 = precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall);
        return new RouteMetric(tp, fp, fn, precision, recall, f1, predicted.Count, proposals.Count());
    }

    private static double ContextCoverage(
        SourceDocument source,
        ReasoningRouteObservation ceiling,
        ReasoningRouteObservation shadow)
    {
        var ids = source.Paragraphs.Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select(item => ContextId(source, item))
            .ToArray();
        if (ids.Length == 0) return 1;
        var ceilingCoverage = ids.Count(id => ceiling.ContextVisible.Contains(id));
        var shadowCoverage = ids.Count(id => shadow.ContextVisible.Contains(id));
        return (double)Math.Min(ceilingCoverage, shadowCoverage) / ids.Length;
    }

    private static string ContextId(SourceDocument source, SourceParagraph paragraph) =>
        $"{source.DocumentId}:{paragraph.SourceId}:{paragraph.SourceOrdinal}:{paragraph.Text.Length}";

    private static IReadOnlyList<ReasoningGoldOccurrence> LoadAndValidateGold(string repoRoot)
    {
        var manifestPath = Path.Combine(repoRoot, OccurrenceManifest.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("R1-B occurrence manifest missing.", manifestPath);
        var manifest = JsonSerializer.Deserialize<StrictGoldOccurrenceMaterializationReport>(
            File.ReadAllText(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (manifest?.Status != "PASS" || manifest.ExpectedOccurrences != 311 || manifest.MaterializedOccurrences != 311 || manifest.MaterializedDocuments != 6)
            throw new InvalidDataException("R1-B blocked: exact occurrence materialization is not PASS 311/311.");

        var result = new List<ReasoningGoldOccurrence>();
        foreach (var documentId in StrictGoldOccurrenceMaterializer.DocumentIds)
        {
            var path = Path.Combine(repoRoot, RetentionRoot.Replace('/', Path.DirectorySeparatorChar), "..", "strict-gold-occurrence-v1", $"{documentId}.occurrence-gold-v1.json");
            result.AddRange(ReasoningGoldArtifactLoader.LoadOccurrence(Path.GetFullPath(path)));
        }
        if (result.Count != 311 || result.Select(item => item.GoldOccurrenceId).Distinct(StringComparer.Ordinal).Count() != 311)
            throw new InvalidDataException("R1-B blocked: exact Gold occurrence set is not unique 311/311.");
        return result;
    }

    private static string RecommendOwner(IReadOnlyList<RepeatResult> repeats)
    {
        var metrics = repeats.SelectMany(item => item.Documents)
            .Select(item => item.Evaluation.Metrics)
            .ToArray();
        var ceiling = metrics.Average(item => item.Ceiling.F1 ?? 0);
        var production = metrics.Average(item => item.Production.F1 ?? 0);
        var shadow = metrics.Average(item => item.Shadow.F1 ?? 0);
        if (ceiling >= 0.99 && production < 0.99) return "SYSTEM_RETENTION";
        if (shadow > production + 0.02) return "SYSTEM_RETENTION_ARCHITECTURE";
        return ceiling < 0.99 ? "MODEL_CAPABILITY" : "NO_SINGLE_OWNER_ESTABLISHED";
    }

    private static string RecommendIntervention(IReadOnlyList<RepeatResult> repeats) =>
        RecommendOwner(repeats) switch
        {
            "SYSTEM_RETENTION" or "SYSTEM_RETENTION_ARCHITECTURE" => "inspect measured first-system-loss stages; no heuristic change in R1-B",
            "MODEL_CAPABILITY" => "improve context/prompt/model only after the diagnostic review",
            _ => "retain current pipeline and adjudicate the largest measured error bucket",
        };

    private static object TimeoutConfiguration(ReasoningProviderTimeoutOptions options) => new
    {
        connectSeconds = (int)options.ConnectTimeout.TotalSeconds,
        firstByteSeconds = (int)options.FirstByteTimeout.TotalSeconds,
        inactivitySeconds = (int)options.InactivityTimeout.TotalSeconds,
        totalSeconds = (int)options.TotalRequestTimeout.TotalSeconds,
    };

    private static object BuildProviderLivenessArtifact(
        string status,
        ReasoningProviderTimeoutOptions timeoutOptions,
        IReadOnlyList<ReasoningCompletionTelemetry> telemetry,
        IReadOnlyList<ReasoningCompletionStats> completionStats,
        int providerCalls) {
        var timeoutAttempts = telemetry
            .Where(item => IsTimeoutFailure(item.FailureClass))
            .ToArray();
        return new
        {
            artifactKind = "a99_provider_liveness",
            schemaVersion = "a99-provider-liveness-v1",
            status,
            provider = "OpenRouter",
            timeouts = TimeoutConfiguration(timeoutOptions),
            providerCalls,
            attemptCount = telemetry.Count,
            timeoutCount = timeoutAttempts.Length,
            timeoutFailureClasses = timeoutAttempts
                .Select(item => item.FailureClass!)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            timeoutStages = timeoutAttempts
                .Where(item => !string.IsNullOrWhiteSpace(item.TimeoutStage))
                .GroupBy(item => item.TimeoutStage!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            transportRetryCount = completionStats.Sum(item => item.Completion.RetryCount),
            outputLimitSplitCount = completionStats.Sum(item => item.Completion.RangeSplitCount),
            partialResponsesScored = false,
            boundedRetries = true,
        };
    }

    private static object BuildSourceVisibilityArtifact(
        string status,
        ReasoningProviderTimeoutOptions timeoutOptions,
        IReadOnlyList<ReasoningCompletionTelemetry> telemetry,
        IReadOnlyList<ReasoningCompletionStats> completionStats,
        int providerCalls) {
        var sourceFailures = telemetry
            .Where(item => item.ValidationReason?.StartsWith("reasoning-response-source-not-visible", StringComparison.Ordinal) == true)
            .ToArray();
        var spanFailures = telemetry
            .Where(item => item.ValidationReason?.StartsWith("reasoning-response-span-invalid-for-visible-source", StringComparison.Ordinal) == true)
            .ToArray();
        return new
        {
            artifactKind = "a99_source_visibility_contract",
            schemaVersion = "a99-source-visibility-contract-v1",
            status,
            identityContract = "canonicalSourceId plus explicit sourceOccurrenceId alias; no fuzzy repair",
            canonicalSourceIdAccepted = true,
            explicitOccurrenceAliasAccepted = true,
            unknownSourceIdFailClosed = true,
            visibleButNotOwnedClassified = true,
            requestIdentityChecks = new[] { "requestId", "semanticPassId", "attemptId", "ownedRange" },
            spanValidation = "against visible source RawText",
            sourceVisibilityFailureCount = sourceFailures.Length,
            spanValidationFailureCount = spanFailures.Length,
            ownershipViolationCount = completionStats.Sum(item => item.Completion.OwnershipViolationCount),
            returnedSourceIds = telemetry
                .SelectMany(item => item.ReturnedSourceIds)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            visibleSourceIds = telemetry
                .SelectMany(item => item.VisibleSourceIds)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            ownedSourceIds = telemetry
                .SelectMany(item => item.OwnedSourceIds)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            goldFirewall = new
            {
                goldUsedForSourceMapping = false,
                goldUsedForResponseRepair = false,
                goldJoinedAfterRoutes = true,
            },
            providerTimeouts = TimeoutConfiguration(timeoutOptions),
            providerCalls,
            holdoutTouched = false,
        };
    }

    private static bool IsTimeoutFailure(string? failureClass) => failureClass is
        ReasoningCompletionFailureClass.ProviderConnectTimeout or
        ReasoningCompletionFailureClass.ProviderFirstByteTimeout or
        ReasoningCompletionFailureClass.ProviderStreamInactivityTimeout or
        ReasoningCompletionFailureClass.ProviderTotalTimeout;

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false), ct);
    }

    private static async Task WriteJsonLinesAsync(string path, IEnumerable<object> rows, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllLinesAsync(path, rows.Select(row => JsonSerializer.Serialize(row, JsonOptions)), new UTF8Encoding(false), ct);
    }

    private static async Task WriteMarkdownAsync(string path, object aggregate, IReadOnlyList<RepeatResult> repeats, int goldCount, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Accuracy-99 model capability versus system retention");
        sb.AppendLine();
        sb.AppendLine("This is an evaluation-only diagnostic on `DIAGNOSTIC_DEV_SUBSET_V1`. Exact Gold joins occur after route execution; no Gold, prompts, or raw completions are stored in the artifacts.");
        sb.AppendLine();
        sb.AppendLine($"- Gold occurrences: `{goldCount}`");
        sb.AppendLine("- Repeats: `3` paired A -> B -> C");
        sb.AppendLine("- Provider calls: recorded in `final-diagnosis.v1.json`");
        sb.AppendLine($"- Recommendation: `{RecommendOwner(repeats)}`");
        sb.AppendLine();
        sb.AppendLine("## Repeat metrics");
        sb.AppendLine();
        sb.AppendLine("| Repeat | Ceiling F1 | Production F1 | Shadow F1 | Retention gap |");
        sb.AppendLine("|---:|---:|---:|---:|---:|");
        foreach (var repeat in repeats)
        {
            var metrics = repeat.Documents.Select(item => item.Evaluation.Metrics).ToArray();
            sb.AppendLine($"| {repeat.Repeat} | {metrics.Average(item => item.Ceiling.F1 ?? 0):F4} | {metrics.Average(item => item.Production.F1 ?? 0):F4} | {metrics.Average(item => item.Shadow.F1 ?? 0):F4} | {metrics.Average(item => item.RetentionGapF1 ?? 0):F4} |");
        }
        sb.AppendLine();
        sb.AppendLine("Unsupported parent and hierarchy dimensions remain `NOT_EVALUABLE` unless Gold provides them.");
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false), ct);
    }

    private static async Task WriteCompletionMarkdownAsync(
        string repoRoot,
        string status,
        IReadOnlyList<string> failureClasses,
        IReadOnlyList<ReasoningCompletionTelemetry> telemetry,
        int rangeSplits,
        int retries,
        string? blockerClass,
        CancellationToken ct)
    {
        var path = Path.Combine(repoRoot, "docs", "accuracy", "accuracy99-provider-completion-integrity-v1.md");
        var sb = new StringBuilder();
        sb.AppendLine("# Accuracy-99 provider completion integrity");
        sb.AppendLine();
        sb.AppendLine($"- Status: `{status}`");
        sb.AppendLine($"- Attempts: `{telemetry.Count}`");
        sb.AppendLine($"- Successful completions: `{telemetry.Count(item => item.JsonParseSucceeded && item.CompletionEnvelopeComplete && item.FailureClass is null)}`");
        sb.AppendLine($"- Failed completions: `{telemetry.Count(item => item.FailureClass is not null)}`");
        sb.AppendLine($"- Range splits: `{rangeSplits}`");
        sb.AppendLine($"- Retries: `{retries}`");
        sb.AppendLine($"- Failure classes: `{(failureClasses.Count == 0 ? "none" : string.Join(", ", failureClasses))}`");
        if (blockerClass is not null)
            sb.AppendLine($"- Run blocker: `{blockerClass}`");
        sb.AppendLine("- Partial responses scored: `false`");
        sb.AppendLine("- Raw prompts/completions: `not stored`");
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false), ct);
    }

    private static string FileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string? GitRevision(string repoRoot)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = "rev-parse HEAD",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : null;
    }

    private sealed record RouteMetric(int TruePositive, int FalsePositive, int FalseNegative, double Precision, double Recall, double F1, int Predicted, int Proposed);

    private sealed record DocumentRunResult(
        string DocumentId,
        int GoldOccurrences,
        ReasoningDocumentEvaluation Evaluation,
        object RouteMetrics,
        double ContextCoverage,
        int CeilingProviderCalls,
        int ProductionProviderCalls,
        int ShadowProviderCalls,
        double SemanticPassCoverage,
        double OutputOwnershipCoverage,
        IReadOnlyList<ReasoningCompletionStats> CompletionStats);

    private sealed record RepeatResult(
        int Repeat,
        int ProviderCalls,
        IReadOnlyList<DocumentRunResult> Documents,
        IReadOnlyList<ReasoningCompletionTelemetry> CompletionTelemetry)
    {
        public IReadOnlyList<ReasoningCompletionStats> CompletionStats() => Documents
            .SelectMany(item => item.CompletionStats)
            .ToArray();

        public object Summary => new
        {
            repeat = Repeat,
            providerCalls = ProviderCalls,
            sourceToModelContextCoverage = Documents.Count == 0 ? 0 : Documents.Min(item => item.ContextCoverage),
            sourceToSemanticPassCoverage = Documents.Count == 0 ? 0 : Documents.Min(item => item.SemanticPassCoverage),
            sourceOutputOwnershipCoverage = Documents.Count == 0 ? 0 : Documents.Min(item => item.OutputOwnershipCoverage),
            documents = Documents.Select(item => new
            {
                item.DocumentId,
                item.GoldOccurrences,
                evaluatedOccurrences = item.Evaluation.EvaluatedOccurrenceCount,
                notEvaluableOccurrences = item.Evaluation.NotEvaluableOccurrenceCount,
                metrics = item.Evaluation.Metrics,
                routeMetrics = item.RouteMetrics,
                firstLoss = item.Evaluation.Ledger.GroupBy(row => row.FirstSystemLossStage.ToString())
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            }).ToArray(),
        };

        public object Artifact => new
        {
            artifactKind = "a99_reasoning_retention_repeat",
            schemaVersion = "a99-reasoning-retention-repeat-v1",
            status = "PASS",
            population = Population,
            repeat = Repeat,
            providerCalls = ProviderCalls,
            sourceToModelContextCoverage = Documents.Count == 0 ? 0 : Documents.Min(item => item.ContextCoverage),
            sourceToSemanticPassCoverage = Documents.Count == 0 ? 0 : Documents.Min(item => item.SemanticPassCoverage),
            sourceOutputOwnershipCoverage = Documents.Count == 0 ? 0 : Documents.Min(item => item.OutputOwnershipCoverage),
            goldJoinedAfterRoutes = true,
            documents = Documents.Select(item => new
            {
                item.DocumentId,
                item.GoldOccurrences,
                evaluatedOccurrences = item.Evaluation.EvaluatedOccurrenceCount,
                notEvaluableOccurrences = item.Evaluation.NotEvaluableOccurrenceCount,
                metrics = item.Evaluation.Metrics,
                routeMetrics = item.RouteMetrics,
                firstLoss = item.Evaluation.Ledger.GroupBy(row => row.FirstSystemLossStage.ToString())
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            }).ToArray(),
            rawPromptsStored = false,
            rawCompletionsStored = false,
            holdoutTouched = false,
        };
    }
}
