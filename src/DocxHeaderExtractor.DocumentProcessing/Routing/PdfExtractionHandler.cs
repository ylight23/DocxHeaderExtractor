using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Routing;

/// <summary>
/// The PDF lane, owning PDF uploads and nothing else.
/// <para>
/// It runs the V6 heading selector as the PDF heading authority, reading only the uploaded PDF. Without a selector the lane refuses
/// any run that needs the model (PDF_HEADING_SELECTOR_REQUIRED); with the model disabled it returns the source only, as before.
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
public sealed class PdfExtractionHandler : IDocumentExtractionHandler, IDisposable
{
    private readonly PipelineOptions _options;
    private readonly IInferenceTransportFactory? _transportFactory;
    private readonly bool _sendsDataExternally;
    private readonly bool _ownsTransport;
    private readonly IHeadingSelector? _selector;
    private IInferenceTransport? _transport;

    public PdfExtractionHandler(
        PipelineOptions options,
        IInferenceTransport? transport,
        bool sendsDataExternally,
        IHeadingSelector? selector = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transport = transport;
        _sendsDataExternally = sendsDataExternally;
        _ownsTransport = false;
        _selector = selector;
    }

    public PdfExtractionHandler(PipelineOptions options, IInferenceTransportFactory transportFactory, IHeadingSelector? selector = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _sendsDataExternally = transportFactory.SendsDataExternally;
        _ownsTransport = true;
        _selector = selector;
    }

    public SourceType Handles => SourceType.Pdf;

    public async Task<DocumentExtractionExecutionResult> ExtractAsync(
        UploadedFile file,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var transport = _options.DisableLlm ? null : await GetTransportAsync(ct);
        return await PdfExtractionPipeline.RunWithSelectorAsync(
            file, _options, transport, _selector, _sendsDataExternally, ct);
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
