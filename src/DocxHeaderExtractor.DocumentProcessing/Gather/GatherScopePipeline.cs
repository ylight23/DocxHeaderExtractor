namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>Selection, review and provenance for one destination scope. Unproposed units remain visible for recall audits.</summary>
public sealed record GatherScopeResult(GatherRun Selection, GatherQuoteRepairResult Repair, GatherBatchedReviewResult Review,
    GatherSection ReviewedSection, IReadOnlyList<GatherExcluded> Excluded, IReadOnlyList<string> UnproposedUnitIds,
    GatherBatchedReviewResult CoverageReview, IReadOnlyList<string> UnresolvedUnitIds)
{
    /// <summary>
    /// Indices of quote refusals (in <see cref="Repair"/>.Remaining) whose complete source units are already present in reviewed facts. The
    /// original quote failures stay in <see cref="Repair"/> for audit, but are no longer reported as missing content.
    /// </summary>
    public IReadOnlyList<int> ResolvedRefusalIndices { get; init; } = [];

    /// <summary>Processing and unit-accounting status only; partial quotes do not prove semantic recall.</summary>
    public bool Complete => ReviewedSection.Run.Complete && Review.Failures.Count == 0 && CoverageReview.Failures.Count == 0 &&
        Review.Review.Verdicts.All(v => v.Decision != "UNSURE") &&
        CoverageReview.Review.Verdicts.All(v => v.Decision != "UNSURE") && UnresolvedUnitIds.Count == 0 &&
        ReviewedSection.Run.Facts.All(f => !f.Flags.Contains("SCOPE_UNREVIEWED", StringComparer.Ordinal));
}

/// <summary>Qwen proposal with exact source quotes, deterministic binding and a separate bounded semantic review.</summary>
public static class GatherScopePipeline
{
    public static async Task<GatherScopeResult> RunAsync(string goal, string heading, string targetLocation, string? nextHeadingLocation,
        IReadOnlyList<GatherSource> sources, IGatherModel proposer, IGatherModel reviewer,
        GatherCheckpointStore? checkpoints = null, int reviewBatchSize = 10, int reviewConcurrency = 4, CancellationToken ct = default)
    {
        var selection = await GatherRunner.RunAsync(goal, sources, proposer, ct, checkpoints).ConfigureAwait(false);
        var repair = await GatherQuoteRepair.RepairAsync(goal, selection.Refused, sources, proposer, ct).ConfigureAwait(false);
        if (repair.Remaining.Count > 0 && repair.Decisions.Count > 0)
        {
            // Retry protocol/binding failures once; a meaningful UNSURE is a decision, not a format error to sample away.
            var previous = repair.Decisions.Where(d => d.Status == "UNRESOLVED" &&
                !d.Reason.StartsWith("MODEL_UNSURE:", StringComparison.Ordinal) && !d.Reason.StartsWith("REPAIR_NONRETRYABLE:", StringComparison.Ordinal))
                .ToDictionary(d => d.OriginalIndex);
            var retryable = repair.Remaining.Where(r => previous.ContainsKey(r.Index)).ToArray();
            if (retryable.Length > 0)
            {
                var retry = await GatherQuoteRepair.RepairAsync(goal, retryable, sources, proposer, ct, previous, round: 2).ConfigureAwait(false);
                var retried = retryable.Select(r => r.Index).ToHashSet();
                var remaining = repair.Remaining.Where(r => !retried.Contains(r.Index)).Concat(retry.Remaining).OrderBy(r => r.Index).ToArray();
                repair = new GatherQuoteRepairResult(repair.Decisions.Concat(retry.Decisions).ToArray(), remaining);
            }
        }
        var facts = selection.Facts.ToList();
        var duplicates = selection.Duplicates.ToList();
        static string TextKey(GatheredFact fact) => string.Join("␟", fact.Parts.Select(p => p.Text));
        foreach (var decision in repair.Decisions.Where(d => d.Fact is not null))
        {
            var repaired = decision.Fact!;
            var existing = facts.FindIndex(f => TextKey(f) == TextKey(repaired));
            if (existing >= 0) duplicates.Add(new FactDuplicate(existing, decision.OriginalIndex, repaired.Parts));
            else facts.Add(repaired);
        }
        var working = selection with { Facts = facts, Duplicates = duplicates, Refused = repair.Remaining };
        var guarded = working with { Facts = working.Facts.Select(f => f with { Flags = [.. f.Flags, "SCOPE_UNREVIEWED"] }).ToArray() };
        var review = await GatherBatchedReview.ReviewAsync(goal, guarded, sources, reviewer, reviewBatchSize, reviewConcurrency, ct).ConfigureAwait(false);
        var section = new GatherSection(targetLocation, heading, goal, guarded, GatherDocument.Conflicts(guarded.Facts), nextHeadingLocation);
        var (applied, excluded) = GatherReviewApply.Apply(section, review.Review);
        // Duplicate proposals still carry source evidence and must not be reported as unproposed.
        var proposedUnits = working.Facts.SelectMany(f => f.Parts).Concat(working.Duplicates.SelectMany(d => d.Parts))
            .Select(p => p.UnitId).ToHashSet(StringComparer.Ordinal);
        var unproposedUnits = sources.SelectMany(s => s.Units).Where(u => !proposedUnits.Contains(u.Id)).ToArray();
        var coverageFacts = unproposedUnits.Select(u =>
        {
            var text = u.Text.Trim();
            var start = u.Text.IndexOf(text, StringComparison.Ordinal);
            return new GatheredFact([new SourcedPart(u.Id, u.SourceId, u.Location, "text", text, start, start + text.Length)], ["SCOPE_UNREVIEWED"]);
        }).ToArray();
        var coverageRun = new GatherRun(coverageFacts, [], [], [], [], [], 1);
        var coverage = await GatherBatchedReview.ReviewAsync(goal, coverageRun, sources, reviewer, reviewBatchSize, reviewConcurrency, ct).ConfigureAwait(false);
        var coverageSection = new GatherSection(targetLocation, heading, goal, coverageRun, [], nextHeadingLocation);
        var (recovered, coverageExcluded) = GatherReviewApply.Apply(coverageSection, coverage.Review);
        var combinedFacts = applied.Run.Facts.Concat(recovered.Run.Facts).ToArray();
        var sourceById = sources.SelectMany(s => s.Units).ToDictionary(u => u.Id, StringComparer.Ordinal);
        var completeReviewedUnitIds = combinedFacts.Where(f => !f.Flags.Contains("SCOPE_UNREVIEWED", StringComparer.Ordinal))
            .SelectMany(f => f.Parts)
            .Where(p => sourceById.TryGetValue(p.UnitId, out var unit) && p.Text == unit.Text.Trim() &&
                p.Start == unit.Text.IndexOf(p.Text, StringComparison.Ordinal) && p.End == p.Start + p.Text.Length)
            .Select(p => p.UnitId).ToHashSet(StringComparer.Ordinal);
        var resolvedRefusals = working.Refused.Where(r => r.Proposal is { Parts.Count: > 0 } &&
            r.Proposal.Parts.All(p => completeReviewedUnitIds.Contains(p.UnitId))).Select(r => r.Index).ToHashSet();
        var finalRefused = applied.Run.Refused.Where(r => !resolvedRefusals.Contains(r.Index)).ToArray();
        var combined = applied with { Run = applied.Run with { Facts = combinedFacts, Refused = finalRefused }, Conflicts = GatherDocument.Conflicts(combinedFacts) };
        var unresolved = coverage.Review.Verdicts.Where(v => v.Decision == "UNSURE").Select(v => unproposedUnits[v.Index].Id).ToArray();
        return new GatherScopeResult(selection, repair, review, combined, excluded.Concat(coverageExcluded).ToArray(),
            unproposedUnits.Select(u => u.Id).ToArray(), coverage, unresolved)
        { ResolvedRefusalIndices = resolvedRefusals.Order().ToArray() };
    }
}
