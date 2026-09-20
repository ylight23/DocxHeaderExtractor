using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The three boundaries, and the rule that separates them.
/// <para>
/// Upload decides the format from bytes. Authority turns one file into one canonical document
/// without knowing what will be asked of it. Projection answers the question. The load-bearing
/// property is the middle one: the same file must yield the same canonical document no matter what
/// the user wants out of it, or two answers to two questions can never be compared.
/// </para>
/// </summary>
public sealed class ExtractionBoundaryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"dhx-boundary-{Guid.NewGuid():N}");

    public ExtractionBoundaryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void The_upload_record_identifies_the_file_by_its_bytes_not_its_name()
    {
        var path = Path.Combine(_directory, "invoice.pdf");
        SampleDocumentFactory.Create(path);

        var file = UploadedFile.FromLocalPath(path, originalFileName: "Bao cao quy 4.pdf");

        Assert.Equal(SourceType.Docx, file.DetectedType);
        // The name survives only so a download can carry it.
        Assert.Equal("Bao cao quy 4.pdf", file.OriginalFileName);
        Assert.Equal(64, file.Sha256.Length);
    }

    [Fact]
    public void The_hash_is_of_the_bytes_that_were_actually_read()
    {
        var first = Path.Combine(_directory, "a.docx");
        var second = Path.Combine(_directory, "b.docx");
        SampleDocumentFactory.Create(first);
        File.Copy(first, second);

        Assert.Equal(
            UploadedFile.FromLocalPath(first).Sha256,
            UploadedFile.FromLocalPath(second).Sha256);
    }

    [Fact]
    public async Task The_dispatcher_sends_a_docx_to_the_docx_lane_and_looks_nowhere_else()
    {
        var path = Path.Combine(_directory, "report.docx");
        SampleDocumentFactory.Create(path);
        // A same-named PDF beside it must change nothing.
        await File.WriteAllTextAsync(Path.Combine(_directory, "report.pdf"), "%PDF-1.7\n");
        using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var dispatcher = new CanonicalExtractionDispatcher(
            new DocxCanonicalSourceExtractor(pipeline), new PdfCanonicalSourceExtractor(new PipelineOptions { DisableLlm = true }));

        var execution = await dispatcher.ExtractAsync(
            new AuthorityExtractionRequest(UploadedFile.FromLocalPath(path)));

        Assert.Equal("docx-canonical-vnext", execution.Result.Provenance.Route);
        Assert.Equal(
            DocxHeaderExtractor.Core.Models.ExecutionContracts.ExplicitUploadedDocxCanonical,
            execution.Result.Provenance.ExecutionContract);
    }

    [Fact]
    public async Task A_file_of_an_unowned_format_is_refused_by_the_dispatcher()
    {
        var path = Path.Combine(_directory, "notes.docx");
        await File.WriteAllBytesAsync(path, [0x00, 0x01, 0x02, 0x03]);
        var dispatcher = new CanonicalExtractionDispatcher(new PdfCanonicalSourceExtractor(new PipelineOptions { DisableLlm = true }));

        await Assert.ThrowsAsync<UnsupportedSourceException>(() => dispatcher.ExtractAsync(
            new AuthorityExtractionRequest(UploadedFile.FromLocalPath(path))));
    }

    [Fact]
    public void Intent_cannot_reach_the_authority_boundary_at_all()
    {
        // Structural, not behavioural: AuthorityExtractionRequest carries one member, and no
        // projection type is reachable from the authority pipeline's signature. A future field
        // carrying intent would fail here rather than quietly making canonical truth depend on
        // the question being asked.
        Assert.Equal(
            [typeof(UploadedFile)],
            typeof(AuthorityExtractionRequest).GetProperties().Select(property => property.PropertyType));

        Assert.DoesNotContain(
            typeof(AuthorityExtractionPipeline).GetMethods()
                .SelectMany(method => method.GetParameters())
                .Select(parameter => parameter.ParameterType),
            type => type.Namespace?.Contains("Projection", StringComparison.Ordinal) == true);
    }

}
