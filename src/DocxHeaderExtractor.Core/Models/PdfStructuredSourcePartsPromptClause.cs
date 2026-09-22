namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// What the structured contract adds to the shared semantic-core prompt.
/// <para>
/// The core prompt (<c>CanonicalSemanticEngine.SystemPrompt</c>) decides meaning: what a heading is,
/// where it sits, whether it repeats. That does not change here. What changes is how a decided
/// heading is addressed - one or more exact selections over visual-segment atoms instead of one
/// alias - and this clause teaches only that.
/// </para>
/// </summary>
internal static class PdfStructuredSourcePartsPromptClause
{
    private const string Raw = """

        Source coordinates in this document are visual-segment aliases, each one line-or-part of a
        line the page actually has. Return sourceParts: an ordered list of exact selections, one or
        more per heading.
          - WHOLE_ALIAS   the entire atom is the heading (or part of it, together with other parts)
          - VERBATIM_TEXT only part of the atom belongs; copy that exact substring character for
                          character, with "occurrence" or left/rightExactContext if it repeats
                          inside the atom
        A heading that spans more than one atom - wrapped onto a following line, or continuing into
        the next segment of one row - returns one sourceParts entry per atom it occupies, in the
        order the source has them. Each entry is copied from its own atom; do not merge distant
        atoms into one entry and do not invent text no atom contains.

        Never return offsets, spans, pages, boxes or any numeric coordinate: the harness locates
        every selection itself. A layout label shown beside an atom (its containing block) is
        context for reading, never a source address - do not return it in place of an atom's own
        alias, and do not include a neighbouring atom merely because it shares that label.
        """;

    // Normalized to LF here, independently of CanonicalSemanticEngine's own normalizer: this type
    // lives in Core, which that engine depends on, not the other way around. Same rule either way -
    // a prompt is bytes on the wire and must not depend on how the source file was checked out.
    public static readonly string Text = Raw.ReplaceLineEndings("\n");
}
