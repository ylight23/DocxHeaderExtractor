using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class EvaluationAnchorResolverTests
{
    [Fact]
    public void Resolves_regenerated_paragraph_anchor_from_reviewed_title()
    {
        var key = AnswerKey.Parse("7 2 # Cash Contributions", "sample");
        var result = EvaluationAnchorResolver.Resolve(key,
        [
            Paragraph(2, "Body text"),
            Paragraph(31, "Cash Contributions (cont'd)"),
        ]);

        Assert.True(result.Complete);
        Assert.Equal(31, result.Key.PositiveEntries.Single().Index);
        Assert.Equal("normalized-title+document-order", result.Entries.Single().Method);
    }

    [Fact]
    public void Keeps_incomplete_resolution_partial_instead_of_fabricating_anchor()
    {
        var key = AnswerKey.Parse("7 2 # Missing heading", "sample");
        var result = EvaluationAnchorResolver.Resolve(key, [Paragraph(2, "Other paragraph")]);

        Assert.False(result.Complete);
        Assert.True(result.Key.IsPartial);
        Assert.Equal("unresolved", result.Entries.Single().Status);
    }

    private static SlimParagraph Paragraph(int index, string text) => new() { Index = index, Text = text };
}
