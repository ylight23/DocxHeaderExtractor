using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Lanes.Pdf;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Provider;
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
/// The model route is asked of the provider only when a run calls the model, as the DOCX pipeline does.
/// </para>
/// </summary>
public sealed class PdfHandler : IDocumentExtractionHandler
{
    private readonly PipelineOptions _options;
    private readonly IModelProvider _provider;
    private readonly HeadingAgents _agents;

    public PdfHandler(PipelineOptions options, IModelProvider provider, HeadingAgents agents)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    }

    public SourceType Handles => SourceType.Pdf;

    public async Task<RunResult> ExtractAsync(
        UploadedFile file,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ct.ThrowIfCancellationRequested();
        var route = _options.DisableLlm ? null : _provider.CreateRoute();
        if (file.DetectedType == SourceType.Pdf && PdfSourceIntegrity.Read(file.LocalPath, ct).RequiresOcr)
            return await ScanLane.RunAsync(file.LocalPath, _options, route, _agents, ct).ConfigureAwait(false);
        return await PdfPipeline.RunAsync(file, _options, route, _agents, ct);
    }
}
