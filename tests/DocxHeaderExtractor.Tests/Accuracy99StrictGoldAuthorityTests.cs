using DocxHeaderExtractor.Eval.Accuracy99;

namespace DocxHeaderExtractor.Tests;

public sealed class Accuracy99StrictGoldAuthorityTests
{
    [Fact]
    public void V3_policy_preserves_truthful_provenance_without_disqualifying_user_finalization()
    {
        Assert.Equal("USER_FINALIZED_STRICT_GOLD_V3", A99StrictGoldAuthorityRules.UserFinalizedStrictGoldV3);
        Assert.Equal("SOURCE_STRUCTURAL", A99StrictGoldAuthorityRules.SourceStructuralProvenance);
        Assert.Equal("DETERMINISTIC_GENERATED", A99StrictGoldAuthorityRules.DeterministicGeneratedProvenance);
    }

    [Fact]
    public void Assisted_schema_valid_reference_is_strictly_eligible_after_user_finalization()
    {
        Assert.True(A99StrictGoldAuthorityRules.IsEligible(
            "VALID",
            A99StrictGoldAuthorityRules.HumanReviewedModelAssisted,
            eligibleForStrictA99Claim: true));
    }

    [Fact]
    public void Strict_claim_requires_explicit_authority_and_validator_status()
    {
        Assert.False(A99StrictGoldAuthorityRules.IsEligible("MISSING", A99StrictGoldAuthorityRules.StrictHumanGold, true));
        Assert.False(A99StrictGoldAuthorityRules.IsEligible("VALID", A99StrictGoldAuthorityRules.NotReviewed, true));
        Assert.True(A99StrictGoldAuthorityRules.IsEligible("VALID", A99StrictGoldAuthorityRules.StrictHumanGold, true));
    }

    [Fact]
    public void Unknown_authority_and_missing_finalization_stay_non_strict()
    {
        Assert.False(A99StrictGoldAuthorityRules.IsEligible("VALID", "UNKNOWN", true));
        Assert.False(A99StrictGoldAuthorityRules.IsEligible("VALID", A99StrictGoldAuthorityRules.HumanReviewedModelAssisted, false));
    }

    [Fact]
    public void Canonical_eligibility_requires_user_final_authority_and_complete_review()
    {
        var entry = new A99StrictGoldAuthorityEntry
        {
            DocumentId = "DOC-TEST",
            ValidatorStatus = "VALID",
            ReferenceAuthority = A99StrictGoldAuthorityRules.UserFinalizedStrictGold,
            EligibleForStrictA99Claim = true,
            ExposureStatus = "MODEL_ASSISTED",
            GoldStatus = A99StrictGoldAuthorityRules.StrictGold,
            FinalAuthority = A99StrictGoldAuthorityRules.UserFinalAuthority,
            ReferenceProvenance = A99StrictGoldAuthorityRules.HumanWithModelAssistanceProvenance,
            UserFinalApproval = true,
            ReviewedEntireDocument = true,
            HeadingSetExhaustive = true,
        };

        Assert.True(A99StrictGoldAuthorityRules.IsEligible(entry, referenceValidated: true));
        Assert.False(A99StrictGoldAuthorityRules.IsEligible(entry with { UserFinalApproval = false }, referenceValidated: true));
        Assert.False(A99StrictGoldAuthorityRules.IsEligible(entry with { UnresolvedSemanticUncertainty = true }, referenceValidated: true));
    }

    [Fact]
    public void Image_only_strict_gold_can_be_semantic_gold_without_span_capability()
    {
        var entry = new A99StrictGoldAuthorityEntry
        {
            DocumentId = "DOC-0202",
            ValidatorStatus = "VALID",
            ReferenceAuthority = A99StrictGoldAuthorityRules.UserFinalizedStrictGold,
            EligibleForStrictA99Claim = true,
            ExposureStatus = "MODEL_ASSISTED",
            GoldStatus = A99StrictGoldAuthorityRules.StrictGold,
            FinalAuthority = A99StrictGoldAuthorityRules.UserFinalAuthority,
            ReferenceProvenance = A99StrictGoldAuthorityRules.HumanWithModelAssistanceProvenance,
            UserFinalApproval = true,
            ReviewedEntireDocument = true,
            HeadingSetExhaustive = true,
            SemanticEvaluable = true,
            CharacterSpanEvaluable = false,
        };

        Assert.True(A99StrictGoldAuthorityRules.IsEligible(entry, referenceValidated: true));
        Assert.True(entry.SemanticEvaluable);
        Assert.False(entry.CharacterSpanEvaluable);
    }
}
