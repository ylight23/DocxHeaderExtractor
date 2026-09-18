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

        REQUIRED for every heading: exactly one relationHints entry saying where it sits.
          "parent-node:<sourceAlias>" - it belongs under that earlier heading.
          "parent-node:ROOT"          - it is a top-level section of this document.
          "parent-node:NONE"          - it is a heading but holds NO position in the section tree.
        Use NONE for the document's own title and subtitle, running headers and footers, table and
        figure labels, form labels and signature labels. They are real headings and you should
        still report them, but they are not sections: they have no level and nothing is filed
        under them. Putting a title at ROOT instead pushes every real section one level deeper.
        Omit the entry entirely only when the evidence genuinely does not let you decide - that is
        an open question for a human, not the same as NONE, which is your decision.
        Never emit a numeric level: the harness derives level from the relations you return.

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
        // Reasoning surface #3. The first pass answers three questions at once — is this a
        // heading, what does it mean, where does it sit — and measurably trades placement away
        // when the prompt grows. Rather than crowd that prompt further, headings it left
        // unresolved come back in one narrow follow-up that asks only about position, against a
        // heading list that is already settled. Bounded to a single round: an unresolved heading
        // is a legitimate outcome, not something to keep re-asking about.
        var boundHeadings = transport is null
            ? result.TextPipeline.BoundHeadings
            : await PlaceUnresolvedHeadingsAsync(
                result.TextPipeline.BoundHeadings, transport, cancellationToken);
        var derived = ModelRelationHierarchyResolver
            .DeriveHierarchyFromModelRelations(boundHeadings)
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
            ConflictCensus = SemanticConflictCensus.Take(
                result.ConflictNormalization,
                CanonicalSemanticGlobalConflictDetector.Detect(
                    result.NormalizedModelProposals,
                    SemanticSourceAliasCatalog.FromCatalog(catalog),
                    result.SemanticConflicts),
                result.SemanticAdjudicationCalls,
                result.GlobalReopenCalls,
                result.TextPipeline.BoundHeadings.Select(item => item.SourceId)
                    .ToHashSet(StringComparer.Ordinal)),
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

    private const string PlacementPrompt = """
        You are the structural stage of the A99 canonical document pipeline. The heading list below
        is already settled: do not add, remove, rename or re-judge any entry. Decide one thing only
        — where each heading in "toPlace" sits relative to the others.

        Answer with {"placements":[{"alias":"<alias>","parent":"<alias>|ROOT|NONE"}]}.
          "<alias>" - it belongs under that heading, which must appear earlier in the list.
          "ROOT"    - it is a top-level section of this document.
          "NONE"    - it is a heading but holds no position in the section tree: the document's own
                      title or subtitle, a meeting date or venue line, a running header, a table or
                      figure label, a form label, an annex label.
        Omit an alias entirely if the evidence still does not let you decide. Never return a level:
        the harness derives depth from the relations you give.
        """;

    /// <summary>
    /// Re-asks only about headings the first pass left unplaced, and only about placement. The
    /// reply may add a parent relation to those headings and nothing else: a heading that was
    /// already placed keeps its relation, and no heading is added or dropped here.
    /// </summary>
    private static async Task<IReadOnlyList<CanonicalSemanticBoundHeading>> PlaceUnresolvedHeadingsAsync(
        IReadOnlyList<CanonicalSemanticBoundHeading> bound,
        IHeaderClassifier classifier,
        CancellationToken cancellationToken)
    {
        var derived = ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(bound);
        var unplaced = derived
            .Where(item => item.Resolution == ModelRelationHierarchyResolver.Unresolved)
            .Select(item => item.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        if (unplaced.Count == 0) return bound;

        var ordered = bound.OrderBy(item => item.SourceOrdinal).ThenBy(item => item.Start).ToArray();
        var packet = JsonSerializer.Serialize(new
        {
            headings = ordered.Select(item => new { alias = item.Alias, text = item.Text }).ToArray(),
            toPlace = ordered.Where(item => unplaced.Contains(item.SourceId))
                .Select(item => item.Alias).ToArray(),
        });

        string raw;
        try
        {
            raw = await classifier.BoundaryCutAsync(
                PlacementPrompt, packet, cancellationToken, expectedItemCount: unplaced.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Placement is an improvement pass. If it cannot run, the headings stay unresolved,
            // which is exactly what they already were.
            return bound;
        }

        Dictionary<string, string> parentByAlias;
        try
        {
            using var document = JsonDocument.Parse(raw);
            parentByAlias = document.RootElement.TryGetProperty("placements", out var placements)
                ? placements.EnumerateArray()
                    .Where(item => item.TryGetProperty("alias", out _) && item.TryGetProperty("parent", out _))
                    .GroupBy(item => item.GetProperty("alias").GetString() ?? string.Empty, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().GetProperty("parent").GetString() ?? string.Empty,
                        StringComparer.Ordinal)
                : [];
        }
        catch (JsonException)
        {
            return bound;
        }

        var aliasesToPlace = ordered.Where(item => unplaced.Contains(item.SourceId))
            .Select(item => item.Alias).ToHashSet(StringComparer.Ordinal);
        return bound.Select(item =>
        {
            // Only a heading that was actually unresolved may gain a relation here, so a second
            // pass can never overwrite what the semantic pass already decided.
            if (!aliasesToPlace.Contains(item.Alias)) return item;
            if (!parentByAlias.TryGetValue(item.Alias, out var parent) || string.IsNullOrWhiteSpace(parent))
                return item;
            return item with { RelationHints = [.. item.RelationHints, $"parent-node:{parent}"] };
        }).ToArray();
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
                // A reply that is not JSON at all - truncated mid-object, wrapped in prose, empty -
                // costs this segment. It used to throw out of the segment loop and end the document,
                // so one bad reply among sixteen discarded the other fifteen with no record of why.
                JsonDocument? parsed = null;
                try
                {
                    parsed = JsonDocument.Parse(raw);
                }
                catch (JsonException error)
                {
                    issues.Add(new SemanticContractIssue("UNPARSEABLE_REPLY", null,
                        $"The reply for this segment was not valid JSON: {error.Message}"));
                }

                if (parsed is null) continue;
                using var document = parsed;
                var segmentIssues = CanonicalSemanticContractValidator.ValidateJson(document.RootElement);
                if (segmentIssues.Count > 0)
                {
                    issues.AddRange(segmentIssues);
                    continue;
                }
                // Ownership is enforced here as well as in the contract validator: a segment may
                // read its neighbours for context but may never claim an occurrence it does not own.
                // One malformed entry must cost that entry, not the document. The contract
                // validator rejects what it can describe; a reply missing a required field cannot
                // even be read, so it is dropped here and recorded as a contract issue.
                if (!document.RootElement.TryGetProperty("headings", out var headings) ||
                    headings.ValueKind != JsonValueKind.Array)
                {
                    issues.Add(new SemanticContractIssue("MISSING_HEADINGS_ARRAY", null,
                        "The reply carried no headings array."));
                    continue;
                }
                foreach (var element in headings.EnumerateArray())
                {
                    if (ParseProposal(element) is not { } proposal)
                    {
                        issues.Add(new SemanticContractIssue("UNREADABLE_PROPOSAL", null,
                            "A heading entry omitted a required field and was dropped."));
                        continue;
                    }
                    if (ownedAliases.Contains(proposal.SourceAlias)) proposals.Add(proposal);
                }
            }
            return new(proposals, new CanonicalSemanticInferenceTelemetry(classifier.ModelName))
            {
                ContractIssues = issues,
            };
        }

        /// <summary>
        /// Null when the entry cannot be read. Absent optional fields are fine; a field that is
        /// present with the wrong JSON type is not, and costs this entry alone.
        /// <para>
        /// Every read goes through a typed accessor rather than <c>GetString</c>/<c>GetInt32</c>
        /// directly. Those throw on type confusion, and the throw escaped this method, the entry
        /// loop and the segment loop, so a single reply with <c>"occurrence": "1"</c> or
        /// <c>"sourceAliases": "S0123"</c> ended the whole document with nothing recorded.
        /// </para>
        /// </summary>
        private static CanonicalSemanticProposal? ParseProposal(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            if (Text(element, "sourceAlias") is not { Length: > 0 } sourceAlias) return null;
            if (!element.TryGetProperty("isHeading", out var isHeadingValue) ||
                isHeadingValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;

            if (!TryTextArray(element, "sourceAliases", out var sourceAliases)) return null;
            if (!TryTextArray(element, "relationHints", out var relationHints)) return null;
            if (!TryOrdinal(element, "occurrence", out var occurrence)) return null;
            if (!TryText(element, "verbatimText", out var verbatimText)) return null;
            if (!TryTextArray(element, "verbatimParts", out var verbatimParts)) return null;
            if (!TryText(element, "semanticRole", out var semanticRole)) return null;
            if (!TryText(element, "structuralType", out var structuralType)) return null;
            if (!TryText(element, "scope", out var scope)) return null;
            if (!TryText(element, "leftExactContext", out var left)) return null;
            if (!TryText(element, "rightExactContext", out var right)) return null;
            if (!TryText(element, "selectionMode", out var mode)) return null;

            return new CanonicalSemanticProposal(
                sourceAlias,
                isHeadingValue.GetBoolean(),
                verbatimText,
                verbatimParts,
                semanticRole,
                structuralType,
                scope,
                relationHints,
                sourceAliases,
                occurrence,
                left,
                right,
                mode);
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        /// <summary>False only when the field is present and is not a string or null.</summary>
        private static bool TryText(JsonElement element, string name, out string? text)
        {
            text = null;
            if (!element.TryGetProperty(name, out var value)) return true;
            if (value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.String) return false;
            text = value.GetString();
            return true;
        }

        /// <summary>False only when the field is present and is not an array of strings.</summary>
        private static bool TryTextArray(JsonElement element, string name, out string[]? items)
        {
            items = null;
            if (!element.TryGetProperty(name, out var value)) return true;
            if (value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.Array) return false;
            var result = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                // A null or non-string element makes the list unreadable: silently substituting an
                // empty string would shift every later alias-to-part mapping by one.
                if (item.ValueKind != JsonValueKind.String) return false;
                result.Add(item.GetString()!);
            }
            items = [.. result];
            return true;
        }

        /// <summary>False only when the field is present and is not a 32-bit integer.</summary>
        private static bool TryOrdinal(JsonElement element, string name, out int? ordinal)
        {
            ordinal = null;
            if (!element.TryGetProperty(name, out var value)) return true;
            if (value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) return false;
            ordinal = number;
            return true;
        }
    }
}
