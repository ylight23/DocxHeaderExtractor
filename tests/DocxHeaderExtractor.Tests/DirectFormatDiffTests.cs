using DocxHeaderExtractor.DocumentProcessing.Changes;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.Tests;

public sealed class DirectFormatDiffTests
{
    static ComparisonResult Compare(string a, string b, bool enabled = true)
    {
        var x = RawDocxFixtureTests.Build(a); var y = RawDocxFixtureTests.Build(b);
        try { return DocxComparer.Compare(DocxVersionReader.Read(x), DocxVersionReader.Read(y), new ComparerOptions(CompareDirectFormatting: enabled)); }
        finally { File.Delete(x); File.Delete(y); }
    }

    [Fact]
    public void Bold_change_is_reported_with_text_range_only_when_enabled()
    {
        const string a = "<w:p><w:r><w:t>Hello</w:t></w:r></w:p>";
        const string b = "<w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Hello</w:t></w:r></w:p>";
        var change = Assert.Single(Compare(a, b).Changes);
        Assert.Equal("direct_format_modified", change.Kind);
        var property = Assert.Single(change.FormatChanges!);
        Assert.Equal("run.bold", property.Property);
        Assert.Equal("True", property.After);
        Assert.Equal((0, 5), (property.Start, property.End));
        Assert.Empty(Compare(a, b, false).Changes);
    }

    [Fact]
    public void Splitting_runs_without_changing_format_is_not_a_difference()
    {
        const string a = "<w:p><w:r><w:t>Hello</w:t></w:r></w:p>";
        const string b = "<w:p><w:r><w:t>Hel</w:t></w:r><w:r><w:t>lo</w:t></w:r></w:p>";
        Assert.Empty(Compare(a, b).Changes);
    }

    [Fact]
    public void Paragraph_style_change_is_reported_even_when_text_changes()
    {
        const string a = "<w:p><w:pPr><w:pStyle w:val=\"One\"/></w:pPr><w:r><w:t>Same text old</w:t></w:r></w:p>";
        const string b = "<w:p><w:pPr><w:pStyle w:val=\"Two\"/></w:pPr><w:r><w:t>Same text new</w:t></w:r></w:p>";
        var result = Compare(a, b);
        var format = Assert.Single(result.Changes.Where(c => c.Kind == "direct_format_modified"));
        Assert.Contains(format.FormatChanges!, c => c.Property == "paragraph.styleId" && c.Before == "One" && c.After == "Two");
        Assert.Contains("run_formatting_only_for_identical_text", result.Scope);
        Assert.Contains(result.Changes, c => c.Kind == "text_modified");
    }
}
