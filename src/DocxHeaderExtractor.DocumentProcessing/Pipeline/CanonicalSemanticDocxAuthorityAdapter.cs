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

        sourceEvidence is in document order. Evaluate every alias in ownedSourceAliases and return
        one entry for each heading you find among them. Entries marked "owned": false are shown
        only so you can read the surrounding document; never return one of them as a heading.
        The "attention" flag is a hint, not the set of allowed headings: any owned occurrence may
        be a heading. Neighbouring entries are the local context; no context is repeated per item.

        REQUIRED for every heading: its IMMEDIATE PARENT, as exactly one relationHints entry
        "parent-node:<sourceAlias>" naming a heading that appears earlier in document order, or
        "parent-node:ROOT" when the heading is top level. Omit it only when the evidence genuinely
        does not let you decide. Never emit a numeric level: the harness derives level from the
        parent relations you return.

        OPTIONAL, and only in addition to the parent hint: when a heading is another occurrence of
        a section you already reported — the same section shown again, a continued table header, a
        running title — add "same-node:<key>", giving every occurrence of that one section the same
        short key. Identical wording is NOT enough on its own: two different forms may both be
        titled "CURRICULUM VITAE" and are then different sections, so give them different keys.
        Never let this hint displace the parent hint.
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
        HeaderClassifierCanonicalTextModel? canonicalModel = null;
        if (transport is null)
        {
            result = CanonicalSemanticProductionEntryPoint.Run(
                input with { SemanticProposals = DeterministicProposals(policyState, source, aliasesBySourceId) });
        }
        else
        {
            canonicalModel = new HeaderClassifierCanonicalTextModel(transport);
            result = await CanonicalSemanticProductionEntryPoint.RunAsync(
                input, canonicalModel,
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
        // The alias catalog spans the whole document while Contexts holds only the paragraphs this
        // route carries, so a bound heading can name a source this route has no context for. Such
        // a heading cannot be materialized; drop it here instead of indexing a missing key.
        var derived = ModelRelationHierarchyResolver
            .DeriveHierarchyFromModelRelations(result.TextPipeline.BoundHeadings)
            .Where(item => source.Contexts.ContainsKey(item.SourceId))
            .ToArray();
        var structures = derived
            .ToDictionary(item => item.SourceId, item =>
            {
                var facts = source.Contexts[item.SourceId].ModelContext.Source;
                return new PdfValidatedStructure(
                    item.SourceId, item.Level, item.ParentSourceId, item.Resolution, "requires_review")
                {
                    DomainRole = facts.DomainRole,
                    StructuralScope = facts.StructuralScope,
                    DomainExclusionProposed = facts.DomainEvidence.ProposesOutlineExclusion,
                };
            }, StringComparer.Ordinal);
        var hierarchyFacts = PdfHierarchyFactsInventory.Inspect(validated, source.ModelContexts);
        // The outline carries one entry per semantic section. Repeated occurrences stay in the
        // canonical graph and in the route audit; collapsing them is the projection's job, and it
        // collapses only what the model declared to be the same node.
        var primarySourceIds = derived
            .Where(item => item.IsPrimaryOccurrence)
            .Select(item => item.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        var structuralAuthority = DocxAuthorityPipeline.MaterializeStructuralAuthority(
            validated.Where(item => primarySourceIds.Contains(item.SourceId)).ToArray(),
            structures, source.Contexts);
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
            RawAnalystResponses = canonicalModel?.RawResponses ?? [],
            ModelInputContracts = canonicalModel is null ? [] : [CanonicalSemanticContract.ProtocolVersion],
            ModelRequests = result.PrimaryTextModelCalls == 0
                ? []
                : [new RouteModelRequestAudit(
                    $"docx:{policyState.Source.DocumentId}:primary",
                    "canonical-primary-semantic",
                    source.Blocks.Select(block => block.Id).ToArray(),
                    true,
                    canonicalModel?.RawResponses.Count > 0,
                    canonicalModel?.RawResponses.Count > 0 ? "complete" : "failed")],
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
            BatchTelemetry = new PdfPipelineBatchTelemetry(
                source.Blocks.Count,
                source.Blocks.Count,
                result.PrimaryTextModelCalls == 0 ? 0 : 1,
                result.PrimaryTextModelCalls,
                0,
                0,
                0,
                result.TextPipeline.BoundHeadings.Count,
                0,
                0,
                0,
                result.CanonicalOccurrences.Count,
                0,
                result.TotalModelCalls,
                canonicalModel?.RawResponses.Count ?? 0,
                0),
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

    /// <summary>
    /// Numbering/marker observations handed to the model as evidence. They carry no hierarchy
    /// authority here: the model decides parent relations, the harness derives level from them.
    /// </summary>
    private static IReadOnlyList<string> MarkerFactsOf(PdfSourceFacts source)
    {
        if (source.Marker is not { } marker) return [];
        var facts = new List<string>
        {
            $"marker-family:{marker.Family}",
            $"marker-signature:{marker.Signature}",
            $"marker-depth:{marker.Depth}",
            $"marker-is-path:{(marker.IsPath ? "true" : "false")}",
        };
        if (!marker.Components.IsDefaultOrEmpty)
            facts.Add($"marker-components:{string.Join('.', marker.Components)}");
        return facts;
    }

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
            MarkerFactsOf(context.ModelContext.Source),
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
        public List<string> RawResponses { get; } = [];

        /// <summary>
        /// Request-shaped view of one owned occurrence. LocalBefore/LocalAfter are deliberately
        /// dropped: within a segment the neighbouring evidence entries already are that context,
        /// and repeating them was 45% of the payload. Per-run formatting spans are dropped too;
        /// style, numbering and marker facts carry the same signal far more compactly.
        /// </summary>
        private static object OwnedEvidence(CanonicalSemanticSourceEvidence item) => new
        {
            alias = item.SourceAlias,
            text = item.ExactSourceText,
            owned = true,
            scope = item.StructuralScope,
            tableDepth = item.TableDepth,
            inTableOfContents = item.InTableOfContents,
            style = item.StyleFacts,
            numbering = item.NumberingFacts,
            markers = item.MarkerFacts,
            attention = item.CandidateAttention.HeuristicMatch,
        };

        /// <summary>Owned occurrences evaluated per request. Keeps one document bounded.</summary>
        internal const int OwnedPerSegment = 120;

        /// <summary>Neighbouring occurrences a segment may read but never claim.</summary>
        internal const int VisibleMargin = 20;

        public async Task<CanonicalSemanticTextInferenceResult> InferAsync(
            CanonicalSemanticProductionInput input,
            SemanticContextPacket packedContext,
            string requestId,
            CancellationToken cancellationToken = default)
        {
            var evidence = input.SourceEvidence ?? [];
            var proposals = new List<CanonicalSemanticProposal>();
            var issues = new List<SemanticContractIssue>();
            for (var start = 0; start < evidence.Count; start += OwnedPerSegment)
            {
                var owned = evidence.Skip(start).Take(OwnedPerSegment).ToArray();
                if (owned.Length == 0) break;
                var from = Math.Max(0, start - VisibleMargin);
                var to = Math.Min(evidence.Count, start + owned.Length + VisibleMargin);
                var visible = evidence.Skip(from).Take(to - from).ToArray();
                var ownedAliases = owned.Select(item => item.SourceAlias).ToHashSet(StringComparer.Ordinal);
                var packet = JsonSerializer.Serialize(new
                {
                    protocol = CanonicalSemanticContract.ProtocolVersion,
                    ownedSourceAliases = owned.Select(item => item.SourceAlias).ToArray(),
                    // Evidence is already in document order, so a neighbour IS the local context.
                    // Owned entries carry the decision facts; margin entries carry text only.
                    sourceEvidence = visible.Select(item => ownedAliases.Contains(item.SourceAlias)
                        ? OwnedEvidence(item)
                        : (object)new { alias = item.SourceAlias, text = item.ExactSourceText, owned = false })
                        .ToArray(),
                });
                var raw = await classifier.BoundaryCutAsync(
                    SystemPrompt,
                    packet + "\nSCHEMA=" + JsonSerializer.Serialize(CanonicalSemanticContract.Schema()),
                    cancellationToken,
                    expectedItemCount: owned.Length);
                RawResponses.Add(raw);
                using var document = JsonDocument.Parse(raw);
                var segmentIssues = CanonicalSemanticContractValidator.ValidateJson(document.RootElement);
                if (segmentIssues.Count > 0)
                {
                    issues.AddRange(segmentIssues);
                    continue;
                }
                // Ownership is enforced here as well as in the contract validator: a segment may
                // read its neighbours for context but may never claim an occurrence it does not own.
                proposals.AddRange(document.RootElement.GetProperty("headings").EnumerateArray()
                    .Select(ParseProposal)
                    .Where(item => ownedAliases.Contains(item.SourceAlias)));
            }
            return new(proposals, new CanonicalSemanticInferenceTelemetry(classifier.ModelName))
            {
                ContractIssues = issues,
            };
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
