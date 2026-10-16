using DocxHeaderExtractor.HeadingAgent.Scan;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>The prompts one route sends to the model, read-only. The web page shows them; nothing here is configurable.</summary>
/// <param name="Instruction">The user instruction the run sends.</param>
/// <param name="SystemPrompt">The system prompt of the route's agent.</param>
/// <param name="GroupPrompt">The grouping prompt the route sends after selection (both routes divide their selected lines into headings).</param>
public sealed record RoutePrompts(string Instruction, string SystemPrompt, string? GroupPrompt);

/// <summary>Public view of the prompts the PDF and DOCX routes send. The protocol classes stay internal.</summary>
public static class PromptCatalog
{
    public static RoutePrompts Pdf { get; } = new(AgentProtocol.Instruction, AgentProtocol.SystemPrompt, AgentGrouping.GroupPrompt);

    public static RoutePrompts Docx { get; } = new(DocxAgentProtocol.Instruction, DocxAgentProtocol.SystemPrompt, DocxAgentProtocol.GroupPrompt);

    public static RoutePrompts Scan { get; } = new(ScanAgentProtocol.Instruction, ScanAgentProtocol.SystemPrompt, ScanAgentFlavor.Instance.GroupPrompt);
}
