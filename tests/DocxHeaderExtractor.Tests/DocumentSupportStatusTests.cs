using System.Text.Json;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

public sealed class DocumentSupportStatusTests
{
    [Fact]
    public void Unknown_mode_is_support_not_proven_not_ood()
    {
        var status = DocumentSupportStatus.From(Outline(DocumentMode.Unknown, headings: 0, route: null, paragraphCount: 4));

        Assert.Equal(DocumentSupportStatus.SupportNotProven, status.ReliabilityStatus);
        Assert.Equal(DocumentSupportStatus.CompletedWithoutSupportedRoute, status.ExtractionStatus);
    }

    [Fact]
    public void Unknown_mode_can_still_have_headings_without_forcing_empty_or_error()
    {
        var status = DocumentSupportStatus.From(Outline(DocumentMode.Unknown, headings: 2, route: "auto:conservative", paragraphCount: 4));

        Assert.Equal(DocumentSupportStatus.Completed, status.ExtractionStatus);
        Assert.Equal(DocumentSupportStatus.SupportNotProven, status.ReliabilityStatus);
    }

    [Fact]
    public void Supported_normal_route_remains_normal()
    {
        var status = DocumentSupportStatus.From(Outline(DocumentMode.SemanticOnly, headings: 1, route: "auto:semantic", paragraphCount: 2));

        Assert.Equal(DocumentSupportStatus.Completed, status.ExtractionStatus);
        Assert.Equal(DocumentSupportStatus.SupportEstablished, status.ReliabilityStatus);
    }

    [Fact]
    public void Empty_nonempty_result_without_route_is_fail_loud_and_has_no_accuracy_claim()
    {
        var outline = Outline(DocumentMode.Unknown, headings: 0, route: null, paragraphCount: 3);
        var status = DocumentSupportStatus.From(outline);
        var json = JsonSerializer.Serialize(status);

        Assert.Equal(DocumentSupportStatus.CompletedWithoutSupportedRoute, status.ExtractionStatus);
        Assert.Equal(DocumentSupportStatus.SupportNotProven, status.ReliabilityStatus);
        Assert.DoesNotContain("99", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accuracy", json, StringComparison.OrdinalIgnoreCase);
    }

    private static DocumentOutline Outline(DocumentMode mode, int headings, string? route, int paragraphCount) => new()
    {
        File = "sample.docx",
        ParagraphCount = paragraphCount,
        Headings = Enumerable.Range(0, headings).Select(index => new HeadingRecord
        {
            Index = index,
            Level = 1,
            Text = $"Heading {index}",
            Source = HeadingSource.Heuristic,
        }).ToArray(),
        DeterministicRoute = route,
        DocumentMode = new DocumentModeReport(mode, paragraphCount, 0, 0, 0, 0, 0, false),
    };
}
