using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfGoldBoundOccurrenceEvaluatorTests
{
    [Fact]
    public void Whole_alias_and_full_verbatim_same_span_match()
    {
        var aliases = Aliases("MINUTES");
        var gold = Gold(new PdfGoldHeading("S0001", "WHOLE_ALIAS", "SECTION"));
        var prediction = Bind(aliases, new CanonicalSemanticProposal(
            "S0001", true, "MINUTES", SemanticRole: "SECTION",
            SelectionMode: "VERBATIM_TEXT"));

        var result = PdfGoldBoundOccurrenceEvaluator.Evaluate(gold, [prediction], aliases);

        Assert.Equal(1, result.Semantic.TruePositive);
        Assert.Equal(0, result.Semantic.FalseNegative);
        Assert.Equal(0, result.Semantic.FalsePositive);
    }

    [Fact]
    public void Whole_alias_and_partial_verbatim_are_different_spans()
    {
        var aliases = Aliases("MINUTES");
        var gold = Gold(new PdfGoldHeading("S0001", "WHOLE_ALIAS", "SECTION"));
        var prediction = Bind(aliases, new CanonicalSemanticProposal(
            "S0001", true, "MIN", SemanticRole: "SECTION",
            SelectionMode: "VERBATIM_TEXT"));

        var result = PdfGoldBoundOccurrenceEvaluator.Evaluate(gold, [prediction], aliases);

        Assert.Equal(0, result.Semantic.TruePositive);
        Assert.Equal(1, result.Semantic.FalseNegative);
        Assert.Equal(1, result.Semantic.FalsePositive);
    }

    [Fact]
    public void Different_disambiguation_representation_same_span_matches()
    {
        var aliases = Aliases("Intro");
        var gold = Gold(new PdfGoldHeading("S0001", "VERBATIM_TEXT", "SECTION")
        {
            VerbatimText = "Intro",
        });
        var prediction = Bind(aliases, new CanonicalSemanticProposal(
            "S0001", true, null, SemanticRole: "SECTION",
            SelectionMode: "WHOLE_ALIAS"));

        var result = PdfGoldBoundOccurrenceEvaluator.Evaluate(gold, [prediction], aliases);

        Assert.Equal(1, result.Semantic.TruePositive);
    }

    [Fact]
    public void Same_alias_different_substring_spans_do_not_match()
    {
        var aliases = Aliases("Intro details");
        var gold = Gold(new PdfGoldHeading("S0001", "VERBATIM_TEXT", "SECTION")
        {
            VerbatimText = "Intro",
        });
        var prediction = Bind(aliases, new CanonicalSemanticProposal(
            "S0001", true, "details", SemanticRole: "SECTION",
            SelectionMode: "VERBATIM_TEXT"));

        var result = PdfGoldBoundOccurrenceEvaluator.Evaluate(gold, [prediction], aliases);

        Assert.Equal(0, result.Semantic.TruePositive);
        Assert.Equal(1, result.Semantic.FalseNegative);
        Assert.Equal(1, result.Semantic.FalsePositive);
    }

    [Fact]
    public void Duplicate_text_different_occurrences_do_not_match()
    {
        var aliases = Aliases("Intro Intro");
        var gold = Gold(new PdfGoldHeading("S0001", "VERBATIM_TEXT", "SECTION")
        {
            VerbatimText = "Intro",
            Occurrence = 1,
        });
        var prediction = Bind(aliases, new CanonicalSemanticProposal(
            "S0001", true, "Intro", SemanticRole: "SECTION",
            Occurrence: 2, SelectionMode: "VERBATIM_TEXT"));

        var result = PdfGoldBoundOccurrenceEvaluator.Evaluate(gold, [prediction], aliases);

        Assert.Equal(0, result.Semantic.TruePositive);
    }

    [Fact]
    public void Duplicate_text_different_disambiguators_same_span_match()
    {
        var aliases = Aliases("XIntroY IntroZ");
        var gold = Gold(new PdfGoldHeading("S0001", "VERBATIM_TEXT", "SECTION")
        {
            VerbatimText = "Intro",
            Occurrence = 1,
        });
        var prediction = Bind(aliases, new CanonicalSemanticProposal(
            "S0001", true, "Intro", SemanticRole: "SECTION",
            LeftExactContext: "X", RightExactContext: "Y",
            SelectionMode: "VERBATIM_TEXT"));

        var result = PdfGoldBoundOccurrenceEvaluator.Evaluate(gold, [prediction], aliases);

        Assert.Equal(1, result.Semantic.TruePositive);
    }

    [Fact]
    public void Multiple_claims_in_one_alias_remain_distinct()
    {
        var aliases = Aliases("First Second");
        var gold = Gold(
            new PdfGoldHeading("S0001", "VERBATIM_TEXT", "SECTION") { VerbatimText = "First" },
            new PdfGoldHeading("S0001", "VERBATIM_TEXT", "SECTION") { VerbatimText = "Second" });
        var predictions = new[]
        {
            Bind(aliases, new CanonicalSemanticProposal("S0001", true, "First", SemanticRole: "SECTION", SelectionMode: "VERBATIM_TEXT")),
            Bind(aliases, new CanonicalSemanticProposal("S0001", true, "Second", SemanticRole: "SECTION", SelectionMode: "VERBATIM_TEXT")),
        };

        var result = PdfGoldBoundOccurrenceEvaluator.Evaluate(gold, predictions, aliases);

        Assert.Equal(2, result.Semantic.TruePositive);
    }

    [Fact]
    public void Composite_ordered_parts_match_only_in_the_same_order()
    {
        var first = new CanonicalSemanticBoundPart("S0001", "b1", 0, "A", 0, 1);
        var second = new CanonicalSemanticBoundPart("S0002", "b2", 1, "B", 0, 1);
        var gold = new PdfBoundOccurrence([first, second], "SECTION", "S0001");
        var prediction = new PdfBoundOccurrence([first, second], "SECTION", "S0001");

        var result = PdfGoldBoundOccurrenceEvaluator.EvaluateBound([gold], [prediction]);

        Assert.Equal(1, result.Semantic.TruePositive);
    }

    [Fact]
    public void Composite_same_text_different_order_does_not_match()
    {
        var first = new CanonicalSemanticBoundPart("S0001", "b1", 0, "A", 0, 1);
        var second = new CanonicalSemanticBoundPart("S0002", "b2", 1, "B", 0, 1);
        var gold = new PdfBoundOccurrence([first, second], "SECTION", "S0001");
        var prediction = new PdfBoundOccurrence([second, first], "SECTION", "S0001");

        var result = PdfGoldBoundOccurrenceEvaluator.EvaluateBound([gold], [prediction]);

        Assert.Equal(0, result.Semantic.TruePositive);
        Assert.Equal(1, result.Semantic.FalseNegative);
        Assert.Equal(1, result.Semantic.FalsePositive);
    }

    private static PdfGoldDocument Gold(params PdfGoldHeading[] headings) =>
        new("DOC-TEST", "sha", headings);

    private static IReadOnlyList<SemanticSourceAlias> Aliases(params string[] texts) =>
        SemanticSourceAliasCatalog.FromCatalog(new DocumentSourceCatalog(
            texts.Select((text, index) => new DocumentSourceUnit(
                $"b{index + 1}",
                index,
                text,
                new SourceAnchor { SourceType = "pdf", ParagraphId = $"b{index + 1}", ParagraphIndex = index },
                new StructuralSpan(0, text.Length)))));

    private static PdfBoundOccurrence Bind(
        IReadOnlyList<SemanticSourceAlias> aliases,
        CanonicalSemanticProposal proposal)
    {
        var bound = CanonicalSemanticExactBinder.Bind(
            [proposal], aliases, out var observations);
        Assert.Single(bound);
        Assert.Equal(CanonicalSemanticBindingStatus.Bound, Assert.Single(observations).Status);
        var item = bound[0];
        return new(item.Parts, item.SemanticRole, item.Alias);
    }
}
