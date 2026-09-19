using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The PDF lane, run on a real PDF.
/// <para>
/// A PDF upload is extracted from that PDF alone and produces its own canonical document. It is not
/// compared with, corrected by, or merged into the DOCX of the same material - that is a separate
/// thing a user would have to ask for.
/// </para>
/// </summary>
public sealed class PdfCanonicalLaneTests
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string SameMaterialAsDocx =
        "todo10_8/heading_corpus_95_word/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.docx";

    [Fact]
    public async Task A_pdf_upload_produces_its_own_canonical_document()
    {
        var file = UploadedFile.FromLocalPath(Path.Combine(RepositoryRoot(), Pdf));
        Assert.Equal(SourceType.Pdf, file.DetectedType);

        var document = await PdfCanonicalExtraction.RunAsync(file, new PipelineOptions { DisableLlm = true });

        Assert.Equal("pdf-canonical-vnext", document.Provenance.Route);
        Assert.Equal("pdf-source-document", document.Provenance.SourceCatalogKind);
        Assert.Equal(ExecutionContracts.ExplicitUploadedPdfCanonical, document.Provenance.ExecutionContract);
        Assert.Equal("pdf", document.DocumentIdentity.SourceKind);
        // Source occurrences exist even with no model: the lane reads the PDF itself.
        Assert.NotEmpty(document.SourceCatalog.Units);
    }

    [Fact]
    public async Task The_source_universe_is_every_text_block_not_a_filtered_candidate_set()
    {
        // The same ceiling rule the DOCX lane holds. A repeated line, a header-zone line or a
        // table-like line is still an occurrence the model must be allowed to judge; the annotation
        // travels with it as evidence rather than removing it. If this ever drops below the raw
        // block count, the PDF lane has grown the hidden gate the DOCX lane had removed.
        var file = UploadedFile.FromLocalPath(Path.Combine(RepositoryRoot(), Pdf));

        var document = await PdfCanonicalExtraction.RunAsync(file, new PipelineOptions { DisableLlm = true });

        var withArtefacts = document.SourceCatalog.Units
            .Count(unit => unit.Text.Trim().Length <= 3);
        Assert.True(document.SourceCatalog.Units.Count > 100,
            $"source universe collapsed to {document.SourceCatalog.Units.Count} units");
        // Page numbers and other short artefacts are present as occurrences, not filtered away.
        Assert.True(withArtefacts > 0, "short layout artefacts were removed from the source universe");
    }

    [Fact]
    public async Task The_pdf_lane_never_reads_the_docx_of_the_same_material()
    {
        // Both files exist in this repository, side by side in the corpus, which is exactly the
        // arrangement the old sibling lookup exploited. The PDF result must be built from the PDF.
        var root = RepositoryRoot();
        Assert.True(File.Exists(Path.Combine(root, SameMaterialAsDocx)));
        var file = UploadedFile.FromLocalPath(Path.Combine(root, Pdf));

        var document = await PdfCanonicalExtraction.RunAsync(file, new PipelineOptions { DisableLlm = true });

        Assert.All(document.SourceCatalog.Units, unit =>
            Assert.Equal("pdf", unit.SourceAnchor.SourceType));
        Assert.DoesNotContain(document.SourceCatalog.Units, unit =>
            unit.SourceId.StartsWith("body[1]/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pdf_and_a_docx_of_the_same_material_are_two_independent_documents()
    {
        var root = RepositoryRoot();
        var pdf = await PdfCanonicalExtraction.RunAsync(
            UploadedFile.FromLocalPath(Path.Combine(root, Pdf)), new PipelineOptions { DisableLlm = true });
        using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var docx = await pipeline.RunDocumentAsync(Path.Combine(root, SameMaterialAsDocx));

        Assert.NotEqual(pdf.Provenance.Route, docx.Provenance.Route);
        Assert.NotEqual(pdf.Provenance.ExecutionContract, docx.Provenance.ExecutionContract);
        // Neither carries the other's source identities.
        var pdfIds = pdf.SourceCatalog.Units.Select(unit => unit.SourceId).ToHashSet(StringComparer.Ordinal);
        var docxIds = docx.SourceCatalog.Units.Select(unit => unit.SourceId).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(pdfIds.Intersect(docxIds, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_pdf_canonical_document_projects_with_the_same_intent_as_a_docx_one()
    {
        // The point of sharing the semantic stage: one projection contract over both formats.
        var file = UploadedFile.FromLocalPath(Path.Combine(RepositoryRoot(), Pdf));
        var document = await PdfCanonicalExtraction.RunAsync(file, new PipelineOptions { DisableLlm = true });

        var projected = CanonicalProjector.Project(new ProjectionRequest(
            document, new ExtractionIntent { Task = ExtractionTask.Headings }));

        Assert.Equal(["text", "level", "parent", "source"], projected.Fields);
    }

    [Fact]
    public async Task A_docx_handed_to_the_pdf_lane_is_refused_rather_than_parsed()
    {
        var file = UploadedFile.FromLocalPath(Path.Combine(RepositoryRoot(), SameMaterialAsDocx));

        await Assert.ThrowsAsync<UnsupportedSourceException>(() =>
            PdfCanonicalExtraction.RunAsync(file, new PipelineOptions { DisableLlm = true }));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
