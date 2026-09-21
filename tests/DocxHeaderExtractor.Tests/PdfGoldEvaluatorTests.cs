using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfGoldEvaluatorTests
{
    [Fact]
    public void Identical_occurrence_and_role_pass()
    {
        var gold = Gold("S0001", "AgendaItem");
        var evaluation = PdfGoldEvaluator.Evaluate(gold,
            [new PdfPredictedHeading(
                "S0001", "WHOLE_ALIAS", SemanticRole: "AgendaItem")]);

        Assert.Equal(1, evaluation.Semantic.TruePositive);
        Assert.Equal(1, evaluation.SemanticRole.Compared);
        Assert.Equal(1, evaluation.SemanticRole.Agreed);
        Assert.Equal(0, evaluation.SemanticRole.Mismatched);
        Assert.Empty(evaluation.SemanticRole.Mismatches);
    }

    [Fact]
    public void Different_role_is_an_explicit_mismatch_on_the_matched_occurrence()
    {
        var gold = Gold("S0001", "AgendaItem");
        var evaluation = PdfGoldEvaluator.Evaluate(gold,
            [new PdfPredictedHeading(
                "S0001", "WHOLE_ALIAS", SemanticRole: "MeetingSection")]);

        Assert.Equal(1, evaluation.Semantic.TruePositive);
        Assert.Equal(0, evaluation.Semantic.FalseNegative);
        Assert.Equal(0, evaluation.Semantic.FalsePositive);
        Assert.Equal(1, evaluation.SemanticRole.Mismatched);
        Assert.Contains(
            "S0001|WHOLE_ALIAS||: expected AgendaItem, got MeetingSection",
            Assert.Single(evaluation.SemanticRole.Mismatches));
    }

    [Fact]
    public void Null_and_non_null_roles_are_not_equal()
    {
        var gold = Gold("S0001", "AgendaItem");
        var evaluation = PdfGoldEvaluator.Evaluate(gold,
            [new PdfPredictedHeading("S0001", "WHOLE_ALIAS")]);

        Assert.Equal(1, evaluation.Semantic.TruePositive);
        Assert.Equal(1, evaluation.SemanticRole.Mismatched);
        Assert.Contains("got <null>", Assert.Single(evaluation.SemanticRole.Mismatches));
    }

    [Fact]
    public void Changing_role_does_not_change_occurrence_identity()
    {
        var gold = Gold("S0001", "AgendaItem");
        var withWrongRole = PdfGoldEvaluator.Evaluate(gold,
            [new PdfPredictedHeading("S0001", "WHOLE_ALIAS", SemanticRole: "OtherRole")]);
        var withSameRole = PdfGoldEvaluator.Evaluate(gold,
            [new PdfPredictedHeading("S0001", "WHOLE_ALIAS", SemanticRole: "AgendaItem")]);

        Assert.Equal(withSameRole.Semantic.TruePositive, withWrongRole.Semantic.TruePositive);
        Assert.Equal(withSameRole.Semantic.FalseNegative, withWrongRole.Semantic.FalseNegative);
        Assert.Equal(withSameRole.Semantic.FalsePositive, withWrongRole.Semantic.FalsePositive);
        Assert.Equal(1, withWrongRole.SemanticRole.Mismatched);
        Assert.Equal(1, withSameRole.SemanticRole.Agreed);
    }

    [Fact]
    public void DOC0252_gold_passes_with_all_approved_roles_unchanged()
    {
        var authority = PdfGoldOccurrenceAuthorityLoader.LoadFromFiles(
            Path("eval/a99-closed-loop/pdf-gold-doc0252/review-decisions.v1.json"),
            Path("eval/a99-closed-loop/pdf-gold-doc0252/source-universe.v1.json"));
        var gold = PdfGoldOccurrenceMaterializer.Freeze(authority);
        var predicted = gold.Headings.Select(heading => new PdfPredictedHeading(
            heading.SourceAlias,
            heading.SelectionMode,
            heading.VerbatimText,
            heading.Occurrence,
            heading.ParentSourceAlias,
            heading.SemanticRole)).ToArray();

        var evaluation = PdfGoldEvaluator.Evaluate(gold, predicted);

        Assert.Equal(41, gold.Headings.Count);
        Assert.Equal(41, evaluation.Semantic.TruePositive);
        Assert.Equal(0, evaluation.Semantic.FalseNegative);
        Assert.Equal(0, evaluation.Semantic.FalsePositive);
        Assert.Equal(41, evaluation.SemanticRole.Compared);
        Assert.Equal(41, evaluation.SemanticRole.Agreed);
        Assert.Equal(0, evaluation.SemanticRole.Mismatched);
        Assert.Equal(0, gold.ProviderCalls);
    }

    [Fact]
    public void Role_evaluation_is_deterministic()
    {
        var gold = Gold("S0001", "AgendaItem");
        var predicted = new[]
        {
            new PdfPredictedHeading("S0001", "WHOLE_ALIAS", SemanticRole: "OtherRole"),
        };

        var first = PdfGoldEvaluator.Evaluate(gold, predicted);
        var second = PdfGoldEvaluator.Evaluate(gold, predicted);

        Assert.Equal(
            JsonSerializer.Serialize(first),
            JsonSerializer.Serialize(second));
    }

    private static PdfGoldDocument Gold(string alias, string role) =>
        new("DOC-TEST", "source", [new PdfGoldHeading(alias, "WHOLE_ALIAS", role)]);

    private static string Path(string relativePath) =>
        System.IO.Path.Combine(TestRepository.Root(),
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

}
