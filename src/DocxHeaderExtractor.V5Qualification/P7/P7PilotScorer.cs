namespace DocxHeaderExtractor.V5Qualification.P7;

// A reviewed exit (or independently attested document end) closes a Gold unit.
// This is an annotation contract, not a geometry-derived boundary rule.
internal sealed record PilotGoldUnit(string Anchor, IReadOnlyList<ReviewedPart> Parts,
    string? ReviewedFirstOutside, bool DocumentEndAttested);
internal sealed record PilotPrediction(string Id, string Anchor, IReadOnlyList<ReviewedPart> Parts);
internal sealed record PilotBoundaryScore(string PredictionId, string Outcome, string Reason,
    IReadOnlyList<ReviewedPart> FullPrediction);
internal sealed record PilotScore(bool EvaluableMembership, IReadOnlyList<string> ReadinessGaps,
    int? MembershipTP, int? MembershipFP, int? MembershipFN, int Exact, int Wrong,
    int NotEvaluable, int ContractInvalid, int DuplicateAnchors, int OverlappingPairs,
    IReadOnlyList<PilotBoundaryScore> Boundaries);

/// <summary>
/// Provider-free qualification scorer. Membership is occurrence-level set membership, not
/// anchor existence. Boundary denominator is separately closed Gold units at true anchors.
/// Unknown crossing truth is never scored as exact/wrong; full predictions remain observable.
/// No pruning, clipping, implicit negatives, semantic adjudication or authority is performed.
/// </summary>
internal static class P7PilotScorer
{
    public const string Version = "P7_ADJUDICATION_PILOT_SCORER_V1";

    public static PilotScore Score(EvaluationScope scope, IReadOnlyList<ReviewOccurrence> atoms,
        IReadOnlyList<EvaluationAnnotation> annotations, IReadOnlyList<PilotGoldUnit> units,
        IReadOnlyList<PilotPrediction> predictions)
    {
        var gaps = P7EvaluationUniverse.ReadinessGaps(scope, scope.SourceSha256, scope.UniverseSha256,
            scope.SnapshotSha256, atoms.Select(a => a.Page).Concat(scope.Pages).Distinct().ToArray(), atoms, annotations).ToList();
        if (predictions.Select(p => p.Id).Distinct().Count() != predictions.Count) gaps.Add("DUPLICATE_PREDICTION_ID");
        if (units.Select(u => u.Anchor).Distinct().Count() != units.Count) gaps.Add("DUPLICATE_GOLD_ANCHOR");
        var byAlias = atoms.GroupBy(a => a.Alias).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var labels = annotations.GroupBy(a => a.Alias).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        bool Valid(IReadOnlyList<ReviewedPart> parts) => parts.Count > 0 &&
            parts.Select(p => p.Alias).Distinct().Count() == parts.Count && parts.All(p =>
                byAlias.TryGetValue(p.Alias, out var a) && p.Start >= 0 && p.Length > 0 && (long)p.Start + p.Length <= a.Length);
        var order = atoms.Select((a, i) => (a.Alias, i)).GroupBy(x => x.Alias).ToDictionary(g => g.Key, g => g.First().i);
        bool Ordered(IReadOnlyList<ReviewedPart> parts) => parts.Select(p => order[p.Alias]).SequenceEqual(parts.Select(p => order[p.Alias]).Order());
        foreach (var u in units)
        {
            if (!Valid(u.Parts) || u.Parts[0].Alias != u.Anchor || !Ordered(u.Parts))
            { gaps.Add("GOLD_UNIT_REFERENCE_INVALID"); continue; }
            if (u.Parts.Any(p => !labels.TryGetValue(p.Alias, out var a) || a.Status != "ADJUDICATED" ||
                a.HeadingMembership != true || a.ExtentParts is null || !a.ExtentParts.SequenceEqual(u.Parts)))
                gaps.Add("GOLD_UNIT_ANNOTATIONS_INCONSISTENT");
            if (u.DocumentEndAttested && (u.ReviewedFirstOutside is not null || order[u.Parts[^1].Alias] != atoms.Count - 1))
                gaps.Add("DOCUMENT_END_ATTESTATION_INVALID");
            if (u.ReviewedFirstOutside is not null &&
                (!labels.TryGetValue(u.ReviewedFirstOutside, out var outside) || outside.Status != "ADJUDICATED" ||
                 !order.TryGetValue(u.ReviewedFirstOutside, out var exitIndex) || exitIndex != order[u.Parts[^1].Alias] + 1 ||
                 outside.ExtentParts?.SequenceEqual(u.Parts) == true))
                gaps.Add("GOLD_EXIT_WITNESS_INVALID");
        }
        foreach (var a in annotations.Where(a => a.Status == "ADJUDICATED" && a.HeadingMembership == true))
            if (a.ExtentParts is null || units.Count(u => u.Parts.SequenceEqual(a.ExtentParts)) != 1)
                gaps.Add("GOLD_UNIT_COVERAGE_INCOMPLETE");
        var ready = gaps.Count == 0;
        var scores = new List<PilotBoundaryScore>();
        var validPredictions = new List<PilotPrediction>();
        foreach (var p in predictions)
        {
            if (!Valid(p.Parts) || p.Parts[0].Alias != p.Anchor || !Ordered(p.Parts))
            { scores.Add(new(p.Id, "CONTRACT_INVALID", "INVALID_OR_UNORDERED_SOURCE_PARTS", p.Parts)); continue; }
            validPredictions.Add(p);
            var observation = P7EvaluationUniverse.ObservePrediction(scope, atoms, p.Parts.Select(x => x.Alias).ToArray(), annotations);
            if (!ready || !observation.MayScoreExactBoundary)
            { scores.Add(new(p.Id, "NOT_EVALUABLE", ready ? observation.Classification : "UNIVERSE_OR_GOLD_NOT_READY", p.Parts)); continue; }
            var unit = units.SingleOrDefault(u => u.Anchor == p.Anchor);
            if (unit is null)
            {
                // Membership/anchor admission failure is not forced into an exact-extent denominator.
                scores.Add(new(p.Id, "NOT_EVALUABLE", labels[p.Anchor].HeadingMembership == false
                    ? "NON_HEADING_ANCHOR_MEMBERSHIP_DIAGNOSTIC" : "NO_CLOSED_GOLD_ANCHOR_UNIT", p.Parts)); continue;
            }
            if (unit.ReviewedFirstOutside is null && !unit.DocumentEndAttested)
            { scores.Add(new(p.Id, "NOT_EVALUABLE", "GOLD_UNIT_BOUNDARY_OPEN", p.Parts)); continue; }
            var exact = p.Parts.SequenceEqual(unit.Parts);
            var goldEnd = order[unit.Parts[^1].Alias]; var predEnd = order[p.Parts[^1].Alias];
            scores.Add(new(p.Id, exact ? "EXACT" : predEnd < goldEnd ? "UNDEREXTENT" : predEnd > goldEnd ? "OVEREXTENT" : "WRONG_PARTS",
                "CLOSED_REVIEWED_GOLD_UNIT", p.Parts));
        }
        // Invalid calls never create a favorable complete-membership score. Raw counts remain visible.
        var membershipReady = ready && scores.All(s => s.Outcome != "CONTRACT_INVALID");
        var goldMembers = annotations.Where(a => a.Status == "ADJUDICATED" && a.HeadingMembership == true).Select(a => a.Alias).ToHashSet();
        var predictedMembers = validPredictions.SelectMany(p => p.Parts).Where(p => scope.Pages.Contains(byAlias[p.Alias].Page)).Select(p => p.Alias).ToHashSet();
        var overlap = 0;
        for (var i = 0; i < validPredictions.Count; i++)
            for (var j = i + 1; j < validPredictions.Count; j++)
                if (validPredictions[i].Parts.Any(a => validPredictions[j].Parts.Any(b => a.Alias == b.Alias &&
                    a.Start < (long)b.Start + b.Length && b.Start < (long)a.Start + a.Length))) overlap++;
        return new(membershipReady, gaps.Distinct().Order().ToArray(),
            membershipReady ? predictedMembers.Count(goldMembers.Contains) : null,
            membershipReady ? predictedMembers.Count(a => !goldMembers.Contains(a)) : null,
            membershipReady ? goldMembers.Count(a => !predictedMembers.Contains(a)) : null,
            scores.Count(s => s.Outcome == "EXACT"), scores.Count(s => s.Outcome is "UNDEREXTENT" or "OVEREXTENT" or "WRONG_PARTS"),
            scores.Count(s => s.Outcome == "NOT_EVALUABLE"), scores.Count(s => s.Outcome == "CONTRACT_INVALID"),
            validPredictions.GroupBy(p => p.Anchor).Sum(g => Math.Max(0, g.Count() - 1)), overlap, scores);
    }
}
