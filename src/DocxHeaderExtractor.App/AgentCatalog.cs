using System.Reflection;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Selectors;

namespace DocxHeaderExtractor.App;

/// <summary>What a host shows about the agents: their prompts (constants in code, shown read-only) and the heading agent's build.</summary>
public static class AgentCatalog
{
    /// <summary>The prompt and instruction each route sends to the model.</summary>
    public static object Prompts => new { pdf = PromptCatalog.Pdf, docx = PromptCatalog.Docx, scan = PromptCatalog.Scan };

    /// <summary>The informational version of the heading agent assembly that is loaded.</summary>
    public static string HeadingAgentVersion =>
        typeof(AgentSelector).Assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .Cast<AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
}
