namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>V7's general structural rule with required source-layout evidence before classification.</summary>
internal static class PdfMembershipInstructionV8
{
    const string EvidenceRule = """
        Before deciding any target, call get_page_layout for its page and continue with the returned nextOffset when needed until a successful
        result has shown that target alias. Read the returned text and layout together with the surrounding lines before classifying it. Every
        target alias must appear in a successful get_page_layout result before you return a final answer; citing only facts already in the request
        does not satisfy this requirement.
        """;

    public const string Instruction = PdfMembershipInstructionV7.Instruction + "\n" + EvidenceRule;
}
