using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Tools;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.Sources.Pdf;

namespace DocxHeaderExtractor.HeadingAgent.Selectors;

/// <summary>
/// The production PDF heading selector: the V6 direct agent based on D4.12 (docs/experiments/p7-f1-v6-d4.12-owner-trials.md,
/// reference plan of commit 7763b91), promoted on 2026-10-14. The PDF is parsed with the production parser and cut into layout segments (whole
/// pages when the actual request fits the resource envelope, else parser blocks/visual rows/atoms, with 20 context lines each side); the first request
/// supplies the targets' existing tool facts alongside their alias, page and text. The conversation is <see cref="AgentSelector"/>'s.
/// </summary>
public sealed class PdfSelector : AgentSelector, IPrimedPdfSourceSelector
{
    readonly RequestPolicy policy;
    readonly bool inlineGrouping;
    readonly bool sourceBound;
    readonly bool boundaryReview;
    readonly string? groupingInstruction;
    readonly bool pairwiseGrouping;
    readonly bool generalBoundaryPrompt;
    readonly int membershipInstructionVersion;
    readonly bool rasterGeometry;
    readonly bool adaptiveVisualSelection;
    readonly bool compactGeometry;
    readonly bool dynamicCompletionBudget;
    readonly int visualMaxWidth;
    readonly int visualQuality;
    readonly bool visualGrayscale;
    readonly bool visualCropMargins;
    readonly string? visualCacheRoot;
    readonly bool visualBilevel;
    readonly bool shapeReview;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, PdfPreparedSource> primedSources = new(StringComparer.Ordinal);
    internal bool RasterGeometryEnabled => rasterGeometry;
    private protected override bool PairwisePdfGrouping => pairwiseGrouping;
    private protected override bool BoundaryReviewEnabled => boundaryReview;
    private protected override bool ShapeReviewEnabled => shapeReview;
    private protected override string GroupingInstruction => groupingInstruction ?? base.GroupingInstruction;
    private protected override bool GeneralPdfBoundaryPrompt => generalBoundaryPrompt;
    // PDF packets can legitimately take longer than the shared 300-second default. The caller's cancellation token still stops the run.
    private protected override TimeSpan? RequestTimeout => System.Threading.Timeout.InfiniteTimeSpan;
    private protected override bool RequireCompleteRun => true;
    /// <inheritdoc cref="AgentSelector"/>
    public PdfSelector(Func<HttpMessageHandler>? handlerFactory = null, int? parallelism = null, Action<ModelReply>? onModelReply = null,
        string? instruction = null, int firstSegments = 0, RequestPolicy? resourcePolicy = null, bool inlineGrouping = false, bool sourceBound = false,
        bool clarifyMembership = false, int membershipInstructionVersion = 2, bool boundaryReview = false, bool separateGroupingInstruction = false,
        bool pairwiseGrouping = false, bool generalBoundaryPrompt = false, bool rasterGeometry = false, bool adaptiveVisualSelection = false,
        bool compactGeometry = false, int visualMaxWidth = 1200, int visualQuality = 58, bool dynamicCompletionBudget = false,
        bool shapeReview = false, bool visualGrayscale = false, bool visualCropMargins = false, string? visualCacheRoot = null, bool visualBilevel = false)
        : base(handlerFactory, parallelism, onModelReply,
            ResolveInstruction(instruction, clarifyMembership, membershipInstructionVersion), firstSegments)
    {
        if (visualMaxWidth < 256 || visualMaxWidth > 2400) throw new ArgumentOutOfRangeException(nameof(visualMaxWidth));
        if (visualQuality < 1 || visualQuality > 100) throw new ArgumentOutOfRangeException(nameof(visualQuality));
        if (dynamicCompletionBudget && membershipInstructionVersion != 14) throw new ArgumentException("DYNAMIC_COMPLETION_REQUIRES_PDF_UNIT_SELECTION");
        this.compactGeometry = compactGeometry;
        this.shapeReview = shapeReview;
        this.dynamicCompletionBudget = dynamicCompletionBudget;
        this.visualMaxWidth = visualMaxWidth;
        this.visualQuality = visualQuality;
        this.visualGrayscale = visualGrayscale;
        this.visualCropMargins = visualCropMargins;
        this.visualCacheRoot = visualCacheRoot;
        this.visualBilevel = visualBilevel;
        if (inlineGrouping && sourceBound) throw new ArgumentException("INDEPENDENT_PDF_CANDIDATES_REQUIRED");
        if (boundaryReview && !sourceBound) throw new ArgumentException("BOUNDARY_REVIEW_REQUIRES_SOURCE_BINDING");
        if (pairwiseGrouping && inlineGrouping) throw new ArgumentException("PAIRWISE_GROUPING_REQUIRES_SEPARATE_GROUP_STAGE");
        this.pairwiseGrouping = pairwiseGrouping;
        this.rasterGeometry = rasterGeometry;
        this.adaptiveVisualSelection = adaptiveVisualSelection;
        this.membershipInstructionVersion = membershipInstructionVersion;
        this.generalBoundaryPrompt = generalBoundaryPrompt;
        this.boundaryReview = boundaryReview;
        groupingInstruction = ResolveGroupingInstruction(instruction, separateGroupingInstruction, generalBoundaryPrompt);
        policy = resourcePolicy ?? new RequestPolicy(); policy.Validate(); this.inlineGrouping = inlineGrouping; this.sourceBound = sourceBound;
    }

    public override string SelectorId => (rasterGeometry ? "pdf-raster-geometry-v1:" : "") + SelectorIdCore + ":wait-for-complete-response"
        + (compactGeometry ? ":geometry-columns-v1" : "")
        + (adaptiveVisualSelection && (visualMaxWidth != 1200 || visualQuality != 58) ? $":visual-w{visualMaxWidth}-q{visualQuality}" : "")
        + (adaptiveVisualSelection && visualGrayscale ? ":visual-gray-v1" : "")
        + (adaptiveVisualSelection && visualCropMargins ? ":visual-crop-v1" : "")
        + (adaptiveVisualSelection && visualBilevel ? ":visual-bilevel-v1" : "")
        + (dynamicCompletionBudget ? ":unit-completion-adaptive-v1" : "")
        + (shapeReview ? ":shape-review-" + AgentProtocol.Sha(ShapeReview.Prompt)[..8] : "");
    string SelectorIdCore => (UsesPreloadedEvidence ? "pdf-preflight-" + (InstructionText == PdfMembershipInstructionV10.Instruction ? "v2:" : "v1:") + (boundaryReview ? "name-review-" + AgentProtocol.Sha(PdfNameContributionReview.Instruction)[..8] + ":" : "") : "") + "p7-f1-v6-d4.12:TOOLFACTS_ANALYSIS_V3:facts-in-request:instruction-" + InstructionTag + ":" + BindingPolicyVersion + ":group-selected-text-v1:ordered-page-budget-v1:" + AgentProtocol.Sha(System.Text.Json.JsonSerializer.Serialize(policy))[..8] + (adaptiveVisualSelection ? ":adaptive-page-visual-v1" : "") + (inlineGrouping ? ":inline-groups-v1" : "") + (sourceBound ? ":source-bound-v1" : "") + (UnitSelectionEnabled ? ":structural-geometry-selection-units-v2:unit-rejection-reason-v1" : "") + (boundaryReview ? ":boundary-review-v1" : "") + (groupingInstruction is not null ? ":group-instruction-" + AgentProtocol.Sha(groupingInstruction)[..8] : "") + (pairwiseGrouping ? ":pairwise-grouping-v1:" + AgentProtocol.Sha(PdfBoundaryDecisions.PromptFor(generalBoundaryPrompt))[..8] : "");

    internal static string? ResolveGroupingInstruction(string? customInstruction, bool separate, bool general = false) =>
        customInstruction is null && separate ? general ? PdfGeneralBoundaryInstruction.Instruction : PdfBoundaryInstruction.Instruction : null;

    internal static string ResolveInstruction(string? instruction, bool clarify, int version)
    {
        if (version is not (2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 12 or 13 or 14)) throw new ArgumentOutOfRangeException(nameof(version));
        return instruction ?? (clarify ? version switch { 14 => PdfMembershipInstructionV12.Instruction, 13 => PdfMembershipInstructionV13.Instruction, 12 => PdfMembershipInstructionV12.Instruction, 10 => PdfMembershipInstructionV10.Instruction, 9 => PdfMembershipInstructionV9.Instruction, 8 => PdfMembershipInstructionV8.Instruction, 7 => PdfMembershipInstructionV7.Instruction, 6 => PdfMembershipInstructionV6.Instruction, 5 => PdfMembershipInstructionV5.Instruction, 4 => PdfMembershipInstructionV4.Instruction,
            3 => PdfMembershipInstructionV3.Instruction, _ => PdfMembershipInstructionV2.Instruction } : PdfAgentProtocol.Instruction);
    }

    bool UsesPreloadedEvidence => InstructionText is var text && (text == PdfMembershipInstructionV9.Instruction || text == PdfMembershipInstructionV10.Instruction);
    bool UsesLayoutBlockEvidence => InstructionText == PdfMembershipInstructionV13.Instruction;
    private protected override bool UnitSelectionEnabled => membershipInstructionVersion == 14;

    /// <inheritdoc/>
    public void PrimePdfSource(string path, PdfPreparedSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(source);
        primedSources[Path.GetFullPath(path)] = source;
    }

    /// <inheritdoc/>
    public void UnprimePdfSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        primedSources.TryRemove(Path.GetFullPath(path), out _);
    }

    private protected override IAgentSource Load(string path) => Load(path, CancellationToken.None);

    private protected override IAgentSource Load(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        AgentSource? source;
        string failure;
        if (primedSources.TryRemove(fullPath, out var primed))
            source = AgentSource.FromPrepared(primed, path, out failure, policy, InstructionText, inlineGrouping: inlineGrouping, sourceBound: sourceBound,
                requirePageLayoutEvidence: InstructionText == PdfMembershipInstructionV8.Instruction,
                preloadPageLayoutEvidence: UsesPreloadedEvidence, includeLayoutBlockEvidence: UsesLayoutBlockEvidence, unitSelection: UnitSelectionEnabled,
                rasterGeometry: rasterGeometry, adaptiveVisualSelection: adaptiveVisualSelection, cancellationToken: cancellationToken,
                compactGeometry: compactGeometry, visualMaxWidth: visualMaxWidth, visualQuality: visualQuality,
                dynamicCompletionBudget: dynamicCompletionBudget, visualGrayscale: visualGrayscale, visualCropMargins: visualCropMargins, visualCacheRoot: visualCacheRoot, visualBilevel: visualBilevel);
        else
            source = AgentSource.Load(path, out failure, policy, InstructionText, inlineGrouping: inlineGrouping, sourceBound: sourceBound,
            requirePageLayoutEvidence: InstructionText == PdfMembershipInstructionV8.Instruction,
            preloadPageLayoutEvidence: UsesPreloadedEvidence, includeLayoutBlockEvidence: UsesLayoutBlockEvidence, unitSelection: UnitSelectionEnabled,
            rasterGeometry: rasterGeometry, adaptiveVisualSelection: adaptiveVisualSelection, cancellationToken: cancellationToken,
            compactGeometry: compactGeometry, visualMaxWidth: visualMaxWidth, visualQuality: visualQuality,
            dynamicCompletionBudget: dynamicCompletionBudget, visualGrayscale: visualGrayscale, visualCropMargins: visualCropMargins, visualCacheRoot: visualCacheRoot, visualBilevel: visualBilevel);
        return source ?? throw new InvalidOperationException("PDF_AGENT_SOURCE_REFUSED:" + failure);
    }
}
