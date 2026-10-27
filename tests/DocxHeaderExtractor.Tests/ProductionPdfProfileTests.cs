using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Runtime;

namespace DocxHeaderExtractor.Tests;

public sealed class ProductionPdfProfileTests
{
    [Fact]
    public void V12_default_reuses_shared_policy_without_pdf_specific_rules_and_keeps_the_contract()
    {
        var options = new PipelineOptions();
        var selection = PdfSelector.ResolveInstruction(null, options.PdfClarifyMembership, options.PdfMembershipInstructionVersion);
        var grouping = PdfSelector.ResolveGroupingInstruction(null, options.PdfSeparateGroupingInstruction, options.PdfMembershipInstructionVersion >= 7)!;
        var prompts = new[] { selection, grouping, SourceBoundSelection.SystemPrompt + PdfRasterEvidence.Prompt,
            PdfBoundaryDecisions.PromptFor(options.PdfMembershipInstructionVersion >= 7) };
        foreach (var prompt in prompts)
            Assert.DoesNotMatch(@"(?i)\b(participants?|meeting|agenda|session|venue|postal|committee)\b|World Bank|L0550|PDF072|PDF023", prompt);
        Assert.Equal(PdfMembershipInstructionV12.Instruction, selection);
        Assert.StartsWith(AgentProtocol.InstructionStructuralV1, selection);
        Assert.Contains(AgentProtocol.FormattingEvidenceParagraph, selection);
        Assert.Equal(PdfGeneralBoundaryInstruction.Instruction, grouping);
        Assert.Equal("custom user instruction", PdfSelector.ResolveInstruction("custom user instruction", true, options.PdfMembershipInstructionVersion));
        Assert.Contains("sourceText", SourceBoundSelection.SystemPrompt);
        Assert.Contains("headingExtent", selection);
        Assert.Contains("UNRESOLVED", SourceBoundSelection.SystemPrompt);
    }

    [Fact]
    public void V9_source_preflight_and_bounded_review_require_explicit_opt_in()
    {
        var preflight = new PdfSelector(sourceBound: true, clarifyMembership: true, membershipInstructionVersion: 9);
        var reviewed = new PdfSelector(sourceBound: true, clarifyMembership: true, membershipInstructionVersion: 9, boundaryReview: true);
        Assert.Contains("pdf-preflight-v1:", preflight.SelectorId);
        Assert.DoesNotContain("boundary-review-v1", preflight.SelectorId);
        Assert.Contains("name-review-", reviewed.SelectorId);
        Assert.Contains("boundary-review-v1", reviewed.SelectorId);
        Assert.Equal(14, new PipelineOptions().PdfMembershipInstructionVersion);
    }

    sealed class Provider(IHeadingSelector? pdf = null, IHeadingPlacer? placer = null) : IModelProvider
    {
        public readonly Dictionary<string, int> Calls = [];
        public T? Create<T>(string role) where T : class
        {
            Calls[role] = Calls.GetValueOrDefault(role) + 1;
            return role switch { AgentRoles.PdfSelector => pdf as T, AgentRoles.Placer => placer as T, _ => null };
        }
    }

    [Fact]
    public void Native_pdf_defaults_use_generic_prompts_and_the_previous_pdf_profile_is_explicit()
    {
        var options = new PipelineOptions();
        var routes = HeadingAgentSetup.CreateRoutes(options, new Provider());
        var qualified = HeadingAgentSetup.CreateRoutes(new PipelineOptions
        {
            PdfSourceBoundSelection = true, PdfClarifyMembership = true, PdfMembershipInstructionVersion = 6,
            PdfSeparateGroupingInstruction = true, PdfPairwiseGrouping = true, RepairParentChains = true,
            PdfRepairParentChains = false, PdfInlineGrouping = false, PdfBoundaryReview = false, WholeDocumentPlacement = false,
            PdfDynamicCompletionBudget = false
        }, new Provider());
        var candidate = HeadingAgentSetup.CreateRoutes(new PipelineOptions
        {
            PdfSourceBoundSelection = true, PdfClarifyMembership = true, PdfMembershipInstructionVersion = 14,
            PdfAdaptiveVisualSelection = true, PdfVisualSelectionMaxRequestBytes = 786432,
            PdfCompactGeometry = true, PdfVisualMaxWidth = 960, PdfVisualQuality = 50, PdfVisualGrayscale = true,
            PdfDynamicCompletionBudget = true, PdfJobs = 8,
            PdfSeparateGroupingInstruction = true, PdfPairwiseGrouping = true, RepairParentChains = true,
            PdfRepairParentChains = false, PdfInlineGrouping = false, PdfBoundaryReview = false, WholeDocumentPlacement = false
        }, new Provider());
        var toolEvidenceCandidate = HeadingAgentSetup.CreateRoutes(new PipelineOptions
        {
            PdfSourceBoundSelection = true, PdfClarifyMembership = true, PdfMembershipInstructionVersion = 8,
            PdfSeparateGroupingInstruction = true, PdfPairwiseGrouping = true, RepairParentChains = true,
            PdfRepairParentChains = false, PdfInlineGrouping = false, PdfBoundaryReview = false, WholeDocumentPlacement = false,
            PdfDynamicCompletionBudget = false
        }, new Provider());
        Assert.NotEqual(qualified.NativePdf.Pdf!.SelectorId, routes.NativePdf.Pdf!.SelectorId);
        Assert.Equal(qualified.NativePdf.Placer!.Contract, routes.NativePdf.Placer!.Contract);
        Assert.Equal(candidate.NativePdf.Pdf!.SelectorId, routes.NativePdf.Pdf.SelectorId);
        Assert.NotEqual(candidate.NativePdf.Pdf.SelectorId, toolEvidenceCandidate.NativePdf.Pdf!.SelectorId);
        Assert.NotEqual(toolEvidenceCandidate.NativePdf.Pdf.SelectorId, routes.NativePdf.Pdf.SelectorId);
        Assert.Equal(new HeadingPlacer(repairParentChains: true).Contract, routes.NativePdf.Placer.Contract);
        Assert.Equal(new HeadingPlacer().Contract, routes.Shared.Placer!.Contract);
        Assert.Same(routes.Shared.Pdf, routes.NativePdf.Pdf);
        Assert.Same(routes.Shared.Docx, routes.NativePdf.Docx);
        Assert.Same(routes.Shared.Scan, routes.NativePdf.Scan);
        Assert.False(options.RepairParentChains);
        Assert.False(options.PdfBoundaryReview);
        Assert.False(options.PdfInlineGrouping);
        Assert.False(options.WholeDocumentPlacement);
    }

    [Fact]
    public void Host_supplied_pdf_selector_and_placer_keep_precedence_and_are_requested_once()
    {
        var pdf = new PdfSelector(instruction: "User rule"); var placer = new HeadingPlacer();
        var provider = new Provider(pdf, placer);
        var routes = HeadingAgentSetup.CreateRoutes(new PipelineOptions(), provider);
        Assert.Same(routes.Shared, routes.NativePdf);
        Assert.Same(pdf, routes.NativePdf.Pdf);
        Assert.Same(placer, routes.NativePdf.Placer);
        Assert.Equal(1, provider.Calls[AgentRoles.PdfSelector]);
        Assert.Equal(1, provider.Calls[AgentRoles.Placer]);
    }

    [Fact]
    public void Explicit_legacy_pdf_options_preserve_the_low_level_legacy_contract()
    {
        var routes = HeadingAgentSetup.CreateRoutes(new PipelineOptions
        {
            PdfSourceBoundSelection = false, PdfClarifyMembership = false, PdfMembershipInstructionVersion = 2,
            PdfSeparateGroupingInstruction = false, PdfPairwiseGrouping = false, PdfRepairParentChains = false,
            PdfRasterGeometryEvidence = false, PdfAdaptiveVisualSelection = false, PdfVisualSelectionMaxRequestBytes = null,
            PdfCompactGeometry = false, PdfVisualMaxWidth = 1200, PdfVisualQuality = 58, PdfVisualGrayscale = false,
            PdfDynamicCompletionBudget = false, PdfJobs = null
        }, new Provider());
        Assert.Same(routes.Shared, routes.NativePdf);
        Assert.Equal(new PdfSelector().SelectorId, routes.NativePdf.Pdf!.SelectorId);
        Assert.Equal(new HeadingPlacer().Contract, routes.NativePdf.Placer!.Contract);
    }

    [Fact]
    public void Explicit_generic_parent_repair_still_applies_to_every_route()
    {
        var routes = HeadingAgentSetup.CreateRoutes(new PipelineOptions
        { RepairParentChains = true, PdfRepairParentChains = false }, new Provider());
        Assert.Same(routes.Shared, routes.NativePdf);
        Assert.Equal(new HeadingPlacer(repairParentChains: true).Contract, routes.Shared.Placer!.Contract);
    }
}
