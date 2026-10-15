namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>The prompts one route sends to the model, read-only. The web page shows them; nothing here is configurable.</summary>
/// <param name="Instruction">The user instruction the run sends.</param>
/// <param name="SystemPrompt">The system prompt of the route's agent.</param>
/// <param name="GroupPrompt">The grouping prompt, when the route has one (DOCX); null otherwise.</param>
public sealed record RoutePrompts(string Instruction, string SystemPrompt, string? GroupPrompt);

/// <summary>Public view of the prompts the PDF and DOCX routes send. The protocol classes stay internal.</summary>
public static class PromptCatalog
{
    public static RoutePrompts Pdf { get; } = new(AgentProtocol.Instruction, AgentProtocol.SystemPrompt, null);

    public static RoutePrompts Docx { get; } = new(DocxAgentProtocol.Instruction, DocxAgentProtocol.SystemPrompt, DocxAgentProtocol.GroupPrompt);
}
