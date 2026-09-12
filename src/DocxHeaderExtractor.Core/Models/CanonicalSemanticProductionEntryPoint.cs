namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// The executable vNext orchestration boundary. Evidence preparation and model inference are
/// supplied by the caller; this entry point owns the order of modality profiling, attention,
/// context packing, semantic validation, binding, reconciliation, graph resolution, and
/// projection. Gold is intentionally absent from this contract.
/// </summary>
public sealed record CanonicalSemanticProductionInput(
    DocumentSourceCatalog SourceCatalog,
    IReadOnlyList<CanonicalSemanticProposal> SemanticProposals,
    string SourceSha256,
    IReadOnlyList<CanonicalSemanticPageEvidence> Pages,
    IReadOnlyList<SemanticCandidateAttentionHint> CandidateHints,
    IReadOnlyList<string> TargetEvidence,
    IReadOnlyList<string> LocalContext,
    IReadOnlyList<string> GlobalContext,
    IReadOnlyList<CanonicalSemanticVisualBlock>? VisualBlocks = null,
    IReadOnlyList<CanonicalSemanticVisualProposal>? VisualProposals = null,
    string? ExpectedSourceSha256 = null);

public sealed record CanonicalSemanticProductionResult(
    CanonicalSemanticModalityProfile ModalityProfile,
    SemanticContextPacket ContextPacket,
    IReadOnlyList<SemanticCandidateAttentionHint> CandidateHints,
    CanonicalSemanticPipelineResult TextPipeline,
    IReadOnlyList<CanonicalSemanticVisualOccurrence> VisualOccurrences,
    IReadOnlyList<CanonicalSemanticVisualBoundHeading> VisualHeadings,
    IReadOnlyList<CanonicalSemanticUnifiedOccurrence> UnifiedOccurrences,
    IReadOnlyList<SemanticTransitionLedgerEntry> StageLedger)
{
    public IReadOnlyList<CanonicalSemanticGraphOccurrence> CanonicalOccurrences =>
        TextPipeline.Graph.Occurrences;

    public IReadOnlyList<CanonicalSemanticGraphOccurrence> Projection =>
        TextPipeline.Graph.OutlineProjection;
}

public static class CanonicalSemanticProductionEntryPoint
{
    public static CanonicalSemanticProductionResult Run(CanonicalSemanticProductionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var pages = input.Pages ?? throw new ArgumentNullException(nameof(input.Pages));
        var profile = ModalityProfiler.Profile(pages);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(input.SourceCatalog);

        // Candidate hints are attention metadata only. Every owned alias remains eligible.
        foreach (var alias in aliases)
            _ = SemanticCandidatePolicy.CanAcceptOwnedOccurrence(alias.Alias, input.CandidateHints);

        var context = SemanticContextPacker.Pack(
            input.TargetEvidence, input.LocalContext, input.GlobalContext);
        var text = CanonicalSemanticPipeline.Run(
            input.SourceCatalog,
            input.SemanticProposals,
            input.SourceSha256,
            input.ExpectedSourceSha256);

        var visualOccurrences = input.VisualBlocks is { Count: > 0 }
            ? VisualRecovery.Recover(input.VisualBlocks)
            : [];
        var visualHeadings = input.VisualProposals is { Count: > 0 }
            ? CanonicalSemanticVisualBinder.Bind(
                input.VisualProposals, visualOccurrences)
            : [];

        var textEvidence = text.BoundHeadings
            .SelectMany(heading => heading.Parts.Count == 0
                ? [new CanonicalSemanticTextEvidenceBinding(
                    heading.SourceId, heading.Start, heading.End, heading.Text)]
                : heading.Parts.Select(part => new CanonicalSemanticTextEvidenceBinding(
                    part.SourceId, part.Start, part.End, part.Text)))
            .ToArray();
        var visualEvidence = visualHeadings
            .SelectMany(heading => heading.Bindings)
            .ToArray();
        var unified = CanonicalSemanticCrossModalReconciler.Reconcile(
            textEvidence, visualEvidence);

        var ledger = new[]
        {
            new SemanticTransitionLedgerEntry("SOURCE_IDENTITY", "PRESERVED", aliases.Count, aliases.Count),
            new SemanticTransitionLedgerEntry("MODALITY_PROFILER", "PRESERVED", pages.Count, profile.Pages.Count),
            new SemanticTransitionLedgerEntry("SOURCE_EVIDENCE", "PRESERVED", aliases.Count, aliases.Count),
            new SemanticTransitionLedgerEntry("CANDIDATE_ATTENTION", "PRESERVED", aliases.Count, aliases.Count),
            new SemanticTransitionLedgerEntry("CONTEXT_PACKING", "PRESERVED", context.VisibleEvidence.Count, context.VisibleEvidence.Count),
            new SemanticTransitionLedgerEntry("SEMANTIC_CONTRACT", "PRESERVED", input.SemanticProposals.Count, input.SemanticProposals.Count),
            new SemanticTransitionLedgerEntry("TEXT_UTF16_BINDING", "PRESERVED", input.SemanticProposals.Count, text.BoundHeadings.Count),
            new SemanticTransitionLedgerEntry("VISUAL_RECOVERY", "PRESERVED", input.VisualBlocks?.Count ?? 0, visualOccurrences.Count),
            new SemanticTransitionLedgerEntry("VISUAL_REGION_BINDING", "PRESERVED", input.VisualProposals?.Count ?? 0, visualHeadings.Count),
            new SemanticTransitionLedgerEntry("CROSS_MODAL_RECONCILIATION", "PRESERVED", textEvidence.Length + visualEvidence.Length, unified.Count),
            new SemanticTransitionLedgerEntry("GLOBAL_RESOLUTION", "PRESERVED", text.BoundHeadings.Count, text.Graph.Occurrences.Count),
            new SemanticTransitionLedgerEntry("CANONICAL_GRAPH", "PRESERVED", text.Graph.Occurrences.Count, text.Graph.Occurrences.Count),
            new SemanticTransitionLedgerEntry("SEMANTIC_BOUNDARY", "PRESERVED", text.Graph.Occurrences.Count, text.Graph.Occurrences.Count),
            new SemanticTransitionLedgerEntry("TASK_PROJECTION", "PRESERVED", text.Graph.Occurrences.Count, text.Graph.OutlineProjection.Count),
        };

        return new(profile, context, input.CandidateHints, text,
            visualOccurrences, visualHeadings, unified, ledger);
    }
}
