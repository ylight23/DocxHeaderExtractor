using DocxHeaderExtractor.Eval.Accuracy99;

namespace DocxHeaderExtractor.Tests;

public sealed class Accuracy99StrictGoldAuthorityTests
{
    [Fact]
    public void Assisted_schema_valid_reference_is_not_strictly_eligible()
    {
        Assert.False(A99StrictGoldAuthorityRules.IsEligible(
            "VALID",
            A99StrictGoldAuthorityRules.HumanReviewedModelAssisted,
            eligibleForStrictA99Claim: false));
    }

    [Fact]
    public void Strict_claim_requires_explicit_authority_and_validator_status()
    {
        Assert.False(A99StrictGoldAuthorityRules.IsEligible("MISSING", A99StrictGoldAuthorityRules.StrictHumanGold, true));
        Assert.False(A99StrictGoldAuthorityRules.IsEligible("VALID", A99StrictGoldAuthorityRules.NotReviewed, true));
        Assert.True(A99StrictGoldAuthorityRules.IsEligible("VALID", A99StrictGoldAuthorityRules.StrictHumanGold, true));
    }

    [Fact]
    public void Unknown_or_assisted_authority_cannot_become_strict_by_schema_validity_alone()
    {
        Assert.False(A99StrictGoldAuthorityRules.IsEligible("VALID", "UNKNOWN", true));
        Assert.False(A99StrictGoldAuthorityRules.IsEligible("VALID", A99StrictGoldAuthorityRules.HumanReviewedModelAssisted, true));
    }
}
