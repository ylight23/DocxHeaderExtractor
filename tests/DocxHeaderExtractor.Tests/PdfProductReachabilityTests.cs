using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The PDF lane, reached the way a user reaches it.
/// <para>
/// The lane and the dispatcher were built and tested, and the product could not call either: every
/// upload went through <c>LegacyDocConverter.EnsureDocx</c> first, which refuses anything that is
/// not OOXML on its extension, and the DOCX pipeline behind it refuses a PDF by its bytes. So a PDF
/// upload failed in the Web, CLI and MCP hosts while the library tests all passed. A lane no host
/// can call is not a supported format, and no local suite could have said so - every one of them
/// called the lane directly.
/// </para>
/// <para>
/// These go through <see cref="PipelineDocumentExtractionTool"/>, which is the single tool all
/// three hosts construct, so they fail if the wiring is removed from any of them at once.
/// </para>
/// </summary>
public sealed class PdfProductReachabilityTests : IDisposable
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"dhx-reach-{Guid.NewGuid():N}");

    public PdfProductReachabilityTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_tool_every_host_builds_extracts_an_uploaded_pdf()
    {
        var outline = await RunAsync(CopyPdf("minutes.pdf"));

        Assert.Equal("pdf-canonical-vnext", outline.DeterministicRoute);
        // Source occurrences, the same thing this field counts on the DOCX side. With no model
        // there are deliberately no headings - the canonical lanes have no heuristic that proposes
        // one - so what is proved here is that the PDF was read, not that it was understood.
        Assert.True(outline.ParagraphCount > 100, $"pdf reported {outline.ParagraphCount} source units");
        Assert.NotNull(outline.RouteAudit);
        Assert.Empty(outline.Headings);
    }

    [Fact]
    public async Task A_pdf_upload_carries_model_proposals_all_the_way_to_the_host_outline()
    {
        // The stage after reading: with something answering, the lane must place headings on the
        // outline a host consumes. The script claims occurrences rather than judging them, so this
        // measures the wiring between the semantic stage and the outline, nothing about the model.
        using var model = new ScriptedSemanticClassifier();
        var outline = await RunAsync(CopyPdf("claimed.pdf"), model: model);

        Assert.Equal("pdf-canonical-vnext", outline.DeterministicRoute);
        Assert.NotEmpty(outline.Headings);
        Assert.True(model.Calls > 0, "the PDF lane never reached the classifier");
        Assert.Equal("scripted-owned-prefix", outline.Model);
        // The text on the outline is the source occurrence's, never the model's echo of it.
        Assert.All(outline.Headings, heading => Assert.False(string.IsNullOrWhiteSpace(heading.Text)));
    }

    [Fact]
    public async Task A_docx_upload_still_takes_the_docx_lane()
    {
        var path = Path.Combine(_directory, "report.docx");
        SampleDocumentFactory.Create(path);

        var outline = await RunAsync(path);

        Assert.Equal("docx-canonical-vnext", outline.DeterministicRoute);
    }

    [Fact]
    public async Task The_lane_follows_the_bytes_even_when_the_name_says_otherwise()
    {
        // The whole reason detection is byte-based. Naming a PDF ".docx" used to reach an OOXML
        // reader; it must now reach the PDF lane, and a DOCX named ".pdf" must not.
        var pdfNamedDocx = CopyPdf("minutes.docx");
        var docxNamedPdf = Path.Combine(_directory, "report.pdf");
        SampleDocumentFactory.Create(docxNamedPdf);

        Assert.Equal("pdf-canonical-vnext", (await RunAsync(pdfNamedDocx)).DeterministicRoute);
        Assert.Equal("docx-canonical-vnext", (await RunAsync(docxNamedPdf)).DeterministicRoute);
    }

    [Fact]
    public async Task The_repair_loop_reaches_the_pdf_lane_rather_than_being_ignored()
    {
        // The tool declares SupportsRepair for every format it accepts. A quarantine that silently
        // did nothing on one lane would make the harness's repair loop mean two different things
        // depending on what was uploaded - and it would look like a model failure, not a wiring one.
        var path = CopyPdf("quarantine.pdf");
        var full = await RunAsync(path, model: new ScriptedSemanticClassifier());
        Assert.NotEmpty(full.Headings);

        var repaired = await RunAsync(
            path,
            quarantine: full.Headings.Select(heading => heading.Index).ToHashSet(),
            model: new ScriptedSemanticClassifier());

        Assert.True(repaired.Headings.Count < full.Headings.Count,
            $"quarantine changed nothing: {full.Headings.Count} headings before and after");
    }

    [Fact]
    public async Task The_dispatcher_re_reads_the_file_rather_than_trusting_an_older_record()
    {
        // The record can be built before the bytes are finished being written. Routing on a stale
        // type sends the file to a parser that was never given the format it expects; routing on
        // fresh bytes while passing a stale hash records an identity that is not what was parsed.
        var path = Path.Combine(_directory, "swapped.docx");
        SampleDocumentFactory.Create(path);
        var stale = UploadedFile.FromLocalPath(path);
        Assert.Equal(SourceType.Docx, stale.DetectedType);

        File.Copy(Path.Combine(RepositoryRoot(), Pdf.Replace('/', Path.DirectorySeparatorChar)), path, overwrite: true);
        using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions { DisableLlm = true });
        using var pdfLane = new PdfCanonicalSourceExtractor(new PipelineOptions { DisableLlm = true });
        var dispatcher = new CanonicalExtractionDispatcher(
            new DocxCanonicalSourceExtractor(pipeline), pdfLane);

        var execution = await dispatcher.ExtractAsync(new AuthorityExtractionRequest(stale));

        Assert.Equal("pdf-canonical-vnext", execution.Result.Provenance.Route);
        // The hash travelled with the routing decision: what is recorded is what was read.
        Assert.NotEqual(stale.Sha256, execution.CompatibilityOutline.ProductOutput!.SourceDocumentSha256);
    }

    private static async Task<DocumentOutline> RunAsync(
        string path, IReadOnlySet<int>? quarantine = null, IHeaderClassifier? model = null)
    {
        var options = new PipelineOptions { DisableLlm = model is null };
        using var tool = model is null
            ? new PipelineDocumentExtractionTool(options)
            : new PipelineDocumentExtractionTool(options, model);
        return await tool.ExecuteAsync(new AgentToolInvocation(
            new DocumentAgentRequest(path),
            1,
            quarantine is null ? null : new AgentRepairFeedback([], quarantine.ToArray())));
    }

    private string CopyPdf(string name)
    {
        var target = Path.Combine(_directory, name);
        File.Copy(Path.Combine(RepositoryRoot(), Pdf.Replace('/', Path.DirectorySeparatorChar)), target);
        return target;
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
