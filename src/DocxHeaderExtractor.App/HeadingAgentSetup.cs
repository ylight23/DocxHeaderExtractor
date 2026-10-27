using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.HeadingAgent.Scan;
using DocxHeaderExtractor.HeadingAgent.Selectors;

namespace DocxHeaderExtractor.App;

/// <summary>
/// The one place the heading lanes get their agents. Each slot comes from the model provider when the host supplies its own (a test, an
/// evaluation run), and from the production default otherwise.
/// <para>
/// To add an agent to the heading lanes: its interface in Contracts/Agents, a role name in <see cref="AgentRoles"/>, a property on
/// <see cref="HeadingAgents"/>, and its default here. docs/architecture/extending.md walks through it.
/// </para>
/// </summary>
public static class HeadingAgentSetup
{
    internal static RequestPolicy ResourcePolicyFor(PipelineOptions options)
    {
        var profile = new RequestPolicy();
        var policy = profile with
        {
            MaxRequestBytes = options.MaxModelRequestBytes ?? profile.MaxRequestBytes,
            ContextTokens = options.ModelContextTokens ?? profile.ContextTokens,
            MaxPromptTokens = options.MaxModelPromptTokens ?? profile.MaxPromptTokens,
            MaxCompletionTokens = options.MaxModelOutputTokens ?? profile.MaxCompletionTokens,
            ContinuationReserveBytes = options.ContinuationReserveBytes ?? profile.ContinuationReserveBytes,
            ImageTokenAllowance = options.ImageTokenAllowance ?? profile.ImageTokenAllowance,
        };
        policy.Validate();
        return policy;
    }

    /// <param name="onModelReply">Diagnostics only: called with every reply of the selectors' conversations. Null records nothing.</param>
    public static HeadingAgents Create(PipelineOptions options, IModelProvider provider, Action<ModelReply>? onModelReply = null)
        => CreateRoutes(options, provider, onModelReply).Shared;

    /// <summary>The PDF production repair default is scoped to its lane; host-supplied agents keep precedence.</summary>
    internal static (HeadingAgents Shared, HeadingAgents NativePdf) CreateRoutes(PipelineOptions options, IModelProvider provider, Action<ModelReply>? onModelReply = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(provider);
        var scan = provider.Create<IScanHeadingSelector>(AgentRoles.ScanSelector) ?? new ScanSelector(options.ScanEvidenceRoot,
            parallelism: options.ScanJobs, onModelReply: onModelReply, vision: options.ScanVision, reuseSelection: options.ScanReuseSelection,
            transcriptionOptions: options.ScanJobs is { } jobs ? new ScanTranscriptionPreflight.Options(Parallelism: jobs) : null,
            visionOptions: options.ScanJobs is { } seeJobs ? new ScanVisionPreflight.Options(MaxReadingsPerPage: 0, Parallelism: seeJobs) : null);
        var docx = provider.Create<IHeadingSelector>(AgentRoles.DocxSelector) ?? new DocxSelector(onModelReply: onModelReply, extraction: options.Extraction);
        var sharedPolicy = ResourcePolicyFor(options);
        var pdfPolicy = options.PdfAdaptiveVisualSelection && options.PdfVisualSelectionMaxRequestBytes is { } visualCap
            ? sharedPolicy with { MaxRequestBytes = visualCap }
            : sharedPolicy;
        pdfPolicy.Validate();
        var pdf = provider.Create<IHeadingSelector>(AgentRoles.PdfSelector) ?? new PdfSelector(onModelReply: onModelReply, parallelism: options.PdfJobs, resourcePolicy: pdfPolicy, inlineGrouping: options.PdfInlineGrouping,
            sourceBound: options.PdfSourceBoundSelection, clarifyMembership: options.PdfClarifyMembership,
            membershipInstructionVersion: options.PdfMembershipInstructionVersion, boundaryReview: options.PdfBoundaryReview,
            separateGroupingInstruction: options.PdfSeparateGroupingInstruction, pairwiseGrouping: options.PdfPairwiseGrouping,
            generalBoundaryPrompt: options.PdfMembershipInstructionVersion >= 7, rasterGeometry: options.PdfRasterGeometryEvidence,
            adaptiveVisualSelection: options.PdfAdaptiveVisualSelection, compactGeometry: options.PdfCompactGeometry,
            visualMaxWidth: options.PdfVisualMaxWidth, visualQuality: options.PdfVisualQuality, visualGrayscale: options.PdfVisualGrayscale, visualCropMargins: options.PdfVisualCropMargins, visualBilevel: options.PdfVisualBilevel, visualCacheRoot: options.SourceCacheRoot,
            dynamicCompletionBudget: options.PdfDynamicCompletionBudget && options.PdfMembershipInstructionVersion >= 14,
            shapeReview: options.PdfShapeReview);
        var suppliedPlacer = provider.Create<IHeadingPlacer>(AgentRoles.Placer);
        var placer = suppliedPlacer ?? new HeadingPlacer(wholeDocument: options.WholeDocumentPlacement,
            repairParentChains: options.RepairParentChains);
        var shared = new HeadingAgents(Pdf: pdf, Docx: docx, Scan: scan, Placer: placer);
        var nativePdf = suppliedPlacer is null && options.PdfRepairParentChains && !options.RepairParentChains
            ? shared with { Placer = new HeadingPlacer(wholeDocument: options.WholeDocumentPlacement, repairParentChains: true) }
            : shared;
        return (shared, nativePdf);
    }
}
