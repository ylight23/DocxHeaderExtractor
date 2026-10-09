using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7PageEvidenceContextTests
{
    private static PdfSourceBuildResult Fixture() => PdfSourceAdapter.BuildWithDetails([
        Line(1, 700, 10, "Prefix"), Line(1, 650, 10, "Anchor"),
        Line(1, 650, 150, "Separate horizontal region"), Line(1, 610, 10, "Below left"),
        Line(1, 610, 150, "Below right"), Line(1, 500, 10, "Far below"),
        Line(2, 700, 10, "Cross-page occurrence")], new string('a', 64));
    private static PdfLine Line(int page, double y, double x, string text) => new(page, y, 12, text, 0, "", 0,
        x, x + 80, "Times", "", Bottom: y - 10, Top: y);
    private static string Alias(PdfSourceBuildResult fixture, string text) => fixture.Snapshot.Atoms.Single(x => x.Text == text).Alias;

    [Fact]
    public void Local_context_preserves_prefix_and_below_multiple_horizontal_regions_without_row_labels()
    {
        var fixture = Fixture(); var store = PdfSourceEvidenceStore.Build(fixture.Snapshot, fixture.Details);
        var result = PdfPageEvidenceContextBuilder.Build(store, [Alias(fixture, "Anchor")], new(PageContextScope.LocalVerticalWindow));
        var visible = result.Pages.Single().Observations.Select(x => x.SourceAlias).ToArray();
        foreach (var text in new[] { "Prefix", "Anchor", "Separate horizontal region", "Below left", "Below right" })
            Assert.Contains(Alias(fixture, text), visible);
        Assert.DoesNotContain(Alias(fixture, "Far below"), visible);
        Assert.All(result.Pages.Single().Observations, x => Assert.False(x.Selectable));
        var json = System.Text.Encoding.UTF8.GetString(result.CanonicalBytes());
        foreach (var label in new[] { "SAME_VISUAL_ROW", "TABLE_HEADER", "HEADING", "CONTINUATION", "glyphs" }) Assert.DoesNotContain(label, json);
    }

    [Fact]
    public void Page_and_adjacent_page_scopes_do_not_claim_semantic_continuation_or_full_visual_pages()
    {
        var fixture = Fixture(); var store = PdfSourceEvidenceStore.Build(fixture.Snapshot, fixture.Details);
        var aliases = new[] { Alias(fixture, "Anchor") };
        var page = PdfPageEvidenceContextBuilder.Build(store, aliases, new(PageContextScope.SubjectPages));
        Assert.Single(page.Pages); Assert.Equal(6, page.Pages[0].Observations.Count);
        Assert.Contains("NOT_FULL_VISUAL_PAGE", page.Pages[0].Coverage);
        var expanded = PdfPageEvidenceContextBuilder.Build(store, aliases,
            new(PageContextScope.SubjectAndAdjacentPages, AdjacentPageRadius: 1));
        Assert.Equal(2, expanded.Pages.Count);
        Assert.Contains(Alias(fixture, "Cross-page occurrence"), expanded.Pages[1].Observations.Select(x => x.SourceAlias));
    }

    [Fact]
    public void Context_is_deterministic_readonly_and_preserves_all_exact_spans_and_source_order()
    {
        var fixture = Fixture(); var store = PdfSourceEvidenceStore.Build(fixture.Snapshot, fixture.Details);
        var original = store.CanonicalBytes(); var atoms = JsonSerializer.Serialize(fixture.Snapshot.Atoms);
        var first = PdfPageEvidenceContextBuilder.Build(store, [Alias(fixture, "Anchor"), Alias(fixture, "Below right")], new(PageContextScope.SubjectPages));
        var second = PdfPageEvidenceContextBuilder.Build(store, [Alias(fixture, "Below right"), Alias(fixture, "Anchor")], new(PageContextScope.SubjectPages));
        Assert.Equal(first.CanonicalBytes(), second.CanonicalBytes());
        Assert.Equal(original, store.CanonicalBytes()); Assert.Equal(atoms, JsonSerializer.Serialize(fixture.Snapshot.Atoms));
        var entries = store.Entries.ToDictionary(x => x.SourceAlias);
        foreach (var observation in first.Pages.SelectMany(x => x.Observations))
        {
            var expected = entries[observation.SourceAlias];
            Assert.Equal(expected.Ordinal, observation.Ordinal); Assert.Equal(expected.SpanStart, observation.SpanStart); Assert.Equal(expected.SpanEnd, observation.SpanEnd);
        }
        Assert.Equal(first.Pages[0].Observations.OrderBy(x => x.Ordinal).Select(x => x.SourceAlias), first.Pages[0].Observations.Select(x => x.SourceAlias));
    }

    [Fact]
    public void Missing_geometry_stays_unavailable_no_font_height_or_source_order_fallback()
    {
        var fixture = PdfSourceAdapter.BuildWithDetails([
            Line(1, 650, 10, "Anchor") with { Top = null, Bottom = null }, Line(1, 640, 10, "Nearby")], new string('b', 64));
        var store = PdfSourceEvidenceStore.Build(fixture.Snapshot, fixture.Details);
        var alias = Alias(fixture, "Anchor");
        var local = PdfPageEvidenceContextBuilder.Build(store, [alias], new(PageContextScope.LocalVerticalWindow));
        Assert.Single(local.Pages[0].Observations); Assert.Contains(alias, local.MissingGeometryAliases);
        Assert.Equal("NOT_AVAILABLE", local.Pages[0].Observations[0].Fields.Single(x => x.Name == "bbox").Availability);
        var page = PdfPageEvidenceContextBuilder.Build(store, [alias], new(PageContextScope.SubjectPages));
        Assert.Equal(2, page.Pages[0].Observations.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_radius_rejected(double radius)
    {
        var fixture = Fixture(); var store = PdfSourceEvidenceStore.Build(fixture.Snapshot, fixture.Details);
        Assert.Throws<InvalidOperationException>(() => PdfPageEvidenceContextBuilder.Build(store, [Alias(fixture, "Anchor")], new(PageContextScope.LocalVerticalWindow, radius)));
    }

    [Fact]
    public void Unknown_duplicate_subject_and_byte_cap_fail_closed_without_truncation()
    {
        var fixture = Fixture(); var store = PdfSourceEvidenceStore.Build(fixture.Snapshot, fixture.Details);
        var alias = Alias(fixture, "Anchor");
        Assert.Throws<InvalidOperationException>(() => PdfPageEvidenceContextBuilder.Build(store, ["foreign"], new(PageContextScope.SubjectPages)));
        Assert.Throws<InvalidOperationException>(() => PdfPageEvidenceContextBuilder.Build(store, [alias, alias], new(PageContextScope.SubjectPages)));
        Assert.Equal("page-context-byte-cap-exceeded", Assert.Throws<InvalidOperationException>(() =>
            PdfPageEvidenceContextBuilder.Build(store, [alias], new(PageContextScope.SubjectPages, MaxContextUtf8Bytes: 16))).Message);
    }
}
