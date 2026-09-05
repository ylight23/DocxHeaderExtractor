using System.Text.Json;
using DocxHeaderExtractor.Eval.HarnessLift;

namespace DocxHeaderExtractor.Tests;

public sealed class HarnessLiftOccurrenceJoinTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Exact_source_id_join_is_preferred()
    {
        var result = Join(new() { ReferenceSourceId = "@p-2" }, Source("p-2", 2, "Heading"));

        Assert.Equal(HarnessOccurrenceJoinStatus.ExactSourceId, result.JoinStatus);
        Assert.Equal("EXACT_SOURCE_ID", result.JoinMethod);
    }

    [Fact]
    public void Stable_id_is_an_explicit_identity_strategy()
    {
        var result = Join(new() { ReferenceStableId = "stable-2" }, Source("p-2", 2, "Heading", "stable-2"));

        Assert.Equal(HarnessOccurrenceJoinStatus.ExactSourceId, result.JoinStatus);
        Assert.Equal("p-2", result.ResolvedSourceId);
    }

    [Fact]
    public void Exact_span_join_is_supported()
    {
        var result = Join(new() { ReferenceSpan = new(3, 10) }, Source("p-1", 1, "abcdefghijk", span: new(3, 10)));

        Assert.Equal(HarnessOccurrenceJoinStatus.ExactSpan, result.JoinStatus);
    }

    [Fact]
    public void Exact_ordinal_and_text_join_is_supported()
    {
        var result = Join(new() { ReferenceOrdinal = 4, ReferenceText = "  A\n heading " }, Source("p-4", 4, "A heading"));

        Assert.Equal(HarnessOccurrenceJoinStatus.ExactOrdinalText, result.JoinStatus);
    }

    [Fact]
    public void Unique_exact_text_is_the_last_permitted_identity_strategy()
    {
        var result = Join(new() { ReferenceText = "Unique title" }, Source("p-8", 8, "Unique title"));

        Assert.Equal(HarnessOccurrenceJoinStatus.UniqueExactText, result.JoinStatus);
    }

    [Fact]
    public void Duplicate_exact_text_is_ambiguous()
    {
        var result = Join(new() { ReferenceText = "Repeated" }, Source("p-1", 1, "Repeated"), Source("p-2", 2, "Repeated"));

        Assert.Equal(HarnessOccurrenceJoinStatus.Ambiguous, result.JoinStatus);
    }

    [Fact]
    public void Missing_identity_is_not_found()
    {
        var result = Join(new() { ReferenceText = "Absent" }, Source("p-1", 1, "Present"));

        Assert.Equal(HarnessOccurrenceJoinStatus.NotFound, result.JoinStatus);
    }

    [Fact]
    public void Wrong_source_sha_is_not_supported()
    {
        var reference = new HarnessReferenceOccurrenceInput
        {
            ReferenceId = "r",
            DocumentId = "DOC-1",
            SourceSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            ReferenceAuthority = HarnessReferenceAuthority.HumanKey,
            ReferenceSourceId = "p-1",
        };

        var result = HarnessOccurrenceIdentityJoiner.Join(reference, [Source("p-1", 1, "A")], Sha);

        Assert.Equal(HarnessOccurrenceJoinStatus.NotSupported, result.JoinStatus);
        Assert.Equal("SOURCE_SHA_MISMATCH", result.JoinMethod);
    }

    [Fact]
    public void Filename_similarity_is_not_a_join_strategy()
    {
        var result = Join(new() { ReferenceText = "other-file-title" }, Source("p-1", 1, "title"));

        Assert.Equal(HarnessOccurrenceJoinStatus.NotFound, result.JoinStatus);
    }

    [Fact]
    public void Fuzzy_text_is_not_a_join_strategy()
    {
        var result = Join(new() { ReferenceText = "Introduction to law" }, Source("p-1", 1, "Introduction law"));

        Assert.Equal(HarnessOccurrenceJoinStatus.NotFound, result.JoinStatus);
    }

    [Fact]
    public void Exact_join_preserves_source_id_ordinal_and_span()
    {
        var result = Join(new() { ReferenceSourceId = "p-9" }, Source("p-9", 9, "A title", span: new(0, 7)));

        Assert.Equal("p-9", result.ResolvedSourceId);
        Assert.Equal(9, result.ResolvedOrdinal);
        Assert.Equal(new HarnessSpan(0, 7), result.ResolvedSpan);
        Assert.Equal("A title", result.ResolvedSourceText);
    }

    [Fact]
    public void Official_metric_eligibility_is_field_independent_from_join_status()
    {
        var result = Join(new() { ReferenceSourceId = "missing", SupportedFields = ["level"] });

        Assert.True(result.OfficialMetricEligible);
        Assert.Equal(HarnessOccurrenceJoinStatus.NotFound, result.JoinStatus);
        Assert.Contains("level", result.SupportedFields);
    }

    [Fact]
    public void Silver_is_retained_but_is_not_official()
    {
        var result = Join(new()
        {
            ReferenceSourceId = "p-1",
            ReferenceAuthority = HarnessReferenceAuthority.ModelAssistedSilver,
        }, Source("p-1", 1, "A"));

        Assert.False(result.OfficialMetricEligible);
        Assert.Equal(HarnessOccurrenceJoinStatus.ExactSourceId, result.JoinStatus);
    }

    [Fact]
    public void Heuristic_is_retained_but_is_not_official()
    {
        var result = Join(new()
        {
            ReferenceText = "A",
            ReferenceAuthority = HarnessReferenceAuthority.HeuristicReference,
        }, Source("p-1", 1, "A"));

        Assert.False(result.OfficialMetricEligible);
    }

    [Fact]
    public void Source_structural_reference_is_official_for_declared_fields()
    {
        var result = Join(new()
        {
            ReferenceSourceId = "p-1",
            ReferenceAuthority = HarnessReferenceAuthority.SourceStructuralReference,
            SupportedFields = ["level", "parent"],
        });

        Assert.True(result.OfficialMetricEligible);
        Assert.Equal(["level", "parent"], result.SupportedFields);
    }

    [Fact]
    public void Join_does_not_create_a_negative_label()
    {
        var result = Join(new() { ReferenceSourceId = "p-1", ExpectedIsHeading = null });

        Assert.Null(result.ExpectedIsHeading);
    }

    [Fact]
    public void Join_does_not_use_pdf_line_index_as_ordinal_without_text()
    {
        var result = Join(new() { ReferenceOrdinal = 17 }, Source("p-1", 17, "Different"));

        Assert.Equal(HarnessOccurrenceJoinStatus.NotFound, result.JoinStatus);
    }

    [Fact]
    public void Review_source_sha_mismatch_is_rejected()
    {
        var result = ValidateReview(Review() with { SourceSha256 = "wrong" });

        Assert.False(result.Accepted);
        Assert.Equal("source-sha-mismatch", result.Reason);
    }

    [Fact]
    public void Review_packet_sha_mismatch_is_rejected()
    {
        var result = ValidateReview(Review() with { PacketSha256 = "wrong" });

        Assert.False(result.Accepted);
        Assert.Equal("packet-sha-mismatch", result.Reason);
    }

    [Fact]
    public void Review_heading_span_must_fit_source_text()
    {
        var result = ValidateReview(Review() with { HeadingSpan = new(0, 99) });

        Assert.False(result.Accepted);
        Assert.Equal("heading-span-invalid", result.Reason);
    }

    [Fact]
    public void Review_level_must_be_between_one_and_nine()
    {
        var result = ValidateReview(Review() with { Level = "10" });

        Assert.False(result.Accepted);
        Assert.Equal("level-invalid", result.Reason);
    }

    [Fact]
    public void Review_role_must_be_compatible_with_heading_label()
    {
        var result = ValidateReview(Review() with { IsHeading = "YES", Role = "body" });

        Assert.False(result.Accepted);
        Assert.Equal("heading-role-incompatible", result.Reason);
    }

    [Fact]
    public void Review_requires_a_reviewer_identity()
    {
        var result = ValidateReview(Review() with { ReviewerId = "" });

        Assert.False(result.Accepted);
        Assert.Equal("reviewer-id-missing", result.Reason);
    }

    [Fact]
    public void Review_requires_a_timestamp()
    {
        var result = ValidateReview(Review() with { ReviewedAt = "" });

        Assert.False(result.Accepted);
        Assert.Equal("reviewed-at-missing", result.Reason);
    }

    [Fact]
    public void Review_requires_decision_version()
    {
        var result = ValidateReview(Review() with { DecisionVersion = "" });

        Assert.False(result.Accepted);
        Assert.Equal("decision-version-missing", result.Reason);
    }

    [Fact]
    public void Valid_review_is_accepted_without_mutating_source()
    {
        var source = Source("p-1", 1, "A title");
        var result = HarnessHumanReviewValidator.Validate(Review(), source, Sha, "packet");

        Assert.True(result.Accepted);
        Assert.Equal("A title", source.RawText);
    }

    [Fact]
    public void Model_trace_has_no_gold_or_expected_properties()
    {
        var trace = new HarnessModelOccurrenceTrace { RunId = "run", DocumentId = "DOC-1", SourceId = "p-1" };
        var json = JsonSerializer.Serialize(trace);

        Assert.DoesNotContain("gold", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expected", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Model_trace_does_not_contain_raw_prompt_or_response()
    {
        var trace = new HarnessModelOccurrenceTrace { RunId = "run", DocumentId = "DOC-1", SourceId = "p-1" };
        var json = JsonSerializer.Serialize(trace);

        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("response", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Model_exposure_is_not_inferred_from_final_inclusion()
    {
        var trace = new HarnessModelOccurrenceTrace
        {
            RunId = "run", DocumentId = "DOC-1", SourceId = "p-1",
            FinalIncluded = true, ModelExposed = false,
        };

        Assert.False(trace.ModelExposed);
        Assert.True(trace.FinalIncluded);
    }

    [Fact]
    public void Unknown_join_status_is_not_promoted_to_exact()
    {
        var result = Join(new() { ReferenceSourceId = "not-present" });

        Assert.NotEqual(HarnessOccurrenceJoinStatus.ExactSourceId, result.JoinStatus);
        Assert.False(result.JoinStatus is HarnessOccurrenceJoinStatus.ExactSpan or HarnessOccurrenceJoinStatus.ExactOrdinalText or HarnessOccurrenceJoinStatus.UniqueExactText);
    }

    [Fact]
    public void Ambiguous_join_has_no_resolved_source()
    {
        var result = Join(new() { ReferenceText = "Repeated" }, Source("p-1", 1, "Repeated"), Source("p-2", 2, "Repeated"));

        Assert.Null(result.ResolvedSourceId);
        Assert.Null(result.ResolvedOrdinal);
    }

    [Fact]
    public void Normalization_is_unicode_and_whitespace_stable()
    {
        Assert.Equal("café title", HarnessOccurrenceIdentityJoiner.NormalizeText(" cafe\u0301  title "));
    }

    [Fact]
    public void Exact_source_id_wins_over_duplicate_text()
    {
        var result = Join(new() { ReferenceSourceId = "p-2", ReferenceText = "Repeated" }, Source("p-1", 1, "Repeated"), Source("p-2", 2, "Repeated"));

        Assert.Equal(HarnessOccurrenceJoinStatus.ExactSourceId, result.JoinStatus);
        Assert.Equal("p-2", result.ResolvedSourceId);
    }

    [Fact]
    public void Exact_ordinal_text_wins_over_duplicate_text_elsewhere()
    {
        var result = Join(new() { ReferenceOrdinal = 2, ReferenceText = "Repeated" }, Source("p-1", 1, "Repeated"), Source("p-2", 2, "Repeated"));

        Assert.Equal(HarnessOccurrenceJoinStatus.ExactOrdinalText, result.JoinStatus);
        Assert.Equal("p-2", result.ResolvedSourceId);
    }

    [Fact]
    public void Reference_identity_is_retained_in_join_result()
    {
        var result = Join(new() { ReferenceSourceId = "@p-1", ReferenceOrdinal = 7 }, Source("p-1", 1, "A"));

        Assert.Equal("@p-1", result.ReferenceOccurrenceIdentity.SourceId);
        Assert.Equal(7, result.ReferenceOccurrenceIdentity.Index);
    }

    [Fact]
    public void Source_occurrence_span_is_full_parser_paragraph_by_default()
    {
        var source = Source("p-1", 1, "A title");

        Assert.Equal(new HarnessSpan(0, 7), source.Span);
    }

    [Fact]
    public void Review_cannot_change_the_source_occurrence_record()
    {
        var source = Source("p-1", 1, "Original");
        var before = JsonSerializer.Serialize(source);
        _ = HarnessHumanReviewValidator.Validate(Review() with { HeadingSpan = new(0, 8) }, source, Sha, "packet");

        Assert.Equal(before, JsonSerializer.Serialize(source));
    }

    [Fact]
    public void Review_unknown_level_is_allowed_as_unadjudicated()
    {
        var result = ValidateReview(Review() with { Level = "UNKNOWN" });

        Assert.True(result.Accepted);
    }

    [Fact]
    public void Review_unknown_span_is_allowed_as_unadjudicated()
    {
        var result = ValidateReview(Review() with { HeadingSpan = null });

        Assert.True(result.Accepted);
    }

    [Fact]
    public void Official_source_reference_can_be_joined_without_gold_label()
    {
        var result = Join(new()
        {
            ReferenceSourceId = "p-1",
            ReferenceAuthority = HarnessReferenceAuthority.SourceStructuralReference,
            SupportedFields = ["level"],
            ExpectedLevel = 2,
        });

        Assert.True(result.OfficialMetricEligible);
        Assert.Null(result.ExpectedIsHeading);
        Assert.Equal(2, result.ExpectedLevel);
    }

    private static HarnessOccurrenceJoinResult Join(
        HarnessReferenceOccurrenceInput partial,
        params HarnessSourceOccurrence[] sources) =>
        HarnessOccurrenceIdentityJoiner.Join(partial with
        {
            ReferenceId = partial.ReferenceId ?? "reference",
            DocumentId = partial.DocumentId ?? "DOC-1",
            SourceSha256 = string.IsNullOrWhiteSpace(partial.SourceSha256) ? Sha : partial.SourceSha256,
        }, sources, Sha);

    private static HarnessSourceOccurrence Source(string id, int ordinal, string text, string? stableId = null, HarnessSpan? span = null) =>
        new(id, stableId ?? id, ordinal, span ?? new(0, text.Length), text);

    private static HarnessHumanReviewDecision Review() => new()
    {
        DocumentId = "DOC-1", SourceId = "p-1", SourceOrdinal = 1,
        IsHeading = "YES", Role = "heading", Level = "1",
        ReviewerId = "reviewer", ReviewedAt = "2026-09-05T00:00:00Z",
        SourceSha256 = Sha, PacketSha256 = "packet", DecisionVersion = "v1",
    };

    private static HarnessReviewValidationResult ValidateReview(HarnessHumanReviewDecision decision) =>
        HarnessHumanReviewValidator.Validate(decision, Source("p-1", 1, "A title"), Sha, "packet");
}
