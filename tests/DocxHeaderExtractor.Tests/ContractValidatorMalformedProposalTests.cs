using DocxHeaderExtractor.Core.Models;
using Xunit;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The contract validator exists to reject malformed model output. It must return issues for any
/// shape a provider can emit, never throw: an exception here aborts the whole document instead of
/// dropping one bad proposal.
/// </summary>
public class ContractValidatorMalformedProposalTests
{
    private static IReadOnlyDictionary<string, SemanticSourceAlias> Aliases(params string[] texts) =>
        texts.Select((text, index) => new SemanticSourceAlias(
                $"S{index + 1:0000}", $"p{index + 1}", index + 1, text, new StructuralSpan(0, text.Length)))
            .ToDictionary(item => item.Alias, StringComparer.Ordinal);

    [Fact]
    public void Multiple_aliases_with_a_single_verbatim_text_is_rejected_not_thrown()
    {
        var proposal = new CanonicalSemanticProposal(
            "S0001", true, "Framework Agreement",
            SourceAliases: ["S0001", "S0002"]);

        var issues = CanonicalSemanticContractValidator.Validate(
            proposal, Aliases("Framework Agreement", "and Consulting"));

        Assert.Contains(issues, issue => issue.Code == "COMPOSITE_MAPPING_MISMATCH");
    }

    [Fact]
    public void Composite_with_matching_cardinality_is_accepted()
    {
        var proposal = new CanonicalSemanticProposal(
            "S0001", true, "Framework Agreement",
            VerbatimParts: ["Framework Agreement", "and Consulting"],
            SourceAliases: ["S0001", "S0002"]);

        var issues = CanonicalSemanticContractValidator.Validate(
            proposal, Aliases("Framework Agreement", "and Consulting"));

        Assert.Empty(issues);
    }

    [Fact]
    public void Heading_without_any_verbatim_text_is_rejected_not_thrown()
    {
        var proposal = new CanonicalSemanticProposal("S0001", true, null);

        var issues = CanonicalSemanticContractValidator.Validate(proposal, Aliases("Some heading"));

        Assert.Contains(issues, issue => issue.Code == "MISSING_VERBATIM_TEXT");
    }

    [Fact]
    public void Single_alias_with_one_verbatim_text_still_binds()
    {
        var proposal = new CanonicalSemanticProposal("S0001", true, "Some heading");

        var issues = CanonicalSemanticContractValidator.Validate(proposal, Aliases("Some heading"));

        Assert.Empty(issues);
    }

    [Fact]
    public void Whole_alias_selection_needs_no_verbatim_text_and_rejects_multiple_aliases()
    {
        var accepted = new CanonicalSemanticProposal(
            "S0001", true, null, SelectionMode: CanonicalSemanticSelectionMode.WholeAlias);
        Assert.Empty(CanonicalSemanticContractValidator.Validate(accepted, Aliases("Some heading")));

        var rejected = new CanonicalSemanticProposal(
            "S0001", true, null,
            SourceAliases: ["S0001", "S0002"],
            SelectionMode: CanonicalSemanticSelectionMode.WholeAlias);
        Assert.Contains(
            CanonicalSemanticContractValidator.Validate(rejected, Aliases("A", "B")),
            issue => issue.Code == "WHOLE_ALIAS_REQUIRES_ONE_ALIAS");
    }
}
