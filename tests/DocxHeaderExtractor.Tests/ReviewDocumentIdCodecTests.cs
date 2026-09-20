using DocxHeaderExtractor.Application.Review;

namespace DocxHeaderExtractor.Tests;

public sealed class ReviewDocumentIdCodecTests
{
    [Theory]
    [InlineData(@"C:\Temp\review.docx")]
    [InlineData("/tmp/review.docx")]
    [InlineData("/tmp/tài liệu/đề mục.docx")]
    public void Versioned_route_id_round_trips_document_identity(string documentId)
    {
        var routeId = ReviewDocumentIdCodec.Encode(documentId);

        Assert.StartsWith("r1_", routeId);
        Assert.DoesNotContain('/', routeId);
        Assert.DoesNotContain('+', routeId);
        Assert.DoesNotContain('=', routeId);
        Assert.True(ReviewDocumentIdCodec.TryDecode(routeId, out var decoded));
        Assert.Equal(documentId, decoded);
    }

    [Theory]
    [InlineData("r1_")]
    [InlineData("r1_not-valid*")]
    [InlineData("r1_A")]
    [InlineData("/tmp/review.docx")]
    public void Malformed_or_unsafe_route_ids_are_rejected(string routeId)
    {
        Assert.False(ReviewDocumentIdCodec.TryDecode(routeId, out _));
    }

    [Fact]
    public void Safe_legacy_route_ids_remain_supported()
    {
        Assert.True(ReviewDocumentIdCodec.TryDecode("doc-4", out var decoded));
        Assert.Equal("doc-4", decoded);
    }
}
