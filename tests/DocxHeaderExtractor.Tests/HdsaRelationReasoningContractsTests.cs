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
            "child", [], new("order=1", null, null, null, null));

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
            "child", ["parent"], new("order=2", null, null, null, null));
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
            new("E", candidates, new("order=5", null, null, null, null))));
        Assert.Empty(HdsaParentAttentionCandidates.PrimaryPreceding(ids, "missing"));
    }
}
