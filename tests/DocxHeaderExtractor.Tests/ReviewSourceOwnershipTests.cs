using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Review;

namespace DocxHeaderExtractor.Tests;

public sealed class ReviewSourceOwnershipTests
{
    [Fact]
    public void Review_reader_preserves_source_and_nonblank_source_index_order()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dhx-review-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "review.docx");
            SampleDocumentFactory.Create(path);
            var expected = new OpenXmlDocumentSource().Read(path);
            var actual = new ReviewDocumentSourceReader().Read(path);
            Assert.Equal(expected.Paragraphs.Select(p => (p.SourceOrdinal, p.Text)),
                actual.Document.Paragraphs.Select(p => (p.SourceOrdinal, p.Text)));
            Assert.Equal(expected.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.Text))
                .Select(p => p.SourceOrdinal), actual.SourceIndexes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Common_source_ir_and_review_snapshot_have_distinct_names_and_contracts()
    {
        var assembly = typeof(ReviewDocumentSourceSnapshot).Assembly;
        Assert.Single(assembly.GetTypes().Where(type => type.Name == "DocumentSourceSnapshot"));
        Assert.Equal(new[] { "Document", "SourceIndexes" },
            typeof(ReviewDocumentSourceSnapshot).GetProperties().Select(p => p.Name));
        Assert.Null(assembly.GetType("DocxHeaderExtractor.DocumentProcessing.Review.DocumentSourceSnapshot"));
        Assert.Null(assembly.GetType("DocxHeaderExtractor.DocumentProcessing.Review.AuthorityDocumentSourceReader"));
    }
}
