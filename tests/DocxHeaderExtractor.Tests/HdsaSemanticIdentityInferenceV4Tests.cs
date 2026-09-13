using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class HdsaSemanticIdentityInferenceV4Tests
{
    [Fact]
    public void Pair_discovery_is_exhaustive_ordered_and_deterministic()
    {
        var input = Input(
            new("S3", 3, "Repeat"),
            new("S1", 1, "Repeat"),
            new("S2", 2, "Continuation"));

        var first = HdsaSemanticNodeResolverV4.DiscoverPairs(input);
        var second = HdsaSemanticNodeResolverV4.DiscoverPairs(input);

        Assert.Equal(3, first.Count);
        Assert.Equal(first.Select(pair => pair.PairId), second.Select(pair => pair.PairId));
        Assert.Equal(["S1", "S1", "S2"], first.Select(pair => pair.Left.OccurrenceId));
        Assert.Equal(["S2", "S3", "S3"], first.Select(pair => pair.Right.OccurrenceId));
        Assert.Equal(first.Select(pair => pair.PairId), first.Select(pair => pair.PairId).Distinct());
    }

    [Fact]
    public void Same_repeat_merges_and_records_explicit_provenance()
    {
        var input = Input(new("S1", 1, "Results"), new("S2", 4, "RESULTS"));
        var request = HdsaSemanticNodeResolverV4.CreateRequest(input, "S1", "S2");
        var result = HdsaSemanticNodeResolverV4.Resolve(input, [Observation(request, HdsaSemanticIdentityInferenceDecision.SameSemanticRepeat)]);

        Assert.True(result.IsValid);
        Assert.Single(result.Predictions);
        Assert.Equal(["S1", "S2"], result.Predictions[0].MemberOccurrenceIds);
        Assert.Single(result.AcceptedRelations);
        Assert.Equal(HdsaSemanticIdentityRelationType.SameSemanticRepeat, result.AcceptedRelations[0].Relation);
        var record = Assert.Single(result.RelationRecords);
        Assert.Equal(HdsaSemanticIdentityInferenceDecision.SameSemanticRepeat, record.RelationType);
        Assert.True(record.AcceptedByValidator);
        Assert.False(record.GoldUsed);
        Assert.Equal("MODEL", record.InferenceSource);
        Assert.StartsWith("IR-", record.RelationId, StringComparison.Ordinal);
    }

    [Fact]
    public void Continuation_collapses_right_into_left_without_fabricating_transitive_edges()
    {
        var input = Input(new("S1", 1, "Chapter"), new("S2", 2, "continued"), new("S3", 3, "tail"));
        var first = HdsaSemanticNodeResolverV4.CreateRequest(input, "S1", "S2");
        var second = HdsaSemanticNodeResolverV4.CreateRequest(input, "S2", "S3");
        var result = HdsaSemanticNodeResolverV4.Resolve(input,
        [
            Observation(first, HdsaSemanticIdentityInferenceDecision.ContinuationOf, "r1"),
            Observation(second, HdsaSemanticIdentityInferenceDecision.ContinuationOf, "r2"),
        ]);

        Assert.True(result.IsValid);
        Assert.Single(result.Predictions);
        Assert.Equal(["S1", "S2", "S3"], result.Predictions[0].MemberOccurrenceIds);
        Assert.Equal(2, result.AcceptedRelations.Count);
        Assert.Contains(result.AcceptedRelations, item => item.FromOccurrenceId == "S2" && item.ToOccurrenceId == "S1");
        Assert.Contains(result.AcceptedRelations, item => item.FromOccurrenceId == "S3" && item.ToOccurrenceId == "S2");
        Assert.DoesNotContain(result.RelationRecords, item => item.LeftOccurrenceId == "S1" && item.RightOccurrenceId == "S3" && item.AcceptedByValidator);
    }

    [Fact]
    public void Distinct_and_unresolved_keep_occurrences_split()
    {
        var input = Input(new("S1", 1, "A"), new("S2", 2, "B"), new("S3", 3, "C"));
        var distinct = HdsaSemanticNodeResolverV4.CreateRequest(input, "S1", "S2");
        var unresolved = HdsaSemanticNodeResolverV4.CreateRequest(input, "S2", "S3");
        var result = HdsaSemanticNodeResolverV4.Resolve(input,
        [
            Observation(distinct, HdsaSemanticIdentityInferenceDecision.Distinct, "distinct"),
            Observation(unresolved, HdsaSemanticIdentityInferenceDecision.Unresolved, "unresolved"),
        ]);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.Predictions.Count);
        Assert.Empty(result.AcceptedRelations);
        Assert.Equal(
            [HdsaSemanticIdentityInferenceDecision.Distinct, HdsaSemanticIdentityInferenceDecision.Unresolved],
            result.RelationRecords.Select(item => item.RelationType).Order());
        Assert.All(result.RelationRecords, item => Assert.True(item.AcceptedByValidator));
    }

    [Fact]
    public void Text_adjacency_style_and_layout_alone_do_not_merge()
    {
        var input = Input(
            new("S1", 1, "Results", "h2", "section-a"),
            new("S2", 2, "Results", "h2", "section-a"));

        var result = HdsaSemanticNodeResolverV4.Resolve(input, []);

        Assert.Equal(2, result.Predictions.Count);
        Assert.Empty(result.AcceptedRelations);
        Assert.Empty(result.RelationRecords);
    }

    [Fact]
    public void Gold_and_legacy_provenance_are_rejected()
    {
        var input = Input(new("S1", 1, "A"), new("S2", 2, "B"));
        var request = HdsaSemanticNodeResolverV4.CreateRequest(input, "S1", "S2");
        var goldObservation = Observation(request, HdsaSemanticIdentityInferenceDecision.SameSemanticRepeat, "gold") with { GoldUsed = true };
        var legacyObservation = Observation(request, HdsaSemanticIdentityInferenceDecision.SameSemanticRepeat, "legacy") with { LegacyUsed = true };

        var result = HdsaSemanticNodeResolverV4.Resolve(input, [goldObservation, legacyObservation]);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Predictions.Count);
        Assert.Empty(result.AcceptedRelations);
        Assert.Contains("GOLD_OR_LEGACY_PROVENANCE", result.Errors);
        Assert.All(result.RelationRecords, item => Assert.False(item.AcceptedByValidator));
    }

    [Fact]
    public void Request_serialization_and_hash_are_deterministic_and_gold_free()
    {
        var input = Input(new("S1", 1, "A"), new("S2", 2, "B"));
        var request = HdsaSemanticNodeResolverV4.CreateRequest(input, "S1", "S2");

        var first = HdsaSemanticNodeResolverV4.SerializeRequest(request);
        var second = HdsaSemanticNodeResolverV4.SerializeRequest(request);

        Assert.Equal(first, second);
        Assert.Equal(HdsaSemanticNodeResolverV4.RequestHash(request), HdsaSemanticNodeResolverV4.RequestHash(request));
        Assert.DoesNotContain("parent", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("level", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"goldDerivedInput\":false", first, StringComparison.Ordinal);
        Assert.Contains("SAME_SEMANTIC_REPEAT", JsonSerializer.Serialize(HdsaSemanticNodeResolverV4.Schema()), StringComparison.Ordinal);
    }

    [Fact]
    public void Stale_request_unknown_pair_and_extra_response_fail_closed()
    {
        var input = Input(new("S1", 1, "A"), new("S2", 2, "B"));
        var request = HdsaSemanticNodeResolverV4.CreateRequest(input, "S1", "S2");
        var stale = Observation(request, HdsaSemanticIdentityInferenceDecision.Distinct) with { RequestHash = "stale" };
        var staleValidation = HdsaSemanticNodeResolverV4.Validate(request, stale);
        Assert.False(staleValidation.Accepted);
        Assert.Equal("STALE_REQUEST_HASH", staleValidation.RejectionReason);

        Assert.Throws<FormatException>(() => HdsaSemanticNodeResolverV4.Parse(
            "{\"sourceSha256\":\"s\",\"preprocessingSnapshotHash\":\"p\",\"pairId\":\"x\",\"leftOccurrenceId\":\"S1\",\"rightOccurrenceId\":\"S2\",\"decision\":\"DISTINCT\",\"parent\":null}"));
        Assert.Throws<InvalidDataException>(() => HdsaSemanticNodeResolverV4.CreateRequest(input, "S2", "S1"));
    }

    [Fact]
    public void Conflicting_same_and_distinct_decisions_are_rejected_without_merge()
    {
        var input = Input(new("S1", 1, "A"), new("S2", 2, "B"));
        var request = HdsaSemanticNodeResolverV4.CreateRequest(input, "S1", "S2");
        var result = HdsaSemanticNodeResolverV4.Resolve(input,
        [
            Observation(request, HdsaSemanticIdentityInferenceDecision.SameSemanticRepeat, "same"),
            Observation(request, HdsaSemanticIdentityInferenceDecision.Distinct, "distinct"),
        ]);

        Assert.False(result.IsValid);
        Assert.Contains("CONFLICTING_RELATIONS", result.Errors);
        Assert.Equal(2, result.Predictions.Count);
        Assert.Empty(result.AcceptedRelations);
    }

    [Fact]
    public void Multiple_continuation_parents_are_rejected_fail_closed()
    {
        var input = Input(new("S1", 1, "A"), new("S2", 2, "B"), new("S3", 3, "C"));
        var first = HdsaSemanticNodeResolverV4.CreateRequest(input, "S1", "S3");
        var second = HdsaSemanticNodeResolverV4.CreateRequest(input, "S2", "S3");
        var result = HdsaSemanticNodeResolverV4.Resolve(input,
        [
            Observation(first, HdsaSemanticIdentityInferenceDecision.ContinuationOf, "r1"),
            Observation(second, HdsaSemanticIdentityInferenceDecision.ContinuationOf, "r2"),
        ]);

        Assert.False(result.IsValid);
        Assert.Contains("MULTIPLE_INCOMPATIBLE_CONTINUATION_PARENTS", result.Errors);
        Assert.Equal(3, result.Predictions.Count);
        Assert.Empty(result.AcceptedRelations);
    }

    [Fact]
    public void V4_catalog_is_deterministic_and_uses_only_accepted_explicit_relations()
    {
        var input = Input(new("S1", 1, "A"), new("S2", 2, "B"));
        var request = HdsaSemanticNodeResolverV4.CreateRequest(input, "S1", "S2");
        var observations = new[] { Observation(request, HdsaSemanticIdentityInferenceDecision.SameSemanticRepeat) };
        var first = HdsaSemanticNodeResolverV4.Resolve(input, observations);
        var second = HdsaSemanticNodeResolverV4.Resolve(input, observations);
        var firstCatalog = HdsaSemanticNodeCatalogBuilder.Build(input, first);
        var secondCatalog = HdsaSemanticNodeCatalogBuilder.Build(input, second);

        Assert.Equal(firstCatalog.CatalogFingerprint, secondCatalog.CatalogFingerprint);
        Assert.Equal(firstCatalog.Entries.Select(item => item.MemberOccurrenceIds), secondCatalog.Entries.Select(item => item.MemberOccurrenceIds));
        Assert.False(firstCatalog.GoldUsed);
        Assert.Equal("semantic-identity-inference-v4", firstCatalog.ResolverVersion);
    }

    private static HdsaSemanticNodeResolutionInput Input(params HdsaSemanticNodeSourceOccurrence[] occurrences) =>
        new("source-sha", "snapshot-sha", occurrences, false);

    private static HdsaSemanticIdentityInferenceObservation Observation(
        HdsaSemanticIdentityInferenceRequest request,
        HdsaSemanticIdentityInferenceDecision decision,
        string suffix = "response")
    {
        var response = new HdsaSemanticIdentityInferenceResponse(
            request.SourceSha256,
            request.PreprocessingSnapshotHash,
            request.Pair.PairId,
            request.Pair.Left.OccurrenceId,
            request.Pair.Right.OccurrenceId,
            decision);
        return new(
            request,
            response,
            HdsaSemanticNodeResolverV4.RequestHash(request),
            "response-hash-" + suffix,
            "MODEL",
            "test-model",
            "test-provider",
            false,
            false);
    }
}
