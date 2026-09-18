using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Projection;
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

        var document = await dispatcher.ExtractAsync(
            new AuthorityExtractionRequest(UploadedFile.FromLocalPath(path)));

        Assert.Equal("docx-canonical-vnext", document.Provenance.Route);
        Assert.Equal(
            DocxHeaderExtractor.Core.Models.ExecutionContracts.ExplicitUploadedDocxCanonical,
            document.Provenance.ExecutionContract);
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

    [Fact]
    public async Task Two_different_questions_read_one_unchanged_canonical_document()
    {
        // The freeze this whole architecture rests on. Asking for headings must not send anything
        // looking only for headings; the canonical graph is the same object both times.
        var path = Path.Combine(_directory, "structure.docx");
        SampleDocumentFactory.Create(path);
        using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var document = await pipeline.RunDocumentAsync(path);

        var headings = CanonicalProjector.Project(new ProjectionRequest(
            document, new ExtractionIntent { Task = ExtractionTask.Headings }));
        var structure = CanonicalProjector.Project(new ProjectionRequest(
            document, new ExtractionIntent { Task = ExtractionTask.DocumentStructure }));

        Assert.Equal(headings.Records.Count, structure.Records.Count);
        Assert.Equal(["text", "level", "parent", "source"], headings.Fields);
        Assert.Contains("semanticRole", structure.Fields);
    }

    [Fact]
    public async Task A_custom_projection_selects_fields_without_inventing_any()
    {
        var path = Path.Combine(_directory, "custom.docx");
        SampleDocumentFactory.Create(path);
        using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var document = await pipeline.RunDocumentAsync(path);

        var projected = CanonicalProjector.Project(new ProjectionRequest(document, new ExtractionIntent
        {
            Task = ExtractionTask.CustomProjection,
            UserInstruction = "Lay tieu de, loai tieu de va cap",
            RequestedFields = ["text", "semanticRole", "level"],
            OutputFormat = OutputFormat.Xlsx,
        }));

        Assert.Equal(["text", "semanticRole", "level"], projected.Fields);
        Assert.Equal(OutputFormat.Xlsx, projected.OutputFormat);
        Assert.All(projected.Records, record =>
            Assert.Equal(["text", "semanticRole", "level"], record.Fields.Keys));
    }

    [Fact]
    public async Task A_field_the_canonical_document_does_not_carry_is_refused_not_guessed()
    {
        var path = Path.Combine(_directory, "missing-field.docx");
        SampleDocumentFactory.Create(path);
        using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var document = await pipeline.RunDocumentAsync(path);

        var error = Assert.Throws<ArgumentException>(() => CanonicalProjector.Project(
            new ProjectionRequest(document, new ExtractionIntent
            {
                Task = ExtractionTask.CustomProjection,
                RequestedFields = ["text", "invoiceTotal"],
            })));

        Assert.Contains("invoiceTotal", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projection_is_deterministic_on_one_canonical_document()
    {
        var path = Path.Combine(_directory, "stable.docx");
        SampleDocumentFactory.Create(path);
        using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });
        var document = await pipeline.RunDocumentAsync(path);
        var intent = new ExtractionIntent { Task = ExtractionTask.Headings };

        var first = CanonicalProjector.Project(new ProjectionRequest(document, intent));
        var second = CanonicalProjector.Project(new ProjectionRequest(document, intent));

        Assert.Equal(
            first.Records.Select(record => string.Join("|", record.Fields.Values)),
            second.Records.Select(record => string.Join("|", record.Fields.Values)));
    }
}
