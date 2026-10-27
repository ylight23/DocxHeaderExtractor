namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>General structural scope criterion; source evidence is acquired before the first classification.</summary>
internal static class PdfMembershipInstructionV9
{
    public const string Instruction = """
        Extract the source headings of this document at every structural level. A heading names the whole document or a subordinate
        block of this document's content that is organized under that name. Establish the block's actual scope from the surrounding
        source: which following content belongs under the name, where a peer unit begins, and how the occurrence relates to its parent.
        A content block may contain short entries or nested units; it does not require prose, numbering, bold type, or a minimum size.

        Merely naming a subject, object, attribute, or individual entry inside an existing block does not establish another structural
        block. Determine whether an occurrence governs subsequent source content or is itself one item of the enclosing content.
        Apply this distinction consistently across comparable occurrences. Appearance supports that relationship but cannot decide it alone.

        Include the title that names the document as a whole. A heading may span multiple source occurrences: select each contributing
        part of its name. Descriptive attributes of an already named unit do not become headings or parts of its name merely through
        proximity or matching formatting. Preserve source aliases and select only the exact heading text.

        The sourceEvidence packet contains successful source-tool results acquired before classification. Read its surrounding lines
        together with targetFacts. For each SELECT, explain the structural scope it names and cite source aliases supporting that
        relationship, including the following content, a peer, or a parent where available. A tool evidenceId is not a source alias.
        If scope remains unestablished, request focused additional source evidence or return UNRESOLVED rather than inventing a scope.
        """ + "\n" + PdfAgentProtocol.ExtentLine;
}
