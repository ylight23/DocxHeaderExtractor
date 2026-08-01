using DocxHeaderExtractor.Core.Chunking;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Vml = DocumentFormat.OpenXml.Vml;
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
    public void Stable_ids_are_unique_and_repeatable_across_extractions()
    {
        var first = Extract().Paragraphs.Select(p => p.StableId).ToList();
        var second = Extract().Paragraphs.Select(p => p.StableId).ToList();

        Assert.Equal(first, second);
        Assert.Equal(first.Count, first.Distinct(StringComparer.Ordinal).Count());
        Assert.All(first, id => Assert.StartsWith("body[1]/", id));
    }

    [Fact]
    public void Tables_can_be_excluded()
    {
        var doc = Extract(new ExtractionOptions { IncludeTables = false });
        Assert.DoesNotContain(doc.Paragraphs, p => p.TableDepth > 0);
    }

    [Fact]
    public async Task Heuristic_only_run_reports_only_candidates_as_reviewed()
    {
        var log = new List<string>();
        using var pipeline = new HeaderExtractionPipeline(new PipelineOptions
        {
            DisableLlm = true,
            ReviewAllParagraphs = true,
            Log = log.Add,
        });

        var outline = await pipeline.RunAsync(_docx);
        var expectedCandidates = Extract().Candidates.Count();

        Assert.Equal(expectedCandidates, outline.CandidateCount);
        Assert.Contains(log, line => line.Contains($"luật xét {expectedCandidates} ứng viên"));
        Assert.DoesNotContain(log, line => line.Contains("LLM review"));
    }

    [Fact]
    public void Textbox_paragraph_is_extracted_separately_from_its_anchor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-textbox-{Guid.NewGuid():N}.docx");
        try
        {
            using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var boxContent = new TextBoxContent(
                    new Paragraph(new Run(new Text("TIÊU ĐỀ TRONG TEXTBOX"))));
                var picture = new Picture(new Vml.Shape(new Vml.TextBox(boxContent)));
                var anchor = new Paragraph(new Run(new Text("Đoạn neo")), new Run(picture));
                main.Document = new Document(new Body(anchor));
                main.Document.Save();
            }

            var paragraphs = new DocxSlimExtractor().Extract(path).Paragraphs;

            Assert.Contains(paragraphs, p => p.Text == "Đoạn neo");
            Assert.Contains(paragraphs, p => p.Text == "TIÊU ĐỀ TRONG TEXTBOX");
            Assert.DoesNotContain(paragraphs, p => p.Text.Contains("Đoạn neoTIÊU ĐỀ", StringComparison.Ordinal));
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    [Fact]
    public void Preserves_normalized_run_offsets_for_inline_heading_boundary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-runs-{Guid.NewGuid():N}.docx");
        try
        {
            using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(new Paragraph(
                    new Run(new RunProperties(new Bold()), new Text("2.3.1. Thành công")),
                    new Run(new Text(":   Tỉ lệ thành công: 20%") { Space = SpaceProcessingModeValues.Preserve }))));
                main.Document.Save();
            }

            var paragraph = new DocxSlimExtractor().Extract(path).Paragraphs.Single();

            Assert.Equal("2.3.1. Thành công: Tỉ lệ thành công: 20%", paragraph.Text);
            Assert.Equal(2, paragraph.TextSpans.Count);
            Assert.True(paragraph.TextSpans[0].Bold);
            Assert.False(paragraph.TextSpans[1].Bold);
            Assert.Equal(17, paragraph.TextSpans[1].Start);
            Assert.Contains("br=\"0-17\"", SlimXmlSerializer.ToFullXml(
                new SlimDocument { FileName = "test.docx", SourcePath = path, Paragraphs = [paragraph] }.Build(),
                new ExtractionOptions()));
            Assert.True(InlineHeadingSplitter.TryFindBoundary(paragraph, out var headingEnd, out var bodyStart));
            Assert.Equal(17, headingEnd);
            Assert.Equal(19, bodyStart);
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }
}

public sealed class NumberingResolverTests : IDisposable
{
    private readonly string _docx = Path.Combine(Path.GetTempPath(), "dhx-numbering-" + Guid.NewGuid().ToString("N") + ".docx");

    public void Dispose() => LegacyDocConverter.TryDelete(_docx);

    [Fact]
    public void Reads_displayed_labels_and_level_override_from_numbering_xml()
    {
        using (var doc = WordprocessingDocument.Create(_docx, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(
                P(0, "Phần một"), P(1, "Mục một"), P(1, "Mục hai"), P(0, "Phần hai"), P(1, "Mục một")));
            var numbering = main.AddNewPart<NumberingDefinitionsPart>();
            using var xml = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
                <w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:abstractNum w:abstractNumId="0">
                    <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="upperRoman"/><w:lvlText w:val="%1."/></w:lvl>
                    <w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1.%2."/></w:lvl>
                  </w:abstractNum><w:num w:numId="1"><w:abstractNumId w:val="0"/>
                    <w:lvlOverride w:ilvl="1"><w:lvl w:ilvl="1"><w:numFmt w:val="lowerLetter"/><w:lvlText w:val="%2)"/></w:lvl></w:lvlOverride>
                  </w:num>
                </w:numbering>
                """));
            numbering.FeedData(xml);
            main.Document.Save();
        }

        var paragraphs = new DocxSlimExtractor().Extract(_docx).Paragraphs;

        Assert.Equal(["I.", "a)", "b)", "II.", "a)"], paragraphs.Select(p => p.NumberLabel));
        Assert.Equal([1, 2, 2, 1, 2], paragraphs.Select(p => p.NumberingDepth));
    }

    private static Paragraph P(int level, string text) => new(
        new ParagraphProperties(new NumberingProperties(
            new NumberingLevelReference { Val = level }, new NumberingId { Val = 1 })),
        new Run(new Text(text)));
}
