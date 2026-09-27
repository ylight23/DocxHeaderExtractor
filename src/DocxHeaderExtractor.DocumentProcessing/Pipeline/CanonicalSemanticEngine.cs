using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The semantic stage, shared by every source format.
/// <para>
/// Nothing here knows whether the occurrences came from OOXML paragraphs or PDF text blocks. That
/// is the point: the prompt, the segmentation, the contract, the binder, the hierarchy resolver and
/// the placement pass must be identical across formats, or the two lanes will quietly disagree
/// about level derivation and parent wiring and the difference will surface only as an
/// unexplainable metric gap between a DOCX and its own PDF.
/// </para>
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

    internal static IReadOnlyList<string> MarkerFactsOf(PdfSourceFacts source)
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

    private const string RawPlacementPrompt = """
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

    internal static PdfSemanticRole ParseSemanticRole(string? role) =>
        Enum.TryParse<PdfSemanticRole>(role, ignoreCase: true, out var parsed)
            ? parsed
            : PdfSemanticRole.SectionHeading;

    /// <summary>
    /// I8. Says that a heading may be part of an occurrence, and fences that permission tightly.
    /// <para>
    /// Deliberately narrow. The failure this addresses is a heading glued to the prose that follows
    /// it in one source occurrence, where the model currently proposes nothing because the whole
    /// occurrence is not a heading. What it must not become is permission to edit text, or to
    /// assemble a heading from two separated pieces of one occurrence - that is a different
    /// contract, and composite headings across several occurrences already have one.
    /// </para>
    /// </summary>
    private const string RawPartialSpanClause = """

        A heading may occupy either the whole source occurrence, or ONE exact contiguous substring
        of a single owned source occurrence. Use the second form when a heading is followed, in the
        same occurrence, by text that is not part of it.
          - return the same sourceAlias
          - return verbatimText as that exact contiguous substring, copied character for character
          - do not normalize, rewrite, repair, shorten or paraphrase it
          - do not return offsets or coordinates: the harness locates the substring itself
        Two separated pieces of one occurrence are NOT a partial span. sourceAliases remains for a
        heading that genuinely runs across several occurrences, each part copied from its own.

        If that substring appears more than once inside the occurrence, say which one you mean:
        add "occurrence" as its 1-based ordinal, or "leftExactContext"/"rightExactContext" copied
        exactly from the characters beside it. A short heading often repeats inside a longer word -
        "Africa" occurs twice in "Africa Gregoire ... African Development Bank" - and an unmarked
        duplicate is rejected rather than guessed at.
        """;


    /// <summary>
    /// EXP_MASTHEAD_METADATA. Says that prominence is not structure, and fences the tree-less
    /// relation so it cannot absorb what prominence alone suggests.
    /// <para>
    /// Deliberately additive. It removes no category the policy already admits - a subtitle, a
    /// running header, a table or figure label may still be reported where the policy admits them,
    /// because no approved claim in the corpus adjudicates those either way and absence of evidence
    /// is not evidence of absence. What it adds is the test those categories were missing: a claim
    /// reported outside the section tree must still be a label the policy accepts, not merely text
    /// that looks like one.
    /// </para>
    /// </summary>
    private const string RawNonStructuralMetadataClause = """

        Prominence is not structure. A block is not a heading merely because it is set apart and
        names an organisation, an event, a meeting or session format, a date, a venue, an address,
        or similar descriptive detail. That identifies the document or the occasion it records; it
        does not divide the document.

        Report such a block only if it is the document's own accepted title, or if it names a
        structural unit whose content follows beneath it. Where the same identity is stated again
        later - at the head of an annex, a schedule or an attachment - restating it opens no new
        unit and is not reported.

        parent-node:NONE is for the accepted labels named above, not a place to put prominent text
        that establishes no structural unit.
        """;

    /// <summary>
    /// EXP_MASTHEAD_METADATA_E2. Where E1 said what metadata is, this says in what order the two
    /// questions are answered.
    /// <para>
    /// E1 named the category and suppressed nothing: all three masthead families survived all three
    /// repeats. The reasoning it left available was that prominent identifying text might still be
    /// an accepted label, and that the tree-less relation was somewhere to put it. So this clause
    /// adds an invariant rather than more categories - eligibility is decided first, and the
    /// placement value cannot be the reason a span is admitted.
    /// </para>
    /// </summary>
    private const string RawMembershipBeforePlacementClause = """

        Heading eligibility comes before placement. First decide whether the source span itself is
        an accepted structural label. Report it only if it is either the document's own canonical
        title, or it opens and names a distinct structural unit at that location.

        A masthead or identification block that merely states or restates the organisation,
        programme, event, meeting format, date, venue, address, or similar facts about the document
        or occasion is descriptive metadata, not a new structural unit. This remains true when the
        block appears at the beginning of an annex, schedule, attachment, or later section: location
        and visual prominence do not by themselves make the repeated identity a heading.

        Apply parent-node, including NONE, only after the span has independently passed heading
        eligibility. parent-node:NONE describes the placement of an already accepted heading; it
        must never be used to admit text that otherwise establishes no structural unit.
        """;

    /// <summary>
    /// V3_ATTENTION_FREE_EXCLUSION_CONSISTENCY. One clause, worded by function rather than by typography or by a
    /// literal list, as authorized (user, 2026-09-27) after LLM_SEMANTIC_PILOT_V1: the model named page furniture,
    /// contents entries and index entries correctly in semanticRole and returned them as headings anyway, so 116 of
    /// SRC-095's 160 false claims were navigation the contract never told it to exclude.
    /// <para>
    /// "ordinary" carries the weight in both directions: an ordinary contents line or object caption is excluded, and
    /// the third paragraph keeps a genuine region opener - the index group letters of S095_Q2, a heading inside a
    /// contents region - a heading. Nothing here names a document, a page or a text.
    /// </para>
    /// </summary>
    private const string RawHeadingExclusionConsistencyClause = """

        An occurrence is not a heading when its primary function is ordinary navigation, page furniture, a
        footnote/source note, an ordinary object caption, or a table row/column header.

        Do not mark such occurrences as headings merely because they are standalone, bold, numbered, repeated,
        or visually prominent.

        This exclusion does not apply to a genuine region opener that organizes content beneath it, including a
        heading inside an index/contents region.
        """;

    private const string RawSemanticFunctionMembershipPrompt = """
        You are the semantic-function membership stage of the A99 canonical document pipeline.
        Return strict JSON matching the supplied schema. Classify occurrences, not strings. An
        identical string in a table of contents and in body text can have different functions.

        semanticFunction is the only membership authority. DOCUMENT_IDENTITY identifies the
        artifact's own semantic title identity. REGION_STRUCTURE names or opens a semantic region
        whose following content belongs beneath it. NAVIGATION points to or lists content elsewhere.
        PAGE_FURNITURE serves repeated page presentation. OBJECT_CAPTION describes an embedded
        object. TABLE_STRUCTURE is internal to a table. FOOTNOTE_OR_SOURCE supports other content.
        BODY_INFORMATION is ordinary content without a region-opening function. METADATA describes
        the artifact without being its title identity.

        Return sourceParts and exactly one closed semanticFunction for each occurrence you report.
        Do not return membership, isHeading, semanticRole, hierarchy, relation, scope, titleRelation,
        offsets, or any other field. The harness derives membership: only DOCUMENT_IDENTITY and
        REGION_STRUCTURE are members; every other function is not.
        """;

    /// <summary>
    /// The Stage-1 semantic core: membership, and no other task.
    /// <para>
    /// Derived from <see cref="RawSystemPrompt"/> by removing the instructions whose only purpose is
    /// placement, occurrence-grouping or a coordinate the harness now owns. It is a decomposition of
    /// the existing policy, not a refinement of it: no masthead rule, no E1 wording, no E2 wording,
    /// and nothing new about what counts as a claim.
    /// </para>
    /// <para>
    /// One piece of membership policy had to move rather than be dropped. The base prompt states
    /// which things are real headings that hold no position in the section tree - the document's own
    /// title and subtitle, running headers and footers, table and figure labels, form and signature
    /// labels - inside the paragraph that explains parent-node:NONE. That sentence is membership
    /// wearing a placement token. Deleting the paragraph wholesale would have quietly narrowed what
    /// the model accepts and made the experiment a policy change; so the membership content is kept
    /// and carried by the disposition instead, which is what the disposition is for.
    /// </para>
    /// </summary>
    private const string RawStage1MembershipPrompt = """
        You are the semantic membership stage of the A99 canonical document pipeline.
        Decide semantic meaning only for the supplied parser-owned source aliases. Return strict
        JSON matching the supplied schema. sourceAlias and verbatimText are the only source
        references allowed. Do not return offsets, spans, pages, boxes, coordinates, or generated
        text. Formatting and numbering are evidence, never deterministic truth.

        sourceEvidence is in document order. Evaluate every alias in ownedSourceAliases and return
        one claim for each accepted structural label you find among them. Entries marked
        "owned": false are shown only so you can read the surrounding document; never return one of
        them as a claim. Any owned occurrence may be one. Neighbouring entries are the local
        context; no context is repeated per item.

        REQUIRED for every claim: "membership", saying which kind of accepted label it is.
          "STRUCTURAL_UNIT" - it opens and names a unit of the document whose content follows it.
          "DOCUMENT_LABEL"  - it identifies the document or one of its parts without opening a unit:
                              the document's own title and subtitle, running headers and footers,
                              table and figure labels, form labels and signature labels. These are
                              real labels and you should still report them.
        Omit an occurrence entirely only when the evidence genuinely does not let you decide whether
        it is an accepted label at all - that is an open question for a human, not a third kind of
        membership.
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

    /// <summary>The placement prompt, line endings settled.</summary>
    internal static string PlacementPrompt { get; } = NormalizePromptLineEndings(RawPlacementPrompt);

    /// <summary>The I8 clause, line endings settled before it is appended.</summary>
    internal static string PartialSpanClause { get; } = NormalizePromptLineEndings(RawPartialSpanClause);

    /// <summary>The EXP_MASTHEAD_METADATA clause, line endings settled before it is appended.</summary>
    internal static string NonStructuralMetadataClause { get; } =
        NormalizePromptLineEndings(RawNonStructuralMetadataClause);

    /// <summary>The EXP_MASTHEAD_METADATA_E2 clause, line endings settled before it is appended.</summary>
    internal static string MembershipBeforePlacementClause { get; } =
        NormalizePromptLineEndings(RawMembershipBeforePlacementClause);

    /// <summary>The Stage-1 membership prompt, line endings settled.</summary>
    internal static string Stage1MembershipPrompt { get; } =
        NormalizePromptLineEndings(RawStage1MembershipPrompt);

    /// <summary>
    /// The Stage-1 request's system prompt: the membership core plus whatever the contract teaches
    /// about naming source. No experiment clause is accepted here - a membership arm that also
    /// carried a wording intervention would move two variables at once, which is the mistake this
    /// whole stage exists to stop making.
    /// </summary>
    internal static string MembershipPromptFor(SemanticCoordinateContract contract) =>
        MembershipPromptFor(contract, SemanticRequestVersions.ProductionDefault);

    internal static string MembershipPromptFor(SemanticCoordinateContract contract, SemanticRequestVersion version)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var core = SemanticRequestVersions.Require(version) == SemanticRequestVersion.V1_ATTENTION_LEGACY
            ? HistoricalContracts.AttentionLegacyV1.Stage1MembershipPrompt
            : Stage1MembershipPrompt;
        return contract.PromptClause is null ? core : core + contract.PromptClause;
    }

    /// <summary>The V3 clause, line endings settled before it is appended.</summary>
    internal static string HeadingExclusionConsistencyClause { get; } =
        NormalizePromptLineEndings(RawHeadingExclusionConsistencyClause);

    internal static string SemanticFunctionMembershipPrompt { get; } =
        NormalizePromptLineEndings(RawSemanticFunctionMembershipPrompt);

    /// <summary>The discovery prompt of a request version; an unknown version is refused.</summary>
    internal static string SystemPromptOf(SemanticRequestVersion version) =>
        SemanticRequestVersions.Require(version) switch
        {
            SemanticRequestVersion.V1_ATTENTION_LEGACY => HistoricalContracts.AttentionLegacyV1.SystemPrompt,
            // V3 is V2's prompt plus one clause: the evidence and every other instruction are the same bytes.
            SemanticRequestVersion.V3_ATTENTION_FREE_EXCLUSION_CONSISTENCY => SystemPrompt + HeadingExclusionConsistencyClause,
            SemanticRequestVersion.V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY => SemanticFunctionMembershipPrompt,
            _ => SystemPrompt,
        };

    /// <summary>The prompt this run sends. One clause per intervention, appended, never rewritten.</summary>
    internal static string SystemPromptFor(CanonicalSemanticExperiment experiment)
    {
        var core = SystemPromptOf(experiment.RequestVersion);
        var prompt = experiment.CommunicatePartialSpan ? core + PartialSpanClause : core;
        if (experiment.ConstrainNonStructuralMetadata) prompt += NonStructuralMetadataClause;
        return experiment.RequireMembershipBeforePlacement ? prompt + MembershipBeforePlacementClause : prompt;
    }

    /// <summary>
    /// The shared core, plus whatever coordinate shape the lane's contract teaches.
    /// <para>
    /// A contract with no <see cref="SemanticCoordinateContract.PromptClause"/> gets exactly the
    /// prompt it always got - DOCX and legacy PDF both fall here, which is what keeps their prompt
    /// hashes unmoved by this seam existing at all. A contract that introduces a new coordinate
    /// shape appends its own teaching text; the core paragraphs it is appended to are identical
    /// either way, which is what keeps this one semantic core rather than two.
    /// </para>
    /// </summary>
    internal static string SystemPromptFor(SemanticCoordinateContract contract, CanonicalSemanticExperiment experiment)
    {
        // Keep semantic interventions in the semantic section and append the coordinate contract
        // after it. This preserves the original E1 insertion point when the arm is paired with a
        // structured coordinate contract; putting E1 after coordinate serialization instructions
        // would make prompt order an unintended experimental variable.
        var core = SystemPromptOf(experiment.RequestVersion);
        var prompt = experiment.CommunicatePartialSpan ? core + PartialSpanClause : core;
        if (experiment.ConstrainNonStructuralMetadata)
            prompt += NonStructuralMetadataClause;
        if (experiment.RequireMembershipBeforePlacement)
            prompt += MembershipBeforePlacementClause;
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
    internal sealed class HeaderClassifierCanonicalTextModel(
        IHeaderClassifier classifier,
        SemanticCoordinateContract contract,
        CanonicalSemanticExperiment? experiment = null,
        ISemanticEvidencePackingPolicy? packingPolicy = null,
        IReadOnlySet<string>? selectedPackIds = null) : ICanonicalSemanticTextModel
    {
        private readonly CanonicalSemanticExperiment _experiment = experiment ?? CanonicalSemanticExperiment.Baseline;
        private readonly ISemanticEvidencePackingPolicy _packingPolicy =
            packingPolicy ?? SemanticEvidencePackingPolicies.Default;
        private readonly IReadOnlySet<string>? _selectedPackIds = selectedPackIds;

        /// <summary>The lane's coordinate contract, which builds the schema and checks the reply.</summary>
        public SemanticCoordinateContract Contract { get; } = contract;

        /// <summary>The request-partition policy used by this execution.</summary>
        public string PackingPolicyId => _packingPolicy.PolicyId;

        /// <summary>The immutable policy version used by this execution.</summary>
        public string PackingPolicyVersion => _packingPolicy.PolicyVersion;

        public List<string> RawResponses { get; } = [];
        public List<SemanticAuthorityTransportCall> TransportCalls { get; } = [];

        /// <summary>
        /// Request-shaped view of one owned occurrence. LocalBefore/LocalAfter are deliberately
        /// dropped: within a segment the neighbouring evidence entries already are that context,
        /// and repeating them was 45% of the payload. Per-run formatting spans are dropped too;
        /// style, numbering and marker facts carry the same signal far more compactly.
        /// </summary>
        /// <summary>
        /// Absent, not zero, for a format that has no such concept - the same rule I7 established
        /// for openStructuralContext. A PDF has no nested-table depth, and sending
        /// <c>tableDepth: 0</c> stated a measurement nothing had made; the model cannot tell an
        /// omitted field from a measured absence once it is on the wire as a number.
        /// <para>
        /// Two literal shapes rather than one built dynamically, so the DOCX request stays
        /// byte-identical to what it has always been and every frozen DOCX hash still holds.
        /// </para>
        /// </summary>
        /// <summary>
        /// The layout label beside an owned item, absent for every lane whose atoms already are
        /// its layout unit - DOCX and legacy PDF both fall here, which is what keeps their request
        /// bytes exactly what they have always been. Only a lane whose <see cref="CanonicalSemanticProductionInput.LayoutBlockBySourceId"/>
        /// is populated - today, the structured PDF profile - adds the field at all.
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
            if (item.TableDepth is { } tableDepth)
                return location is null
                    ? new
                    {
                        alias = item.SourceAlias,
                        text = item.ExactSourceText,
                        owned = true,
                        tableDepth,
                        style = item.StyleFacts,
                        numbering = item.NumberingFacts,
                        markers = item.MarkerFacts,
                    }
                    : new
                    {
                        alias = item.SourceAlias,
                        text = item.ExactSourceText,
                        owned = true,
                        tableDepth,
                        location,
                        style = item.StyleFacts,
                        numbering = item.NumberingFacts,
                        markers = item.MarkerFacts,
                    };
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
                        markers = item.MarkerFacts,
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
                        markers = item.MarkerFacts,
                    };
            return location is null
                ? new
                {
                    alias = item.SourceAlias,
                    text = item.ExactSourceText,
                    owned = true,
                    style = item.StyleFacts,
                    numbering = item.NumberingFacts,
                    markers = item.MarkerFacts,
                }
                : new
                {
                    alias = item.SourceAlias,
                    text = item.ExactSourceText,
                    owned = true,
                    location,
                    style = item.StyleFacts,
                    numbering = item.NumberingFacts,
                    markers = item.MarkerFacts,
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

        /// <summary>Owned occurrences evaluated per request. Keeps one document bounded.</summary>
        internal const int OwnedPerSegment = SemanticEvidencePackingPolicies.OwnedPerPack;

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
        /// <see cref="InferAsync"/> sends to a classifier, available here without one. A dry-run
        /// caller uses this directly; nothing re-derives request bytes from fields on the side.
        /// </summary>
        public IReadOnlyList<ComposedSegment> ComposeRequests(CanonicalSemanticProductionInput input) =>
            ComposeSegments(input).ToArray();

        private IEnumerable<ComposedSegment> ComposeSegments(CanonicalSemanticProductionInput input)
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
                var version = SemanticRequestVersions.Require(_experiment.RequestVersion);
                var sourceEvidence = visible.Select(item => ownedAliases.Contains(item.SourceAlias)
                    ? version == SemanticRequestVersion.V1_ATTENTION_LEGACY
                        ? HistoricalContracts.AttentionLegacyV1.OwnedEvidence(item, input.LayoutBlockBySourceId)
                        : OwnedEvidence(item, input.LayoutBlockBySourceId)
                    : (object)MarginEvidence(item, input.LayoutBlockBySourceId))
                    .ToArray();

                // I7 adds a field; the baseline must not carry an empty one. Serialising
                // openStructuralContext unconditionally made every baseline packet differ from the
                // packet the pre-I7 code sent, which quietly moved the thing every arm is measured
                // against. An arm that is off contributes nothing to the request at all.
                object packet = _experiment.CarryStructuralAncestors
                    ? new
                    {
                        protocol = Contract.ProtocolVersion,
                        ownedSourceAliases = owned.Select(item => item.SourceAlias).ToArray(),
                        // The structural state already open where this segment begins, so a segment
                        // continuing inside a container is not shown that container's contents
                        // without the container. Parser-owned marker evidence, not a harness claim
                        // about parents: it says what was open, never what anything's parent is.
                        openStructuralContext = owned[0].ActiveStructuralAncestors,
                        sourceEvidence,
                    }
                    : new
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
            CanonicalSemanticProductionInput input,
            SemanticContextPacket packedContext,
            string requestId,
            CancellationToken cancellationToken = default)
        {
            var proposals = new List<CanonicalSemanticProposal>();
            var parsedProposals = new List<CanonicalSemanticProposal>();
            var issues = new List<SemanticContractIssue>();
            foreach (var segment in ComposeSegments(input))
            {
                var owned = segment.Owned;
                var ownedAliases = owned.Select(item => item.SourceAlias).ToHashSet(StringComparer.Ordinal);
                var systemPrompt = SystemPromptFor(Contract, _experiment);
                var requestPayload = JsonSerializer.Serialize(new
                {
                    systemPrompt,
                    userMessage = segment.RequestBytes,
                });
                var raw = await classifier.BoundaryCutAsync(
                    systemPrompt,
                    segment.RequestBytes,
                    cancellationToken,
                    expectedItemCount: owned.Count);
                RawResponses.Add(raw);
                TransportCalls.Add(SemanticAuthorityTransportCall.Create(
                    RawResponses.Count,
                    "semantic",
                    segment.PackId,
                    requestPayload,
                    raw));
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
                        parsedProposals.Add(proposal);
                        if (ownedAliases.Contains(proposal.SourceAlias)) proposals.Add(proposal);
                    }
                }
            }
            return new(proposals, new CanonicalSemanticInferenceTelemetry(classifier.ModelName))
            {
                ContractIssues = issues,
                ParsedProposals = parsedProposals.ToArray(),
                RawModelResponseHash = SemanticAuthorityReplayHashing.RawModelResponseHash(RawResponses),
                TransportCalls = TransportCalls.ToArray(),
            };
        }

    }
}
