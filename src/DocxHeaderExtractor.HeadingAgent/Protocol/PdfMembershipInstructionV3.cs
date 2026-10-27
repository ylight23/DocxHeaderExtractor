namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>Positive local-title criterion; organizational wording is not an exclusion category.</summary>
internal static class PdfMembershipInstructionV3
{
    public const string Instruction = PdfAgentProtocol.Instruction + "\n" + Clarification;
    internal const string Clarification = """
        Names, introduces, and divides are alternative functions. A title can name a content unit
        without dividing paragraphs. Apply the instruction at the actual source occurrence, including
        the start of a separate document embedded in an attachment or appendix, not only at the start
        of the main document. The name of a program, organization or group can constitute that local
        title when it identifies the unit beginning there; its institutional wording alone is not a
        reason to exclude it. Equally, an organizational attribution elsewhere need not be a heading.
        Decide the local role from the source and what follows, not from a category of words.
        A document title and an embedded-document title may be genuine headings even if later placed
        outside the parent-child outline. Membership and hierarchy are separate decisions.
        Judge each line next to a title separately: dates, venues, addresses and meeting format can
        describe the event without naming a content unit. Shared bold styling, alignment or proximity
        does not make them title lines. Select each genuine contributing line of a multi-line title,
        not the entire cover block. A section label introducing the content that follows is different
        from a reference or schedule entry that only points elsewhere.
        Do not reject a local title merely because it is on a cover or inside an appendix, and do not
        select adjacent descriptive metadata merely because it is on that same cover. If the evidence
        does not settle the local function, ask for evidence or return UNRESOLVED.
        """;
}
