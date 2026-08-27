using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfVisualStructuralReplayEvaluatorTests
{
    [Fact]
    public void Measures_title_and_anchor_without_inventing_hierarchy_metrics()
    {
        var key = AnswerKey.Parse("@body[1]/p[10] 2 # Chapter One\n@body[1]/p[11] 4 # Article One", "sample");
        var result = PdfVisualStructuralReplayEvaluator.Evaluate(key,
        [
            Trace("body[1]/p[10]", "Chapter One"),
            Trace("body[1]/p[11]", "Article"),
            Trace("body[1]/p[12]", "Body list"),
        ]);

        Assert.Equal(1, result.Title.Hits);
        Assert.Equal(2, result.Anchor.Hits);
        Assert.Equal("not-measured", result.Level.State);
        Assert.Equal("not-measured", result.FinalStructural.State);
        Assert.Equal(2, result.Unmatched.Count);
    }

    private static PdfVisualRecoveryTrace Trace(string stableId, string text) => new(
        "region", 1, "HeadingTopic", 0.9, text, "evidence", "visual-ocr-canonical-map", text, stableId);
}
