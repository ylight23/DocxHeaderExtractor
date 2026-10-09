using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7PageEvidenceCoverageTests
{
    private static readonly CoverageSource[] Source = [new("O1", "A", 1, "title", true),
        new("O2", "B", 1, "body", true), new("O3", "C", 2, "next", true)];

    [Fact]
    public void Whole_pack_text_is_not_whole_visual_page_or_spatial_evidence()
    {
        using var json = JsonDocument.Parse("""{"occurrences":[{"id":"O1","page":1,"text":"title"},{"id":"O2","page":1,"text":"body"},{"id":"O3","page":2,"text":"next"}]}""");
        var result = PageEvidenceCoverageAudit.Measure(json.RootElement, Source, false);
        Assert.Equal(3, result.VisibleTextOccurrences);
        Assert.Equal(0, result.VisibleBoundsOccurrences);
        Assert.All(result.Pages, page => Assert.Equal("FULL_CANONICAL_OCCURRENCE_PAGE", page.CanonicalOccurrenceCoverage));
        Assert.False(result.FullVisualPageEvidence);
    }

    [Fact]
    public void Local_neighbors_do_not_imply_page_context_and_unbound_text_is_not_identity()
    {
        using var json = JsonDocument.Parse("""{"occurrences":[{"primary":"O2","page":1,"text":"body","previous":{"occurrence":"O1","page":1,"text":"title"}}],"context":[{"page":2,"text":"next"}]}""");
        var result = PageEvidenceCoverageAudit.Measure(json.RootElement, Source, false);
        Assert.Equal(2, result.VisibleTextOccurrences);
        Assert.Equal(1, result.AnonymousTextContexts);
        Assert.Equal("NONE", result.Pages[1].CanonicalOccurrenceCoverage);
    }

    [Fact]
    public void Tail_omission_is_explicit_and_measurement_is_order_invariant()
    {
        using var json = JsonDocument.Parse("""{"occurrences":[{"occurrence":"O2","page":1,"text":"body"},{"occurrence":"O3","page":2,"text":"next"}]}""");
        var before = json.RootElement.GetRawText();
        var result = PageEvidenceCoverageAudit.Measure(json.RootElement, Source, false);
        Assert.Equal("PARTIAL", result.Pages[0].CanonicalOccurrenceCoverage);
        Assert.Equal(new[] { "A" }, result.Pages[0].MissingAliases);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(PageEvidenceCoverageAudit.Measure(json.RootElement, Source.Reverse().ToArray(), false)));
        Assert.Equal(before, json.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("O99", "title", 1)]
    [InlineData("O1", "forged", 1)]
    [InlineData("O1", "title", 2)]
    public void Misbound_source_rows_fail_closed(string id, string text, int page)
    {
        var json = JsonSerializer.SerializeToElement(new { occurrences = new[] { new { id, text, page } } });
        Assert.Throws<InvalidOperationException>(() => PageEvidenceCoverageAudit.Measure(json, Source, false));
    }

    [Fact]
    public void B_pair_counts_measure_availability_not_asserted_relations()
    {
        var evidence = Source.Select(item => new { occurrence = item.Occurrence, source = new { sourceAlias = item.Alias,
            fields = new object[] { new { name = "text", availability = "OBSERVED", value = (object)item.Text },
                new { name = "bbox", availability = "OBSERVED", value = (object)new { left = 1, right = 2, top = 2, bottom = 1 } } } } }).ToArray();
        var json = JsonSerializer.SerializeToElement(new { stageInput = new { }, sourceEvidence = evidence });
        var result = PageEvidenceCoverageAudit.Measure(json, Source, true);
        Assert.Equal(1, result.SamePagePairsWithBounds);
        Assert.Equal(2, result.CrossPagePairsWithBounds);
        Assert.False(result.FullVisualPageEvidence);
        Assert.Throws<InvalidOperationException>(() => PageEvidenceCoverageAudit.Measure(json, Source.Select(x => x with { BoundsAvailable = false }).ToArray(), true));
    }
}
