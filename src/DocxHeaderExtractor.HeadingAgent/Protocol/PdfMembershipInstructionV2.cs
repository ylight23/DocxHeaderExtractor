namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>Opt-in semantic clarification; selection schema and source authority are unchanged.</summary>
internal static class PdfMembershipInstructionV2
{
    public const string Instruction = PdfAgentProtocol.Instruction + "\n" + Clarification;
    internal const string Clarification = """
        Apply names, introduces, OR divides as alternatives: a title can name a unit without dividing
        paragraphs. Evaluate the source occurrence at its own location, including the start of an
        embedded document, attachment or appendix. A title naming that local unit can be a genuine
        heading even when it is outside the main outline. Do not exclude it merely because it appears
        on a cover, inside an appendix, or has no parent or children in the section tree. Heading
        membership is separate from the later parent-placement decision.
        Distinguish the name of that unit from descriptive information accompanying it. Meeting format,
        dates, venues, addresses, authorship and organizational labels normally describe the event or
        document; being bold, centered, standalone, or adjacent to a title does not make them heading
        lines. Include such text only when its actual local function is to name, introduce or divide a
        content unit, or when it is genuinely part of that unit's title. Do not select an entire cover
        block as one heading merely because its lines share formatting. For a multi-line title select
        the contributing title lines; examine adjacent metadata separately.
        Apply this same distinction at the beginning of the document and at later embedded-document
        boundaries. A local heading that introduces content is different from a reference or schedule
        entry that only points to a heading elsewhere. Do not exclude a whole embedded document as
        metadata or a table of contents without judging the function of each occurrence there.
        Before NOT_SELECT, check all three functions, not only whether the line divides text. If the
        source evidence does not settle its role, use the available evidence tools or UNRESOLVED.
        """;
}
