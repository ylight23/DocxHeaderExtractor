using DocxHeaderExtractor.Eval;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.Tests;

public sealed class AnswerKeyNegativePartialTests
{
    [Fact]
    public void PartialKeyCanPenalizeReviewedFalsePositive()
    {
        var key = AnswerKey.Parse(
            """
            # partial_human
            @body[1]/p[1] 1 # Real Heading
            !@body[1]/p[2] 1 # Metric Row
            """).ResolveStableIds(new Dictionary<string, int>
            {
                ["body[1]/p[1]"] = 1,
                ["body[1]/p[2]"] = 2,
            });

        var outline = new DocumentOutline
        {
            File = "synthetic.docx",
            ParagraphCount = 2,
            CandidateCount = 2,
            Headings =
            [
                Heading(1, "Real Heading"),
                Heading(2, "Metric Row"),
            ],
        };

        var score = Evaluator.Score("synthetic", outline, [1, 2], key);

        Assert.True(score.PartialTruth);
        Assert.Equal(1, score.TruePositive);
        Assert.Equal(2, score.ResultCount);
        Assert.Equal(0.5, score.Precision);
        Assert.Equal([2], score.FalsePositives);
    }

    private static HeadingRecord Heading(int index, string text) => new()
    {
        Index = index,
        StableId = $"body[1]/p[{index}]",
        Level = 1,
        Text = text,
        Source = HeadingSource.Structure,
        Confidence = 1,
    };
}
