using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Repair;

namespace DocxHeaderExtractor.Tests;

public sealed class PartialKeyPackageTests
{
    [Fact]
    public async Task PartialKeyPackageWritesReviewArtifactsAndPartialKeyMarker()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dhx-key-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var input = Path.Combine(temp, "sample.docx");
        SampleDocumentFactory.Create(input);

        var packager = new PartialKeyPackage(new PipelineOptions
        {
            DisableLlm = true,
        });

        var result = await packager.RunAsync(
            input,
            new PartialKeyPackageOptions(Path.Combine(temp, "packages"), MaxHeadings: 2));

        Assert.True(File.Exists(result.DraftKeyPath));
        Assert.True(File.Exists(result.ReviewCsvPath));
        Assert.True(File.Exists(result.OutlineJsonPath));
        Assert.InRange(result.SelectedHeadings, 1, 2);
        Assert.True(result.TotalHeadings >= result.SelectedHeadings);
        Assert.StartsWith("distributed_even:", result.SampleStrategy);
        Assert.True(result.LineProbe.TextParagraphs > 0);

        var key = await File.ReadAllTextAsync(result.DraftKeyPath);
        Assert.Contains("partial_human", key);
        Assert.Contains("@body", key);

        var csv = await File.ReadAllTextAsync(result.ReviewCsvPath);
        Assert.Contains("reviewAction,reviewLevel,reviewText", csv);
        Assert.Contains("# route,", csv);
    }

    [Fact]
    public async Task DistributedSampleCoversTheEndOfTheOutline()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dhx-key-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var input = Path.Combine(temp, "synthetic.docx");
        SampleDocumentFactory.Create(input);
        var outline = new DocumentOutline
        {
            File = input,
            ParagraphCount = 10,
            CandidateCount = 10,
            Headings = Enumerable.Range(1, 10).Select(i => new HeadingRecord
            {
                Index = i,
                StableId = $"body[1]/p[{i}]",
                Level = 1,
                Text = $"Heading {i}",
                Source = HeadingSource.Structure,
                Confidence = 0.9,
            }).ToList(),
        };

        var packager = new PartialKeyPackage(new PipelineOptions());

        var result = await packager.RunAsync(
            input,
            outline,
            new PartialKeyPackageOptions(Path.Combine(temp, "packages"), MaxHeadings: 3));

        var key = await File.ReadAllTextAsync(result.DraftKeyPath);
        Assert.Contains("Heading 1", key);
        Assert.Contains("Heading 6", key);
        Assert.Contains("Heading 10", key);
        Assert.DoesNotContain("Heading 2", key);
    }
}
