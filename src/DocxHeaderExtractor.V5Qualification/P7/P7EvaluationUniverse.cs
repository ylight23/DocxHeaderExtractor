namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record EvaluationAnnotation(string Alias, string Status, string? SemanticFunction,
    bool? HeadingMembership, IReadOnlyList<ReviewedPart>? ExtentParts);
internal sealed record EvaluationScope(string Version, string SourceSha256, string UniverseSha256,
    string SnapshotSha256, IReadOnlyList<int> Pages, string BoundaryPolicy);
internal sealed record PredictionScopeObservation(string Classification, bool RetainDiagnostic,
    bool MayScoreExactBoundary, IReadOnlyList<string> TouchingScope, IReadOnlyList<string> OutsideScope);

/// <summary>
/// Qualification-only page-scope gate. Evaluation scope is source-derived, never predicted.
/// Membership, function and extent stay independent annotation fields. No missing row is OTHER.
/// Does not decide semantic truth or turn a physical fact into a heading decision.
/// </summary>
internal static class P7EvaluationUniverse
{
    public const string Version = "P7_PAGE_EVALUATION_UNIVERSE_V2";
    public const string CrossingPolicy = "RETAIN_FULL_PREDICTION_NO_TRIMMING_NO_SILENT_EXCLUSION";
    public static IReadOnlyList<string> ReadinessGaps(EvaluationScope scope, string source, string universe,
        string snapshot, IReadOnlyList<int> pdfPages, IReadOnlyList<ReviewOccurrence> sourceAtoms,
        IReadOnlyList<EvaluationAnnotation> annotations)
    {
        var gaps = new SortedSet<string>(StringComparer.Ordinal);
        if (scope.Version != Version || scope.SourceSha256 != source || scope.UniverseSha256 != universe ||
            scope.SnapshotSha256 != snapshot) gaps.Add("EVALUATION_SNAPSHOT_MISMATCH");
        if (scope.Pages.Count == 0 || scope.Pages.Distinct().Count() != scope.Pages.Count || scope.Pages.Any(p => !pdfPages.Contains(p)))
            gaps.Add("EVALUATION_PAGE_SCOPE_INVALID");
        if (scope.BoundaryPolicy != CrossingPolicy) gaps.Add("CROSS_SCOPE_POLICY_UNSAFE");
        if (sourceAtoms.Select(a => a.Alias).Distinct().Count() != sourceAtoms.Count ||
            annotations.Select(a => a.Alias).Distinct().Count() != annotations.Count ||
            !annotations.Select(a => a.Alias).Order(StringComparer.Ordinal).SequenceEqual(sourceAtoms.Select(a => a.Alias).Order(StringComparer.Ordinal)))
            gaps.Add("EXPLICIT_FULL_SOURCE_PARTITION_REQUIRED");
        foreach (var a in annotations)
        {
            var matches = sourceAtoms.Where(x => x.Alias == a.Alias).Take(2).ToArray();
            if (matches.Length != 1) { gaps.Add("ANNOTATION_REFERENCE_INVALID"); continue; }
            var atom = matches[0];
            var inside = scope.Pages.Contains(atom.Page);
            if (!inside)
            {
                if (a.Status != "OUT_OF_EVALUATION_SCOPE" || a.SemanticFunction is not null || a.HeadingMembership is not null || a.ExtentParts is not null)
                    gaps.Add("OUTSIDE_SCOPE_MUST_REMAIN_UNSCORED");
                continue;
            }
            if (a.Status == "PENDING")
            {
                if (a.SemanticFunction is not null || a.HeadingMembership is not null || a.ExtentParts is not null)
                    gaps.Add("PENDING_NOT_NEGATIVE_OR_PARTIAL_GOLD");
                gaps.Add("EVALUATION_ADJUDICATION_PENDING");
                continue;
            }
            if (a.Status != "ADJUDICATED") { gaps.Add("EVALUATION_STATUS_INVALID"); continue; }
            if (a.SemanticFunction is not ("ESTABLISHES_STRUCTURE" or "REPRESENTS_STRUCTURE" or "OTHER") ||
                a.HeadingMembership is null || a.ExtentParts is null) gaps.Add("ADJUDICATION_INCOMPLETE");
            if (a.HeadingMembership == false && a.ExtentParts?.Count > 0 || a.HeadingMembership == true && a.ExtentParts?.Count == 0)
                gaps.Add("MEMBERSHIP_EXTENT_INCONSISTENT");
            if (a.HeadingMembership == true && a.ExtentParts is not null && !a.ExtentParts.Any(p => p.Alias == a.Alias))
                gaps.Add("MEMBERSHIP_NOT_IN_OWN_UNIT_EXTENT");
            if (a.ExtentParts is null) continue;
            foreach (var part in a.ExtentParts)
            {
                var targets = sourceAtoms.Where(x => x.Alias == part.Alias).ToArray();
                if (targets.Length != 1 || part.Start < 0 || part.Length <= 0 || (long)part.Start + part.Length > targets[0].Length)
                    gaps.Add("EVALUATION_EXTENT_REFERENCE_INVALID");
                else if (!scope.Pages.Contains(targets[0].Page)) gaps.Add("GOLD_EXTENT_CROSSES_SCOPE_REQUIRES_SCOPE_EXPANSION");
            }
        }
        return gaps.ToArray();
    }

    public static IReadOnlyList<EvaluationAnnotation> Draft(EvaluationScope scope, IReadOnlyList<ReviewOccurrence> atoms) =>
        atoms.Select(a => new EvaluationAnnotation(a.Alias, scope.Pages.Contains(a.Page) ? "PENDING" : "OUT_OF_EVALUATION_SCOPE", null, null, null)).ToArray();

    public static PredictionScopeObservation ObservePrediction(EvaluationScope scope, IReadOnlyList<ReviewOccurrence> atoms,
        IReadOnlyList<string> predictedParts, IReadOnlyList<EvaluationAnnotation> annotations)
    {
        if (atoms.Select(a => a.Alias).Distinct().Count() != atoms.Count)
            return new("INVALID_SOURCE_UNIVERSE", true, false, [], []);
        var byAlias = atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        if (predictedParts.Count == 0 || predictedParts.Any(a => !byAlias.ContainsKey(a)))
            return new("INVALID_SOURCE_REFERENCE", true, false, [], []);
        var inside = predictedParts.Where(a => scope.Pages.Contains(byAlias[a].Page)).ToArray();
        var outside = predictedParts.Where(a => !scope.Pages.Contains(byAlias[a].Page)).ToArray();
        if (inside.Length == 0) return new("OUT_OF_EVALUATION_SCOPE", true, false, inside, outside);
        var readiness = ReadinessGaps(scope, scope.SourceSha256, scope.UniverseSha256, scope.SnapshotSha256,
            atoms.Select(a => a.Page).Concat(scope.Pages).Distinct().ToArray(), atoms, annotations);
        if (readiness.Count > 0) return new("NOT_EVALUABLE_UNIVERSE_NOT_READY", true, false, inside, outside);
        // Preservation is not evaluability. Outside rows have deliberately unreviewed truth.
        // Even an entirely inside prediction needs a closed Gold unit in the scorer below.
        return new(outside.Length > 0 ? "CROSS_SCOPE_NOT_EVALUABLE_UNKNOWN_TRUTH" : "IN_EVALUATION_SCOPE",
            true, outside.Length == 0, inside, outside);
    }
}
