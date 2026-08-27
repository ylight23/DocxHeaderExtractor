using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// DOCX source adapter for the same authority pipeline used by PDF-first extraction. DOCX text,
/// stable paragraph identity, and pointer spans remain source facts; the 9B model can only make
/// closed role/span/parent proposals through the shared analyst contracts.
/// </summary>
internal static class DocxAuthorityPipeline
{
    internal static DocxAuthoritySource BuildForAudit(SlimDocument document, DocumentModeReport mode) => Build(document, mode);

    public static async Task<DocxAuthorityPipelineResult> RunAsync(
        SlimDocument document,
        DocumentModeReport mode,
        IHeaderClassifier analyst,
        CancellationToken ct = default)
    {
        var source = Build(document, mode);
        if (source.Blocks.Count == 0) return new DocxAuthorityPipelineResult([], null);

        var roles = await PdfBlockAnalyst.AnalyzeAsync(analyst, source.Blocks, source.ModelContexts, ct);
        var spans = await PdfBlockAnalyst.ResolveHeadingSpansAsync(analyst, source.Blocks, roles.Decisions, source.ModelContexts, ct);
        var traces = PdfProposalValidator.Trace(source.ModelContexts, spans.Decisions);
        var validated = spans.Decisions.Where(decision => source.Contexts.TryGetValue(decision.Id, out var context) &&
                IsEligible(decision, context))
            .Select(decision => new PdfValidatedHeading(decision.Id, decision.HeadingSpan!, decision.Role,
                source.Contexts[decision.Id].Scope, "docx-source-pointer-span"))
            .ToArray();
        var markerStructures = PdfHierarchyResolver.Resolve(validated, source.ModelContexts);
        var hierarchyFacts = PdfHierarchyFactsInventory.Inspect(validated, source.ModelContexts);
        var semanticHierarchy = await PdfSemanticHierarchyFallback.ResolveAsync(analyst, validated, markerStructures, source.ModelContexts, ct);
        var structures = semanticHierarchy.Structures.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var byId = source.Contexts;
        var headings = validated.Select(item =>
        {
            var context = source.Contexts[item.SourceId];
            var paragraph = context.Paragraph;
            var structure = structures[item.SourceId];
            var span = item.HeadingSpan;
            return new HeadingRecord
            {
                Index = paragraph.Index,
                StableId = paragraph.StableId,
                Level = structure.Level,
                Text = paragraph.Text[span.Start..span.End],
                OriginalText = paragraph.Text,
                HeadingSpan = span,
                BoundarySource = "docx-source-pointer-span",
                StyleId = paragraph.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0,
                DecisionStatus = HeadingDecisionStatus.RequiresReview,
                ConfidenceBasis = "docx-authority-validated-review",
            };
        }).ToArray();
        var audit = new RouteExecutionAudit(
            "docx-authority-v1",
            source.Blocks.Count,
            source.Blocks.Count,
            0,
            0,
            source.Blocks.Select(block => new RouteBlockAudit(block.Id, 0, block.DisplayText)).ToArray(),
            source.Blocks.Select(block => new RouteBlockAudit(block.Id, 0, block.DisplayText)).ToArray(),
            [],
            spans.Decisions.Select(decision => new RouteBlockDecisionAudit(decision.Id, decision.Role.ToString(), decision.Confidence)).ToArray(),
            validated.Select(item => item.SourceId).ToArray(),
            [],
            validated.Select(item => item.SourceId).ToArray())
        {
            RawAnalystResponses = roles.RawResponses.Concat(spans.RawResponses).Concat(semanticHierarchy.RawResponses).ToArray(),
            ModelInputContracts = roles.InputContracts.Concat(spans.InputContracts).Concat(semanticHierarchy.InputContracts).ToArray(),
            CandidateStageTraces = traces,
            ValidatedStructures = semanticHierarchy.Structures,
            HierarchyProposals = semanticHierarchy.Audit,
            HierarchyFacts = hierarchyFacts,
        };
        return new DocxAuthorityPipelineResult(headings, audit);
    }

    private static bool IsEligible(PdfBlockDecision decision, DocxAuthorityContext context) =>
        decision.Role == PdfBlockRole.HeadingTopic &&
        context.Scope == "document_body" &&
        !DocumentDomainPolicy.IsExcludedFromOutline(context.ModelContext.Source.DomainRole) &&
        decision.HeadingSpan is { } span && span.Start >= 0 && span.End > span.Start && span.End <= context.Paragraph.Text.Length;

    private static DocxAuthoritySource Build(SlimDocument document, DocumentModeReport mode)
    {
        var paragraphs = document.Paragraphs.Where(paragraph => paragraph.Role != ParagraphRole.Empty &&
                !string.IsNullOrWhiteSpace(paragraph.Text))
            .OrderBy(paragraph => paragraph.Index)
            .ToArray();
        var result = new Dictionary<string, DocxAuthorityContext>(StringComparer.Ordinal);
        var modelContexts = new Dictionary<string, PdfCandidateContext>(StringComparer.Ordinal);
        var blocks = new List<PdfSemanticBlock>(paragraphs.Length);
        var activeStack = new List<string>();
        var scopeTracker = new StructuralScopeTracker();
        for (var index = 0; index < paragraphs.Length; index++)
        {
            var paragraph = paragraphs[index];
            var id = SourceId(paragraph);
            var scope = ScopeOf(paragraph);
            var marker = NumberingAudit.ParseParagraph(paragraph, paragraph.Text) is { } strict
                ? new PdfMarkerFact(strict.Signature, strict.Depth, strict.Kind.ToString().ToLowerInvariant(), strict.Kind == NumberKind.Arabic)
                : PdfMarkerFactsParser.Parse(paragraph.Text);
            var evidence = new List<string>
            {
                paragraph.Text.Length <= 180 ? "short_source_paragraph" : "long_source_paragraph",
                marker is null ? "no_marker" : $"marker:{marker.Value.Family}",
            };
            if (paragraph.TableDepth > 0) evidence.Add("table_like");
            if (paragraph.InTableOfContents) evidence.Add("toc_entry");
            var facts = new PdfSourceFacts(id, paragraph.Text, 0, 1, 0, -paragraph.Index, 0, -paragraph.Index,
                scope, evidence)
            {
                Marker = marker,
                LineIds = [paragraph.StableId],
                EvidenceDetails = evidence.Select(item => new PdfObservedEvidence(item, "true",
                    item.StartsWith("marker:", StringComparison.Ordinal) ? "marker_parser" : "docx_parser")).ToArray(),
            };
            facts = scopeTracker.Apply(facts);
            facts = facts with { DomainRole = DocumentDomainPolicy.Classify(facts, mode.Mode.ToString()) };
            var previous = paragraphs.Take(index).TakeLast(3).Select(item => Excerpt(item.Text)).ToArray();
            var next = paragraphs.Skip(index + 1).Take(3).Select(item => Excerpt(item.Text)).ToArray();
            var parents = paragraphs.Take(index).TakeLast(8).Select(SourceId).ToArray();
            var modelContext = new PdfCandidateContext(facts, previous, next, parents, mode.Mode.ToString(), activeStack.TakeLast(4).ToArray());
            var context = new DocxAuthorityContext(paragraph, scope, modelContext);
            result.Add(id, context);
            modelContexts.Add(id, modelContext);
            var line = new PdfLine(0, -paragraph.Index, paragraph.FontSizePt ?? 11, paragraph.Text,
                paragraph.Bold ? 1 : 0, "", paragraph.Italic ? 1 : 0, 0, 1, paragraph.StyleName ?? "docx", "docx");
            blocks.Add(new PdfSemanticBlock(id, [line], PdfStyleClusterProfile.StyleOf(line), 0,
                -paragraph.Index, -paragraph.Index, 0, 1, paragraph.Text));
            if (scope == "document_body" && marker is not null)
                activeStack.Add($"{id}: {Excerpt(paragraph.Text)}");
        }
        return new DocxAuthoritySource(blocks, result, modelContexts);
    }

    private static string ScopeOf(SlimParagraph paragraph) =>
        paragraph.InTableOfContents ? "table_of_contents" :
        paragraph.TableDepth > 0 ? "table" :
        PdfStructuralScopeDetector.IsFormalSyntax(paragraph.Text) ? "code_or_grammar" :
        "document_body";

    private static string SourceId(SlimParagraph paragraph) =>
        string.IsNullOrWhiteSpace(paragraph.StableId) ? $"p{paragraph.Index}" : paragraph.StableId;

    private static string Excerpt(string text) => text.Length <= 180 ? text : text[..180];
}

internal sealed record DocxAuthorityContext(SlimParagraph Paragraph, string Scope, PdfCandidateContext ModelContext);

internal sealed record DocxAuthoritySource(
    IReadOnlyList<PdfSemanticBlock> Blocks,
    IReadOnlyDictionary<string, DocxAuthorityContext> Contexts,
    IReadOnlyDictionary<string, PdfCandidateContext> ModelContexts);

internal sealed record DocxAuthorityPipelineResult(IReadOnlyList<HeadingRecord> Headings, RouteExecutionAudit? Audit);
