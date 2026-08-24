using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfValidatedOutputPolicyTests
{
    [Fact]
    public void ProjectionUsesPdfSourceIdentityRatherThanDocxStableId()
    {
        var headings = new[]
        {
            Heading("shared-docx-id", "pdf-toc", "Contents entry"),
            Heading("shared-docx-id", "pdf-heading", "Real heading"),
        };
        var structures = new[]
        {
            new PdfValidatedStructure("pdf-toc", 1, null, "scope", "requires_review")
            {
                DomainRole = PdfDomainRole.OutlineReference,
                StructuralScope = "table_of_contents",
            },
            new PdfValidatedStructure("pdf-heading", 1, null, "scope", "requires_review")
            {
                DomainRole = PdfDomainRole.Unknown,
                StructuralScope = "document_body",
            },
        };

        var result = PdfValidatedOutputPolicy.ProjectDocumentOutline(headings, structures);

        var actual = Assert.Single(result);
        Assert.Equal("Real heading", actual.Text);
        Assert.Equal("pdf-heading", actual.SourceId);
        Assert.Equal(HeadingDecisionStatus.RequiresReview, actual.DecisionStatus);
    }

    private static HeadingRecord Heading(string stableId, string sourceId, string text) => new()
    {
        Index = sourceId == "pdf-toc" ? 1 : 2,
        StableId = stableId,
        SourceId = sourceId,
        Level = 1,
        Text = text,
        HeadingSpan = new TextOffsetSpan(0, text.Length),
        Source = HeadingSource.Structure,
        Confidence = .9,
    };
}
