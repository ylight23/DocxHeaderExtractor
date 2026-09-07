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
                var request = new ReasoningModelRequest
                {
                    RequestId = ReasoningPrompt.BuildRequestId(source.DocumentId, segment.ContextSegmentId, ConfigurationSignature(route)),
                    DocumentId = source.DocumentId,
                    Route = route.ToString(),
                    SemanticPassId = semanticPassId,
                    ContextSegmentId = segment.ContextSegmentId,
                    SystemPrompt = ReasoningPrompt.System,
                    UserPrompt = ReasoningPrompt.BuildUser(segment, route == ReasoningRoute.ReasoningPreservingShadow),
                    SourceOccurrenceIds = segment.SourceOccurrenceIds,
                    OwnedSourceOccurrenceIds = segment.OwnedSourceOccurrenceIds,
                    ConfigurationSignature = ConfigurationSignature(route),
                };

                try
                {
                    var response = await _model.CompleteAsync(request, ct);
                    if (!response.Complete)
                        throw new InvalidDataException("reasoning-response-complete-marker-missing");
                    ValidateResponseOwnership(response, segment, occurrenceById);
                    responses.Add(response);
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

                    if ((ex.FailureClass is ReasoningCompletionFailureClass.TransportTruncation or
                        ReasoningCompletionFailureClass.Timeout) && transientRetries < _maxTransientRetries)
                    {
                        transientRetries++;
                        completion.RetryCount++;
                        continue;
                    }
                    throw WithAttemptHistory(ex);
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

    private static void ValidateResponseOwnership(
        ReasoningModelResponse response,
        ReasoningContextSegment segment,
        IReadOnlyDictionary<string, ReasoningSourceOccurrence> occurrenceById)
    {
        if (response.OwnedRange is { } range &&
            (range.Start != segment.OwnedStartOrdinal || range.End != segment.OwnedEndOrdinal))
            throw new InvalidDataException("reasoning-response-owned-range-mismatch");

        var visibleSourceIds = segment.SourceOccurrenceIds
            .Where(occurrenceById.ContainsKey)
            .Select(id => occurrenceById[id].SourceId)
            .ToHashSet(StringComparer.Ordinal);
        var ownedSourceIds = segment.OwnedSourceOccurrenceIds
            .Where(occurrenceById.ContainsKey)
            .Select(id => occurrenceById[id].SourceId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var proposal in response.Headings)
        {
            if (!visibleSourceIds.Contains(proposal.SourceId))
                throw new InvalidDataException("reasoning-response-source-not-visible");
            if (!ownedSourceIds.Contains(proposal.SourceId))
                throw new InvalidDataException("reasoning-response-outside-owned-range");
        }
    }

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
        public List<string> FailureClasses { get; } = [];

        public ReasoningCompletionStats ToStats() => new(
            new ReasoningCompletionRunStats(
                AttemptCount,
                SuccessfulCompletionCount,
                FailedCompletionCount,
                RangeSplitCount,
                RetryCount,
                SemanticPassCount,
                ConsolidationPassCount),
            FailureClasses.Distinct(StringComparer.Ordinal).ToArray());
    }
}
