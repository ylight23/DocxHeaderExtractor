using DocxHeaderExtractor.DocumentProcessing.Changes;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.Tests;

public sealed class TrackedRevisionSideTests
{
    [Fact]
    public void Revision_location_from_document_does_not_label_template_only_change()
    {
        var before = RawDocxFixtureTests.Build("<w:p><w:r><w:t>Removed unique</w:t></w:r></w:p><w:p><w:r><w:t>Kept unique</w:t></w:r></w:p>");
        var after = RawDocxFixtureTests.Build("<w:p><w:ins w:id=\"1\" w:author=\"A\"><w:r><w:t>Kept unique</w:t></w:r></w:ins></w:p>");
        try
        {
            var result = DocxComparer.Compare(DocxVersionReader.Read(before), DocxVersionReader.Read(after));
            Assert.NotEmpty(result.DocumentRevisions!);
            var removed = Assert.Single(result.Changes, c => c.Kind == "paragraph_only_in_template");
            Assert.DoesNotContain("tracked_revision_recorded", removed.Note ?? "");
        }
        finally { File.Delete(before); File.Delete(after); }
    }
}
