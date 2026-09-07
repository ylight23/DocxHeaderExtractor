using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Evaluation-only shadow harness. It keeps source visibility independent from candidate policy
/// and materializes only after the generic hard source/span validator accepts a proposal.
/// </summary>
public sealed class ReasoningPreservingHeadingHarness
{
    private readonly IReasoningSemanticModel _model;
    private readonly int _maxContextCharacters;
    private readonly int _windowCharacters;
    private readonly int _maxTransientRetries;
    private readonly ReasoningExecutionBudgetOptions _budgetOptions;

    public ReasoningPreservingHeadingHarness(
        IReasoningSemanticModel model,
        int maxContextCharacters = 80_000,
        int windowCharacters = 48_000,
        int maxTransientRetries = 2,
        ReasoningExecutionBudgetOptions? budgetOptions = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _maxContextCharacters = maxContextCharacters;
        _windowCharacters = windowCharacters;
        _budgetOptions = budgetOptions ?? new ReasoningExecutionBudgetOptions
        {
            MaxTransientRetries = Math.Max(0, maxTransientRetries),
        };
        _budgetOptions.Validate();
        _maxTransientRetries = budgetOptions?.MaxTransientRetries ?? Math.Max(0, maxTransientRetries);
    }

    public async Task<ReasoningRouteObservation> RunAsync(
        SourceDocument source,
        DocxPolicyState policyState,
        ReasoningRoute route,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(policyState);
        if (route == ReasoningRoute.ProductionSystem)
            throw new ArgumentException("Production is observed through AuthorityExtractionPipeline, not this harness.", nameof(route));

        var pack = ReasoningContextBuilder.Build(
            source,
            policyState,
            _maxContextCharacters,
            _windowCharacters);
        var allProposals = new List<ReasoningHeadingProposal>();
        var completion = new CompletionAccumulator();
        foreach (var segment in pack.Segments)
        {
            var responses = await CompleteWithRecoveryAsync(
                source,
                pack,
                segment,
                route,
                "semantic-heading-extraction-v1",
                completion,
                ct);
            allProposals.AddRange(responses);
        }

        // Hierarchical mode gets one explicit consolidation pass. It sees every source identity
        // and the window proposals, but it never sees Gold or a candidate-filtered source subset.
        if (pack.Segments.Count > 1)
        {
            var consolidated = new List<ReasoningHeadingProposal>();
            foreach (var consolidation in BuildConsolidationSegments(pack, allProposals))
            {
                var responses = await CompleteWithRecoveryAsync(
                    source,
                    pack,
                    consolidation,
                    route,
                    "global-structural-consolidation-v1",
                    completion,
                    ct);
                consolidated.AddRange(responses);
            }
            if (consolidated.Count > 0)
                allProposals = [.. consolidated];
        }

        var materialized = ReasoningProposalMaterializer.Materialize(source, policyState, allProposals);
        var acceptedIds = materialized.Validated
            .Where(row => row.Accepted)
            .Select(row => row.ElementId)
            .ToHashSet(StringComparer.Ordinal);
        var proposed = allProposals
            .GroupBy(p => $"{p.SourceId}:{p.HeadingSpan.Start}:{p.HeadingSpan.End}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var occurrenceCount = pack.Occurrences.Count;
        var visibleCount = pack.Segments.SelectMany(item => item.SourceOccurrenceIds)
            .Distinct(StringComparer.Ordinal).Count();
        var ownedCount = pack.Segments.SelectMany(item => item.OwnedSourceOccurrenceIds)
            .Distinct(StringComparer.Ordinal).Count();
        return new ReasoningRouteObservation
        {
            Route = route,
            ContextVisible = pack.Occurrences.Select(o => o.SourceOccurrenceId).ToHashSet(StringComparer.Ordinal),
            Proposed = proposed,
            Validated = materialized.Validated,
            FinalIncluded = acceptedIds,
            ProviderCalls = _model.ProviderCalls,
            CompletionStats = completion.ToStats(),
            SourceToModelContextCoverage = occurrenceCount == 0 ? 1 : (double)visibleCount / occurrenceCount,
            SourceToSemanticPassCoverage = occurrenceCount == 0 ? 1 : (double)ownedCount / occurrenceCount,
            SourceOutputOwnershipCoverage = occurrenceCount == 0 ? 1 : (double)ownedCount / occurrenceCount,
        };
    }

    public static string ConfigurationSignature(ReasoningRoute route) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{ReasoningPrompt.Version}|qwen-compatible|temperature=0|reasoning=none|route={route}")))
            .ToLowerInvariant();

    private async Task<IReadOnlyList<ReasoningHeadingProposal>> CompleteWithRecoveryAsync(
        SourceDocument source,
        ReasoningContextPack pack,
        ReasoningContextSegment initialSegment,
        ReasoningRoute route,
        string semanticPassId,
        CompletionAccumulator completion,
        CancellationToken ct)
    {
        var pending = new Queue<ReasoningContextSegment>();
        pending.Enqueue(initialSegment);
        var responses = new List<ReasoningHeadingProposal>();
        var occurrenceById = pack.Occurrences.ToDictionary(item => item.SourceOccurrenceId, StringComparer.Ordinal);
        var passStartedUtc = DateTimeOffset.UtcNow;
        var passClock = System.Diagnostics.Stopwatch.StartNew();
        var passBudgetMs = (long)_budgetOptions.SemanticPassTimeout.TotalMilliseconds;
        var passAttempts = 0;
        var passRetries = 0;
        string? terminalClass = null;
        var passCompletedNormally = false;

        try
        {
            while (pending.Count > 0)
            {
                var segment = pending.Dequeue();
                var transientRetries = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var elapsedBeforeAttempt = passClock.Elapsed;
                    var remainingBeforeAttempt = _budgetOptions.SemanticPassTimeout - elapsedBeforeAttempt;
                    if (remainingBeforeAttempt <= TimeSpan.Zero)
                    {
                        terminalClass = ReasoningCompletionFailureClass.ProviderSemanticPassTimeout;
                        completion.FailedCompletionCount++;
                        completion.FailureClasses.Add(terminalClass);
                        throw PassTimeout(source, route, semanticPassId, segment.ContextSegmentId, passClock.Elapsed);
                    }

                    completion.AttemptCount++;
                    passAttempts++;
                    var requestId = ReasoningPrompt.BuildRequestId(source.DocumentId, segment.ContextSegmentId, ConfigurationSignature(route));
                    var attemptNumber = transientRetries + 1;
                    var visibleOccurrences = segment.SourceOccurrenceIds
                        .Select(id => occurrenceById[id])
                        .ToArray();
                    var ownedScope = BuildOwnedOutputScope(segment, occurrenceById);
                    var ownedSourceIdsForTelemetry = segment.OwnedSourceOccurrenceIds
                        .Where(occurrenceById.ContainsKey)
                        .Select(id => occurrenceById[id].SourceId)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var request = new ReasoningModelRequest
                    {
                        RequestId = requestId,
                        DocumentId = source.DocumentId,
                        Route = route.ToString(),
                        SemanticPassId = semanticPassId,
                        ContextSegmentId = segment.ContextSegmentId,
                        SystemPrompt = ReasoningPrompt.System,
                        UserPrompt = ReasoningPrompt.BuildUser(
                            segment,
                            route == ReasoningRoute.ReasoningPreservingShadow,
                            ownedScope),
                        SourceOccurrenceIds = segment.SourceOccurrenceIds,
                        OwnedSourceOccurrenceIds = segment.OwnedSourceOccurrenceIds,
                        OwnedOutputScope = ownedScope,
                        AttemptId = $"{requestId}:attempt-{attemptNumber}",
                        ConfigurationSignature = ConfigurationSignature(route),
                    };
                    var attemptStartedUtc = DateTimeOffset.UtcNow;
                    var attemptClock = System.Diagnostics.Stopwatch.StartNew();
                    var attemptTimeout = Min(_budgetOptions.AttemptTotalTimeout, remainingBeforeAttempt);

                    try
                    {
                        var response = await CompleteAttemptAsync(request, attemptTimeout, ct);
                        var attemptTelemetry = (_model as IReasoningCompletionTelemetrySource)?.CompletionTelemetry.LastOrDefault();
                        var attemptEndedUtc = DateTimeOffset.UtcNow;
                        var attemptElapsed = attemptClock.Elapsed;
                        AnnotateAttempt(
                            attemptTelemetry,
                            request,
                            attemptNumber,
                            visibleOccurrences,
                            ownedSourceIdsForTelemetry,
                            attemptStartedUtc,
                            attemptEndedUtc,
                            attemptElapsed,
                            attemptTimeout,
                            passBudgetMs,
                            elapsedBeforeAttempt,
                            remainingBeforeAttempt,
                            _budgetOptions.SemanticPassTimeout - passClock.Elapsed,
                            failureClass: null,
                            retryScheduled: false,
                            retrySuppressedByDeadline: false);
                        completion.AddAttemptBudget(new ReasoningAttemptBudgetTelemetry(
                            source.DocumentId,
                            route.ToString(),
                            semanticPassId,
                            segment.ContextSegmentId,
                            attemptNumber,
                            _maxTransientRetries,
                            attemptStartedUtc,
                            attemptEndedUtc,
                            (long)attemptElapsed.TotalMilliseconds,
                            (long)attemptTimeout.TotalMilliseconds,
                            passBudgetMs,
                            (long)elapsedBeforeAttempt.TotalMilliseconds,
                            (long)remainingBeforeAttempt.TotalMilliseconds,
                            Math.Max(0, (long)(_budgetOptions.SemanticPassTimeout - passClock.Elapsed).TotalMilliseconds),
                            null,
                            null,
                            false,
                            false));
                        var scopedResponse = ValidateResponseOwnership(response, request, segment, occurrenceById, completion);
                        responses.AddRange(scopedResponse);
                        completion.SuccessfulCompletionCount++;
                        if (semanticPassId.StartsWith("global-", StringComparison.Ordinal))
                            completion.ConsolidationPassCount++;
                        else
                            completion.SemanticPassCount++;
                        break;
                    }
                    catch (ReasoningCompletionException ex)
                    {
                        completion.FailedCompletionCount++;
                        completion.FailureClasses.Add(ex.FailureClass);
                        var attemptTelemetry = (_model as IReasoningCompletionTelemetrySource)?.CompletionTelemetry.LastOrDefault();
                        var attemptEndedUtc = DateTimeOffset.UtcNow;
                        var attemptElapsed = attemptClock.Elapsed;
                        var remainingAfterAttempt = Math.Max(0, (_budgetOptions.SemanticPassTimeout - passClock.Elapsed).TotalMilliseconds);
                        var retry = ex.FailureClass != ReasoningCompletionFailureClass.ProviderAttemptAbortUnconfirmed &&
                            IsTransientProviderFailure(ex.FailureClass) && transientRetries < _maxTransientRetries;
                        var suppressRetry = retry && remainingAfterAttempt <= 1;
                        AnnotateAttempt(
                            attemptTelemetry,
                            request,
                            attemptNumber,
                            visibleOccurrences,
                            ownedSourceIdsForTelemetry,
                            attemptStartedUtc,
                            attemptEndedUtc,
                            attemptElapsed,
                            attemptTimeout,
                            passBudgetMs,
                            elapsedBeforeAttempt,
                            remainingBeforeAttempt,
                            TimeSpan.FromMilliseconds(remainingAfterAttempt),
                            ex.FailureClass,
                            retryScheduled: retry && !suppressRetry,
                            retrySuppressedByDeadline: suppressRetry);
                        completion.AddAttemptBudget(new ReasoningAttemptBudgetTelemetry(
                            source.DocumentId,
                            route.ToString(),
                            semanticPassId,
                            segment.ContextSegmentId,
                            attemptNumber,
                            _maxTransientRetries,
                            attemptStartedUtc,
                            attemptEndedUtc,
                            (long)attemptElapsed.TotalMilliseconds,
                            (long)attemptTimeout.TotalMilliseconds,
                            passBudgetMs,
                            (long)elapsedBeforeAttempt.TotalMilliseconds,
                            (long)remainingBeforeAttempt.TotalMilliseconds,
                            (long)remainingAfterAttempt,
                            ex.FailureClass,
                            ex.Telemetry.TimeoutStage,
                            retry && !suppressRetry,
                            suppressRetry));

                        if (ex.FailureClass == ReasoningCompletionFailureClass.ProviderOutputLimit)
                        {
                            if (!CanSplitOwnedRange(segment, occurrenceById))
                                throw WithAttemptHistory(ex);
                            var split = SplitOwnership(segment, pack.Occurrences);
                            pending.Enqueue(split.Left);
                            pending.Enqueue(split.Right);
                            completion.RangeSplitCount++;
                            break;
                        }

                        if (retry && !suppressRetry)
                        {
                            transientRetries++;
                            passRetries++;
                            completion.RetryCount++;
                            continue;
                        }

                        if (retry && suppressRetry)
                        {
                            terminalClass = ReasoningCompletionFailureClass.ProviderSemanticPassTimeout;
                            completion.FailureClasses.Add(terminalClass);
                            throw PassTimeout(source, route, semanticPassId, segment.ContextSegmentId, passClock.Elapsed, ex);
                        }

                        throw WithAttemptHistory(ex);
                    }
                    catch (Exception ex) when (ex is InvalidDataException)
                    {
                        completion.FailedCompletionCount++;
                        completion.FailureClasses.Add(ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid);
                        var telemetry = (_model as IReasoningCompletionTelemetrySource)?.CompletionTelemetry.LastOrDefault()
                            ?? new ReasoningCompletionTelemetry { FailureClass = ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid };
                        telemetry.ValidationReason = ex.Message;
                        AnnotateAttempt(
                            telemetry,
                            request,
                            attemptNumber,
                            visibleOccurrences,
                            ownedSourceIdsForTelemetry,
                            attemptStartedUtc,
                            DateTimeOffset.UtcNow,
                            attemptClock.Elapsed,
                            attemptTimeout,
                            passBudgetMs,
                            elapsedBeforeAttempt,
                            remainingBeforeAttempt,
                            TimeSpan.FromMilliseconds(Math.Max(0, (_budgetOptions.SemanticPassTimeout - passClock.Elapsed).TotalMilliseconds)),
                            ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid,
                            false,
                            false);
                        throw WithAttemptHistory(new ReasoningCompletionException(
                            ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid,
                            ex.Message,
                            telemetry,
                            ex));
                    }
                }
            }

            passCompletedNormally = true;
            return responses;
        }
        catch (ReasoningCompletionException ex)
        {
            terminalClass ??= ex.FailureClass;
            throw;
        }
        finally
        {
            var passEndedUtc = DateTimeOffset.UtcNow;
            completion.AddSemanticPass(new ReasoningSemanticPassTelemetry(
                source.DocumentId,
                route.ToString(),
                semanticPassId,
                initialSegment.ContextSegmentId,
                passStartedUtc,
                passEndedUtc,
                passClock.ElapsedMilliseconds,
                passBudgetMs,
                passAttempts,
                passRetries,
                passCompletedNormally,
                terminalClass));
        }
    }

    private async Task<ReasoningModelResponse> CompleteAttemptAsync(
        ReasoningModelRequest request,
        TimeSpan attemptTimeout,
        CancellationToken callerToken)
    {
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        var completion = _model is IReasoningAttemptTimeoutModel timeoutModel
            ? timeoutModel.CompleteAsync(request, attemptTimeout, attemptCancellation.Token)
            : _model.CompleteAsync(request, attemptCancellation.Token);
        var timeout = Task.Delay(attemptTimeout);
        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, callerToken);
        var finished = await Task.WhenAny(completion, timeout, cancelled);
        if (finished == cancelled)
            throw new OperationCanceledException(callerToken);
        if (finished == timeout)
        {
            attemptCancellation.Cancel();
            var cancellationConfirmed = await Task.WhenAny(
                completion,
                Task.Delay(TimeSpan.FromMilliseconds(100)));
            if (cancellationConfirmed != completion)
            {
                _ = completion.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw new ReasoningCompletionException(
                    ReasoningCompletionFailureClass.ProviderAttemptAbortUnconfirmed,
                    "Provider attempt did not confirm cancellation before its attempt deadline.",
                    new ReasoningCompletionTelemetry { FailureClass = ReasoningCompletionFailureClass.ProviderAttemptAbortUnconfirmed });
            }
        }

        return await completion;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static ReasoningCompletionException PassTimeout(
        SourceDocument source,
        ReasoningRoute route,
        string semanticPassId,
        string contextSegmentId,
        TimeSpan elapsed,
        Exception? inner = null) =>
        new(
            ReasoningCompletionFailureClass.ProviderSemanticPassTimeout,
            $"Semantic pass exceeded its shared deadline after {elapsed.TotalMilliseconds:F0} ms.",
            new ReasoningCompletionTelemetry
            {
                DocumentId = source.DocumentId,
                Route = route.ToString(),
                SemanticPassId = semanticPassId,
                ContextSegmentId = contextSegmentId,
                FailureClass = ReasoningCompletionFailureClass.ProviderSemanticPassTimeout,
                TimeoutStage = "SEMANTIC_PASS",
            },
            inner);

    private static void AnnotateAttempt(
        ReasoningCompletionTelemetry? telemetry,
        ReasoningModelRequest request,
        int attemptNumber,
        IReadOnlyList<ReasoningSourceOccurrence> visibleOccurrences,
        IReadOnlyList<string> ownedSourceIds,
        DateTimeOffset startedUtc,
        DateTimeOffset endedUtc,
        TimeSpan elapsed,
        TimeSpan attemptTimeout,
        long passBudgetMs,
        TimeSpan passElapsedBefore,
        TimeSpan passRemainingBefore,
        TimeSpan passRemainingAfter,
        string? failureClass,
        bool retryScheduled,
        bool retrySuppressedByDeadline)
    {
        if (telemetry is null) return;
        telemetry.AttemptId = request.AttemptId;
        telemetry.AttemptNumber = attemptNumber;
        telemetry.VisibleSourceIds = visibleOccurrences
            .Select(item => item.SourceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        telemetry.OwnedSourceIds = ownedSourceIds.Order(StringComparer.Ordinal).ToArray();
        telemetry.AttemptStartedUtc = startedUtc;
        telemetry.AttemptEndedUtc = endedUtc;
        telemetry.AttemptElapsedMs = (long)elapsed.TotalMilliseconds;
        telemetry.ConfiguredAttemptTimeoutMs = (long)attemptTimeout.TotalMilliseconds;
        telemetry.SemanticPassBudgetMs = passBudgetMs;
        telemetry.SemanticPassElapsedBeforeAttemptMs = (long)passElapsedBefore.TotalMilliseconds;
        telemetry.SemanticPassRemainingBeforeAttemptMs = Math.Max(0, (long)passRemainingBefore.TotalMilliseconds);
        telemetry.SemanticPassRemainingAfterAttemptMs = Math.Max(0, (long)passRemainingAfter.TotalMilliseconds);
        telemetry.FailureClass ??= failureClass;
        telemetry.RetryScheduled = retryScheduled;
        telemetry.RetrySuppressedByDeadline = retrySuppressedByDeadline;
    }

    private ReasoningCompletionException WithAttemptHistory(ReasoningCompletionException exception)
    {
        if (_model is not IReasoningCompletionTelemetrySource source || source.CompletionTelemetry.Count <= 1)
            return exception;
        return new ReasoningCompletionException(
            exception.FailureClass,
            exception.Message,
            exception.Telemetry,
            exception,
            source.CompletionTelemetry.ToArray());
    }

    private static IReadOnlyList<ReasoningHeadingProposal> ValidateResponseOwnership(
        ReasoningModelResponse response,
        ReasoningModelRequest request,
        ReasoningContextSegment segment,
        IReadOnlyDictionary<string, ReasoningSourceOccurrence> occurrenceById,
        CompletionAccumulator completion)
    {
        var scope = request.OwnedOutputScope;
        if (!occurrenceById.TryGetValue(scope.SourceOccurrenceId, out var occurrence) ||
            !string.Equals(occurrence.SourceId, scope.CanonicalSourceId, StringComparison.Ordinal))
            throw new InvalidDataException("reasoning-owned-output-source-not-found");
        if (scope.RawTextLength != occurrence.RawText.Length ||
            scope.OwnedStart < 0 ||
            scope.OwnedEnd <= scope.OwnedStart ||
            scope.OwnedEnd > scope.RawTextLength)
            throw new InvalidDataException("reasoning-owned-output-scope-invalid");

        var scoped = new List<ReasoningHeadingProposal>(response.Headings.Count);
        var emittedOccurrences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var proposal in response.Headings)
        {
            var localSpan = new StructuralSpan(proposal.Start, proposal.End);
            if (localSpan.Start < 0 ||
                localSpan.End <= localSpan.Start ||
                localSpan.End > scope.OwnedEnd - scope.OwnedStart)
                throw new InvalidDataException(
                    $"reasoning-response-local-span-invalid; start={proposal.Start}; end={proposal.End}; " +
                    $"ownedStart={scope.OwnedStart}; ownedEnd={scope.OwnedEnd}");

            var globalSpan = new StructuralSpan(scope.OwnedStart + localSpan.Start, scope.OwnedStart + localSpan.End);
            var occurrenceKey = $"{scope.CanonicalSourceId}:{globalSpan.Start}:{globalSpan.End}";
            if (!emittedOccurrences.Add(occurrenceKey))
                throw new InvalidDataException($"reasoning-response-duplicate-occurrence; occurrence={occurrenceKey}");

            scoped.Add(new ReasoningHeadingProposal
            {
                SourceId = scope.CanonicalSourceId,
                HeadingSpan = globalSpan,
                Text = occurrence.RawText[globalSpan.Start..globalSpan.End],
                SemanticRole = proposal.SemanticRole,
                ProposedLevel = proposal.ProposedLevel,
                Confidence = proposal.Confidence,
                DecisionEvidence = proposal.DecisionEvidence,
            });
        }
        return scoped;
    }

    private static ReasoningOwnedOutputScope BuildOwnedOutputScope(
        ReasoningContextSegment segment,
        IReadOnlyDictionary<string, ReasoningSourceOccurrence> occurrenceById)
    {
        if (segment.OwnedSourceOccurrenceId is null ||
            segment.OwnedStartCharacter is not { } start ||
            segment.OwnedEndCharacter is not { } end ||
            !occurrenceById.TryGetValue(segment.OwnedSourceOccurrenceId, out var occurrence))
            throw new InvalidDataException("reasoning-owned-output-scope-missing");
        return new ReasoningOwnedOutputScope
        {
            CanonicalSourceId = occurrence.SourceId,
            SourceOccurrenceId = occurrence.SourceOccurrenceId,
            RawTextLength = occurrence.RawText.Length,
            OwnedStart = start,
            OwnedEnd = end,
        };
    }

    private static bool CanSplitOwnedRange(
        ReasoningContextSegment segment,
        IReadOnlyDictionary<string, ReasoningSourceOccurrence> occurrenceById) =>
        segment.OwnedStartCharacter is { } start &&
        segment.OwnedEndCharacter is { } end &&
        end - start > 1 &&
        segment.OwnedSourceOccurrenceId is not null &&
        occurrenceById.ContainsKey(segment.OwnedSourceOccurrenceId);

    private static bool IsTransientProviderFailure(string failureClass) =>
        failureClass is ReasoningCompletionFailureClass.TransportTruncation or
            ReasoningCompletionFailureClass.Timeout or
            ReasoningCompletionFailureClass.ProviderConnectTimeout or
            ReasoningCompletionFailureClass.ProviderFirstByteTimeout or
            ReasoningCompletionFailureClass.ProviderStreamInactivityTimeout or
            ReasoningCompletionFailureClass.ProviderTotalTimeout;

    private static (ReasoningContextSegment Left, ReasoningContextSegment Right) SplitOwnership(
        ReasoningContextSegment segment,
        IReadOnlyList<ReasoningSourceOccurrence> occurrences)
    {
        if (segment.OwnedSourceOccurrenceId is null ||
            segment.OwnedStartCharacter is not { } start ||
            segment.OwnedEndCharacter is not { } end)
            throw new InvalidDataException("reasoning-owned-output-scope-missing");
        var midpoint = start + ((end - start) / 2);
        if (midpoint <= start || midpoint >= end)
            throw new InvalidDataException("reasoning-owned-output-range-not-splittable");
        var left = segment with
        {
            ContextSegmentId = segment.ContextSegmentId + $"-split-{start}-{midpoint}",
            OwnedSourceOccurrenceIds = [segment.OwnedSourceOccurrenceId],
            OwnedStartCharacter = start,
            OwnedEndCharacter = midpoint,
        };
        var right = segment with
        {
            ContextSegmentId = segment.ContextSegmentId + $"-split-{midpoint}-{end}",
            OwnedSourceOccurrenceIds = [segment.OwnedSourceOccurrenceId],
            OwnedStartCharacter = midpoint,
            OwnedEndCharacter = end,
        };
        return (left, right);
    }

    private static IReadOnlyList<ReasoningContextSegment> BuildConsolidationSegments(
        ReasoningContextPack pack,
        IReadOnlyList<ReasoningHeadingProposal> proposals)
    {
        var sb = new StringBuilder();
        sb.AppendLine("GLOBAL_PROPOSAL_CONSOLIDATION");
        sb.AppendLine("All source occurrences were visible in at least one prior window.");
        sb.AppendLine(JsonSerializer.Serialize(new
        {
            sourceOccurrenceIds = pack.Occurrences.Select(o => o.SourceOccurrenceId),
            windowProposals = proposals,
        }));
        var baseSegment = new ReasoningContextSegment
        {
            ContextSegmentId = "global-consolidation",
            Ordinal = pack.Segments.Count + 1,
            SourceOccurrenceIds = pack.Occurrences.Select(o => o.SourceOccurrenceId).ToArray(),
            OwnedSourceOccurrenceIds = pack.Occurrences.Select(o => o.SourceOccurrenceId).ToArray(),
            VisibleStartOrdinal = pack.Occurrences.Count == 0 ? null : pack.Occurrences[0].SourceOrdinal,
            VisibleEndOrdinal = pack.Occurrences.Count == 0 ? null : pack.Occurrences[^1].SourceOrdinal,
            OwnedStartOrdinal = pack.Occurrences.Count == 0 ? null : pack.Occurrences[0].SourceOrdinal,
            OwnedEndOrdinal = pack.Occurrences.Count == 0 ? null : pack.Occurrences[^1].SourceOrdinal,
            Text = sb.ToString(),
        };
        return pack.Occurrences.Select(occurrence => baseSegment with
        {
            ContextSegmentId = $"global-consolidation-owned-{occurrence.SourceOrdinal}",
            OwnedSourceOccurrenceIds = [occurrence.SourceOccurrenceId],
            OwnedSourceOccurrenceId = occurrence.SourceOccurrenceId,
            OwnedStartCharacter = 0,
            OwnedEndCharacter = occurrence.RawText.Length,
            OwnedStartOrdinal = occurrence.SourceOrdinal,
            OwnedEndOrdinal = occurrence.SourceOrdinal,
        }).ToArray();
    }

    private sealed class CompletionAccumulator
    {
        public int AttemptCount { get; set; }
        public int SuccessfulCompletionCount { get; set; }
        public int FailedCompletionCount { get; set; }
        public int RangeSplitCount { get; set; }
        public int RetryCount { get; set; }
        public int SemanticPassCount { get; set; }
        public int ConsolidationPassCount { get; set; }
        public int OutOfScopeProposalCount { get; set; }
        public int OwnershipViolationCount { get; set; }
        public List<string> FailureClasses { get; } = [];
        public List<ReasoningSemanticPassTelemetry> SemanticPasses { get; } = [];
        public List<ReasoningAttemptBudgetTelemetry> AttemptBudgets { get; } = [];

        public void AddSemanticPass(ReasoningSemanticPassTelemetry telemetry) => SemanticPasses.Add(telemetry);

        public void AddAttemptBudget(ReasoningAttemptBudgetTelemetry telemetry) => AttemptBudgets.Add(telemetry);

        public ReasoningCompletionStats ToStats()
        {
            var stats = new ReasoningCompletionStats(
                new ReasoningCompletionRunStats(
                AttemptCount,
                SuccessfulCompletionCount,
                FailedCompletionCount,
                RangeSplitCount,
                RetryCount,
                SemanticPassCount,
                ConsolidationPassCount,
                OutOfScopeProposalCount,
                OwnershipViolationCount),
                FailureClasses.Distinct(StringComparer.Ordinal).ToArray())
            {
                SemanticPasses = SemanticPasses.ToArray(),
                AttemptBudgets = AttemptBudgets.ToArray(),
            };
            return stats;
        }
    }
}
