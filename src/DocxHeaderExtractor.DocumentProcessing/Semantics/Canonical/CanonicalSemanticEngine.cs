using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;

/// <summary>
/// Canonical text inference used by the DOCX authority, plus the shared placement prompt.
/// Source aliases and observable facts are supplied by the caller. PDF heading discovery uses
/// the separate qualified function/anchor/end-pointer authority, not this discovery model.
/// </summary>
internal static class CanonicalSemanticEngine
{
    private const string RawSystemPrompt = """
        You are the primary semantic reasoning stage of the A99 canonical document pipeline.
        Decide semantic meaning only for the supplied parser-owned source aliases. Return strict
        JSON matching the supplied schema. sourceAlias/sourceAliases and verbatimText are the only
        source references allowed. Do not return offsets, spans, pages, boxes, coordinates, or
        generated text. Formatting and numbering are evidence, never deterministic truth.

        sourceEvidence is in document order. Evaluate every alias in ownedSourceAliases and return
        one entry for each heading you find among them. Entries marked "owned": false are shown
        only so you can read the surrounding document; never return one of them as a heading.
        Any owned occurrence may be a heading. Neighbouring entries are the local context; no
        context is repeated per item.

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

    /// <summary>
    /// Normalizes to LF, because a prompt is bytes on the wire and must not depend on how the
    /// source file happened to be checked out.
    /// <para>
    /// A raw string literal keeps whatever line endings the file has. Git hands a Windows checkout
    /// CRLF and a Linux one LF, so the same commit produced two different prompts - 2,330
    /// characters against 2,301 - and every frozen prompt hash was therefore a property of the
    /// machine rather than of the code. Two people could run what looked like the same experiment
    /// and send different requests.
    /// </para>
    /// <para>
    /// Normalized once here, where the prompt is owned, rather than at each call site: a caller
    /// that forgot would reintroduce exactly this defect, and nothing would say so.
    /// </para>
    /// </summary>
    internal static string NormalizePromptLineEndings(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return prompt.ReplaceLineEndings("\n");
    }

    /// <summary>The semantic discovery prompt, line endings settled.</summary>
    internal static string SystemPrompt { get; } = NormalizePromptLineEndings(RawSystemPrompt);

    /// <summary>The discovery prompt of a request version; an unknown version is refused.</summary>
    internal static string SystemPromptOf(SemanticRequestVersion version)
    {
        SemanticRequestVersions.Require(version);
        return SystemPrompt;
    }

    /// <summary>The prompt a request version sends, before any coordinate contract clause.</summary>
    internal static string SystemPromptFor(CanonicalSemanticExperiment experiment) =>
        SystemPromptOf(experiment.RequestVersion);

    /// <summary>
    /// The shared core, plus whatever coordinate shape the lane's contract teaches.
    /// <para>
    /// A contract with no <see cref="SemanticCoordinateContract.PromptClause"/> gets exactly the
    /// prompt it always got - DOCX falls here, which is what keeps its prompt
    /// hashes unmoved by this seam existing at all. A contract that introduces a new coordinate
    /// shape appends its own teaching text; the core paragraphs it is appended to are identical
    /// either way, which is what keeps this one semantic core rather than two.
    /// </para>
    /// </summary>
    internal static string SystemPromptFor(SemanticCoordinateContract contract, CanonicalSemanticExperiment experiment)
    {
        var prompt = SystemPromptOf(experiment.RequestVersion);
        return contract.PromptClause is null ? prompt : prompt + contract.PromptClause;
    }

    /// <summary>
    /// The semantic core. One reasoning model, one ontology, one set of instructions - and a
    /// coordinate contract handed in by the lane that knows how its source is addressed.
    /// <para>
    /// It used to read that contract off a static, which quietly made every source format share one
    /// answer to a question only the source can answer. The engine no longer chooses: it does not
    /// look at a file extension, a runtime type or a flag, it uses what it was given.
    /// </para>
    /// </summary>
    internal sealed class CanonicalTextInferenceModel(
        IInferenceTransport transport,
        SemanticCoordinateContract contract,
        ISemanticEvidencePackingPolicy packingPolicy,
        CanonicalSemanticExperiment? experiment = null,
        IReadOnlySet<string>? selectedPackIds = null) : ICanonicalSemanticTextModel
    {
        private readonly CanonicalSemanticExperiment _experiment = experiment ?? CanonicalSemanticExperiment.Baseline;
        // No default: the lane that builds the model names the partition its requests are sent under.
        private readonly ISemanticEvidencePackingPolicy _packingPolicy =
            packingPolicy ?? throw new ArgumentNullException(nameof(packingPolicy));
        private readonly IReadOnlySet<string>? _selectedPackIds = selectedPackIds;

        /// <summary>The lane's coordinate contract, which builds the schema and checks the reply.</summary>
        public SemanticCoordinateContract Contract { get; } = contract;

        public List<string> RawResponses { get; } = [];

        /// <summary>
        /// Request-shaped view of one owned occurrence. LocalBefore/LocalAfter are deliberately
        /// dropped: within a segment the neighbouring evidence entries already are that context,
        /// and repeating them was 45% of the payload. Per-run formatting spans are dropped too;
        /// style and numbering facts carry the same signal far more compactly.
        /// </summary>
        /// <summary>
        /// The layout label beside an owned item, absent for every lane whose atoms already are
        /// its layout unit - DOCX falls here, which is what keeps its request
        /// bytes exactly what they have always been. Only a lane whose
        /// <see cref="CanonicalSemanticTextInferenceInput.LayoutBlockBySourceId"/> is populated
        /// adds the field at all.
        /// <para>
        /// V2 shows observable source facts only (HARNESS MAY REPORT OBSERVABLE SOURCE FACTS; IT MUST
        /// NOT PRE-INTERPRET THEIR MEANING). The scope label and the contents flag are the harness's
        /// own reading - "running page artifact", "table of contents" - of the question the model is
        /// asked, so they are replaced by <see cref="CanonicalSemanticSourceEvidence.LocationFacts"/>:
        /// page, page band, recurrence, hyperlink anchors. The field is omitted, not sent empty, when a
        /// lane has nothing to report.
        /// </para>
        /// </summary>
        private static object OwnedEvidence(
            CanonicalSemanticSourceEvidence item, IReadOnlyDictionary<string, string>? layoutBlockBySourceId)
        {
            var block = layoutBlockBySourceId?.GetValueOrDefault(item.SourceId);
            var location = item.LocationFacts;
            if (block is not null)
                return location is null
                    ? new
                    {
                        alias = item.SourceAlias,
                        block,
                        text = item.ExactSourceText,
                        owned = true,
                        style = item.StyleFacts,
                        numbering = item.NumberingFacts,
                    }
                    : new
                    {
                        alias = item.SourceAlias,
                        block,
                        text = item.ExactSourceText,
                        owned = true,
                        location,
                        style = item.StyleFacts,
                        numbering = item.NumberingFacts,
                    };
            return location is null
                ? new
                {
                    alias = item.SourceAlias,
                    text = item.ExactSourceText,
                    owned = true,
                    style = item.StyleFacts,
                    numbering = item.NumberingFacts,
                }
                : new
                {
                    alias = item.SourceAlias,
                    text = item.ExactSourceText,
                    owned = true,
                    location,
                    style = item.StyleFacts,
                    numbering = item.NumberingFacts,
                };
        }

        private static object MarginEvidence(
            CanonicalSemanticSourceEvidence item, IReadOnlyDictionary<string, string>? layoutBlockBySourceId)
        {
            var block = layoutBlockBySourceId?.GetValueOrDefault(item.SourceId);
            return block is null
                ? new { alias = item.SourceAlias, text = item.ExactSourceText, owned = false }
                : new { alias = item.SourceAlias, block, text = item.ExactSourceText, owned = false };
        }

        /// <summary>Neighbouring occurrences a segment may read but never claim.</summary>
        internal const int VisibleMargin = SemanticEvidencePackingPolicies.VisibleMargin;

        /// <summary>
        /// One request's worth of owned evidence and the exact bytes composed for it. Built by
        /// <see cref="ComposeSegments"/>, the one place that decides what a segment owns and what
        /// it is shown - so a dry-run caller and <see cref="InferAsync"/> partition and compose
        /// identically, not just coincidentally the same today.
        /// </summary>
        internal sealed record ComposedSegment(
            IReadOnlyList<CanonicalSemanticSourceEvidence> Owned, string RequestBytes)
        {
            public string PackId { get; init; } = string.Empty;
        }

        /// <summary>
        /// Every request this input would produce, composed through
        /// <see cref="CanonicalSemanticRequestComposer"/> - the same composition
        /// <see cref="InferAsync"/> sends to a transport, available here without one. A dry-run
        /// caller uses this directly; nothing re-derives request bytes from fields on the side.
        /// </summary>
        public IReadOnlyList<ComposedSegment> ComposeRequests(CanonicalSemanticTextInferenceInput input) =>
            ComposeSegments(input).ToArray();

        private IEnumerable<ComposedSegment> ComposeSegments(CanonicalSemanticTextInferenceInput input)
        {
            var evidence = input.SourceEvidence ?? [];
            var packs = _packingPolicy.BuildPacks(evidence, input.LayoutBlockBySourceId);
            if (_selectedPackIds is not null)
            {
                var knownPackIds = packs.Select(pack => pack.PackId).ToHashSet(StringComparer.Ordinal);
                if (_selectedPackIds.Count == 0 || _selectedPackIds.Any(id => !knownPackIds.Contains(id)))
                    throw new InvalidOperationException("SEMANTIC_PACK_SELECTION_INVALID");
                packs = packs.Where(pack => _selectedPackIds.Contains(pack.PackId)).ToArray();
            }

            foreach (var pack in packs)
            {
                var owned = pack.Owned;
                var visible = pack.Visible;
                var ownedAliases = owned.Select(item => item.SourceAlias).ToHashSet(StringComparer.Ordinal);
                // Evidence is already in document order, so a neighbour IS the local context.
                // Owned entries carry the decision facts; margin entries carry text only.
                SemanticRequestVersions.Require(_experiment.RequestVersion);
                var sourceEvidence = visible.Select(item => ownedAliases.Contains(item.SourceAlias)
                    ? OwnedEvidence(item, input.LayoutBlockBySourceId)
                    : (object)MarginEvidence(item, input.LayoutBlockBySourceId))
                    .ToArray();

                var packet = new
                {
                    protocol = Contract.ProtocolVersion,
                    ownedSourceAliases = owned.Select(item => item.SourceAlias).ToArray(),
                    sourceEvidence,
                };

                yield return new ComposedSegment(
                    owned, CanonicalSemanticRequestComposer.Compose(packet, Contract))
                {
                    PackId = pack.PackId,
                };
            }
        }

        public async Task<CanonicalSemanticTextInferenceResult> InferAsync(
            CanonicalSemanticTextInferenceInput input,
            SemanticContextPacket packedContext,
            string requestId,
            CancellationToken cancellationToken = default)
        {
            var proposals = new List<CanonicalSemanticProposal>();
            var issues = new List<SemanticContractIssue>();
            foreach (var segment in ComposeSegments(input))
            {
                var owned = segment.Owned;
                var ownedAliases = owned.Select(item => item.SourceAlias).ToHashSet(StringComparer.Ordinal);
                var systemPrompt = SystemPromptFor(Contract, _experiment);
                var raw = await transport.BoundaryCutAsync(
                    systemPrompt,
                    segment.RequestBytes,
                    cancellationToken,
                    expectedItemCount: owned.Count);
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
                // The same descriptor that produced the schema checks the reply. Validating against
                // a different one would accept coordinates the model was never offered.
                var segmentIssues = Contract.Validate(document.RootElement);
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
                    // Decoded by the contract that issued the schema this reply answers. Reading it
                    // with any other contract's decoder returns nothing and reports nothing, which
                    // is how a structured run once scored zero against replies that were correct.
                    var decoded = Contract.Decode(element);
                    foreach (var failure in decoded.Failures)
                    {
                        issues.Add(new SemanticContractIssue(failure.Code, null, failure.Detail));
                    }
                    foreach (var proposal in decoded.Proposals)
                    {
                        if (ownedAliases.Contains(proposal.SourceAlias)) proposals.Add(proposal);
                    }
                }
            }
            return new(proposals, new CanonicalSemanticInferenceTelemetry(transport.ModelName))
            {
                ContractIssues = issues,
            };
        }

    }
}
