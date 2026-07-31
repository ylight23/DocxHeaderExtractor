using DocxHeaderExtractor.Core.Chunking;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using Xunit;

namespace DocxHeaderExtractor.Tests;

public sealed class SlimExtractionTests : IDisposable
{
    private readonly string _docx;

    public SlimExtractionTests()
    {
        _docx = Path.Combine(Path.GetTempPath(), $"dhx-test-{Guid.NewGuid():N}.docx");
        SampleDocumentFactory.Create(_docx);
    }

    public void Dispose() => LegacyDocConverter.TryDelete(_docx);

    private SlimDocument Extract(ExtractionOptions? o = null) =>
        new DocxSlimExtractor(o ?? new ExtractionOptions()).Extract(_docx);

    [Fact]
    public void Style_based_headings_are_detected_with_correct_level()
    {
        var doc = Extract();
        var styled = doc.Paragraphs.Where(p => p.Role == ParagraphRole.StyledHeading).ToList();

        Assert.Equal(4, styled.Count);
        Assert.Equal("Chương 1. Tổng quan hệ thống", styled[0].Text);
        Assert.Equal(1, styled[0].GuessedLevel);
        Assert.Equal(2, styled[1].GuessedLevel);   // 1.1
        Assert.Equal(2, styled[2].GuessedLevel);   // 1.2
    }

    [Fact]
    public void Outline_level_is_inherited_from_style_definition()
    {
        var doc = Extract();
        var h1 = doc.Paragraphs.First(p => p.StyleId == "Heading1");

        // outlineLvl chỉ khai báo trong styles.xml, không có trên đoạn.
        Assert.Equal(0, h1.OutlineLevel);
        Assert.True(h1.Bold);          // Bold kế thừa từ StyleRunProperties
        Assert.True(h1.KeepNext);
    }

    [Fact]
    public void Manually_formatted_headings_become_candidates()
    {
        var doc = Extract();
        var fake = doc.Paragraphs.Where(p => p.Role == ParagraphRole.HeadingCandidate).ToList();

        Assert.Contains(fake, p => p.Text.StartsWith("PHỤ LỤC A"));
        Assert.Contains(fake, p => p.Text.StartsWith("2.1 Kết quả"));

        var appendix = fake.First(p => p.Text.StartsWith("PHỤ LỤC A"));
        Assert.True(appendix.AllCaps);
        Assert.Equal("center", appendix.Alignment);
    }

    [Fact]
    public void Table_cells_and_body_text_are_not_candidates()
    {
        var doc = Extract();

        var cells = doc.Paragraphs.Where(p => p.TableDepth > 0).ToList();
        Assert.NotEmpty(cells);
        Assert.All(cells, p => Assert.False(p.IsCandidate));

        var body = doc.Paragraphs.First(p => p.Text.StartsWith("Tài liệu này mô tả"));
        Assert.Equal(ParagraphRole.Normal, body.Role);
    }

    [Fact]
    public void Slim_xml_is_much_smaller_than_raw_document_xml()
    {
        var doc = Extract();
        var lines = SlimXmlSerializer.BuildLines(doc, new ExtractionOptions());
        var slim = SlimXmlSerializer.WrapChunk(lines, 1, 1);

        using var zip = System.IO.Compression.ZipFile.OpenRead(_docx);
        var entry = zip.GetEntry("word/document.xml")!;
        using var reader = new StreamReader(entry.Open());
        var raw = reader.ReadToEnd();

        Assert.True(slim.Length < raw.Length / 2,
            $"XML tinh gọn {slim.Length} ký tự, document.xml gốc {raw.Length} ký tự");
    }

    [Fact]
    public void Collapsed_runs_report_number_of_skipped_paragraphs()
    {
        var doc = Extract();
        var lines = SlimXmlSerializer.BuildLines(doc, new ExtractionOptions { IncludeFollowingContext = false });

        Assert.Contains(lines, l => l.Text.StartsWith("<n c="));
        Assert.All(lines.Where(l => l.IsCandidate), l => Assert.NotNull(l.ParagraphIndex));
    }

    [Fact]
    public void Every_candidate_index_maps_back_to_a_paragraph()
    {
        var doc = Extract();
        foreach (var c in doc.Candidates)
            Assert.Same(c, doc.ByIndex(c.Index));
    }

    [Fact]
    public void Tables_can_be_excluded()
    {
        var doc = Extract(new ExtractionOptions { IncludeTables = false });
        Assert.DoesNotContain(doc.Paragraphs, p => p.TableDepth > 0);
    }
}
