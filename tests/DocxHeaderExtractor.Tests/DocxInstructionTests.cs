using DocxHeaderExtractor.HeadingAgent;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The DOCX run's instruction says formatting is one piece of evidence, never the decision, and that a heading which shares its paragraph with
/// body text is only its own text. The PDF production instruction stays the structural one; the formatting variant is a candidate to compare.
/// </summary>
public sealed class DocxInstructionTests
{
    [Fact]
    public void The_docx_instruction_makes_formatting_one_piece_of_evidence_and_asks_for_the_heading_extent()
    {
        Assert.StartsWith(AgentProtocol.InstructionStructuralV1 + "\n" + AgentProtocol.FormattingEvidenceParagraph + "\n" + DocxAgentProtocol.HeadingExtentParagraph + "\n",
            DocxAgentProtocol.Instruction, StringComparison.Ordinal);
        Assert.Contains("Layout alone never makes a line a heading", DocxAgentProtocol.Instruction, StringComparison.Ordinal);
        Assert.Contains("is a measurement to weigh, not a decision", DocxAgentProtocol.Instruction, StringComparison.Ordinal);
        Assert.Contains("reproduces as a sample or a form", DocxAgentProtocol.Instruction, StringComparison.Ordinal);
        Assert.Contains("one kind of evidence", DocxAgentProtocol.Instruction, StringComparison.Ordinal);
        Assert.Contains("does not by itself make a line a heading", DocxAgentProtocol.Instruction, StringComparison.Ordinal);
        Assert.Contains("determine its exact extent", DocxAgentProtocol.Instruction, StringComparison.Ordinal);
        Assert.Contains("Do not treat the entire paragraph as heading text merely because it begins with a heading", DocxAgentProtocol.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void No_instruction_tells_the_model_to_select_a_whole_paragraph_that_only_opens_with_a_heading()
    {
        Assert.DoesNotContain("select it", AgentProtocol.FormattingEvidenceParagraph, StringComparison.Ordinal);
        Assert.DoesNotContain("select it", DocxAgentProtocol.Instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("select it", AgentProtocol.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pdf_production_instruction_is_the_structural_one_and_the_formatting_variant_is_kept_apart()
    {
        Assert.Equal(AgentProtocol.InstructionStructuralV1, AgentProtocol.Instruction);
        Assert.Equal(AgentProtocol.InstructionStructuralV1 + "\n" + AgentProtocol.FormattingEvidenceParagraph, AgentProtocol.InstructionFormattingEvidenceV2);
        Assert.NotEqual(AgentProtocol.Instruction, DocxAgentProtocol.Instruction);
    }

    [Fact]
    public void Only_the_docx_answer_format_carries_heading_extents()
    {
        Assert.Contains(AgentProtocol.SelectExtentVersion, DocxAgentProtocol.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("\"headingExtent\":{\"start\":0", DocxAgentProtocol.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("A SELECT may also carry headingExtent", DocxAgentProtocol.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("headingExtent", AgentProtocol.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains(AgentProtocol.SelectObjectsVersion + "\"", AgentProtocol.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_view_shows_the_grouping_prompt_of_both_routes()
    {
        Assert.Equal(AgentGrouping.GroupPrompt, PromptCatalog.Pdf.GroupPrompt);
        Assert.Equal(DocxAgentProtocol.GroupPrompt, PromptCatalog.Docx.GroupPrompt);
    }
}
