using System.Text;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class DoclingLayoutOutlineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dhx-docling-" + Guid.NewGuid().ToString("N"));

    public DoclingLayoutOutlineTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void DoclingJsonAlignsHeadingsAndIgnoresNonHeadingLabels()
    {
        var json = Path.Combine(_dir, "probe.json");
        File.WriteAllText(json, """
        {
          "texts": [
            { "label": "title", "text": "Annual Report", "prov": [{ "page_no": 1, "bbox": { "top": 760 } }] },
            { "label": "section_header", "text": "1. Overview", "prov": [{ "page_no": 2, "bbox": { "top": 720 } }] },
            { "label": "table", "text": "TOTAL", "prov": [{ "page_no": 2, "bbox": { "top": 530 } }] },
            { "label": "text", "text": "This is body text.", "prov": [{ "page_no": 2, "bbox": { "top": 500 } }] },
            { "label": "section_header", "text": "1.1 Scope", "prov": [{ "page_no": 3, "bbox": { "top": 710 } }] }
          ]
        }
        """, Encoding.UTF8);
        var slim = new SlimDocument
        {
            FileName = "x.docx",
            SourcePath = "x.docx",
            Paragraphs =
            [
                P(0, "Annual Report"),
                P(1, "The introductory body is here."),
                P(2, "1. Overview This paragraph continues after the heading."),
                P(3, "TOTAL 42 43"),
                P(4, "1.1 Scope The scope body follows."),
            ],
        }.Build();
        var mode = new DocumentModeReport(DocumentMode.FormatDriven, 0, 0, 0, 0, 0, 0, false);

        var result = DoclingLayoutOutline.TryBuild("x.docx", slim, mode, json);

        Assert.Equal(3, result.Headings.Count);
        Assert.Equal("Annual Report", result.Headings[0].Text);
        Assert.Equal("1. Overview", result.Headings[1].Text);
        Assert.Equal("1.1 Scope", result.Headings[2].Text);
        Assert.DoesNotContain(result.Headings, h => h.Text == "TOTAL");
        Assert.All(result.Headings, h =>
        {
            Assert.Equal(DoclingLayoutOutline.Basis, h.ConfidenceBasis);
            Assert.Equal(h.Text, h.OriginalText![h.HeadingSpan!.Start..h.HeadingSpan.End]);
        });
    }

    [Fact]
    public async Task PipelineCanUseExplicitDoclingJsonAsDeterministicRoute()
    {
        var docx = Path.Combine(_dir, "sample.docx");
        var json = Path.Combine(_dir, "sample.docling.json");
        SampleDocumentFactory.Create(docx);
        File.WriteAllText(json, """
        {
          "texts": [
            { "label": "section_header", "text": "Chương 1. Tổng quan hệ thống", "prov": [{ "page_no": 1 }] },
            { "label": "section_header", "text": "1.1. Phạm vi", "prov": [{ "page_no": 1 }] },
            { "label": "section_header", "text": "1.2. Thuật ngữ", "prov": [{ "page_no": 1 }] }
          ]
        }
        """, Encoding.UTF8);

        using var pipeline = new HeaderExtractionPipeline(new PipelineOptions
        {
            DisableLlm = true,
            DoclingJsonPath = json,
        });

        var outline = await pipeline.RunAsync(docx);

        Assert.Equal("auto:docling-layout", outline.DeterministicRoute);
        Assert.Equal(3, outline.Headings.Count);
        Assert.All(outline.Headings, h => Assert.Equal(DoclingLayoutOutline.Basis, h.ConfidenceBasis));
    }

    private static SlimParagraph P(int index, string text) => new()
    {
        Index = index,
        StableId = "p" + index,
        Text = text,
    };

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
