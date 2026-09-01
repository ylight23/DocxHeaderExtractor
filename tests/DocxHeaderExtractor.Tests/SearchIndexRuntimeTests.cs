using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class SearchIndexRuntimeTests
{
    [Fact]
    public async Task Replace_is_idempotent_and_removes_stale_chunks()
    {
        var index = new InMemorySearchIndex();
        await index.ReplaceDocumentAsync("doc-1", [Document("doc-1", "chunk-1", "alpha")]);
        await index.ReplaceDocumentAsync("doc-1", [Document("doc-1", "chunk-2", "beta")]);
        await index.UpsertDocumentAsync(Document("doc-1", "chunk-2", "beta"));

        var snapshot = index.Snapshot();
        var document = Assert.Single(snapshot);
        Assert.Equal("chunk-2", document.ChunkId);
        Assert.DoesNotContain(snapshot, item => item.ChunkId == "chunk-1");
    }

    [Fact]
    public async Task Retrieval_applies_document_section_and_structural_type_filters()
    {
        var index = new InMemorySearchIndex();
        await index.UpsertDocumentAsync(Document(
            "doc-1", "chunk-1", "architecture figure overview", "section-1", [nameof(StructuralElementType.Figure)]));
        await index.UpsertDocumentAsync(Document(
            "doc-2", "chunk-2", "architecture table overview", "section-2", [nameof(StructuralElementType.Table)]));

        var hits = await index.SearchAsync(new RetrievalQuery(
            "architecture overview",
            topK: 10,
            documentIds: ["doc-1"],
            sectionIds: ["section-1"],
            structuralTypes: [StructuralElementType.Figure]));

        var hit = Assert.Single(hits);
        Assert.Equal("doc-1", hit.DocumentId);
        Assert.Equal("chunk-1", hit.ChunkId);
        Assert.Equal("architecture figure overview", hit.Text);
    }

    [Fact]
    public async Task Delete_removes_all_chunks_for_document()
    {
        var index = new InMemorySearchIndex();
        await index.UpsertDocumentAsync(Document("doc-1", "chunk-1", "alpha"));
        await index.UpsertDocumentAsync(Document("doc-1", "chunk-2", "beta"));
        await index.UpsertDocumentAsync(Document("doc-2", "chunk-3", "gamma"));

        await index.DeleteDocumentAsync("doc-1");

        var snapshot = index.Snapshot();
        var remaining = Assert.Single(snapshot);
        Assert.Equal("doc-2", remaining.DocumentId);
    }

    [Fact]
    public async Task Runtime_replaces_projection_for_one_document()
    {
        var source = new DocumentSourceUnit(
            "source-1",
            0,
            "Architecture runtime",
            new SourceAnchor { SourceType = "docx", ParagraphId = "p1", ParagraphIndex = 0 },
            new StructuralSpan(0, "Architecture runtime".Length));
        var catalog = new DocumentSourceCatalog([source]);
        var element = new ValidatedStructuralElement
        {
            Id = "heading-1",
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            Sources = [new SourceReference("source-1", 0, new StructuralSpan(0, source.Text.Length))],
            Text = source.Text,
            Level = 1,
            Validation = new StructuralValidation(true, true, true, true, 1, true, true, true, null),
            Decision = new StructuralDecision("test", "accepted", 1, "parser-facts"),
        };
        var structure = ValidatedStructure.FromElements([element], []);
        var section = new StructuralSection(
            "section:heading-1", "heading-1", null, ["heading-1"], [source.SourceId], [element.Id]);
        var chunk = new DocumentChunk(
            "section:heading-1:chunk:1", section.Id, [source.SourceId], [element.Id], source.Text, 2);
        var extraction = new DocumentExtractionResult(
            new DocumentIdentity("doc-1", "doc.docx", "docx", "doc.docx"),
            catalog,
            structure,
            [section],
            [chunk],
            new DocumentExtractionProvenance("docx-authority-v1", "docx-source-document", 0));
        var index = new InMemorySearchIndex();

        await new SearchIndexRuntime(index).ReplaceAsync(extraction);

        var indexed = Assert.Single(index.Snapshot());
        Assert.Equal(extraction.DocumentIdentity.DocumentId, indexed.DocumentId);
        Assert.Equal(chunk.Id, indexed.ChunkId);
        Assert.Equal(chunk.Text, indexed.Text);
    }

    private static SearchIndexDocument Document(
        string documentId,
        string chunkId,
        string text,
        string sectionId = "section-1",
        IReadOnlyList<string>? structuralTypes = null) => new(
        documentId,
        chunkId,
        sectionId,
        text,
        ["source-1"],
        structuralTypes ?? [nameof(StructuralElementType.Heading)],
        ["heading-1"],
        [],
        []);
}
