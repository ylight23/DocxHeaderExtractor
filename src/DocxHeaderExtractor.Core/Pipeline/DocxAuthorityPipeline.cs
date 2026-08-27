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
        IHeaderClassifier? analyst,
        IReadOnlySet<int>? quarantinedIndexes = null,
        CancellationToken ct = default)
    {
        var source = Build(document, mode, quarantinedIndexes);
        if (source.Blocks.Count == 0) return new DocxAuthorityPipelineResult([], null);

        PdfBlockAnalysis roles;
        PdfBlockAnalysis spans;
        if (analyst is null)
        {
            var decisions = source.Contexts.Values
                .Where(context => IsDeterministicallyStructured(context.Paragraph))
                .Select(context => new PdfBlockDecision(
                    SourceId(context.Paragraph), PdfBlockRole.HeadingTopic, 1,
                    "deterministic-ooxml-structure",
                    new TextOffsetSpan(0, context.Paragraph.Text.Length),
                    SemanticRole: PdfSemanticRole.TopicHeading))
                .ToArray();
            roles = new PdfBlockAnalysis(source.Blocks, decisions, []);
            spans = roles;
        }
        else
        {
            roles = await PdfBlockAnalyst.AnalyzeAsync(analyst, source.Blocks, source.ModelContexts, ct);
            spans = await PdfBlockAnalyst.ResolveHeadingSpansAsync(analyst, source.Blocks, roles.Decisions, source.ModelContexts, ct);
        }

        var traces = PdfProposalValidator.Trace(source.ModelContexts, spans.Decisions);
        var validated = PdfProposalValidator.Validate(source.ModelContexts, spans.Decisions);
        var markerStructures = PdfHierarchyResolver.Resolve(validated, source.ModelContexts);
        var hierarchyFacts = PdfHierarchyFactsInventory.Inspect(validated, source.ModelContexts);
        var semanticHierarchy = analyst is null
            ? new PdfSemanticHierarchyResult(markerStructures, [], [], [])
            : await PdfSemanticHierarchyFallback.ResolveAsync(analyst, validated, markerStructures, source.ModelContexts, ct);
        var structures = semanticHierarchy.Structures.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
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
                SourceId = item.SourceId,
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

    private static bool IsDeterministicallyStructured(SlimParagraph paragraph) =>
        paragraph.HasBuiltInHeadingStyle || paragraph.OutlineLevel is >= 0 and <= 8 ||
        paragraph.NumberingStyleLevel is >= 1 and <= 9;

    private static DocxAuthoritySource Build(SlimDocument document, DocumentModeReport mode,
        IReadOnlySet<int>? quarantinedIndexes = null)
    {
        var paragraphs = document.Paragraphs.Where(paragraph => paragraph.Role != ParagraphRole.Empty &&
                !string.IsNullOrWhiteSpace(paragraph.Text) &&
                (quarantinedIndexes is null || !quarantinedIndexes.Contains(paragraph.Index)))
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
            if (paragraph.HasBuiltInHeadingStyle) evidence.Add("built_in_heading_style");
            if (paragraph.OutlineLevel is >= 0 and <= 8) evidence.Add($"outline_level:{paragraph.OutlineLevel.Value}");
            if (paragraph.NumberingStyleLevel is >= 1 and <= 9) evidence.Add($"numbering_style_level:{paragraph.NumberingStyleLevel.Value}");
            var facts = new PdfSourceFacts(id, paragraph.Text, 0, 1, 0, -paragraph.Index, 0, -paragraph.Index,
                scope, evidence)
            {
                Marker = marker,
                LineIds = [paragraph.StableId],
                EvidenceDetails = evidence.Select(item => new PdfObservedEvidence(item, "true",
                    item.StartsWith("marker:", StringComparison.Ordinal) ? "marker_parser" :
                    item is "built_in_heading_style" || item.StartsWith("outline_level:", StringComparison.Ordinal) ||
                    item.StartsWith("numbering_style_level:", StringComparison.Ordinal) ? "ooxml_parser" : "docx_parser")).ToArray(),
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
