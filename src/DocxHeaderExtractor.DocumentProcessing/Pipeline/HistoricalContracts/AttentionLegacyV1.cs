using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline.HistoricalContracts;

/// <summary>
/// V1_ATTENTION_LEGACY - the semantic request as it was sent before REMOVE_MODEL_VISIBLE_ATTENTION_V1:
/// its two system prompts and its owned-evidence shape, which carried the harness's candidate heuristic
/// to the model as an "attention" flag. Kept byte for byte so that every experiment and replay frozen
/// under it still reproduces exactly what the provider received.
/// <para>
/// Not production. <see cref="SemanticRequestVersion.V2_ATTENTION_FREE"/> is the production default,
/// and this version is selected only through <see cref="Select"/>, by a caller replaying a historical
/// experiment. Nothing here may be edited: a change would silently move the authority of every artifact
/// frozen under it.
/// </para>
/// </summary>
internal static class AttentionLegacyV1
{
    /// <summary>The one way to select V1: an explicit historical experiment or replay profile.</summary>
    internal static CanonicalSemanticExperiment Select(CanonicalSemanticExperiment experiment)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        return experiment with { RequestVersion = SemanticRequestVersion.V1_ATTENTION_LEGACY };
    }

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

    /// <summary>The V1 discovery prompt, line endings settled.</summary>
    internal static string SystemPrompt { get; } = CanonicalSemanticEngine.NormalizePromptLineEndings(RawSystemPrompt);

    private const string RawStage1MembershipPrompt = """
        You are the semantic membership stage of the A99 canonical document pipeline.
        Decide semantic meaning only for the supplied parser-owned source aliases. Return strict
        JSON matching the supplied schema. sourceAlias and verbatimText are the only source
        references allowed. Do not return offsets, spans, pages, boxes, coordinates, or generated
        text. Formatting and numbering are evidence, never deterministic truth.

        sourceEvidence is in document order. Evaluate every alias in ownedSourceAliases and return
        one claim for each accepted structural label you find among them. Entries marked
        "owned": false are shown only so you can read the surrounding document; never return one of
        them as a claim. The "attention" flag is a hint, not the set of allowed claims: any owned
        occurrence may be one. Neighbouring entries are the local context; no context is repeated
        per item.

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

    /// <summary>The V1 Stage-1 membership prompt, line endings settled.</summary>
    internal static string Stage1MembershipPrompt { get; } =
        CanonicalSemanticEngine.NormalizePromptLineEndings(RawStage1MembershipPrompt);

    /// <summary>One owned occurrence as V1 serialized it, attention flag included.</summary>
    internal static object OwnedEvidence(
        CanonicalSemanticSourceEvidence item, IReadOnlyDictionary<string, string>? layoutBlockBySourceId)
    {
        var block = layoutBlockBySourceId?.GetValueOrDefault(item.SourceId);
        if (item.TableDepth is { } tableDepth)
            return new
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
            };
        if (block is not null)
            return new
            {
                alias = item.SourceAlias,
                block,
                text = item.ExactSourceText,
                owned = true,
                scope = item.StructuralScope,
                inTableOfContents = item.InTableOfContents,
                style = item.StyleFacts,
                numbering = item.NumberingFacts,
                markers = item.MarkerFacts,
                attention = item.CandidateAttention.HeuristicMatch,
            };
        return new
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
    }
}
