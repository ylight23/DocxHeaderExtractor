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
            allProposals.AddRange(responses.SelectMany(item => item.Headings));
        }

        // Hierarchical mode gets one explicit consolidation pass. It sees every source identity
        // and the window proposals, but it never sees Gold or a candidate-filtered source subset.
        if (pack.Segments.Count > 1)
        {
            var consolidation = BuildConsolidationSegment(pack, allProposals);
            var responses = await CompleteWithRecoveryAsync(
                source,
                pack,
                consolidation,
                route,
                "global-structural-consolidation-v1",
                completion,
                ct);
            var consolidated = responses.SelectMany(item => item.Headings).ToArray();
            if (consolidated.Length > 0)
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

    private async Task<IReadOnlyList<ReasoningModelResponse>> CompleteWithRecoveryAsync(
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
        var responses = new List<ReasoningModelResponse>();
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
                var ownedSourceIdsForTelemetry = segment.OwnedSourceOccurrenceIds
                    .Where(occurrenceById.ContainsKey)
                    .Select(id => occurrenceById[id].SourceId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var sourceIdentityMap = visibleOccurrences.Select(item => new ReasoningSourceIdentity
                {
                    CanonicalSourceId = item.SourceId,
                    SourceOccurrenceId = item.SourceOccurrenceId,
                    SourceOrdinal = item.SourceOrdinal,
                    RawTextLength = item.RawText.Length,
                }).ToArray();
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
                        requestId,
                        semanticPassId,
                        $"{requestId}:attempt-{attemptNumber}",
                        sourceIdentityMap),
                    SourceOccurrenceIds = segment.SourceOccurrenceIds,
                    OwnedSourceOccurrenceIds = segment.OwnedSourceOccurrenceIds,
                    OwnedStartOrdinal = segment.OwnedStartOrdinal,
                    OwnedEndOrdinal = segment.OwnedEndOrdinal,
                    AttemptId = $"{requestId}:attempt-{attemptNumber}",
                    SourceIdentityMap = sourceIdentityMap,
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
                    if (!response.Complete)
                        throw new InvalidDataException("reasoning-response-complete-marker-missing");
                    var scopedResponse = ValidateResponseOwnership(response, request, segment, occurrenceById, completion);
                    responses.Add(scopedResponse);
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
                        if (segment.OwnedSourceOccurrenceIds.Count <= 1)
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
                catch (Exception ex) when (ex is InvalidDataException or ReasoningResponseIdentityException)
                {
                    completion.FailedCompletionCount++;
                    completion.FailureClasses.Add(ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid);
                    var telemetry = (_model as IReasoningCompletionTelemetrySource)?.CompletionTelemetry.LastOrDefault()
                        ?? new ReasoningCompletionTelemetry { FailureClass = ReasoningCompletionFailureClass.CompleteResponseSchemaInvalid };
                    telemetry.ValidationReason = ex.Message;
                    if (ex is ReasoningResponseIdentityException identity)
                    {
                        telemetry.ReturnedSourceIds = identity.ReturnedSourceIds;
                        telemetry.VisibleSourceIds = identity.VisibleSourceIds;
                        telemetry.OwnedSourceIds = identity.OwnedSourceIds;
                    }
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

    private static ReasoningModelResponse ValidateResponseOwnership(
        ReasoningModelResponse response,
        ReasoningModelRequest request,
        ReasoningContextSegment segment,
        IReadOnlyDictionary<string, ReasoningSourceOccurrence> occurrenceById,
        CompletionAccumulator completion)
    {
        if (response.RequestId is not null && response.RequestId != request.RequestId)
            throw new InvalidDataException($"reasoning-response-request-id-mismatch; expected={request.RequestId}; actual={response.RequestId}");
        if (response.SemanticPassId is not null && response.SemanticPassId != request.SemanticPassId)
            throw new InvalidDataException($"reasoning-response-semantic-pass-id-mismatch; expected={request.SemanticPassId}; actual={response.SemanticPassId}");
        if (response.AttemptId is not null && response.AttemptId != request.AttemptId)
            throw new InvalidDataException($"reasoning-response-attempt-id-mismatch; expected={request.AttemptId}; actual={response.AttemptId}");
        if (response.OwnedRange is { } range &&
            (range.Start != segment.OwnedStartOrdinal || range.End != segment.OwnedEndOrdinal))
            throw new InvalidDataException("reasoning-response-owned-range-mismatch");

        var visibleOccurrences = segment.SourceOccurrenceIds
            .Where(occurrenceById.ContainsKey)
            .Select(id => occurrenceById[id])
            .ToArray();
        var ownedSourceIds = segment.OwnedSourceOccurrenceIds
            .Where(occurrenceById.ContainsKey)
            .Select(id => occurrenceById[id].SourceId)
            .ToHashSet(StringComparer.Ordinal);
        var scoped = new List<ReasoningHeadingProposal>(response.Headings.Count);
        var emittedOccurrences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var proposal in response.Headings)
        {
            var identity = ReasoningSourceIdentityResolver.Resolve(proposal.SourceId, visibleOccurrences, ownedSourceIds);
            if (!identity.IsVisible || identity.CanonicalSourceId is null)
                throw new ReasoningResponseIdentityException(
                    $"reasoning-response-source-not-visible; returnedSourceId={proposal.SourceId}",
                    proposal.SourceId,
                    visibleOccurrences.Select(item => item.SourceId).ToArray(),
                    ownedSourceIds.Order(StringComparer.Ordinal).ToArray());

            var source = visibleOccurrences.First(item => item.SourceId == identity.CanonicalSourceId);
            if (!proposal.HeadingSpan.IsValidFor(source.RawText))
                throw new InvalidDataException(
                    $"reasoning-response-span-invalid-for-visible-source; sourceId={source.SourceId}; " +
                    $"start={proposal.HeadingSpan.Start}; end={proposal.HeadingSpan.End}; rawTextLength={source.RawText.Length}");

            var normalized = proposal with { SourceId = identity.CanonicalSourceId };
            var occurrenceKey = $"{normalized.SourceId}:{normalized.HeadingSpan.Start}:{normalized.HeadingSpan.End}";
            if (!emittedOccurrences.Add(occurrenceKey))
                throw new InvalidDataException($"reasoning-response-duplicate-occurrence; occurrence={occurrenceKey}");

            if (!ownedSourceIds.Contains(normalized.SourceId))
            {
                completion.OutOfScopeProposalCount++;
                completion.OwnershipViolationCount++;
                continue;
            }
            scoped.Add(normalized);
        }
        return response with { Headings = scoped };
    }

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
        var owned = segment.OwnedSourceOccurrenceIds.ToArray();
        var midpoint = owned.Length / 2;
        var leftIds = owned[..midpoint];
        var rightIds = owned[midpoint..];
        return (
            WithOwnership(segment, occurrences, leftIds, $"-split-{leftIds[0]}-{leftIds[^1]}"),
            WithOwnership(segment, occurrences, rightIds, $"-split-{rightIds[0]}-{rightIds[^1]}"));
    }

    private static ReasoningContextSegment WithOwnership(
        ReasoningContextSegment segment,
        IReadOnlyList<ReasoningSourceOccurrence> occurrences,
        IReadOnlyList<string> ownedIds,
        string suffix)
    {
        var owned = ownedIds.Select(id => occurrences.First(item => item.SourceOccurrenceId == id)).ToArray();
        return segment with
        {
            ContextSegmentId = segment.ContextSegmentId + suffix,
            OwnedSourceOccurrenceIds = ownedIds,
            OwnedStartOrdinal = owned[0].SourceOrdinal,
            OwnedEndOrdinal = owned[^1].SourceOrdinal,
        };
    }

    private static ReasoningContextSegment BuildConsolidationSegment(
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
        return new ReasoningContextSegment
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
