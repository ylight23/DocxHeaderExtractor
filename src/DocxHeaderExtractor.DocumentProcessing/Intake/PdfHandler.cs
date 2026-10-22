using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Lanes.Pdf;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Inference;
using DocxHeaderExtractor.DocumentProcessing.Lanes.Scan;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.Sources.Pdf;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Routing;

namespace DocxHeaderExtractor.DocumentProcessing.Intake;

/// <summary>
/// The PDF lane, owning PDF uploads and nothing else.
/// <para>
/// It runs its agents (<see cref="HeadingAgents"/>): the PDF selector as the heading authority, reading only the uploaded PDF, and the
/// placer. Without them the lane refuses any run that needs the model (PDF_HEADING_SELECTOR_REQUIRED, HEADING_PLACER_REQUIRED); with the model disabled it returns the source only, as before.
/// It never looks for a DOCX, and its result stands alone: a PDF and a DOCX of the same document are two independent
/// canonical documents unless a user asks for them to be compared.
/// </para>
/// <para>
/// The transport is resolved the way the DOCX pipeline resolves it - lazily, from a factory, and only
/// if a run actually needs one - so that composing the dispatcher never opens a provider connection
/// for a lane the upload does not use. A transport this class created is disposed by this class; one
/// handed to it belongs to whoever handed it over.
/// </para>
/// </summary>
public sealed class PdfHandler : IDocumentExtractionHandler, IDisposable
{
    private readonly PipelineOptions _options;
    private readonly IInferenceTransportFactory? _transportFactory;
    private readonly bool _sendsDataExternally;
    private readonly bool _ownsTransport;
    private readonly HeadingAgents _agents;
    private IInferenceTransport? _transport;

    public PdfHandler(
        PipelineOptions options,
        IInferenceTransport? transport,
        bool sendsDataExternally,
        HeadingAgents agents)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _transport = transport;
        _sendsDataExternally = sendsDataExternally;
        _ownsTransport = false;
    }

    public PdfHandler(PipelineOptions options, IInferenceTransportFactory transportFactory, HeadingAgents agents)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _sendsDataExternally = transportFactory.SendsDataExternally;
        _ownsTransport = true;
    }

    public SourceType Handles => SourceType.Pdf;

    public async Task<RunResult> ExtractAsync(
        UploadedFile file,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ct.ThrowIfCancellationRequested();
        if (file.DetectedType == SourceType.Pdf && PdfSourceIntegrity.Read(file.LocalPath, ct).RequiresOcr)
            return await ScanLane.RunAsync(file.LocalPath, _options, _options.DisableLlm ? null : await GetTransportAsync(ct),
                _agents, _sendsDataExternally, ct).ConfigureAwait(false);
        var transport = _options.DisableLlm ? null : await GetTransportAsync(ct);
        return await PdfPipeline.RunAsync(
            file, _options, transport, _agents, _sendsDataExternally, ct);
    }

    public void Dispose()
    {
        if (_ownsTransport) _transport?.Dispose();
        _transport = null;
    }

    private async Task<IInferenceTransport?> GetTransportAsync(CancellationToken ct)
    {
        if (_transport is not null) return _transport;
        if (_transportFactory is null) return null;
        // PDF has its own qualified provider policy: it takes only the authorized agent route, never
        // a transport that another route happens to hand out.
        _transport = await _transportFactory.CreateAgentRouteAsync(ct);
        return _transport;
    }
}
