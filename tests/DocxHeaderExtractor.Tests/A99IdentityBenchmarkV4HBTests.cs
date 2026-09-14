using System.Text.Json;
using IdentityBenchmarkV4HB;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV4HBTests
{
    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static FrozenReviewBinding Binding() => new("SA-0001", "C-1", "L-1", "R-1", "sha");
    private static HumanReviewResponse Response(string relation = "UNRESOLVED", string? source = null, string? from = null, string note = "Source context is insufficient to resolve the relation.") =>
        new("SA-0001", "C-1", "L-1", "R-1", relation, "LOW", note, false, source, from);

    [Fact]
    public void Unresolved_is_a_complete_valid_human_judgment()
    {
        var result = AdjudicationValidator.ValidateSet([Response()], new[] { Binding() }.ToDictionary(x => x.ReviewId), true);
        Assert.True(result.IsValid, string.Join(";", result.Errors));
    }

    [Fact]
    public void Unknown_and_duplicate_review_ids_fail_closed()
    {
        var response = Response();
        var bindings = new[] { Binding() }.ToDictionary(x => x.ReviewId);
        var duplicate = AdjudicationValidator.ValidateSet([response, response], bindings, true);
        Assert.Contains(duplicate.Errors, x => x.Contains("DUPLICATE_REVIEW_ID", StringComparison.Ordinal));
        var unknown = AdjudicationValidator.ValidateSet([response with { ReviewId = "SA-9999" }], bindings, true);
        Assert.Contains(unknown.Errors, x => x.Contains("UNKNOWN_REVIEW_ID", StringComparison.Ordinal));
    }

    [Fact]
    public void Binding_mismatch_is_rejected()
    {
        var result = AdjudicationValidator.ValidateResponse(Response() with { CandidateId = "wrong" }, new[] { Binding() }.ToDictionary(x => x.ReviewId));
        Assert.Contains("CANDIDATE_BINDING_MISMATCH", result.Errors);
    }

    [Fact]
    public void Continuation_requires_exact_pair_direction()
    {
        var bindings = new[] { Binding() }.ToDictionary(x => x.ReviewId);
        var missing = AdjudicationValidator.ValidateResponse(Response("CONTINUATION_OF"), bindings);
        Assert.Contains("CONTINUATION_DIRECTION_REQUIRED", missing.Errors);

        var third = AdjudicationValidator.ValidateResponse(Response("CONTINUATION_OF", "L-1", "X-1"), bindings);
        Assert.Contains("CONTINUATION_DIRECTION_OUTSIDE_PAIR", third.Errors);
        Assert.Contains("UNKNOWN_CONTINUED_FROM", third.Errors);
    }

    [Fact]
    public void Invalid_relation_is_rejected()
    {
        var result = AdjudicationValidator.ValidateResponse(Response("LIKELY_REPEAT"), new[] { Binding() }.ToDictionary(x => x.ReviewId));
        Assert.Contains("INVALID_RELATION", result.Errors);
    }

    [Fact]
    public void Non_continuation_cannot_carry_direction()
    {
        var result = AdjudicationValidator.ValidateResponse(Response("UNRESOLVED", "L-1", "R-1"), new[] { Binding() }.ToDictionary(x => x.ReviewId));
        Assert.Contains("NON_CONTINUATION_HAS_DIRECTION", result.Errors);
    }

    [Fact]
    public void All_frozen_review_bindings_accept_explicit_unresolved_responses_in_validator()
    {
        var path = Path.Combine(RepositoryRoot(), "artifacts", "identity-benchmark", "v4", "semantic-adjudication", "review-item-manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        var bindings = manifest.RootElement.GetProperty("items").EnumerateArray().Select(item => new FrozenReviewBinding(
            item.GetProperty("reviewId").GetString()!, item.GetProperty("candidateId").GetString()!,
            item.GetProperty("leftOccurrenceId").GetString()!, item.GetProperty("rightOccurrenceId").GetString()!,
            item.GetProperty("sourceBindingSha256").GetString()!)).ToArray();
        var responses = bindings.Select(binding => new HumanReviewResponse(
            binding.ReviewId, binding.CandidateId, binding.LeftOccurrenceId, binding.RightOccurrenceId,
            "UNRESOLVED", "LOW", "Source evidence is insufficient to resolve this relation.", false)).ToArray();
        var result = AdjudicationValidator.ValidateSet(responses, bindings.ToDictionary(x => x.ReviewId), true);
        Assert.True(result.IsValid, string.Join(";", result.Errors));
        Assert.Equal(128, responses.Length);
    }

    [Fact]
    public void Evidence_note_cannot_cite_model_or_retrieval_outputs()
    {
        var result = AdjudicationValidator.ValidateResponse(Response(note: "The model retrieval suggests continuation."), new[] { Binding() }.ToDictionary(x => x.ReviewId));
        Assert.Contains("EVIDENCE_NOTE_CONTAINS_NON_SOURCE_LANGUAGE", result.Errors);
    }

    [Fact]
    public void Repository_progress_is_frozen_only_after_all_human_labels_are_present()
    {
        var path = Path.Combine(RepositoryRoot(), "artifacts", "identity-benchmark", "v4", "semantic-adjudication", "results", "review-progress.json");
        using var progress = JsonDocument.Parse(File.ReadAllText(path));
        var root = progress.RootElement;
        Assert.Equal("READY_FOR_V4H_SEMANTIC_ACCURACY_EVALUATION", root.GetProperty("status").GetString());
        Assert.Equal(128, root.GetProperty("expectedItems").GetInt32());
        Assert.Equal(128, root.GetProperty("completedItems").GetInt32());
        Assert.Equal(0, root.GetProperty("blankItems").GetInt32());
        Assert.True(root.GetProperty("responseFilePresent").GetBoolean());
        Assert.True(root.GetProperty("frozen").GetBoolean());
        Assert.Equal(0, root.GetProperty("modelPredictionReadCount").GetInt32());
        Assert.Equal(0, root.GetProperty("providerResponseReadCount").GetInt32());
        Assert.Equal(0, root.GetProperty("existingGoldReadCount").GetInt32());
    }
}
