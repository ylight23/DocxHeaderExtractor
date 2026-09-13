using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class HdsaRelationReasoningContractsTests
{
    [Fact]
    public void Relation_spaces_are_distinct_and_only_parent_determines_depth()
    {
        Assert.Equal(HdsaRelationSpace.Structural, HdsaRelationSemantics.Space(HdsaRelationType.ParentOf));
        Assert.Equal(HdsaRelationSpace.Order, HdsaRelationSemantics.Space(HdsaRelationType.Precedes));
        Assert.Equal(HdsaRelationSpace.SemanticIdentity, HdsaRelationSemantics.Space(HdsaRelationType.ContinuationOf));
        Assert.Equal(HdsaRelationSpace.Derived, HdsaRelationSemantics.Space(HdsaRelationType.SiblingOf));
        Assert.True(HdsaRelationSemantics.DeterminesTreeDepth(HdsaRelationType.ParentOf));
        Assert.False(HdsaRelationSemantics.DeterminesTreeDepth(HdsaRelationType.Precedes));
        Assert.False(HdsaRelationSemantics.DeterminesTreeDepth(HdsaRelationType.SameSemanticNode));
    }

    [Fact]
    public void Relation_schema_has_no_level_or_coordinate_fields()
    {
        var json = JsonSerializer.Serialize(HdsaRelationReasoningContract.Schema());

        Assert.DoesNotContain("level", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("offset", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("start", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("end", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT_PARENT", json, StringComparison.Ordinal);
        Assert.Contains("UNRESOLVED", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_and_validator_accept_a_source_backed_parent()
    {
        var request = new HdsaRelationReasoningRequest(
            "child", ["parent"],
            [new("parent", 1, "Parent", "SECTION")],
            new("order=2", "2.1", "Heading 2", "ARTICLE/SECTION", "parent → child"));
        var decision = HdsaRelationReasoningContract.Parse(
            "{\"child\":\"child\",\"decision\":\"SELECT_PARENT\",\"parent\":\"parent\"}");

        var validation = HdsaRelationReasoningContract.Validate(request, decision, new HashSet<string>(["root", "parent", "child"]));

        Assert.True(validation.Accepted);
        Assert.False(validation.ParentWasOutsideAttentionHints);
    }

    [Fact]
    public void Attention_shortlist_is_not_a_recall_gate()
    {
        var request = new HdsaRelationReasoningRequest(
            "child", ["near-parent"],
            [new("near-parent", 9, "Near parent") , new("distant-parent", 2, "Distant parent")],
            new("order=10", null, null, null, "context"));
        var decision = new HdsaRelationReasoningDecision(
            "child", HdsaParentDecision.SelectParent, "distant-parent");

        var validation = HdsaRelationReasoningContract.Validate(
            request, decision, new HashSet<string>(["child", "near-parent", "distant-parent"]));

        Assert.True(validation.Accepted);
        Assert.True(validation.ParentWasOutsideAttentionHints);
    }

    [Fact]
    public void Root_and_unresolved_require_no_parent()
    {
        var request = new HdsaRelationReasoningRequest(
            "child", [], [], new("order=1", null, null, null, null));

        Assert.True(HdsaRelationReasoningContract.Validate(
            request, new("child", HdsaParentDecision.Root), new HashSet<string>(["child"])).Accepted);
        Assert.True(HdsaRelationReasoningContract.Validate(
            request, new("child", HdsaParentDecision.Unresolved), new HashSet<string>(["child"])).Accepted);
        Assert.False(HdsaRelationReasoningContract.Validate(
            request, new("child", HdsaParentDecision.Root, "parent"), new HashSet<string>(["child", "parent"])).Accepted);
    }

    [Fact]
    public void Unknown_parent_and_extra_output_fields_fail_closed()
    {
        var request = new HdsaRelationReasoningRequest(
            "child", ["parent"], [new("parent", 1, "Parent")], new("order=2", null, null, null, null));
        var unknownParent = new HdsaRelationReasoningDecision(
            "child", HdsaParentDecision.SelectParent, "invented");
        var invalid = HdsaRelationReasoningContract.Validate(request, unknownParent, new HashSet<string>(["child", "parent"]));

        Assert.False(invalid.Accepted);
        Assert.Equal("UNKNOWN_PARENT", invalid.RejectionReason);
        Assert.Throws<FormatException>(() => HdsaRelationReasoningContract.Parse(
            "{\"child\":\"child\",\"decision\":\"ROOT\",\"parent\":null,\"level\":1}"));
    }

    [Fact]
    public void Primary_candidates_preserve_document_order_and_are_only_a_shortlist()
    {
        var ids = new[] { "A", "B", "C", "D", "E" };

        var candidates = HdsaParentAttentionCandidates.PrimaryPreceding(ids, "E", 2);

        Assert.Equal(["D", "C"], candidates);
        Assert.True(HdsaParentAttentionCandidates.IsAttentionOnly(
            new("E", candidates, [], new("order=5", null, null, null, null))));
        Assert.Empty(HdsaParentAttentionCandidates.PrimaryPreceding(ids, "missing"));
    }

    [Fact]
    public void HDSA_structural_identity_does_not_depend_on_legacy_hints_or_metadata()
    {
        var ids = new[] { "A", "B", "C" };
        var universeA = new[]
        {
            new HdsaParentUniverseEntry("A", 1, "A", "ARTICLE"),
            new HdsaParentUniverseEntry("B", 2, "B", "SECTION"),
        };
        var universeB = new[]
        {
            new HdsaParentUniverseEntry("A", 1, "A", "LEGACY_ROLE_BOGUS"),
            new HdsaParentUniverseEntry("B", 2, "B", "LEGACY_ROLE_BOGUS"),
        };
        var requestA = new HdsaRelationReasoningRequest(
            "C", ["B"], universeA,
            new("order=3", null, "ARTICLE", "local", "legacy-hints=[]"));
        var requestB = new HdsaRelationReasoningRequest(
            "C", ["B"], universeB,
            new("order=3", null, "LEGACY_ROLE_BOGUS", "local", "legacy-hints=[level:99,parent-node:BAD_PARENT]"));

        Assert.Equal(requestA.Child, requestB.Child);
        Assert.Equal(requestA.CandidateParents, requestB.CandidateParents);
        Assert.Equal(requestA.AuthoritativeParentUniverse.Select(item => item.Id),
            requestB.AuthoritativeParentUniverse.Select(item => item.Id));

        var proposals = new[]
        {
            new HdsaRelationProposal("A", "B", HdsaRelationType.ParentOf),
            new HdsaRelationProposal("B", "C", HdsaRelationType.ParentOf),
        };
        var treeA = HdsaTreeConstructor.Build(ids,
            HdsaGraphValidator.Validate(ids, HdsaRelationNormalizer.Normalize(ids, proposals)));
        var treeB = HdsaTreeConstructor.Build(ids,
            HdsaGraphValidator.Validate(ids, HdsaRelationNormalizer.Normalize(ids, proposals)));

        Assert.Equal(treeA.Nodes, treeB.Nodes);
        Assert.Equal(treeA.Errors, treeB.Errors);
        Assert.Equal((1, 2, 3), (
            treeB.Nodes.Single(node => node.Id == "A").Level,
            treeB.Nodes.Single(node => node.Id == "B").Level,
            treeB.Nodes.Single(node => node.Id == "C").Level));
    }

    [Fact]
    public void Semantic_node_resolver_rejects_gold_derived_input()
    {
        var input = new HdsaSemanticNodeResolutionInput("source", "snapshot", [], true);

        var exception = Assert.Throws<InvalidOperationException>(() => HdsaSemanticNodeResolver.Resolve(input));

        Assert.Contains("GOLD_FIREWALL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Semantic_node_resolver_is_deterministic_and_keeps_occurrences_source_backed()
    {
        var input = new HdsaSemanticNodeResolutionInput(
            "source-sha",
            "preprocessing-sha",
            [
                new("S0002", 2, "B", "style-b", "layout-b"),
                new("S0001", 1, "A", "style-a", "layout-a"),
            ]);

        var first = HdsaSemanticNodeResolver.Resolve(input);
        var second = HdsaSemanticNodeResolver.Resolve(input);

        Assert.Equal(first.SourceSha256, second.SourceSha256);
        Assert.Equal(first.PreprocessingSnapshotHash, second.PreprocessingSnapshotHash);
        Assert.Equal(first.ResolverVersion, second.ResolverVersion);
        Assert.Equal(first.GoldUsed, second.GoldUsed);
        Assert.Equal(first.Predictions.Count, second.Predictions.Count);
        for (var index = 0; index < first.Predictions.Count; index++)
        {
            var expected = first.Predictions[index];
            var actual = second.Predictions[index];
            Assert.Equal(expected.PredictedSemanticNodeId, actual.PredictedSemanticNodeId);
            Assert.Equal(expected.MemberOccurrenceIds, actual.MemberOccurrenceIds);
            Assert.Equal(expected.CanonicalText, actual.CanonicalText);
            Assert.Equal(expected.MergeEvidence, actual.MergeEvidence);
            Assert.Equal(expected.ResolverVersion, actual.ResolverVersion);
            Assert.Equal(expected.GoldUsed, actual.GoldUsed);
        }
        Assert.Equal(["S0001", "S0002"], first.Predictions.SelectMany(item => item.MemberOccurrenceIds));
        Assert.All(first.Predictions, item =>
        {
            Assert.Equal("IDENTITY_ONLY_NO_MERGE", item.MergeEvidence);
            Assert.Equal("identity-no-merge-v1", item.ResolverVersion);
            Assert.False(item.GoldUsed);
            Assert.Single(item.MemberOccurrenceIds);
        });
    }

    [Fact]
    public void Semantic_node_resolver_ignores_poison_legacy_hints()
    {
        var clean = new HdsaSemanticNodeResolutionInput(
            "source-sha", "preprocessing-sha",
            [new("S0001", 1, "A", "style", "layout")]);
        var poisoned = new HdsaSemanticNodeResolutionInput(
            "source-sha", "preprocessing-sha",
            [new("S0001", 1, "A", "style", "layout", ["level:99", "parent-node:BAD_PARENT", "role:BOGUS"])]);

        var cleanResult = HdsaSemanticNodeResolver.Resolve(clean);
        var poisonedResult = HdsaSemanticNodeResolver.Resolve(poisoned);

        Assert.Equal(cleanResult.Predictions.Count, poisonedResult.Predictions.Count);
        for (var index = 0; index < cleanResult.Predictions.Count; index++)
        {
            var expected = cleanResult.Predictions[index];
            var actual = poisonedResult.Predictions[index];
            Assert.Equal(expected.PredictedSemanticNodeId, actual.PredictedSemanticNodeId);
            Assert.Equal(expected.MemberOccurrenceIds, actual.MemberOccurrenceIds);
            Assert.Equal(expected.CanonicalText, actual.CanonicalText);
            Assert.Equal(expected.MergeEvidence, actual.MergeEvidence);
            Assert.Equal(expected.ResolverVersion, actual.ResolverVersion);
            Assert.Equal(expected.GoldUsed, actual.GoldUsed);
        }
        Assert.Equal(cleanResult.ResolverVersion, poisonedResult.ResolverVersion);
        Assert.False(poisonedResult.GoldUsed);
    }

    [Fact]
    public void Semantic_node_resolver_v2_merges_only_adjacent_exact_safe_equivalence()
    {
        var input = new HdsaSemanticNodeResolutionInput(
            "source-sha", "preprocessing-sha",
            [
                new("S0002", 2, "  Results\t", "heading-2", "section-a"),
                new("S0001", 1, "Results", "heading-2", "section-a"),
                new("S0003", 3, "Results", "heading-2", "section-b"),
            ]);

        var result = HdsaSemanticNodeResolverV2.Resolve(input);

        Assert.Equal("exact-safe-equivalence-v2", result.ResolverVersion);
        Assert.Equal(2, result.Predictions.Count);
        Assert.Equal(["S0001", "S0002"], result.Predictions[0].MemberOccurrenceIds);
        Assert.Equal("Results", result.Predictions[0].CanonicalText);
        Assert.Equal("NORMALIZED_TEXT+ADJACENT+EXACT_STYLE+EXACT_LAYOUT", result.Predictions[0].MergeEvidence);
        Assert.Equal(["S0003"], result.Predictions[1].MemberOccurrenceIds);
        Assert.Equal("IDENTITY_ONLY_NO_SAFE_MERGE", result.Predictions[1].MergeEvidence);
        Assert.False(result.GoldUsed);
    }

    [Fact]
    public void Semantic_node_resolver_v2_keeps_weak_or_nonadjacent_matches_split()
    {
        var input = new HdsaSemanticNodeResolutionInput(
            "source-sha", "preprocessing-sha",
            [
                new("S0001", 1, "Results", "heading-2", "section-a"),
                new("S0002", 3, "Results", "heading-2", "section-a"),
                new("S0003", 4, "Results", "heading-2", null),
                new("S0004", 5, "Results", null, "section-a"),
            ]);

        var result = HdsaSemanticNodeResolverV2.Resolve(input);

        Assert.Equal(4, result.Predictions.Count);
        Assert.All(result.Predictions, item => Assert.Single(item.MemberOccurrenceIds));
        Assert.All(result.Predictions, item => Assert.Equal("IDENTITY_ONLY_NO_SAFE_MERGE", item.MergeEvidence));
    }

    [Fact]
    public void Semantic_node_resolver_v2_rejects_gold_and_ignores_poison_hints()
    {
        var clean = new HdsaSemanticNodeResolutionInput(
            "source-sha", "preprocessing-sha",
            [new("S0001", 1, "Results", "heading-2", "section-a")]);
        var poisoned = new HdsaSemanticNodeResolutionInput(
            "source-sha", "preprocessing-sha",
            [new("S0001", 1, "Results", "heading-2", "section-a", ["level:99", "parent-node:BAD_PARENT"])]);

        var cleanResult = HdsaSemanticNodeResolverV2.Resolve(clean);
        var poisonedResult = HdsaSemanticNodeResolverV2.Resolve(poisoned);

        Assert.Equal(cleanResult.Predictions[0].PredictedSemanticNodeId, poisonedResult.Predictions[0].PredictedSemanticNodeId);
        Assert.Equal(cleanResult.Predictions[0].MemberOccurrenceIds, poisonedResult.Predictions[0].MemberOccurrenceIds);
        Assert.Equal(cleanResult.Predictions[0].CanonicalText, poisonedResult.Predictions[0].CanonicalText);
        Assert.Equal(cleanResult.Predictions[0].MergeEvidence, poisonedResult.Predictions[0].MergeEvidence);
        Assert.Throws<InvalidOperationException>(() => HdsaSemanticNodeResolverV2.Resolve(
            new HdsaSemanticNodeResolutionInput("source-sha", "snapshot", [], true)));
    }

    [Fact]
    public void Semantic_node_resolver_v3_requires_explicit_relation_evidence_and_keeps_relation_types_distinct()
    {
        var input = new HdsaSemanticNodeResolutionInput(
            "source-sha", "preprocessing-sha",
            [
                new("S0001", 1, "Results", "heading-2", "section-a"),
                new("S0002", 4, " Results ", "heading-2", "section-a"),
                new("S0003", 5, "Results continued", "heading-2", "section-a"),
                new("S0004", 6, "Results", "heading-2", "section-b"),
            ]);
        var relations = new HdsaSemanticIdentityRelationProposal[]
        {
            new("S0002", "S0001", HdsaSemanticIdentityRelationType.SameSemanticRepeat, "e-repeat", true),
            new("S0003", "S0002", HdsaSemanticIdentityRelationType.ContinuationOf, "e-continuation", true),
        };

        var result = HdsaSemanticNodeResolverV3.Resolve(input, relations);

        Assert.True(result.IsValid);
        Assert.Equal("repeat-continuation-evidence-v3", result.ResolverVersion);
        Assert.Equal(2, result.Predictions.Count);
        Assert.Equal(["S0001", "S0002", "S0003"], result.Predictions[0].MemberOccurrenceIds);
        Assert.Equal("EXPLICIT_RELATIONS:CONTINUATION_OF,SAME_SEMANTIC_REPEAT", result.Predictions[0].MergeEvidence);
        Assert.Equal(["S0004"], result.Predictions[1].MemberOccurrenceIds);
        Assert.Equal(2, result.AcceptedRelations.Count);
        Assert.Contains(result.AcceptedRelations, item => item.Relation == HdsaSemanticIdentityRelationType.SameSemanticRepeat);
        Assert.Contains(result.AcceptedRelations, item => item.Relation == HdsaSemanticIdentityRelationType.ContinuationOf);
    }

    [Fact]
    public void Semantic_node_resolver_v3_does_not_infer_distant_repeat_or_merge_from_weak_evidence()
    {
        var input = new HdsaSemanticNodeResolutionInput(
            "source-sha", "preprocessing-sha",
            [
                new("S0001", 1, "Results", "heading-2", "section-a"),
                new("S0002", 4, "Results", "heading-2", "section-a"),
                new("S0003", 5, "Results continued", "heading-2", "section-a"),
            ]);
        var relations = new HdsaSemanticIdentityRelationProposal[]
        {
            new("S0002", "S0001", HdsaSemanticIdentityRelationType.SameSemanticRepeat, null, true),
            new("S0003", "S0002", HdsaSemanticIdentityRelationType.ContinuationOf, "adjacency-only", false),
        };

        var result = HdsaSemanticNodeResolverV3.Resolve(input, relations);

        Assert.Equal(3, result.Predictions.Count);
        Assert.Empty(result.AcceptedRelations);
        Assert.False(result.IsValid);
        Assert.Contains(result.RejectedRelations, item => item.Reason == "MISSING_PARSER_EVIDENCE");
        Assert.Contains(result.RejectedRelations, item => item.Reason == "INCOMPATIBLE_STRUCTURAL_BOUNDARY");
    }

    [Fact]
    public void Semantic_node_resolver_v3_rejects_multiple_continuation_parents_and_cycles()
    {
        var input = new HdsaSemanticNodeResolutionInput(
            "source-sha", "preprocessing-sha",
            [
                new("S0001", 1, "A"),
                new("S0002", 2, "B"),
                new("S0003", 3, "C"),
            ]);
        var conflicting = new HdsaSemanticIdentityRelationProposal[]
        {
            new("S0003", "S0002", HdsaSemanticIdentityRelationType.ContinuationOf, "e1", true),
            new("S0003", "S0001", HdsaSemanticIdentityRelationType.ContinuationOf, "e2", true),
        };
        var cyclic = new HdsaSemanticIdentityRelationProposal[]
        {
            new("S0002", "S0001", HdsaSemanticIdentityRelationType.ContinuationOf, "e1", true),
            new("S0003", "S0002", HdsaSemanticIdentityRelationType.ContinuationOf, "e2", true),
            new("S0001", "S0003", HdsaSemanticIdentityRelationType.ContinuationOf, "e3", true),
        };

        var conflictResult = HdsaSemanticNodeResolverV3.Resolve(input, conflicting);
        var cycleResult = HdsaSemanticNodeResolverV3.Resolve(input, cyclic);

        Assert.Empty(conflictResult.AcceptedRelations);
        Assert.Contains(conflictResult.Errors, error => error == "MULTIPLE_CONTINUATION_PARENTS");
        Assert.Empty(cycleResult.AcceptedRelations);
        Assert.Contains(cycleResult.Errors, error => error == "CONTINUATION_CYCLE");
        Assert.Equal(3, cycleResult.Predictions.Count);
    }

    [Fact]
    public void Semantic_node_catalog_and_parent_pipeline_are_production_shaped_without_gold()
    {
        var input = new HdsaSemanticNodeResolutionInput(
            "source-sha", "preprocessing-sha",
            [
                new("S0001", 1, "Root", "h1", "scope-a"),
                new("S0002", 2, "Root continued", "h1", "scope-a"),
                new("S0003", 3, "Child", "h2", "scope-a"),
                new("S0004", 4, "Grandchild", "h3", "scope-a"),
            ]);
        var identity = HdsaSemanticNodeResolverV3.Resolve(input, [
            new("S0002", "S0001", HdsaSemanticIdentityRelationType.ContinuationOf, "e-continuation", true),
        ]);
        var catalog = HdsaSemanticNodeCatalogBuilder.Build(input, identity);
        var result = HdsaSemanticHierarchyPipeline.Run(catalog, request =>
        {
            if (request.ChildSemanticNodeId == catalog.Entries[0].SemanticNodeId)
                return new(request.CatalogFingerprint, request.ChildSemanticNodeId, HdsaParentDecision.Root);
            return new(request.CatalogFingerprint, request.ChildSemanticNodeId,
                HdsaParentDecision.SelectParent, request.CandidateParentSemanticNodeIds[0]);
        });

        Assert.Equal(3, catalog.Entries.Count);
        Assert.True(catalog.ProductionBenchmark);
        Assert.False(catalog.GoldUsed);
        Assert.False(result.CatalogWasMutated);
        Assert.Equal(catalog.CatalogFingerprint, result.CatalogFingerprintAfterParentReasoning);
        Assert.Equal(3, result.DecisionValidations.Count(item => item.Accepted));
        Assert.True(result.Tree.IsValid);
        Assert.Equal((1, 2, 3), (
            result.Tree.Nodes[0].Level,
            result.Tree.Nodes[1].Level,
            result.Tree.Nodes[2].Level));
        Assert.True(result.LevelDerivedFromTreeDepth);
        Assert.False(result.GoldReadBeforePrediction);
        Assert.False(result.LegacyHierarchyConsumed);
        Assert.Equal(0, result.ModelCalls);
        Assert.Equal(0, result.ProviderCalls);
    }

    [Fact]
    public void Semantic_node_catalog_fingerprint_is_deterministic_and_poison_resistant()
    {
        var clean = new HdsaSemanticNodeResolutionInput(
            "source-sha", "preprocessing-sha",
            [
                new("S0002", 2, "Child", "h2", "scope"),
                new("S0001", 1, "Root", "h1", "scope"),
            ]);
        var poisoned = new HdsaSemanticNodeResolutionInput(
            "source-sha", "preprocessing-sha",
            [
                new("S0002", 2, "Child", "h2", "scope", ["level:99", "parent-node:BAD"]),
                new("S0001", 1, "Root", "h1", "scope", ["role:BOGUS"]),
            ]);
        var cleanResult = HdsaSemanticNodeResolverV3.Resolve(clean, []);
        var poisonedResult = HdsaSemanticNodeResolverV3.Resolve(poisoned, []);
        var cleanCatalog = HdsaSemanticNodeCatalogBuilder.Build(clean, cleanResult);
        var poisonedCatalog = HdsaSemanticNodeCatalogBuilder.Build(poisoned, poisonedResult);

        Assert.Equal(cleanCatalog.CatalogFingerprint, poisonedCatalog.CatalogFingerprint);
        Assert.Equal(
            cleanCatalog.Entries.Select(item => item.SemanticNodeId),
            poisonedCatalog.Entries.Select(item => item.SemanticNodeId));
        var cleanRequest = HdsaSemanticNodeParentReasoningContract.CreateRequest(cleanCatalog, cleanCatalog.Entries[1].SemanticNodeId);
        var poisonedRequest = HdsaSemanticNodeParentReasoningContract.CreateRequest(poisonedCatalog, poisonedCatalog.Entries[1].SemanticNodeId);
        Assert.Equal(cleanRequest.CandidateParentSemanticNodeIds, poisonedRequest.CandidateParentSemanticNodeIds);
        Assert.Equal(cleanRequest.AuthoritativeParentUniverse.Select(item => item.SemanticNodeId), poisonedRequest.AuthoritativeParentUniverse.Select(item => item.SemanticNodeId));
    }

    [Fact]
    public void Semantic_node_parent_contract_rejects_out_of_candidate_and_accepts_unresolved_without_mutation()
    {
        var input = new HdsaSemanticNodeResolutionInput(
            "source-sha", "snapshot",
            [new("S0001", 1, "A"), new("S0002", 2, "B"), new("S0003", 3, "C")]);
        var identity = HdsaSemanticNodeResolverV3.Resolve(input, []);
        var catalog = HdsaSemanticNodeCatalogBuilder.Build(input, identity);
        var request = HdsaSemanticNodeParentReasoningContract.CreateRequest(catalog, catalog.Entries[2].SemanticNodeId, window: 1);
        var outside = new HdsaSemanticNodeParentDecision(
            catalog.CatalogFingerprint,
            request.ChildSemanticNodeId,
            HdsaParentDecision.SelectParent,
            request.AuthoritativeParentUniverse[0].SemanticNodeId);
        var unresolved = new HdsaSemanticNodeParentDecision(
            catalog.CatalogFingerprint,
            request.ChildSemanticNodeId,
            HdsaParentDecision.Unresolved);

        var outsideValidation = HdsaSemanticNodeParentReasoningContract.Validate(request, outside, catalog);
        var unresolvedValidation = HdsaSemanticNodeParentReasoningContract.Validate(request, unresolved, catalog);

        Assert.False(outsideValidation.Accepted);
        Assert.Equal("PARENT_OUTSIDE_ATTENTION_CANDIDATES", outsideValidation.RejectionReason);
        Assert.True(outsideValidation.ParentWasOutsideAttentionHints);
        Assert.True(unresolvedValidation.Accepted);
        Assert.Null(unresolved.ParentSemanticNodeId);
    }

    [Fact]
    public void Semantic_node_parent_contract_rejects_catalog_mutation_and_gold_derived_inputs()
    {
        var input = new HdsaSemanticNodeResolutionInput(
            "source-sha", "snapshot", [new("S0001", 1, "A"), new("S0002", 2, "B")]);
        var identity = HdsaSemanticNodeResolverV3.Resolve(input, []);
        var catalog = HdsaSemanticNodeCatalogBuilder.Build(input, identity);
        var request = HdsaSemanticNodeParentReasoningContract.CreateRequest(catalog, catalog.Entries[1].SemanticNodeId);
        var mutated = new HdsaSemanticNodeParentDecision(
            "different-catalog", request.ChildSemanticNodeId, HdsaParentDecision.Root);
        var goldDecision = new HdsaSemanticNodeParentDecision(
            catalog.CatalogFingerprint, request.ChildSemanticNodeId, HdsaParentDecision.Root);
        var goldRequest = request with { GoldDerivedInput = true };

        Assert.Equal("DECISION_CATALOG_FINGERPRINT_MISMATCH", HdsaSemanticNodeParentReasoningContract.Validate(request, mutated, catalog).RejectionReason);
        Assert.Equal("CATALOG_FINGERPRINT_MISMATCH_OR_GOLD_INPUT", HdsaSemanticNodeParentReasoningContract.Validate(goldRequest, goldDecision, catalog).RejectionReason);
    }

    [Fact]
    public void Semantic_node_parent_contract_is_catalog_bound_and_has_no_level_output()
    {
        var schema = JsonSerializer.Serialize(HdsaSemanticNodeParentReasoningContract.Schema());
        var decision = HdsaSemanticNodeParentReasoningContract.Parse(
            "{\"catalogFingerprint\":\"fp\",\"childSemanticNodeId\":\"N2\",\"decision\":\"ROOT\",\"parentSemanticNodeId\":null}");

        Assert.DoesNotContain("level", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("depth", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("fp", decision.CatalogFingerprint);
        Assert.Equal("N2", decision.ChildSemanticNodeId);
        Assert.Equal(HdsaParentDecision.Root, decision.Decision);
        Assert.Null(decision.ParentSemanticNodeId);
    }
}
