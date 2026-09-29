using System.Text.Json;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.Tests;

public sealed class DocumentSupportStatusTests
{
    [Fact]
    public void Missing_route_is_support_not_proven_not_ood()
    {
        var status = DocumentSupportStatus.From(Outline(headings: 0, route: null, paragraphCount: 4));

        Assert.Equal(DocumentSupportStatus.SupportNotProven, status.ReliabilityStatus);
        Assert.Equal(DocumentSupportStatus.CompletedWithoutSupportedRoute, status.ExtractionStatus);
    }

    [Fact]
    public void Supported_normal_route_remains_normal()
    {
        var status = DocumentSupportStatus.From(Outline(headings: 1, route: "auto:semantic", paragraphCount: 2));

        Assert.Equal(DocumentSupportStatus.Completed, status.ExtractionStatus);
        Assert.Equal(DocumentSupportStatus.SupportEstablished, status.ReliabilityStatus);
    }

    [Fact]
    public void Empty_nonempty_result_without_route_is_fail_loud_and_has_no_accuracy_claim()
    {
        var outline = Outline(headings: 0, route: null, paragraphCount: 3);
        var status = DocumentSupportStatus.From(outline);
        var json = JsonSerializer.Serialize(status);

        Assert.Equal(DocumentSupportStatus.CompletedWithoutSupportedRoute, status.ExtractionStatus);
        Assert.Equal(DocumentSupportStatus.SupportNotProven, status.ReliabilityStatus);
        Assert.DoesNotContain("99", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accuracy", json, StringComparison.OrdinalIgnoreCase);
    }

    private static DocumentOutline Outline(int headings, string? route, int paragraphCount) => new()
    {
        File = "sample.docx",
        ParagraphCount = paragraphCount,
        Headings = Enumerable.Range(0, headings).Select(index => new HeadingRecord
        {
            Index = index,
            Level = 1,
            Text = $"Heading {index}",
            Source = HeadingSource.LocalRules,
        }).ToArray(),
        DeterministicRoute = route,
    };
}
