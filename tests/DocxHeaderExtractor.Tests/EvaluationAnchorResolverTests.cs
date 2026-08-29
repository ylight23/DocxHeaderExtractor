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
        Assert.Equal("canonical-title+ordered-occurrence", result.Entries.Single().Method);
        Assert.Equal("body[1]/p[31]", result.Entries.Single().ResolvedStableId);
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

    [Fact]
    public void Resolves_duplicate_titles_in_reviewed_document_order_not_stale_index_distance()
    {
        var key = AnswerKey.Parse("@body[1]/p[7] 2 # Repeated\n@body[1]/p[8] 2 # Repeated", "sample");
        var result = EvaluationAnchorResolver.Resolve(key,
        [
            Paragraph(101, "Repeated"),
            Paragraph(202, "Repeated"),
        ]);

        Assert.True(result.Complete);
        Assert.Equal([101, 202], result.Key.PositiveEntries.Select(entry => entry.Index).ToArray());
        Assert.All(result.Entries, entry => Assert.Equal("canonical-title+ordered-occurrence", entry.Method));
    }

    private static SourceParagraph Paragraph(int index, string text) => new()
    {
        SourceOrdinal = index,
        SourceId = $"body[1]/p[{index}]",
        Text = text,
        Style = new SourceStyleFacts(),
        Numbering = new SourceNumberingFacts(),
        Layout = new SourceLayoutFacts(),
    };
}
