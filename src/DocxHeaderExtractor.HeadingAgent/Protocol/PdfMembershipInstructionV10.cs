namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>
/// General structural policy for PDF qualification.  It deliberately describes source relationships,
/// not document genres, labels, or examples observed in a qualification document.
/// </summary>
internal static class PdfMembershipInstructionV10
{
    public const string Instruction = """
        Extract every source heading at every structural level. A heading is the minimal contiguous
        source text that names a document or a block of its content at the occurrence's actual location.
        Decide from source relationships, not from wording, length, numbering, typography, position, or
        resemblance to another occurrence alone.

        A SELECT needs both proofs. First, the scope proof: identify the block the occurrence governs
        from its following content, the next peer, or its parent. Second, the identity proof: establish
        that this occurrence supplies the name of that block, rather than merely describing, qualifying,
        or belonging to a block whose name has already been supplied. A selected part of a multi-line
        name must make its own contribution to identifying that same named block. Apply the minimal-name
        test: if removing a part leaves the remaining selected text still naming the same block, do not
        select that part solely because it is adjacent or formatted alike.

        A subordinate heading partitions its parent's content into its own direct block. Treat comparable
        occurrences independently: an occurrence is not a heading merely because it repeats, looks like,
        or is near another heading. Conversely, a short block, a nested block, or a block containing
        entries can still have a heading when the two proofs hold. Text that is itself an entry, or that
        only contributes information inside an already governed block, is not a heading.

        Preserve aliases and select only the exact text belonging to the minimal name. For every SELECT,
        explain the named block and cite source aliases that show its scope relationship; for a multi-line
        name, explain the selected line's identity contribution. If either proof cannot be established
        from the source evidence, request focused evidence or leave the occurrence unresolved rather than
        guessing.
        """ + "\n" + PdfAgentProtocol.ExtentLine;
}
