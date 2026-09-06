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

    public ReasoningPreservingHeadingHarness(
        IReasoningSemanticModel model,
        int maxContextCharacters = 80_000,
        int windowCharacters = 48_000)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _maxContextCharacters = maxContextCharacters;
        _windowCharacters = windowCharacters;
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
        foreach (var segment in pack.Segments)
        {
            var request = new ReasoningModelRequest
            {
                RequestId = ReasoningPrompt.BuildRequestId(source.DocumentId, segment.ContextSegmentId, ConfigurationSignature(route)),
                DocumentId = source.DocumentId,
                ContextSegmentId = segment.ContextSegmentId,
                SystemPrompt = ReasoningPrompt.System,
                UserPrompt = ReasoningPrompt.BuildUser(segment, route == ReasoningRoute.ReasoningPreservingShadow),
                SourceOccurrenceIds = segment.SourceOccurrenceIds,
                ConfigurationSignature = ConfigurationSignature(route),
            };
            var response = await _model.CompleteAsync(request, ct);
            allProposals.AddRange(response.Headings);
        }

        // Hierarchical mode gets one explicit consolidation pass. It sees every source identity
        // and the window proposals, but it never sees Gold or a candidate-filtered source subset.
        if (pack.Segments.Count > 1)
        {
            var consolidation = BuildConsolidationSegment(pack, allProposals);
            var request = new ReasoningModelRequest
            {
                RequestId = ReasoningPrompt.BuildRequestId(source.DocumentId, "global-consolidation", ConfigurationSignature(route)),
                DocumentId = source.DocumentId,
                ContextSegmentId = "global-consolidation",
                SystemPrompt = ReasoningPrompt.System,
                UserPrompt = ReasoningPrompt.BuildUser(consolidation, route == ReasoningRoute.ReasoningPreservingShadow),
                SourceOccurrenceIds = pack.Occurrences.Select(o => o.SourceOccurrenceId).ToArray(),
                ConfigurationSignature = ConfigurationSignature(route),
            };
            var response = await _model.CompleteAsync(request, ct);
            if (response.Headings.Count > 0)
                allProposals = [.. response.Headings];
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
        return new ReasoningRouteObservation
        {
            Route = route,
            ContextVisible = pack.Occurrences.Select(o => o.SourceOccurrenceId).ToHashSet(StringComparer.Ordinal),
            Proposed = proposed,
            Validated = materialized.Validated,
            FinalIncluded = acceptedIds,
            ProviderCalls = _model.ProviderCalls,
        };
    }

    public static string ConfigurationSignature(ReasoningRoute route) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{ReasoningPrompt.Version}|qwen-compatible|temperature=0|reasoning=none|route={route}")))
            .ToLowerInvariant();

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
            Text = sb.ToString(),
        };
    }
}
