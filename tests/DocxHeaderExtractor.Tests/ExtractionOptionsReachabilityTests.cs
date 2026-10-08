using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Application.Review;
using DocxHeaderExtractor.Cli;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Review;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using ReviewSpan = DocxHeaderExtractor.Application.Review.TextOffsetSpan;

namespace DocxHeaderExtractor.Tests;

public sealed class ExtractionOptionsReachabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"dhx-extraction-options-{Guid.NewGuid():N}");

    public ExtractionOptionsReachabilityTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pipeline_and_review_use_the_same_source_order_including_after_table(bool includeTables)
    {
        var path = CreateFixture();
        var options = new PipelineOptions { DisableLlm = true, Extraction = new ExtractionOptions { IncludeTables = includeTables } };
        using var pipeline = new DocxExtractionPipeline(options, new NoCallInferenceTransportFactory());
        var actual = await pipeline.RunDocumentExecutionAsync(path);
        var review = new ReviewDocumentSourceReader(options).Read(path);
        var expectedTexts = includeTables ? new[] { "Before table", "Inside table", "After table" } : new[] { "Before table", "After table" };
        Assert.Equal(expectedTexts, actual.Result.SourceCatalog.Units.Select(unit => unit.Text));
        Assert.Equal(expectedTexts, review.Document.Paragraphs.Select(p => p.Text));
        Assert.Equal(review.Document.Paragraphs.Select(p => (p.SourceId, p.SourceOrdinal, p.Text)),
            actual.Result.SourceCatalog.Units.Select(unit => (unit.SourceId, unit.SourceOrdinal, unit.Text)));
        Assert.Equal(expectedTexts.Length, actual.Outline.ParagraphCount);
        Assert.Equal(Enumerable.Range(0, expectedTexts.Length), review.SourceIndexes);
        var after = actual.Result.SourceCatalog.Units.Last();
        Assert.Equal("body[1]/p[2]", after.SourceId);
        Assert.Equal(includeTables ? 2 : 1, after.SourceOrdinal);
        Assert.Equal(0, actual.Result.Provenance.ProviderCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Product_and_review_writeback_target_after_table_not_the_table_paragraph(bool includeTables)
    {
        var path = CreateFixture();
        var before = File.ReadAllBytes(path);
        var extraction = new ExtractionOptions { IncludeTables = includeTables };
        var paragraph = new ReviewDocumentSourceReader(extraction).Read(path).Document.Paragraphs.Last();
        var product = new DocumentProductOutput(Convert.ToHexString(SHA256.HashData(before)).ToLowerInvariant(),
            [new DocumentProductHeading("after", paragraph.SourceOrdinal, paragraph.SourceId,
                new DocxTextSpan(0, paragraph.Text.Length), paragraph.Text, "HeadingTopic", 1, null, false, [])]);
        var productTarget = Path.Combine(_directory, "product.docx");
        var productResult = DocxProductWriteback.Apply(path, productTarget, product, extraction,
            new OutlineWritebackOptions { ApplyHeadingStyles = true });
        Assert.Equal(1, productResult.Applied);
        AssertWrittenParagraph(productTarget);

        var plan = new ApprovedWritebackPlan(path, ApprovedWritebackPlanStatus.Ready, "test",
            [new ReviewedHeadingDecision("after", paragraph.SourceId, paragraph.SourceOrdinal, paragraph.Text,
                paragraph.Text, 1, new ReviewSpan(0, paragraph.Text.Length), HumanReviewAction.Accept,
                ReviewState.Accepted, true, null)]);
        var reviewTarget = Path.Combine(_directory, "review.docx");
        Assert.Equal(1, ApprovedWritebackExecutor.Apply(path, reviewTarget, plan, extraction, explicitApproval: true).Applied);
        AssertWrittenParagraph(reviewTarget);
        Assert.Equal(before, File.ReadAllBytes(path));

        if (!includeTables)
        {
            // Human-review upload currently uses default extraction. An incompatible ordinal must
            // fail closed rather than write the table paragraph or silently reinterpret the plan.
            var error = Assert.Throws<InvalidOperationException>(() => ApprovedWritebackExecutor.Apply(
                path, Path.Combine(_directory, "mismatch.docx"), plan, new ExtractionOptions(), explicitApproval: true));
            Assert.Contains("writeback-source-mismatch", error.Message);
            Assert.False(File.Exists(Path.Combine(_directory, "mismatch.docx")));
        }
    }

    [Fact]
    public void Cli_no_tables_sets_the_extraction_policy()
    {
        Assert.False(CommandLineOptions.Parse(["input.docx", "--no-tables"]).Pipeline.Extraction.IncludeTables);
        Assert.True(CommandLineOptions.Parse(["input.docx"]).Pipeline.Extraction.IncludeTables);
    }

    private string CreateFixture()
    {
        var path = Path.Combine(_directory, "tables.docx");
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body(
            new Paragraph(new Run(new Text("Before table"))),
            new Table(new TableRow(new TableCell(new Paragraph(new Run(new Text("Inside table")))))),
            new Paragraph(new Run(new Text("After table")))));
        main.Document.Save();
        return path;
    }

    private static void AssertWrittenParagraph(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var paragraphs = doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().ToArray();
        Assert.Null(paragraphs[0].ParagraphProperties?.OutlineLevel);
        Assert.Null(paragraphs[1].ParagraphProperties?.OutlineLevel);
        Assert.Equal(0, paragraphs[2].ParagraphProperties!.OutlineLevel!.Val!.Value);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
