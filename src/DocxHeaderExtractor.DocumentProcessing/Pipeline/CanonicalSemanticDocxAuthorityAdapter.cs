using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Compatibility transport adapter for the normal DOCX route. The legacy classifier is only a
/// provider transport seam; semantic output crosses into the Core vNext contract before binding,
/// graph resolution, or projection. It never supplies coordinates or hierarchy truth.
/// </summary>
internal static class CanonicalSemanticDocxAuthorityAdapter
{
    private const string SystemPrompt = """
        You are the primary semantic reasoning stage of the A99 canonical document pipeline.
        Decide semantic meaning only for the supplied parser-owned source aliases. Return strict
        JSON matching the supplied schema. sourceAlias/sourceAliases and verbatimText are the only
        source references allowed. Do not return offsets, spans, pages, boxes, coordinates, or
        generated text. Formatting and numbering are evidence, never deterministic truth.
        """;

    public static async Task<StructuralAuthorityResult> RunAsync(
        DocxPolicyState policyState,
        DocumentModeReport mode,
        IHeaderClassifier? transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policyState);
        var source = DocxAuthorityPipeline.BuildForAudit(policyState, mode);
        if (source.Blocks.Count == 0)
            return new StructuralAuthorityResult(new ValidatedStructure([]), null, "empty-docx-source");

        var catalog = DocumentSourceCatalogBuilder.FromSourceDocument(policyState.Source);
        var aliasesBySourceId = SemanticSourceAliasCatalog.FromCatalog(catalog)
            .ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var sourceHash = CanonicalSemanticSourceHash.Compute(policyState.Source.SourcePath);
        var evidence = source.Contexts.Values
            .OrderBy(item => item.Source.SourceOrdinal)
            .Select(item => EvidenceOf(item, aliasesBySourceId[item.Source.SourceId].Alias))
            .ToArray();
        var input = new CanonicalSemanticProductionInput(
            catalog,
            null,
            sourceHash,
            [new CanonicalSemanticPageEvidence("DOCX", true, 0, "docx-source")],
            evidence.Select(item => item.CandidateAttention).ToArray(),
            evidence.Select(item => $"[{item.SourceAlias}] {item.ExactSourceText}").ToArray(),
            [],
            evidence.SelectMany(item => item.LocalBefore.Concat(item.LocalAfter)).ToArray(),
            DocumentId: policyState.Source.DocumentId,
            SourceEvidence: evidence)
        {
            ExpectedSourceSha256 = sourceHash,
            OwnedAliases = null,
        };

        CanonicalSemanticProductionResult result;
        if (transport is null)
        {
            result = CanonicalSemanticProductionEntryPoint.Run(
                input with { SemanticProposals = DeterministicProposals(policyState, source, aliasesBySourceId) });
        }
        else
        {
            result = await CanonicalSemanticProductionEntryPoint.RunAsync(
                input, new HeaderClassifierCanonicalTextModel(transport),
                requestId: $"docx:{policyState.Source.DocumentId}",
                cancellationToken: cancellationToken);
        }

        var decisions = result.TextPipeline.BoundHeadings.Select(item => new PdfBlockDecision(
            item.SourceId,
            PdfBlockRole.HeadingTopic,
            1,
            "canonical-vnext-semantic-contract",
            new TextOffsetSpan(item.Start, item.End),
            SemanticRole: ParseSemanticRole(item.SemanticRole))).ToArray();
        var validated = PdfProposalValidator.Validate(source.ModelContexts, decisions);
        var structures = PdfHierarchyResolver.Resolve(validated, source.ModelContexts)
            .ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var hierarchyFacts = PdfHierarchyFactsInventory.Inspect(validated, source.ModelContexts);
        var structuralAuthority = DocxAuthorityPipeline.MaterializeStructuralAuthority(
            validated, structures, source.Contexts);
        var audit = new RouteExecutionAudit(
            "docx-canonical-vnext",
            source.Blocks.Count,
            source.Blocks.Count,
            0,
            0,
            source.Blocks.Select(block => new RouteBlockAudit(block.Id, 0, block.DisplayText)).ToArray(),
            source.Blocks.Select(block => new RouteBlockAudit(block.Id, 0, block.DisplayText)).ToArray(),
            [],
            decisions.Select(decision => new RouteBlockDecisionAudit(
                decision.Id, decision.Role.ToString(), decision.Confidence)
            {
                SemanticRole = decision.SemanticRole.ToString(),
                ProposedSourceSpan = decision.ProposedSourceSpan,
            }).ToArray(),
            validated.Select(item => item.SourceId).ToArray(),
            [],
            validated.Select(item => item.SourceId).ToArray())
        {
            Route = "docx-canonical-vnext",
            CandidateStageTraces = source.Contexts.Values.Select(context =>
                new PdfCandidateStageTrace(
                    context.Source.SourceId,
                    context.Scope,
                    context.Source.SourceId is not null && validated.Any(item => item.SourceId == context.Source.SourceId)
                        ? "canonical-semantic"
                        : "not-heading",
                    "source-grounded",
                    validated.Any(item => item.SourceId == context.Source.SourceId) ? "valid" : "not-selected",
                    null)).ToArray(),
            ValidatedStructures = structures.Values.ToArray(),
            HierarchyFacts = hierarchyFacts,
            SemanticLane = new RouteLaneExecutionAudit("complete", source.Blocks.Count,
                validated.Count, 0, 0),
            SpanLane = new RouteLaneExecutionAudit("canonical-binder", result.TextPipeline.BoundHeadings.Count,
                result.TextPipeline.BoundHeadings.Count, 0, result.TextPipeline.BindingFailureCount),
        };
        return new StructuralAuthorityResult(
            structuralAuthority,
            audit,
            "docx-canonical-vnext-semantic-authority");
    }

    private static IReadOnlyList<CanonicalSemanticProposal> DeterministicProposals(
        DocxPolicyState policyState,
        DocxAuthoritySource source,
        IReadOnlyDictionary<string, SemanticSourceAlias> aliasesBySourceId) =>
        source.Contexts.Values
            .Where(context => context.Paragraph.HasBuiltInHeadingStyle ||
                context.Source.Style.OutlineLevel is >= 0 and <= 8 ||
                context.Paragraph.NumberingStyleLevel is >= 1 and <= 9)
            .Select(context =>
            {
                var alias = aliasesBySourceId[context.Source.SourceId];
                return new CanonicalSemanticProposal(
                    alias.Alias,
                    true,
                    alias.Text,
                    SemanticRole: "SECTION",
                    StructuralType: "Heading",
                    Scope: context.Scope,
                    SelectionMode: CanonicalSemanticSelectionMode.WholeAlias);
            }).ToArray();

    private static CanonicalSemanticSourceEvidence EvidenceOf(
        DocxAuthorityContext context,
        string alias)
    {
        var paragraph = context.Paragraph;
        var source = context.Source;
        return new CanonicalSemanticSourceEvidence(
            alias,
            source.SourceId,
            source.SourceOrdinal,
            source.Text,
            context.Scope,
            source.Layout.TableDepth,
            source.Layout.SectionIndex,
            source.Layout.InContentControl,
            source.InTableOfContents,
            ["docx-parser-source", $"scope:{context.Scope}"],
            new { source.Style.StyleId, source.Style.StyleName, source.Style.OutlineLevel, source.Style.Bold },
            new { source.Numbering.NumberingId, source.Numbering.NumberingLevel, source.Numbering.NumberLabel },
            source.TextSpans.Select(span => (object)new { span.Start, span.End, span.Bold, span.Italic, span.Underline }).ToArray(),
            [],
            [paragraph.IsCandidate ? "candidate-attention" : "source-visible"],
            context.ModelContext.PreviousBlocks,
            context.ModelContext.NextBlocks,
            new SemanticCandidateAttentionHint(alias, paragraph.IsCandidate, paragraph.IsCandidate ? "policy-candidate" : "policy-non-candidate"));
    }

    private static PdfSemanticRole ParseSemanticRole(string? role) =>
        Enum.TryParse<PdfSemanticRole>(role, ignoreCase: true, out var parsed)
            ? parsed
            : PdfSemanticRole.SectionHeading;

    private sealed class HeaderClassifierCanonicalTextModel(IHeaderClassifier classifier) : ICanonicalSemanticTextModel
    {
        public async Task<CanonicalSemanticTextInferenceResult> InferAsync(
            CanonicalSemanticProductionInput input,
            SemanticContextPacket packedContext,
            string requestId,
            CancellationToken cancellationToken = default)
        {
            var packet = JsonSerializer.Serialize(new
            {
                protocol = CanonicalSemanticContract.ProtocolVersion,
                sourceEvidence = input.SourceEvidence,
                targetEvidence = packedContext.TargetEvidence,
                localContext = packedContext.LocalContext,
                globalContext = packedContext.GlobalContext,
            });
            var raw = await classifier.BoundaryCutAsync(
                SystemPrompt,
                packet + "\nSCHEMA=" + JsonSerializer.Serialize(CanonicalSemanticContract.Schema()),
                cancellationToken);
            using var document = JsonDocument.Parse(raw);
            var issues = CanonicalSemanticContractValidator.ValidateJson(document.RootElement);
            if (issues.Count > 0)
                throw new FormatException(string.Join(",", issues.Select(issue => issue.Code)));
            var proposals = document.RootElement.GetProperty("headings").EnumerateArray()
                .Select(ParseProposal)
                .ToArray();
            return new(proposals, new CanonicalSemanticInferenceTelemetry(classifier.ModelName));
        }

        private static CanonicalSemanticProposal ParseProposal(JsonElement element)
        {
            var sourceAlias = element.GetProperty("sourceAlias").GetString()
                ?? throw new FormatException("SOURCE_ALIAS_MISSING");
            var sourceAliases = element.TryGetProperty("sourceAliases", out var aliases)
                ? aliases.EnumerateArray().Select(item => item.GetString() ?? throw new FormatException("SOURCE_ALIAS_MISSING")).ToArray()
                : null;
            var relationHints = element.TryGetProperty("relationHints", out var relations)
                ? relations.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
                : null;
            return new CanonicalSemanticProposal(
                sourceAlias,
                element.GetProperty("isHeading").GetBoolean(),
                element.TryGetProperty("verbatimText", out var text) ? text.GetString() : null,
                element.TryGetProperty("verbatimParts", out var parts)
                    ? parts.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
                    : null,
                element.TryGetProperty("semanticRole", out var role) ? role.GetString() : null,
                element.TryGetProperty("structuralType", out var type) ? type.GetString() : null,
                element.TryGetProperty("scope", out var scope) ? scope.GetString() : null,
                relationHints,
                sourceAliases,
                element.TryGetProperty("occurrence", out var occurrence) ? occurrence.GetInt32() : null,
                element.TryGetProperty("leftExactContext", out var left) ? left.GetString() : null,
                element.TryGetProperty("rightExactContext", out var right) ? right.GetString() : null,
                element.TryGetProperty("selectionMode", out var mode) ? mode.GetString() : null);
        }
    }
}
