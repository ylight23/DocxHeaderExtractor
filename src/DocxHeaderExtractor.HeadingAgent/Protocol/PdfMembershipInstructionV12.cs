namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>
/// Production PDF membership instruction. Its definition is the same shared structural
/// policy used by the DOCX route; PDF contributes only its existing exact-extent requirement.
/// It deliberately changes no response contract, tool, payload, source packing, or grouping rule.
/// </summary>
internal static class PdfMembershipInstructionV12
{
    public const string Instruction = AgentProtocol.InstructionStructuralV1 + "\n"
        + AgentProtocol.FormattingEvidenceParagraph + "\n"
        + PdfAgentProtocol.ExtentLine;
}
