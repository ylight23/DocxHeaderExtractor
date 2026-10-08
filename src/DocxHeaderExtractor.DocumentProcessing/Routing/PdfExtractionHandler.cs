using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Routing;

/// <summary>
/// The PDF lane, owning PDF uploads and nothing else.
/// <para>
/// It runs the qualified function-conditioned PDF authority chain, reading only the uploaded PDF.
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
    private IInferenceTransport? _transport;

    public PdfExtractionHandler(PipelineOptions options, IInferenceTransport? analyst = null)
        : this(options, analyst, sendsDataExternally: false) { }

    public PdfExtractionHandler(
        PipelineOptions options,
        IInferenceTransport? analyst,
        bool sendsDataExternally)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transport = analyst;
        _sendsDataExternally = sendsDataExternally;
        _ownsTransport = false;
    }

    public PdfExtractionHandler(PipelineOptions options, IInferenceTransportFactory analystFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transportFactory = analystFactory ?? throw new ArgumentNullException(nameof(analystFactory));
        _sendsDataExternally = analystFactory.SendsDataExternally;
        _ownsTransport = true;
    }

    public SourceType Handles => SourceType.Pdf;

    public async Task<DocumentExtractionExecutionResult> ExtractAsync(
        UploadedFile file,
        IReadOnlySet<int>? quarantinedIndexes = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var transport = _options.DisableLlm ? null : await GetTransportAsync(ct);
        return await PdfExtractionPipeline.RunExecutionAsync(
            file, _options, transport, quarantinedIndexes, _sendsDataExternally, ct);
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
        // PDF has its own qualified provider policy. It must never inherit the DOCX factory's
        // default local backend merely because both lanes share the same general abstraction.
        _transport = await _transportFactory.CreatePdfProductionAsync(ct);
        return _transport;
    }
}
