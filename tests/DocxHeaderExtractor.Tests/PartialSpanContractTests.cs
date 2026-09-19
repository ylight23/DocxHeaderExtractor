using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// I8. What "a heading may be part of an occurrence" is allowed to mean.
/// <para>
/// The binder already accepts an exact substring, so the permission being added is an instruction,
/// not a capability. These fence it: a substring must be exact and contiguous, it must come from an
/// alias the segment owns, and it must never become licence to normalize, repair or reassemble
/// text. The model still returns no coordinate - the harness finds the substring itself.
/// </para>
/// </summary>
public sealed class PartialSpanContractTests
{
    private static IReadOnlyDictionary<string, SemanticSourceAlias> Aliases(params string[] texts) =>
        texts.Select((text, index) => new SemanticSourceAlias(
                $"S{index + 1:0000}", $"b{index + 1}", index + 1, text, new StructuralSpan(0, text.Length)))
            .ToDictionary(item => item.Alias, StringComparer.Ordinal);

    private static IReadOnlyList<SemanticContractIssue> Validate(
        string alias, string verbatim, IReadOnlyDictionary<string, SemanticSourceAlias> aliases,
        IReadOnlySet<string>? owned = null) =>
        CanonicalSemanticContractValidator.Validate(
            new CanonicalSemanticProposal(alias, true, verbatim), aliases, owned);

    [Fact]
    public void A_heading_that_is_the_whole_occurrence_is_still_valid()
    {
        var aliases = Aliases("Session I: Welcome and meeting objectives");

        Assert.Empty(Validate("S0001", "Session I: Welcome and meeting objectives", aliases));
    }

    [Fact]
    public void A_prefix_substring_is_valid()
    {
        var aliases = Aliases("Africa Gregoire Mboya de Loubassou, Asian Development Bank");

        Assert.Empty(Validate("S0001", "Africa", aliases));
    }

    [Fact]
    public void A_substring_that_repeats_inside_the_occurrence_must_say_which_one()
    {
        // Found while writing these: the real DOC-0256 occurrence is
        // "Africa Gregoire Mboya de Loubassou, African Development Bank", and "Africa" occurs
        // twice - once alone, once inside "African". The contract already refuses to guess, so
        // permission to return a partial span is useless without telling the model how to
        // disambiguate. The clause now does.
        var aliases = Aliases("Africa Gregoire Mboya de Loubassou, African Development Bank");

        Assert.Contains(Validate("S0001", "Africa", aliases),
            issue => issue.Code == "AMBIGUOUS_BINDING");
        Assert.Empty(CanonicalSemanticContractValidator.Validate(
            new CanonicalSemanticProposal("S0001", true, "Africa", Occurrence: 1), aliases));
        Assert.Empty(CanonicalSemanticContractValidator.Validate(
            new CanonicalSemanticProposal("S0001", true, "Africa", RightExactContext: " Gregoire"), aliases));
    }

    [Fact]
    public void An_infix_substring_is_valid()
    {
        var aliases = Aliases("See Session IV: TAG Functioning below for details");

        Assert.Empty(Validate("S0001", "Session IV: TAG Functioning", aliases));
    }

    [Fact]
    public void Text_that_does_not_occur_in_the_source_is_rejected()
    {
        var aliases = Aliases("Session I: Welcome and meeting objectives");

        Assert.Contains(Validate("S0001", "Session II", aliases),
            issue => issue.Code == "NON_VERBATIM_TEXT");
    }

    [Fact]
    public void Normalized_but_not_verbatim_text_is_rejected()
    {
        // The whole point of the fence. A model that "helpfully" tidies spacing or casing is
        // rewriting the source, and the result can no longer be pointed at.
        var aliases = Aliases("Session  I :  Welcome and meeting objectives");

        Assert.Contains(Validate("S0001", "Session I: Welcome", aliases),
            issue => issue.Code == "NON_VERBATIM_TEXT");
    }

    [Fact]
    public void A_substring_of_an_alias_the_segment_does_not_own_is_rejected()
    {
        var aliases = Aliases("Owned occurrence text", "Visible neighbour heading text");

        var issues = Validate("S0002", "Visible neighbour", aliases, owned: new HashSet<string> { "S0001" });

        Assert.Contains(issues, issue => issue.Code == "OUT_OF_OWNED_SEGMENT");
    }

    [Fact]
    public void A_numeric_offset_anywhere_in_the_reply_is_still_rejected()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{"headings":[{"sourceAlias":"S0001","isHeading":true,"verbatimText":"Africa","start":0,"end":6}]}""");

        Assert.NotEmpty(CanonicalSemanticContractValidator.ValidateJson(document.RootElement));
    }

    [Fact]
    public void The_partial_span_clause_changes_the_prompt_and_nothing_else()
    {
        var baseline = CanonicalSemanticEngine.SystemPromptFor(CanonicalSemanticExperiment.Baseline);
        var withClause = CanonicalSemanticEngine.SystemPromptFor(CanonicalSemanticExperiment.PartialSpanOnly);

        Assert.Equal(CanonicalSemanticEngine.SystemPrompt, baseline);
        Assert.StartsWith(baseline, withClause, StringComparison.Ordinal);
        Assert.Contains("exact contiguous substring", withClause, StringComparison.Ordinal);
        // The fence travels with the permission.
        Assert.Contains("do not normalize, rewrite, repair", withClause, StringComparison.Ordinal);
        Assert.Contains("do not return offsets or coordinates", withClause, StringComparison.Ordinal);
        Assert.Contains("Two separated pieces of one occurrence are NOT a partial span",
            withClause, StringComparison.Ordinal);
        Assert.Contains("appears more than once", withClause, StringComparison.Ordinal);
    }

    [Fact]
    public void The_binder_locates_a_partial_span_without_the_model_supplying_an_offset()
    {
        // The synthetic PDF case: the occurrence reads "Session I: Welcome and meeting objectives"
        // and only "Session I" is the heading. The model names the alias and copies the substring;
        // the harness finds where it sits.
        const string occurrence = "Session I: Welcome and meeting objectives";
        var aliases = Aliases(occurrence);

        Assert.Empty(Validate("S0001", "Session I", aliases));
        Assert.Equal(0, occurrence.IndexOf("Session I", StringComparison.Ordinal));
    }

    [Fact]
    public void A_composite_heading_still_needs_one_part_per_occurrence()
    {
        // Partial span must not be read as licence to glue two separated pieces of one occurrence
        // together. Spanning several occurrences remains its own contract, with each part copied
        // from the occurrence it belongs to.
        var aliases = Aliases("Framework Agreement", "for Consulting Services");

        var matched = CanonicalSemanticContractValidator.Validate(
            new CanonicalSemanticProposal("S0001", true, "Framework Agreement",
                VerbatimParts: ["Framework Agreement", "for Consulting Services"],
                SourceAliases: ["S0001", "S0002"]),
            aliases);
        var mismatched = CanonicalSemanticContractValidator.Validate(
            new CanonicalSemanticProposal("S0001", true, "Framework Agreement",
                SourceAliases: ["S0001", "S0002"]),
            aliases);

        Assert.Empty(matched);
        Assert.Contains(mismatched, issue => issue.Code == "COMPOSITE_MAPPING_MISMATCH");
    }
}
