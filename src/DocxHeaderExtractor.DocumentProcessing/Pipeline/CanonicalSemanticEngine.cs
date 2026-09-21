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

    /// <summary>The prompt this run sends. One clause per intervention, appended, never rewritten.</summary>
    internal static string SystemPromptFor(CanonicalSemanticExperiment experiment) =>
        experiment.CommunicatePartialSpan ? SystemPrompt + PartialSpanClause : SystemPrompt;

    internal sealed class HeaderClassifierCanonicalTextModel(
        IHeaderClassifier classifier,
        CanonicalSemanticExperiment? experiment = null) : ICanonicalSemanticTextModel
    {
        private readonly CanonicalSemanticExperiment _experiment = experiment ?? CanonicalSemanticExperiment.Baseline;

        public List<string> RawResponses { get; } = [];

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
        private static object OwnedEvidence(CanonicalSemanticSourceEvidence item) =>
            item.TableDepth is { } tableDepth
                ? new
                {
                    alias = item.SourceAlias,
                    text = item.ExactSourceText,
                    owned = true,
                    scope = item.StructuralScope,
                    tableDepth,
                    inTableOfContents = item.InTableOfContents,
                    style = item.StyleFacts,
                    numbering = item.NumberingFacts,
                    markers = item.MarkerFacts,
                    attention = item.CandidateAttention.HeuristicMatch,
                }
                : new
                {
                    alias = item.SourceAlias,
                    text = item.ExactSourceText,
                    owned = true,
                    scope = item.StructuralScope,
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
                // Evidence is already in document order, so a neighbour IS the local context.
                // Owned entries carry the decision facts; margin entries carry text only.
                var sourceEvidence = visible.Select(item => ownedAliases.Contains(item.SourceAlias)
                    ? OwnedEvidence(item)
                    : (object)new { alias = item.SourceAlias, text = item.ExactSourceText, owned = false })
                    .ToArray();

                // I7 adds a field; the baseline must not carry an empty one. Serialising
                // openStructuralContext unconditionally made every baseline packet differ from the
                // packet the pre-I7 code sent, which quietly moved the thing every arm is measured
                // against. An arm that is off contributes nothing to the request at all.
                var packet = _experiment.CarryStructuralAncestors
                    ? JsonSerializer.Serialize(new
                    {
                        protocol = CanonicalSemanticContract.ProtocolVersion,
                        ownedSourceAliases = owned.Select(item => item.SourceAlias).ToArray(),
                        // The structural state already open where this segment begins, so a segment
                        // continuing inside a container is not shown that container's contents
                        // without the container. Parser-owned marker evidence, not a harness claim
                        // about parents: it says what was open, never what anything's parent is.
                        openStructuralContext = owned[0].ActiveStructuralAncestors,
                        sourceEvidence,
                    })
                    : JsonSerializer.Serialize(new
                    {
                        protocol = CanonicalSemanticContract.ProtocolVersion,
                        ownedSourceAliases = owned.Select(item => item.SourceAlias).ToArray(),
                        sourceEvidence,
                    });
                var raw = await classifier.BoundaryCutAsync(
                    SystemPromptFor(_experiment),
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
                    if (CanonicalSemanticProposalParser.TryParse(element) is not { } proposal)
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

    }
}
