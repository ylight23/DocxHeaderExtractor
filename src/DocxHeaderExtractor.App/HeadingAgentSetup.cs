using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Inference;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.HeadingAgent.Scan;
using DocxHeaderExtractor.HeadingAgent.Selectors;

namespace DocxHeaderExtractor.App;

/// <summary>
/// The one place the heading lanes get their agents. Each agent comes from the transport factory when the host supplies its own (a test, an
/// evaluation run), and from the production default otherwise.
/// <para>
/// To add an agent to the heading lanes: its interface in Contracts/Agents, a property on <see cref="HeadingAgents"/>, a Create hook on
/// <see cref="IInferenceTransportFactory"/>, and its default here. docs/architecture/extending.md walks through it.
/// </para>
/// </summary>
public static class HeadingAgentSetup
{
    /// <param name="onModelReply">Diagnostics only: called with every reply of the selectors' conversations. Null records nothing.</param>
    public static HeadingAgents Create(PipelineOptions options, IInferenceTransportFactory factory, Action<ModelReply>? onModelReply = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        var scan = factory.CreateScanHeadingSelector() ?? new ScanSelector(options.ScanEvidenceRoot,
            parallelism: options.ScanJobs, onModelReply: onModelReply, vision: options.ScanVision, reuseSelection: options.ScanReuseSelection,
            transcriptionOptions: options.ScanJobs is { } jobs ? new ScanTranscriptionPreflight.Options(Parallelism: jobs) : null,
            visionOptions: options.ScanJobs is { } seeJobs ? new ScanVisionPreflight.Options(MaxReadingsPerPage: 0, Parallelism: seeJobs) : null);
        var docx = factory.CreateDocxHeadingSelector() ?? new DocxSelector(onModelReply: onModelReply, extraction: options.Extraction);
        var pdf = factory.CreatePdfHeadingSelector() ?? new PdfSelector(onModelReply: onModelReply);
        var placer = factory.CreateHeadingPlacer() ?? new HeadingPlacer();
        return new HeadingAgents(Pdf: pdf, Docx: docx, Scan: scan, Placer: placer);
    }
}
