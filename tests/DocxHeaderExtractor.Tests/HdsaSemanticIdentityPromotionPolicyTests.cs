using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class HdsaSemanticIdentityPromotionPolicyTests
{
    [Theory]
    [InlineData(HdsaSemanticIdentityInferenceDecision.SameSemanticRepeat)]
    [InlineData(HdsaSemanticIdentityInferenceDecision.ContinuationOf)]
    public void Model_positive_is_proposed_but_never_authorizes_collapse(HdsaSemanticIdentityInferenceDecision decision)
    {
        var result = HdsaSemanticIdentityPromotionPolicy.Evaluate(decision, "MODEL", "model-evidence", true);

        Assert.Equal(HdsaSemanticIdentityPromotionAction.ModelProposed, result.Action);
        Assert.False(result.CollapseAuthorized);
        Assert.Equal("MODEL_ONLY_RELATION_REQUIRES_CORROBORATION", result.Reason);
    }

    [Fact]
    public void Parser_positive_with_explicit_compatible_evidence_may_collapse()
    {
        var result = HdsaSemanticIdentityPromotionPolicy.Evaluate(
            HdsaSemanticIdentityInferenceDecision.ContinuationOf, "PARSER", "parser-evidence", true);

        Assert.Equal(HdsaSemanticIdentityPromotionAction.AcceptedForIdentityCollapse, result.Action);
        Assert.True(result.CollapseAuthorized);
    }

    [Theory]
    [InlineData(HdsaSemanticIdentityInferenceDecision.Distinct)]
    [InlineData(HdsaSemanticIdentityInferenceDecision.Unresolved)]
    public void Distinct_and_unresolved_keep_split(HdsaSemanticIdentityInferenceDecision decision)
    {
        var result = HdsaSemanticIdentityPromotionPolicy.Evaluate(decision, "MODEL", null, false);

        Assert.Equal(HdsaSemanticIdentityPromotionAction.KeepSplit, result.Action);
        Assert.False(result.CollapseAuthorized);
    }

    [Fact]
    public void Parser_positive_without_safe_evidence_keeps_split()
    {
        var missingEvidence = HdsaSemanticIdentityPromotionPolicy.Evaluate(
            HdsaSemanticIdentityInferenceDecision.SameSemanticRepeat, "PARSER", null, true);
        var incompatible = HdsaSemanticIdentityPromotionPolicy.Evaluate(
            HdsaSemanticIdentityInferenceDecision.SameSemanticRepeat, "PARSER", "evidence", false);

        Assert.Equal(HdsaSemanticIdentityPromotionAction.KeepSplit, missingEvidence.Action);
        Assert.Equal(HdsaSemanticIdentityPromotionAction.KeepSplit, incompatible.Action);
        Assert.All(new[] { missingEvidence, incompatible }, result => Assert.False(result.CollapseAuthorized));
    }

    [Fact]
    public void Gold_and_legacy_provenance_never_authorize_promotion()
    {
        var gold = HdsaSemanticIdentityPromotionPolicy.Evaluate(
            HdsaSemanticIdentityInferenceDecision.SameSemanticRepeat, "PARSER", "evidence", true, goldUsed: true);
        var legacy = HdsaSemanticIdentityPromotionPolicy.Evaluate(
            HdsaSemanticIdentityInferenceDecision.ContinuationOf, "MODEL", "evidence", true, legacyUsed: true);

        Assert.All(new[] { gold, legacy }, result =>
        {
            Assert.Equal(HdsaSemanticIdentityPromotionAction.KeepSplit, result.Action);
            Assert.False(result.CollapseAuthorized);
        });
    }
}
