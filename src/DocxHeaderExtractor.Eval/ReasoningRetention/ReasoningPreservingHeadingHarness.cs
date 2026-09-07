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

    public ReasoningPreservingHeadingHarness(
        IReasoningSemanticModel model,
        int maxContextCharacters = 80_000,
        int windowCharacters = 48_000,
        int maxTransientRetries = 2)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _maxContextCharacters = maxContextCharacters;
        _windowCharacters = windowCharacters;
        _maxTransientRetries = Math.Max(0, maxTransientRetries);
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

        while (pending.Count > 0)
        {
            var segment = pending.Dequeue();
            var transientRetries = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                completion.AttemptCount++;
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

                try
                {
                    var response = await _model.CompleteAsync(request, ct);
                    var attemptTelemetry = (_model as IReasoningCompletionTelemetrySource)?.CompletionTelemetry.LastOrDefault();
                    if (attemptTelemetry is not null)
                    {
                        attemptTelemetry.AttemptId = request.AttemptId;
                        attemptTelemetry.AttemptNumber = attemptNumber;
                        attemptTelemetry.VisibleSourceIds = visibleOccurrences
                            .Select(item => item.SourceId)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
                        attemptTelemetry.OwnedSourceIds = ownedSourceIdsForTelemetry
                            .Order(StringComparer.Ordinal)
                            .ToArray();
                    }
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

                    if (IsTransientProviderFailure(ex.FailureClass) && transientRetries < _maxTransientRetries)
                    {
                        transientRetries++;
                        completion.RetryCount++;
                        continue;
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
                    throw WithAttemptHistory(new ReasoningCompletionException(
                        ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid,
                        ex.Message,
                        telemetry,
                        ex));
                }
            }
        }

        return responses;
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

        public ReasoningCompletionStats ToStats() => new(
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
            FailureClasses.Distinct(StringComparer.Ordinal).ToArray());
    }
}
