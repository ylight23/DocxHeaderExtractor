namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record ReviewOccurrence(string Alias, int Page, int Length);
internal sealed record ReviewedOccurrence(string Alias, string Membership, string Status);
internal sealed record ReviewedPart(string Alias, int Start, int Length);
internal sealed record ReviewedUnit(string Id, string Role, string Status, IReadOnlyList<ReviewedPart> Parts);
internal sealed record SourceOnlyReview(string SourceSha256, string UniverseSha256,
    string SnapshotSha256, string PolicySha256, IReadOnlyList<int> ReviewedPdfPages,
    IReadOnlyList<ReviewedOccurrence> Occurrences, IReadOnlyList<ReviewedUnit> Units,
    string Reviewer, bool SourceOnlyAttested, bool ModelOutputsConsulted, string Status);

/// <summary>
/// Checks review completeness and coordinates, never annotation truth or reviewer independence.
/// Review covers every PDF page (including pages without extracted atoms) and every occurrence.
/// Drafts are deliberately unlabelled; this component does not create approved Gold.
/// </summary>
internal static class P7SourceOnlyReview
{
    public const string Version = "P7_SOURCE_ONLY_REVIEW_V1";
    public static SourceOnlyReview Draft(string source, string universe, string snapshot, string policy,
        IReadOnlyList<ReviewOccurrence> occurrences) => new(source, universe, snapshot, policy, [],
        occurrences.Select(o => new ReviewedOccurrence(o.Alias, "UNADJUDICATED", "NOT_EVALUABLE")).ToArray(),
        [], "", false, false, "DRAFT_NOT_GOLD");

    public static IReadOnlyList<string> ApprovalGaps(SourceOnlyReview review, string source, string universe,
        string snapshot, string policy, IReadOnlyList<int> pdfPages, IReadOnlyList<ReviewOccurrence> occurrences)
    {
        var gaps = new SortedSet<string>(StringComparer.Ordinal);
        if (review.SourceSha256 != source || review.UniverseSha256 != universe ||
            review.SnapshotSha256 != snapshot || review.PolicySha256 != policy) gaps.Add("REVIEW_IDENTITY_MISMATCH");
        if (review.Status != "USER_APPROVED" || string.IsNullOrWhiteSpace(review.Reviewer) ||
            !review.SourceOnlyAttested || review.ModelOutputsConsulted) gaps.Add("INDEPENDENT_APPROVAL_REQUIRED");
        if (pdfPages.Distinct().Count() != pdfPages.Count ||
            review.ReviewedPdfPages.Distinct().Count() != review.ReviewedPdfPages.Count ||
            !review.ReviewedPdfPages.Order().SequenceEqual(pdfPages.Order())) gaps.Add("FULL_PDF_REVIEW_REQUIRED");
        if (occurrences.Select(o => o.Alias).Distinct().Count() != occurrences.Count ||
            review.Occurrences.Select(o => o.Alias).Distinct().Count() != review.Occurrences.Count ||
            !review.Occurrences.Select(o => o.Alias).Order().SequenceEqual(occurrences.Select(o => o.Alias).Order()))
            gaps.Add("FULL_OCCURRENCE_REVIEW_REQUIRED");
        foreach (var item in review.Occurrences)
            if (item.Status == "ADJUDICATED")
            {
                if (item.Membership is not ("IN_SCOPE" or "OUT_OF_SCOPE")) gaps.Add("MEMBERSHIP_LABEL_INVALID");
            }
            else if (item.Status != "NOT_EVALUABLE" || item.Membership != "UNADJUDICATED")
                gaps.Add("UNADJUDICATED_MUST_BE_NOT_EVALUABLE");
        if (review.Units.Select(u => u.Id).Distinct().Count() != review.Units.Count) gaps.Add("DUPLICATE_UNIT");
        foreach (var unit in review.Units)
        {
            if (unit.Role is not ("DOCUMENT_TITLE" or "DOCUMENT_SUBTITLE" or "STRUCTURAL_HEADING")) gaps.Add("UNIT_ROLE_INVALID");
            if (unit.Status is not ("ADJUDICATED" or "NOT_EVALUABLE") || unit.Parts.Count == 0) gaps.Add("UNIT_REVIEW_INVALID");
            foreach (var part in unit.Parts)
            {
                var candidates = occurrences.Where(o => o.Alias == part.Alias).ToArray();
                if (candidates.Length != 1 || part.Start < 0 || part.Length <= 0 ||
                    (long)part.Start + part.Length > candidates[0].Length) gaps.Add("UNIT_SOURCE_SPAN_INVALID");
                if (unit.Status == "ADJUDICATED" && !review.Occurrences.Any(o => o.Alias == part.Alias &&
                    o.Status == "ADJUDICATED" && o.Membership == "IN_SCOPE")) gaps.Add("UNIT_MEMBERSHIP_CONFLICT");
            }
        }
        // Annotation gaps remain explicit; no default negative and no model-derived completion.
        return gaps.ToArray();
    }
}
