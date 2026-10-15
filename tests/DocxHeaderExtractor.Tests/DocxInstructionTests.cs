using DocxHeaderExtractor.HeadingAgent;

namespace DocxHeaderExtractor.Tests;

/// <summary>The DOCX run's instruction says formatting is one piece of evidence, never the decision; the PDF's instruction is untouched.</summary>
public sealed class DocxInstructionTests
{
    [Fact]
    public void The_docx_instruction_makes_formatting_one_piece_of_evidence_among_several()
    {
        Assert.StartsWith(AgentProtocol.InstructionStructuralV1, DocxAgentProtocol.Instruction, StringComparison.Ordinal);
        Assert.Contains("one kind of evidence", DocxAgentProtocol.Instruction, StringComparison.Ordinal);
        Assert.Contains("does not by itself make a line a heading", DocxAgentProtocol.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pdf_production_instruction_is_the_structural_one_plus_the_formatting_evidence_paragraph()
    {
        Assert.Equal(AgentProtocol.InstructionStructuralV1 + "\n" + AgentProtocol.FormattingEvidenceParagraph, AgentProtocol.Instruction);
        Assert.Equal(AgentProtocol.Instruction, AgentProtocol.InstructionFormattingEvidenceV1);
        Assert.Equal(AgentProtocol.Instruction, DocxAgentProtocol.Instruction);
    }
}
