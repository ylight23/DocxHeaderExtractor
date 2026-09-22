using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The Stage-1 membership contract: what a reply may say, and what the harness refuses.
/// <para>
/// The grounding rules are v2's and are not restated here - the model names occurrences and quotes
/// words, the harness derives the mode and the offsets. What is new is that the reply has no field
/// for a role, a relation or a selection mode, so the model is never told a placement question
/// exists and no placement value can admit a claim.
/// </para>
/// </summary>
public sealed class SemanticMembershipContractV1Tests
{
    private const string SourceSha256 = "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";

    // ---- the schema -------------------------------------------------------------------------

    [Fact]
    public void The_schema_offers_no_role_no_relation_and_no_coordinate_mode()
    {
        var schema = JsonSerializer.Serialize(SemanticMembershipContractV1.Schema());

        foreach (var absent in new[]
        {
            "semanticRole", "relationHints", "parent-node", "parentClaimId", "selectionMode",
            "ROOT", "NONE", "hierarchy", "start", "end", "offset",
        })
        {
            Assert.DoesNotContain(absent, schema, StringComparison.Ordinal);
        }

        Assert.Contains("DOCUMENT_LABEL", schema, StringComparison.Ordinal);
        Assert.Contains("STRUCTURAL_UNIT", schema, StringComparison.Ordinal);
        Assert.Equal("a99-semantic-membership-v1", SemanticMembershipContractV1.ProtocolVersion);
    }

    [Fact]
    public void The_predecessors_keep_their_schema_hashes()
    {
        Assert.Equal("69b99b9099b964a5cf5985b8ec618db49c8ee5c3fa8a2bb8f69993cdc2e24f6f",
            SemanticCoordinateContract.PdfStructuredSourceParts.SchemaHash());
        Assert.Equal("565bdc87749a1ce1246238cacd1eb7d550a19939dc9e42d23e76ab3a46a8b0ea",
            SemanticCoordinateContract.PdfStructuredSourcePartsV2.SchemaHash());
        Assert.NotEqual(
            SemanticCoordinateContract.PdfStructuredSourcePartsV2.SchemaHash(),
            SemanticCoordinateContract.PdfSemanticMembershipV1.SchemaHash());
    }

    // ---- accepted shapes --------------------------------------------------------------------

    [Fact]
    public void An_alias_alone_claims_the_whole_occurrence()
    {
        var accepted = Accept(Atoms("Chapter One"), """
            {"claims":[{"membership":"STRUCTURAL_UNIT","sourceParts":[{"sourceAlias":"L0000:S0"}]}]}
            """);

        var claim = Assert.Single(accepted.Accepted);
        Assert.Equal("L0000:S0:0-11", claim.CanonicalIdentity);
        Assert.Equal(Stage1MembershipDisposition.StructuralUnit, claim.Disposition);
    }

    [Fact]
    public void A_quote_that_is_part_of_an_occurrence_claims_only_that_part()
    {
        var accepted = Accept(Atoms("Chapter One. The rest follows here."), """
            {"claims":[{"membership":"STRUCTURAL_UNIT","sourceParts":[
              {"sourceAlias":"L0000:S0","verbatimText":"Chapter One"}]}]}
            """);

        Assert.Equal("L0000:S0:0-11", Assert.Single(accepted.Accepted).CanonicalIdentity);
    }

    [Fact]
    public void A_quote_equal_to_the_whole_occurrence_is_still_the_whole_occurrence()
    {
        // The shape that cost four approved headings under v1. It binds here, as it does under v2.
        var accepted = Accept(Atoms("Chapter One"), """
            {"claims":[{"membership":"STRUCTURAL_UNIT","sourceParts":[
              {"sourceAlias":"L0000:S0","verbatimText":"Chapter One"}]}]}
            """);

        Assert.Equal("L0000:S0:0-11", Assert.Single(accepted.Accepted).CanonicalIdentity);
        Assert.Empty(accepted.Refusals);
    }

    [Fact]
    public void A_document_label_is_accepted_without_any_placement()
    {
        var accepted = Accept(Atoms("Hybrid Meeting"), """
            {"claims":[{"membership":"DOCUMENT_LABEL","sourceParts":[{"sourceAlias":"L0000:S0"}]}]}
            """);

        var claim = Assert.Single(accepted.Accepted);
        Assert.Equal(Stage1MembershipDisposition.DocumentLabel, claim.Disposition);
        Assert.DoesNotContain("NONE", JsonSerializer.Serialize(claim), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_multi_part_claim_is_one_claim_with_one_authority_id()
    {
        var atoms = Atoms("A heading that begins here", "and finishes on this row");
        var accepted = Accept(atoms, """
            {"claims":[{"membership":"STRUCTURAL_UNIT","sourceParts":[
              {"sourceAlias":"L0000:S0"},{"sourceAlias":"L0001:S0"}]}]}
            """);

        var claim = Assert.Single(accepted.Accepted);
        Assert.Equal(2, claim.Coordinates.Count);
        Assert.Equal(["L0000:S0", "L0001:S0"], claim.Coordinates.Select(item => item.Alias));
        Assert.Equal(Stage1AuthorityClaimId.For(SourceSha256, claim.Coordinates).Value, claim.ClaimId.Value);

        // Order is part of the identity, not a rendering detail.
        Assert.NotEqual(
            claim.ClaimId.Value,
            Stage1AuthorityClaimId.For(SourceSha256, [.. claim.Coordinates.Reverse()]).Value);
    }

    // ---- fail closed ------------------------------------------------------------------------

    [Fact]
    public void An_unknown_alias_is_refused()
    {
        var accepted = Accept(Atoms("Chapter One"), """
            {"claims":[{"membership":"STRUCTURAL_UNIT","sourceParts":[{"sourceAlias":"L9999:S0"}]}]}
            """);

        Assert.Empty(accepted.Accepted);
        Assert.Equal("UnknownAlias", Assert.Single(accepted.Refusals).Reason);
    }

    [Fact]
    public void A_quote_that_is_not_in_the_named_occurrence_is_refused_not_widened()
    {
        var accepted = Accept(Atoms("Chapter One"), """
            {"claims":[{"membership":"STRUCTURAL_UNIT","sourceParts":[
              {"sourceAlias":"L0000:S0","verbatimText":"Chapter Two"}]}]}
            """);

        Assert.Empty(accepted.Accepted);
        Assert.Equal("TextNotInAtom", Assert.Single(accepted.Refusals).Reason);
    }

    [Fact]
    public void An_ambiguous_quote_is_refused_unless_the_claim_says_which()
    {
        var atoms = Atoms("Africa Gregoire and the Development Bank in Africa");

        var ambiguous = Accept(atoms, """
            {"claims":[{"membership":"STRUCTURAL_UNIT","sourceParts":[
              {"sourceAlias":"L0000:S0","verbatimText":"Africa"}]}]}
            """);
        Assert.Equal("AmbiguousSelection", Assert.Single(ambiguous.Refusals).Reason);

        var resolved = Accept(atoms, """
            {"claims":[{"membership":"STRUCTURAL_UNIT","sourceParts":[
              {"sourceAlias":"L0000:S0","verbatimText":"Africa","occurrence":2}]}]}
            """);
        Assert.Equal("L0000:S0:44-50", Assert.Single(resolved.Accepted).CanonicalIdentity);
    }

    [Fact]
    public void An_out_of_order_multi_part_claim_is_refused()
    {
        var atoms = Atoms("A heading that begins here", "and finishes on this row");
        var accepted = Accept(atoms, """
            {"claims":[{"membership":"STRUCTURAL_UNIT","sourceParts":[
              {"sourceAlias":"L0001:S0"},{"sourceAlias":"L0000:S0"}]}]}
            """);

        Assert.Empty(accepted.Accepted);
        Assert.Single(accepted.Refusals);
    }

    [Fact]
    public void A_duplicated_part_is_refused()
    {
        var accepted = Accept(Atoms("Chapter One"), """
            {"claims":[{"membership":"STRUCTURAL_UNIT","sourceParts":[
              {"sourceAlias":"L0000:S0"},{"sourceAlias":"L0000:S0"}]}]}
            """);

        Assert.Empty(accepted.Accepted);
        Assert.Single(accepted.Refusals);
    }

    [Fact]
    public void A_claim_outside_the_owned_segment_is_refused()
    {
        var atoms = Atoms("Chapter One", "Context only");
        using var reply = JsonDocument.Parse("""
            {"claims":[{"membership":"STRUCTURAL_UNIT","sourceParts":[{"sourceAlias":"L0001:S0"}]}]}
            """);

        var accepted = SemanticMembershipV1.Accept(
            atoms, SourceSha256, SemanticMembershipV1.Decode(reply.RootElement),
            new HashSet<string>(StringComparer.Ordinal) { "L0000:S0" });

        Assert.Empty(accepted.Accepted);
        Assert.Equal("OutOfOwnedSegment", Assert.Single(accepted.Refusals).Reason);
    }

    [Fact]
    public void A_missing_or_invalid_membership_is_refused_by_the_validator_and_the_decoder()
    {
        foreach (var payload in new[]
        {
            """{"claims":[{"sourceParts":[{"sourceAlias":"L0000:S0"}]}]}""",
            """{"claims":[{"membership":"NONE","sourceParts":[{"sourceAlias":"L0000:S0"}]}]}""",
            """{"claims":[{"membership":"ROOT","sourceParts":[{"sourceAlias":"L0000:S0"}]}]}""",
        })
        {
            using var reply = JsonDocument.Parse(payload);
            Assert.NotEmpty(SemanticMembershipV1.ValidateJson(reply.RootElement));
            Assert.Empty(SemanticMembershipV1.Decode(reply.RootElement).Claims);
        }
    }

    [Fact]
    public void A_reply_in_another_contracts_shape_is_reported_rather_than_read_as_empty()
    {
        // A v2 reply read by this validator: the envelope is wrong, and saying so is the point -
        // an empty decode is indistinguishable from a model that accepted nothing.
        using var v2Reply = JsonDocument.Parse("""
            {"headings":[{"isHeading":true,"sourceParts":[{"sourceAlias":"L0000:S0"}]}]}
            """);

        var issues = SemanticMembershipV1.ValidateJson(v2Reply.RootElement);
        Assert.Equal("MISSING_CLAIMS", Assert.Single(issues).Code);
    }

    [Fact]
    public void A_field_this_contract_never_offered_is_named_rather_than_ignored()
    {
        using var reply = JsonDocument.Parse("""
            {"claims":[{"membership":"STRUCTURAL_UNIT","semanticRole":"section",
              "relationHints":["parent-node:ROOT"],"sourceParts":[{"sourceAlias":"L0000:S0"}]}]}
            """);

        var issues = SemanticMembershipV1.ValidateJson(reply.RootElement);
        Assert.Equal(2, issues.Count);
        Assert.All(issues, issue => Assert.Equal("FIELD_NOT_IN_CONTRACT", issue.Code));
    }

    // ---- the seam ---------------------------------------------------------------------------

    [Fact]
    public void Accepted_claims_flow_into_the_validated_stage1_evaluator()
    {
        var accepted = Accept(Atoms("Chapter One", "Chapter Two"), """
            {"claims":[
              {"membership":"STRUCTURAL_UNIT","sourceParts":[{"sourceAlias":"L0000:S0"}]},
              {"membership":"STRUCTURAL_UNIT","sourceParts":[{"sourceAlias":"L0001:S0"}]}]}
            """);

        var gold = new HashSet<string>(StringComparer.Ordinal) { "L0000:S0:0-11" };
        var score = Stage1MembershipEvaluator.Score(accepted.Accepted, gold, gold);

        Assert.Equal(1, score.TruePositive);
        Assert.Equal(1, score.FalsePositive);
        Assert.Equal(0, score.FalseNegative);
    }

    [Fact]
    public void The_contract_routes_through_the_same_binder_as_the_structured_contract()
    {
        Assert.Equal("SEMANTIC_MEMBERSHIP_V1",
            SemanticCoordinateContract.PdfSemanticMembershipV1.Binding.BindingId);
        Assert.Equal("STRUCTURED_SOURCE_PART_TUPLE",
            SemanticCoordinateContract.PdfSemanticMembershipV1.CoordinateSystem);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static SemanticMembershipAcceptance Accept(SemanticSourceAtom[] atoms, string payload)
    {
        using var reply = JsonDocument.Parse(payload);
        Assert.Empty(SemanticMembershipV1.ValidateJson(reply.RootElement));
        return SemanticMembershipV1.Accept(atoms, SourceSha256, SemanticMembershipV1.Decode(reply.RootElement));
    }

    private static SemanticSourceAtom[] Atoms(params string[] texts) =>
        texts.Select((text, index) => new SemanticSourceAtom(
            $"L{index:0000}:S0", $"atom-{index}", index, 1, index, 0, text)).ToArray();
}
