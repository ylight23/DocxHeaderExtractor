using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Observable outcome of the generic heading pipeline.</summary>
public sealed record HeadingPipelineResult(
    IReadOnlyList<ValidatedHeading> Headings,
    IReadOnlyList<HeadingPipelineDiagnostic> Diagnostics);

/// <summary>Diagnostic state for proposals and source facts; it is never structural authority.</summary>
public sealed record HeadingPipelineDiagnostic(
    string SourceId,
    string Status,
    string? Reason,
    string Provenance);

/// <summary>
/// Deterministic bridge from parser facts and untrusted model proposals to validated headings.
/// Source order, source spans, and marker hierarchy remain parser-owned.
/// </summary>
public static class HeadingPipeline
{
    public static HeadingPipelineResult Evaluate(
        IEnumerable<SourceFacts> sourceFacts,
        IEnumerable<ModelProposal> modelProposals,
        HeadingPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFacts);
        ArgumentNullException.ThrowIfNull(modelProposals);
        policy ??= new HeadingPolicy();

        var sources = sourceFacts
            .OrderBy(facts => facts.Source.ParagraphIndex ?? int.MaxValue)
            .ThenBy(facts => facts.SourceId, StringComparer.Ordinal)
            .ToArray();
        ValidateSourceInventory(sources);
        var sourceById = sources.ToDictionary(facts => facts.SourceId, StringComparer.Ordinal);

        var diagnostics = new List<HeadingPipelineDiagnostic>();
        var selected = SelectProposals(modelProposals, sourceById, policy, diagnostics);
        var validations = new List<ProposalValidationResult>(selected.Count);

        foreach (var proposal in selected)
        {
            var source = !string.IsNullOrWhiteSpace(proposal.SourceId) &&
                         sourceById.TryGetValue(proposal.SourceId, out var found)
                ? found
                : null;
            var validation = ModelProposalValidator.Validate(source, proposal, policy);
            validations.Add(validation);
            if (!validation.Accepted)
            {
                diagnostics.Add(new HeadingPipelineDiagnostic(
                    proposal.SourceId, "rejected", validation.RejectionReason, "model-proposal-validator"));
            }
        }

        var headings = MarkerHierarchyResolver.Resolve(validations, policy)
            .Select(heading =>
            {
                var proposal = selected.Single(item => item.SourceId == heading.SourceId);
                var source = sourceById[heading.SourceId];
                return heading with
                {
                    Confidence = proposal.ModelScore,
                    SourceEvidence = source.ObservedEvidence,
                    SemanticEvidence = proposal.SemanticEvidence,
                    VisualEvidence = proposal.VisualEvidence,
                    Status = "validated",
                    Provenance = "source-facts-validator-marker-hierarchy",
                };
            })
            .ToArray();

        var headingIds = headings.Select(heading => heading.SourceId).ToHashSet(StringComparer.Ordinal);
        foreach (var source in sources.Where(source => !headingIds.Contains(source.SourceId)))
        {
            if (!diagnostics.Any(item => item.SourceId == source.SourceId))
                diagnostics.Add(new HeadingPipelineDiagnostic(
                    source.SourceId, "not-proposed", "missing-model-proposal", "heading-pipeline"));
        }

        return new HeadingPipelineResult(headings, diagnostics
            .OrderBy(item => SourceOrdinal(sourceById, item.SourceId))
            .ThenBy(item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(item => item.Status, StringComparer.Ordinal)
            .ToArray());
    }

    private static IReadOnlyList<ModelProposal> SelectProposals(
        IEnumerable<ModelProposal> proposals,
        IReadOnlyDictionary<string, SourceFacts> sourceById,
        HeadingPolicy policy,
        ICollection<HeadingPipelineDiagnostic> diagnostics)
    {
        var materialized = proposals.ToArray();
        var selected = new List<ModelProposal>();
        foreach (var proposal in materialized.Where(proposal => !policy.Includes(proposal.Role)))
        {
            diagnostics.Add(new HeadingPipelineDiagnostic(
                proposal.SourceId, "ignored", "role-excluded-by-policy", "heading-policy"));
        }

        foreach (var group in materialized
                     .Where(proposal => policy.Includes(proposal.Role))
                     .GroupBy(proposal => proposal.SourceId, StringComparer.Ordinal))
        {
            var ordered = group
                .Select(proposal =>
                {
                    var source = !string.IsNullOrWhiteSpace(proposal.SourceId) &&
                                 sourceById.TryGetValue(proposal.SourceId, out var found)
                        ? found
                        : null;
                    return (Proposal: proposal,
                        Validation: ModelProposalValidator.Validate(source, proposal, policy));
                })
                .OrderByDescending(item => item.Validation.Accepted)
                .ThenByDescending(item => item.Proposal.ModelScore ?? double.MinValue)
                .ThenBy(item => item.Proposal.HeadingSpan?.Start ?? int.MaxValue)
                .ThenBy(item => item.Proposal.HeadingSpan?.End ?? int.MaxValue)
                .ThenBy(item => item.Proposal.Role)
                .ThenBy(item => ProposalSignature(item.Proposal), StringComparer.Ordinal)
                .ToArray();
            var winner = ordered[0].Proposal;
            selected.Add(winner);
            foreach (var discarded in ordered.Skip(1))
            {
                diagnostics.Add(new HeadingPipelineDiagnostic(
                    discarded.Proposal.SourceId, "discarded",
                    discarded.Validation.Accepted ? "duplicate-source-proposal" :
                        discarded.Validation.RejectionReason ?? "duplicate-source-proposal",
                    "deterministic-candidate-selection"));
            }
        }

        return selected
            .OrderBy(proposal => SourceOrdinal(sourceById, proposal.SourceId))
            .ThenBy(proposal => proposal.SourceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static int SourceOrdinal(IReadOnlyDictionary<string, SourceFacts> sourceById, string? sourceId) =>
        !string.IsNullOrWhiteSpace(sourceId) && sourceById.TryGetValue(sourceId, out var source)
            ? source.Source.ParagraphIndex ?? int.MaxValue
            : int.MaxValue;

    private static string ProposalSignature(ModelProposal proposal) => string.Join(
        "|",
        proposal.SourceId,
        proposal.Role,
        proposal.HeadingSpan?.Start,
        proposal.HeadingSpan?.End,
        proposal.ProposedLevel,
        proposal.ProposedParentId,
        string.Join(",", proposal.SemanticEvidence.OrderBy(item => item)),
        string.Join(",", proposal.VisualEvidence.OrderBy(item => item)));

    private static void ValidateSourceInventory(IReadOnlyList<SourceFacts> sources)
    {
        if (sources.Any(facts => string.IsNullOrWhiteSpace(facts.SourceId)))
            throw new InvalidOperationException("heading-source-id-missing");
        if (sources.Select(facts => facts.SourceId).Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw new InvalidOperationException("heading-duplicate-source-id");
        if (sources.Any(facts => !facts.RawSpan.IsValidFor(facts.RawText)))
            throw new InvalidOperationException("heading-source-span-invalid");
        if (sources.Any(facts => !IsValidBoundaryMap(facts)))
            throw new InvalidOperationException("heading-source-boundary-map-invalid");
    }

    private static bool IsValidBoundaryMap(SourceFacts facts)
    {
        if (facts.ParserBoundaries.Count == 0) return true;
        if (facts.ParserBoundaries[0] != 0 || facts.ParserBoundaries[^1] != facts.RawText.Length)
            return false;
        return facts.ParserBoundaries.Zip(facts.ParserBoundaries.Skip(1))
            .All(pair => pair.First < pair.Second && pair.Second <= facts.RawText.Length);
    }
}
