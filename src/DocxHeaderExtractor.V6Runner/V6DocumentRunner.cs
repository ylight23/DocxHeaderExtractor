using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Models.Inference;
using DocxHeaderExtractor.DocumentProcessing;
using DocxHeaderExtractor.Contracts.Authority;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.Contracts.Docx;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.Sources.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.Sources.Routing;
using DocxHeaderExtractor.HeadingAgent;

namespace DocxHeaderExtractor.V6Runner;

/// <summary>
/// The V6 runner the Web and CLI hosts call directly: the file is routed to the lane that owns its format (the DOCX
/// pipeline or the PDF handler), each lane takes its heading selector from the transport factory or the V6 default, and a
/// .doc/.rtf/.odt is converted to OOXML first. Writeback of the outline goes through the same converter.
/// <para>
/// There is no harness here: no consent gate for sending data to the provider, and no validators or writeback target
/// guardrail. The owner accepted that on 2026-10-15.
/// </para>
/// </summary>
public sealed class V6DocumentRunner : IDisposable
{
    private readonly DocxExtractionPipeline _docxLane;
    private readonly PdfExtractionHandler _pdfLane;
    private readonly DocumentExtractionRouter _dispatcher;

    /// <param name="onModelReply">Diagnostics only: called with every model reply. Null records nothing.</param>
    public V6DocumentRunner(PipelineOptions options, IInferenceTransportFactory factory, Action<ModelReply>? onModelReply = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        var scanSelector = factory.CreateScanHeadingSelector() ?? new DocxHeaderExtractor.HeadingAgent.Scan.ScanHeadingAgentSelector(options.ScanEvidenceRoot,
                parallelism: options.ScanJobs, onModelReply: onModelReply, vision: options.ScanVision, reuseSelection: options.ScanReuseSelection,
                transcriptionOptions: options.ScanJobs is { } jobs ? new DocxHeaderExtractor.HeadingAgent.Scan.ScanTranscriptionPreflight.Options(Parallelism: jobs) : null,
                visionOptions: options.ScanJobs is { } seeJobs ? new DocxHeaderExtractor.HeadingAgent.Scan.ScanVisionPreflight.Options(MaxReadingsPerPage: 0, Parallelism: seeJobs) : null);
        _docxLane = new DocxExtractionPipeline(options, factory,
            factory.CreateDocxHeadingSelector() ?? new DocxHeadingAgentSelector(onModelReply: onModelReply, extraction: options.Extraction), scanSelector);
        _pdfLane = new PdfExtractionHandler(options, factory, factory.CreatePdfHeadingSelector() ?? new PdfHeadingAgentSelector(onModelReply: onModelReply), scanSelector);
        _dispatcher = new DocumentExtractionRouter(new DocxExtractionHandler(_docxLane), _pdfLane);
    }

    /// <summary>
    /// Extracts the outline of one uploaded file. The format is read from the bytes, not the extension; a file no lane
    /// owns is converted to OOXML first and the converted copy is removed afterwards.
    /// </summary>
    public async Task<DocumentExtractionExecutionResult> ExtractAsync(
        string inputPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (UploadedSourceDetector.Detect(inputPath) != SourceType.Unknown)
            return await DispatchAsync(inputPath, ct).ConfigureAwait(false);

        var conversion = OfficeDocumentConverter.EnsureDocx(inputPath);
        try
        {
            return await DispatchAsync(conversion.Path, ct).ConfigureAwait(false);
        }
        finally
        {
            OfficeDocumentConverter.Cleanup(conversion);
        }
    }

    private Task<DocumentExtractionExecutionResult> DispatchAsync(string path, CancellationToken ct) =>
        _dispatcher.ExtractAsync(new DocumentExtractionRequest(UploadedFile.FromLocalPath(path)), ct);

    /// <summary>
    /// Writes the outline's <c>w:outlineLvl</c> into a copy of the uploaded file. The source is never modified; the target
    /// is a new file unless <paramref name="overwrite"/> allows replacing one.
    /// </summary>
    public static OutlineWritebackReport WriteOutline(
        string inputPath,
        DocumentOutline outline,
        string targetPath,
        ExtractionOptions extraction,
        bool applyHeadingStyles = false,
        bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(extraction);
        if (outline.DeterministicRoute == "scan-v6-selected-lines")
            throw new NotSupportedException("SCAN_OUTLINE_WRITEBACK_UNSUPPORTED: scanned pages have image coordinates, not editable Word paragraphs.");

        var productOutput = outline.ProductOutput
            ?? throw new InvalidOperationException(
                "Outline không mang DocumentProductOutput - route tạo ra nó không phải V6 authority.");

        var conversion = OfficeDocumentConverter.EnsureDocx(inputPath);
        try
        {
            var result = DocxProductWriteback.Apply(
                conversion.Path,
                targetPath,
                productOutput,
                extraction,
                new OutlineWritebackOptions { ApplyHeadingStyles = applyHeadingStyles, Overwrite = overwrite });
            return new OutlineWritebackReport(result.OutputPath, result.Applied, result.Skipped.Count);
        }
        finally
        {
            OfficeDocumentConverter.Cleanup(conversion);
        }
    }

    public void Dispose()
    {
        _docxLane.Dispose();
        _pdfLane.Dispose();
    }
}

/// <summary>What a writeback did: the copy written, how many headings were applied, and how many were skipped.</summary>
public sealed record OutlineWritebackReport(string OutputPath, int Applied, int Skipped);
