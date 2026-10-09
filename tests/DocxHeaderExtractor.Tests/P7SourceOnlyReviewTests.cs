using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7SourceOnlyReviewTests
{
    private static readonly ReviewOccurrence[] Source = [new("L1", 1, 12), new("L2", 1, 8)];
    private static SourceOnlyReview Approved() => new("source", "universe", "snapshot", "policy", [1, 2],
        [new("L1", "IN_SCOPE", "ADJUDICATED"), new("L2", "OUT_OF_SCOPE", "ADJUDICATED")],
        [new("U1", "DOCUMENT_TITLE", "ADJUDICATED", [new("L1", 0, 12)])], "user", true, false, "USER_APPROVED");
    private static IReadOnlyList<string> Gaps(SourceOnlyReview review) =>
        P7SourceOnlyReview.ApprovalGaps(review, "source", "universe", "snapshot", "policy", [1, 2], Source);

    [Fact]
    public void Draft_has_no_default_negatives_units_or_approval()
    {
        var draft = P7SourceOnlyReview.Draft("source", "universe", "snapshot", "policy", Source);
        Assert.Empty(draft.Units); Assert.Empty(draft.ReviewedPdfPages);
        Assert.All(draft.Occurrences, o => { Assert.Equal("UNADJUDICATED", o.Membership); Assert.Equal("NOT_EVALUABLE", o.Status); });
        Assert.Contains("INDEPENDENT_APPROVAL_REQUIRED", Gaps(draft));
    }

    [Fact]
    public void Full_review_covers_even_pdf_page_without_atoms() => Assert.Empty(Gaps(Approved()));

    [Fact]
    public void Missing_unpredicted_region_is_not_implicitly_negative() =>
        Assert.Contains("FULL_OCCURRENCE_REVIEW_REQUIRED", Gaps(Approved() with { Occurrences = [Approved().Occurrences[0]] }));

    [Fact]
    public void Missing_page_without_atoms_still_blocks_review() =>
        Assert.Contains("FULL_PDF_REVIEW_REQUIRED", Gaps(Approved() with { ReviewedPdfPages = [1] }));

    [Fact]
    public void Model_explanation_exposure_blocks_source_only_approval() =>
        Assert.Contains("INDEPENDENT_APPROVAL_REQUIRED", Gaps(Approved() with { ModelOutputsConsulted = true }));

    [Fact]
    public void Policy_source_or_snapshot_change_invalidates_old_review()
    {
        foreach (var r in new[] { Approved() with { SourceSha256 = "other" }, Approved() with { UniverseSha256 = "other" },
            Approved() with { SnapshotSha256 = "other" }, Approved() with { PolicySha256 = "other" } })
            Assert.Contains("REVIEW_IDENTITY_MISMATCH", Gaps(r));
    }

    [Fact]
    public void Unresolved_adjudication_is_explicit_not_evaluable_not_correct_or_wrong()
    {
        var review = Approved() with { Occurrences = [new("L1", "UNADJUDICATED", "NOT_EVALUABLE"), Approved().Occurrences[1]], Units = [] };
        Assert.Empty(Gaps(review));
        Assert.Contains("UNADJUDICATED_MUST_BE_NOT_EVALUABLE", Gaps(review with { Occurrences = [new("L1", "OUT_OF_SCOPE", "NOT_EVALUABLE"), review.Occurrences[1]] }));
    }

    [Fact]
    public void Fabricated_source_parts_and_out_of_bounds_fail_closed()
    {
        foreach (var part in new[] { new ReviewedPart("FAKE", 0, 1), new("L1", -1, 1), new("L1", 0, 13), new("L1", 0, 0) })
            Assert.Contains("UNIT_SOURCE_SPAN_INVALID", Gaps(Approved() with { Units = [Approved().Units[0] with { Parts = [part] }] }));
    }

    [Fact]
    public void Unit_does_not_upgrade_out_of_scope_membership() =>
        Assert.Contains("UNIT_MEMBERSHIP_CONFLICT", Gaps(Approved() with { Units = [Approved().Units[0] with { Parts = [new("L2", 0, 8)] }] }));

    [Fact]
    public void Duplicate_occurrence_page_or_unit_is_rejected()
    {
        Assert.Contains("FULL_OCCURRENCE_REVIEW_REQUIRED", Gaps(Approved() with { Occurrences = [Approved().Occurrences[0], Approved().Occurrences[0]] }));
        Assert.Contains("FULL_PDF_REVIEW_REQUIRED", Gaps(Approved() with { ReviewedPdfPages = [1, 2, 2] }));
        Assert.Contains("DUPLICATE_UNIT", Gaps(Approved() with { Units = [Approved().Units[0], Approved().Units[0]] }));
    }
}
