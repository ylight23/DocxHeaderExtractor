using System.Text.Json;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfCompanionIsolationTests
{
    private const string CorpusDocumentName = "010_Luat_An_ninh_mang_24-2018-QH14.docx";

    [Fact]
    public void Same_directory_same_basename_pdf_is_found()
    {
        var directory = CreateTempDirectory();
        var docx = Path.Combine(directory, "companion-test.docx");
        var pdf = Path.ChangeExtension(docx, ".pdf");
        try
        {
            SampleDocumentFactory.Create(docx);
            File.WriteAllBytes(pdf, [0x25, 0x50, 0x44, 0x46]);

            Assert.Equal(Path.GetFullPath(pdf), PdfTextbookOutline.FindSiblingPdf(docx));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void Corpus_pdf_is_not_discovered_without_same_directory_companion()
    {
        var directory = CreateTempDirectory();
        var docx = Path.Combine(directory, CorpusDocumentName);
        var corpusPdf = Path.Combine(
            PdfExtractorQualityBenchmarkProbe.RepositoryRoot(),
            "todo10_8", "heading_corpus_100", "01_phap_quy",
            Path.ChangeExtension(CorpusDocumentName, ".pdf"));
        try
        {
            SampleDocumentFactory.Create(docx);

            Assert.True(File.Exists(corpusPdf), $"Expected corpus fixture is missing: {corpusPdf}");
            Assert.False(File.Exists(Path.ChangeExtension(docx, ".pdf")));
            Assert.Null(PdfTextbookOutline.FindSiblingPdf(docx));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task Pipeline_uses_docx_route_when_only_corpus_pdf_exists()
    {
        var directory = CreateTempDirectory();
        var docx = Path.Combine(directory, CorpusDocumentName);
        try
        {
            SampleDocumentFactory.Create(docx);
            using var analyst = new BodyOnlyClassifier();
            using var pipeline = new AuthorityExtractionPipeline(
                new PipelineOptions { DisableLlm = false }, analyst);

            var outline = await pipeline.RunAsync(docx);

            Assert.Equal("docx-authority-v1", outline.DeterministicRoute);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dhx-pdf-companion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTempDirectory(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class BodyOnlyClassifier : IHeaderClassifier
    {
        public string ModelName => "test/body-only";
        public int ContextSize => 4096;
        public string RuntimeDescription => "test-only";
        public int SharedPrefixTokens => 0;

        public Task<ChunkResult> ClassifyAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChunkResult> CritiqueAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChunkResult> ClassifyHierarchyAsync(
            IReadOnlyList<HierarchyItem> context,
            IReadOnlyList<HierarchyItem> headings,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> BoundaryCutAsync(
            string systemPrompt, string userMessage, CancellationToken ct = default)
        {
            using var json = JsonDocument.Parse(userMessage);
            var blocks = json.RootElement.GetProperty("blocks").EnumerateArray()
                .Select(block => new
                {
                    id = block.GetProperty("id").GetString(),
                    role = "body_text",
                    confidence = 1.0,
                });
            return Task.FromResult(JsonSerializer.Serialize(new { blocks }));
        }

        public void Dispose() { }
    }
}
