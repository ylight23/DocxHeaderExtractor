using DocxHeaderExtractor.Cli;
using Xunit;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The scorer exists to stop a measurement from flattering itself. These tests pin the two rules
/// that were learned the hard way: join on text, and separate a root-convention shift from a real
/// hierarchy error.
/// </summary>
public class HeadingLevelScorerTests
{
    private static ScoredHeading H(string text, int? level = null) => new(text, level);

    [Fact]
    public void Matching_ignores_whitespace_and_case_differences()
    {
        var score = HeadingLevelScorer.Score(
            [H("Session I:  Welcome", 1)],
            [H("session i: welcome", 1)]);

        Assert.Equal(1, score.Matched);
        Assert.Equal(1, score.StructuralExact);
    }

    [Fact]
    public void A_constant_shift_counts_as_correct_structure_and_is_reported_as_an_offset()
    {
        // The model treated the document title as an ancestor; the reference did not. Every
        // heading moves by the same amount and the sibling structure is untouched.
        var gold = new[] { H("Session I", 1), H("Session II", 1), H("Topic", 2) };
        var predicted = new[] { H("Session I", 3), H("Session II", 3), H("Topic", 4) };

        var score = HeadingLevelScorer.Score(gold, predicted);

        Assert.Equal(0, score.AbsoluteExact);
        Assert.Equal(3, score.StructuralExact);
        Assert.Equal(2, score.LevelOffset);
    }

    [Fact]
    public void A_real_hierarchy_error_survives_offset_normalization()
    {
        // One heading sits at the wrong depth relative to its siblings; no single shift fixes it.
        var gold = new[] { H("A", 1), H("B", 1), H("C", 1) };
        var predicted = new[] { H("A", 1), H("B", 1), H("C", 3) };

        var score = HeadingLevelScorer.Score(gold, predicted);

        Assert.Equal(2, score.StructuralExact);
        Assert.Equal(0, score.LevelOffset);
        Assert.Single(score.StructuralMismatches);
        Assert.Equal("C", score.StructuralMismatches[0].Text);
    }

    [Fact]
    public void Recall_and_precision_are_reported_against_their_own_denominators()
    {
        var score = HeadingLevelScorer.Score(
            [H("A", 1), H("B", 1), H("C", 1), H("D", 1)],
            [H("A", 1), H("B", 1)]);

        Assert.Equal(2, score.Matched);
        Assert.Equal(0.5, score.Recall);
        Assert.Equal(1.0, score.Precision);
        Assert.Equal(2, score.MissingFromPrediction.Count);
    }

    [Fact]
    public void Headings_without_a_level_are_counted_unresolved_not_wrong()
    {
        var score = HeadingLevelScorer.Score(
            [H("A", 1), H("B", 2)],
            [H("A", 1), H("B", null)]);

        Assert.Equal(2, score.Matched);
        Assert.Equal(1, score.LevelComparable);
        Assert.Equal(1, score.Unresolved);
        Assert.Equal(1, score.StructuralExact);
    }

    [Fact]
    public void Describe_never_prints_an_accuracy_without_its_denominator()
    {
        var text = HeadingLevelScorer
            .Score([H("A", 1), H("B", 2)], [H("A", 1)])
            .Describe("DOC-TEST");

        Assert.Contains("1/1", text);
        Assert.Contains("matched on text    : 1", text);
        Assert.Contains("reference headings : 2", text);
    }

    [Fact]
    public void An_empty_prediction_scores_nothing_rather_than_dividing_by_zero()
    {
        var score = HeadingLevelScorer.Score([H("A", 1)], []);

        Assert.Equal(0, score.Matched);
        Assert.Equal(0, score.LevelComparable);
        Assert.Equal(0, score.Precision);
        Assert.Equal(0, score.StructuralExact);
    }
}
