using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.Contracts.Docx;
using DocxHeaderExtractor.DocumentProcessing.Writeback;
using DocxHeaderExtractor.Sources.Docx;
using DocxHeaderExtractor.DocumentProcessing.Lanes.Docx;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.DocumentProcessing.Intake;
using DocxHeaderExtractor.Sources.Detect;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Selectors;

namespace DocxHeaderExtractor.App;

/// <summary>
/// The composition root the Web and CLI hosts call directly: it takes the heading agents from <see cref="HeadingAgentSetup"/> and hands
/// them to the pipeline, which never references the agents' project. The file is routed to the lane that owns its format (the DOCX
/// pipeline or the PDF handler), and a .doc/.rtf/.odt is converted to OOXML first. Writeback of the outline goes through the same converter.
/// <para>
/// There is no harness here: no consent gate for sending data to the provider, and no validators or writeback target
/// guardrail. The owner accepted that on 2026-10-15.
/// </para>
/// </summary>
public sealed class DocumentRunner
{
    private readonly Router _dispatcher;
    private readonly RequestPolicy requestPolicy;
    private readonly double maxProviderCostUsd;
    private readonly int maxProviderAttempts;
    private readonly TimeSpan? providerBudgetDuration;

    /// <param name="provider">The model route and any agents of the host's own; <see cref="OpenRouterProvider"/> in production.</param>
    /// <param name="onModelReply">Diagnostics only: called with every model reply. Null records nothing.</param>
    public DocumentRunner(PipelineOptions options, IModelProvider provider, Action<ModelReplyView>? onModelReply = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(provider);
        requestPolicy = HeadingAgentSetup.ResourcePolicyFor(options);
        maxProviderCostUsd = options.MaxProviderCostUsd;
        maxProviderAttempts = options.MaxProviderAttempts;
        providerBudgetDuration = options.ProviderBudgetDuration;
        var agents = HeadingAgentSetup.CreateRoutes(options, provider, ModelReplyViews.Adapt(onModelReply));
        _dispatcher = new Router(new DocxHandler(new DocxPipeline(options, provider, agents.Shared)), new PdfHandler(options, provider, agents.NativePdf));
    }

    /// <summary>
    /// Extracts the outline of one uploaded file. The format is read from the bytes, not the extension; a file no lane
    /// owns is converted to OOXML first and the converted copy is removed afterwards.
    /// </summary>
    public async Task<RunResult> ExtractAsync(
        string inputPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var budget = new RunResourceBudget(maxProviderCostUsd, maxProviderAttempts, providerBudgetDuration, requestPolicy);
        using var resourceScope = budget.Enter();
        if (SourceDetector.Detect(inputPath) != SourceType.Unknown)
            return Account(await DispatchAsync(inputPath, ct).ConfigureAwait(false), budget.Observe());

        var conversion = OfficeConverter.EnsureDocx(inputPath);
        try
        {
            return Account(await DispatchAsync(conversion.Path, ct).ConfigureAwait(false), budget.Observe());
        }
        finally
        {
            OfficeConverter.Cleanup(conversion);
        }
    }

    static RunResult Account(RunResult result, RunResourceBudget.Snapshot snapshot) => result with
    {
        ResourceBudget = new(snapshot.CeilingUsd, snapshot.MaxAttempts, snapshot.Attempts,
            snapshot.ConfirmedUsd, snapshot.ProvisionalUsd, snapshot.OutstandingUsd)
    };

    private Task<RunResult> DispatchAsync(string path, CancellationToken ct) =>
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

        var conversion = OfficeConverter.EnsureDocx(inputPath);
        try
        {
            var result = ProductWriteback.Apply(
                conversion.Path,
                targetPath,
                productOutput,
                extraction,
                new OutlineWritebackOptions { ApplyHeadingStyles = applyHeadingStyles, Overwrite = overwrite });
            return new OutlineWritebackReport(result.OutputPath, result.Applied, result.Skipped.Count);
        }
        finally
        {
            OfficeConverter.Cleanup(conversion);
        }
    }
}

/// <summary>What a writeback did: the copy written, how many headings were applied, and how many were skipped.</summary>
public sealed record OutlineWritebackReport(string OutputPath, int Applied, int Skipped);
