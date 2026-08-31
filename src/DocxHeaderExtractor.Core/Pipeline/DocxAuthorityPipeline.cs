using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Application.Policy;
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
    internal static DocxAuthoritySource BuildForAudit(DocxPolicyState policyState, DocumentModeReport mode) =>
        BuildForAudit(policyState, mode, quarantinedIndexes: null);

    public static async Task<StructuralAuthorityResult> RunAsync(
        DocxPolicyState policyState,
        DocumentModeReport mode,
        IHeaderClassifier? analyst,
        IReadOnlySet<int>? quarantinedIndexes = null,
        CancellationToken ct = default)
    {
        var source = BuildForAudit(policyState, mode, quarantinedIndexes);
        return await RunCoreAsync(source, analyst, ct);
    }

    private static async Task<StructuralAuthorityResult> RunCoreAsync(
        DocxAuthoritySource source,
        IHeaderClassifier? analyst,
        CancellationToken ct)
    {
        if (source.Blocks.Count == 0)
            return new StructuralAuthorityResult(new ValidatedStructure([]), null, "empty-docx-source");

        PdfBlockAnalysis roles;
        PdfBlockAnalysis spans;
        if (analyst is null)
        {
            var decisions = source.Contexts.Values
                .Where(context => IsDeterministicallyStructured(context.Source, context.Paragraph))
                .Select(context => new PdfBlockDecision(
                    context.Source.SourceId, PdfBlockRole.HeadingTopic, 1,
                    "deterministic-ooxml-structure",
                    new TextOffsetSpan(0, context.Source.Text.Length),
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
        var structuralAuthority = MaterializeStructuralAuthority(validated, structures, source.Contexts);
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
            SemanticLane = analyst is null ? null : new RouteLaneExecutionAudit("complete", source.Blocks.Count,
                roles.Decisions.Count, 0, 0),
            SpanLane = analyst is null ? null : new RouteLaneExecutionAudit("complete", roles.Decisions.Count,
                spans.Decisions.Count, 0, 0),
        };
        return new StructuralAuthorityResult(structuralAuthority, audit, "docx-source-authority");
    }

    private static ValidatedStructure MaterializeStructuralAuthority(
        IReadOnlyList<PdfValidatedHeading> validated,
        IReadOnlyDictionary<string, PdfValidatedStructure> structures,
        IReadOnlyDictionary<string, DocxAuthorityContext> contexts)
    {
        var elementIdBySourceId = validated.ToDictionary(
            item => item.SourceId,
            item => $"structural:docx:{item.SourceId}",
            StringComparer.Ordinal);
        var elements = new List<ValidatedStructuralElement>(validated.Count);

        foreach (var item in validated)
        {
            var context = contexts[item.SourceId];
            var sourceParagraph = context.Source;
            var hierarchy = structures[item.SourceId];
            var sourceFacts = new SourceFacts
            {
                SourceId = sourceParagraph.SourceId,
                RawText = sourceParagraph.Text,
                Source = new SourceAnchor
                {
                    SourceType = "docx",
                    ParagraphId = sourceParagraph.SourceId,
                    ParagraphIndex = sourceParagraph.SourceOrdinal,
                },
                RawSpan = new SourceTextSpan(0, sourceParagraph.Text.Length),
            };
            var candidate = new StructuralCandidate
            {
                CandidateId = item.SourceId,
                ObservedSourceFacts = [sourceFacts],
            };
            var proposal = new StructuralProposal
            {
                CandidateId = item.SourceId,
                Type = StructuralElementType.Heading,
                Role = ProposedRole.HeadingTopic,
                ProposedSources =
                [
                    new ProposedSourceReference(item.SourceId,
                        new StructuralSpan(item.HeadingSpan.Start, item.HeadingSpan.End)),
                ],
                ProposedParentId = hierarchy.ParentId is { } parent
                    ? elementIdBySourceId.GetValueOrDefault(parent)
                    : null,
                ProposedLevel = hierarchy.Level,
            };
            var decision = new StructuralDecision(
                "structure", nameof(HeadingDecisionStatus.RequiresReview), 0,
                "docx-authority-validated-review");
            var element = StructuralProposalValidator.Materialize(
                candidate, proposal, elementIdBySourceId[item.SourceId], decision,
                elementIdBySourceId.Values.ToHashSet(StringComparer.Ordinal),
                new StructuralProjectionMetadata
                {
                    OriginalText = sourceParagraph.Text,
                    BoundarySource = "docx-source-pointer-span",
                    StyleId = sourceParagraph.Style.StyleId,
                });
            if (element is null)
                throw new InvalidOperationException($"Validated DOCX heading '{item.SourceId}' failed generic materialization.");

            elements.Add(element with
            {
                Sources = element.Sources.Select(source => source with { StableId = sourceParagraph.SourceId }).ToArray(),
            });
        }

        return ValidatedStructure.FromElements(elements);
    }

    private static bool IsDeterministicallyStructured(SourceParagraph source, IPolicyParagraph paragraph) =>
        paragraph.HasBuiltInHeadingStyle || source.Style.OutlineLevel is >= 0 and <= 8 ||
        paragraph.NumberingStyleLevel is >= 1 and <= 9;

    private static DocxAuthoritySource BuildForAudit(
        DocxPolicyState policyState,
        DocumentModeReport mode,
        IReadOnlySet<int>? quarantinedIndexes) =>
        Build(policyState.Source, policyState.Paragraphs.ToDictionary<DocxPolicyParagraph, string, IPolicyParagraph>(p => p.Source.SourceId, p => p), mode,
            (id, text) => PdfMarkerFactsParser.Parse(text), quarantinedIndexes);

    private static DocxAuthoritySource Build(
        SourceDocument sourceDocument,
        IReadOnlyDictionary<string, IPolicyParagraph> policyParagraphs,
        DocumentModeReport mode,
        Func<string, string, PdfMarkerFact?> markerFor,
        IReadOnlySet<int>? quarantinedIndexes = null)
    {
        var compatibilityById = policyParagraphs;
        var paragraphs = sourceDocument.Paragraphs
            .Where(source => compatibilityById.ContainsKey(source.SourceId))
            .Select(source => (Source: source, Compatibility: compatibilityById[source.SourceId]))
            .Where(item => item.Compatibility.Role != ParagraphRole.Empty &&
                !string.IsNullOrWhiteSpace(item.Source.Text) &&
                (quarantinedIndexes is null || !quarantinedIndexes.Contains(item.Compatibility.Index)))
            .OrderBy(item => item.Source.SourceOrdinal)
            .ToArray();
        var result = new Dictionary<string, DocxAuthorityContext>(StringComparer.Ordinal);
        var modelContexts = new Dictionary<string, PdfCandidateContext>(StringComparer.Ordinal);
        var blocks = new List<PdfSemanticBlock>(paragraphs.Length);
        var activeStack = new List<string>();
        var scopeTracker = new StructuralScopeTracker();
        for (var index = 0; index < paragraphs.Length; index++)
        {
            var sourceParagraph = paragraphs[index].Source;
            var paragraph = paragraphs[index].Compatibility;
            var id = sourceParagraph.SourceId;
            var scope = ScopeOf(sourceParagraph, paragraph);
            var marker = markerFor(id, sourceParagraph.Text);
            var evidence = new List<string>
            {
                sourceParagraph.Text.Length <= 180 ? "short_source_paragraph" : "long_source_paragraph",
                marker is null ? "no_marker" : $"marker:{marker.Value.Family}",
            };
            if (sourceParagraph.Layout.TableDepth > 0) evidence.Add("table_like");
            if (paragraph.InTableOfContents) evidence.Add("toc_entry");
            if (paragraph.HasBuiltInHeadingStyle) evidence.Add("built_in_heading_style");
            if (sourceParagraph.Style.OutlineLevel is >= 0 and <= 8) evidence.Add($"outline_level:{sourceParagraph.Style.OutlineLevel.Value}");
            if (paragraph.NumberingStyleLevel is >= 1 and <= 9) evidence.Add($"numbering_style_level:{paragraph.NumberingStyleLevel.Value}");
            var facts = new PdfSourceFacts(id, sourceParagraph.Text, 0, 1, 0, -sourceParagraph.SourceOrdinal, 0, -sourceParagraph.SourceOrdinal,
                scope, evidence)
            {
                Marker = marker,
                LineIds = [sourceParagraph.SourceId],
                EvidenceDetails = evidence.Select(item => new PdfObservedEvidence(item, "true",
                    item.StartsWith("marker:", StringComparison.Ordinal) ? "marker_parser" :
                    item is "built_in_heading_style" || item.StartsWith("outline_level:", StringComparison.Ordinal) ||
                    item.StartsWith("numbering_style_level:", StringComparison.Ordinal) ? "ooxml_parser" : "docx_parser")).ToArray(),
            };
            facts = scopeTracker.Apply(facts);
            facts = facts with { DomainRole = DocumentDomainPolicy.Classify(facts, mode.Mode.ToString()) };
            var previous = paragraphs.Take(index).TakeLast(3).Select(item => Excerpt(item.Source.Text)).ToArray();
            var next = paragraphs.Skip(index + 1).Take(3).Select(item => Excerpt(item.Source.Text)).ToArray();
            var parents = paragraphs.Take(index).TakeLast(8).Select(item => item.Source.SourceId).ToArray();
            var modelContext = new PdfCandidateContext(facts, previous, next, parents, mode.Mode.ToString(), activeStack.TakeLast(4).ToArray());
            var context = new DocxAuthorityContext(sourceParagraph, paragraph, scope, modelContext);
            result.Add(id, context);
            modelContexts.Add(id, modelContext);
            var line = new PdfLine(0, -sourceParagraph.SourceOrdinal, sourceParagraph.Style.FontSizePt ?? 11, sourceParagraph.Text,
                sourceParagraph.Style.Bold ? 1 : 0, "", sourceParagraph.Style.Italic ? 1 : 0, 0, 1, sourceParagraph.Style.StyleName ?? "docx", "docx");
            blocks.Add(new PdfSemanticBlock(id, [line], PdfStyleClusterProfile.StyleOf(line), 0,
                -sourceParagraph.SourceOrdinal, -sourceParagraph.SourceOrdinal, 0, 1, sourceParagraph.Text));
            if (scope == "document_body" && marker is not null)
                activeStack.Add($"{id}: {Excerpt(sourceParagraph.Text)}");
        }
        return new DocxAuthoritySource(blocks, result, modelContexts);
    }

    private static string ScopeOf(SourceParagraph source, IPolicyParagraph compatibility) =>
        compatibility.InTableOfContents ? "table_of_contents" :
        source.Layout.TableDepth > 0 ? "table" :
        PdfStructuralScopeDetector.IsFormalSyntax(source.Text) ? "code_or_grammar" :
        "document_body";

    private static string Excerpt(string text) => text.Length <= 180 ? text : text[..180];
}
internal sealed record DocxAuthorityContext(
    SourceParagraph Source,
    IPolicyParagraph Paragraph,
    string Scope,
    PdfCandidateContext ModelContext);

internal sealed record DocxAuthoritySource(
    IReadOnlyList<PdfSemanticBlock> Blocks,
    IReadOnlyDictionary<string, DocxAuthorityContext> Contexts,
    IReadOnlyDictionary<string, PdfCandidateContext> ModelContexts);
