using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.DocumentProcessing.Routing;

/// <summary>The DOCX lane, owning DOCX uploads and nothing else.</summary>
public sealed class DocxCanonicalSourceExtractor(AuthorityExtractionPipeline pipeline) : ICanonicalSourceExtractor
{
    public SourceType Handles => SourceType.Docx;

    public Task<AuthorityPipelineExecutionResult> ExtractAsync(
        UploadedFile file,
        IReadOnlySet<int>? quarantinedIndexes = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return pipeline.RunDocumentExecutionAsync(file.LocalPath, quarantinedIndexes, ct);
    }
}

/// <summary>
/// The PDF lane, owning PDF uploads and nothing else.
/// <para>
/// It runs the same semantic stage as the DOCX lane through
/// <c>CanonicalSemanticPdfAuthorityAdapter</c>, reading only the uploaded PDF. It never looks for a
/// DOCX, and its result stands alone: a PDF and a DOCX of the same document are two independent
/// canonical documents unless a user asks for them to be compared.
/// </para>
/// <para>
/// The analyst is resolved the way the DOCX pipeline resolves it - lazily, from a factory, and only
/// if a run actually needs one - so that composing the dispatcher never opens a provider connection
/// for a lane the upload does not use. An analyst this class created is disposed by this class; one
/// handed to it belongs to whoever handed it over.
/// </para>
/// </summary>
public sealed class PdfCanonicalSourceExtractor : ICanonicalSourceExtractor, IDisposable
{
    private readonly PipelineOptions _options;
    private readonly IHeaderClassifierFactory? _analystFactory;
    private readonly bool _sendsDataExternally;
    private readonly bool _ownsAnalyst;
    private IHeaderClassifier? _analyst;

    public PdfCanonicalSourceExtractor(PipelineOptions options, IHeaderClassifier? analyst = null)
        : this(options, analyst, sendsDataExternally: false) { }

    public PdfCanonicalSourceExtractor(
        PipelineOptions options,
        IHeaderClassifier? analyst,
        bool sendsDataExternally)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _analyst = analyst;
        _sendsDataExternally = sendsDataExternally;
        _ownsAnalyst = false;
    }

    public PdfCanonicalSourceExtractor(PipelineOptions options, IHeaderClassifierFactory analystFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _analystFactory = analystFactory ?? throw new ArgumentNullException(nameof(analystFactory));
        _sendsDataExternally = analystFactory.SendsDataExternally;
        _ownsAnalyst = true;
    }

    public SourceType Handles => SourceType.Pdf;

    public async Task<AuthorityPipelineExecutionResult> ExtractAsync(
        UploadedFile file,
        IReadOnlySet<int>? quarantinedIndexes = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var analyst = _options.DisableLlm ? null : await GetAnalystAsync(ct);
        return await PdfCanonicalExtraction.RunExecutionAsync(
            file, _options, analyst, quarantinedIndexes, _sendsDataExternally, ct);
    }

    public void Dispose()
    {
        if (_ownsAnalyst) _analyst?.Dispose();
        _analyst = null;
    }

    private async Task<IHeaderClassifier?> GetAnalystAsync(CancellationToken ct)
    {
        if (_analyst is not null) return _analyst;
        if (_analystFactory is null) return null;
        _analyst = await _analystFactory.CreateAsync(_options, ct);
        return _analyst;
    }
}
