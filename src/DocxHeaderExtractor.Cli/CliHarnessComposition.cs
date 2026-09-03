using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Application.Semantics;
using DocxHeaderExtractor.Infrastructure.Sources;

namespace DocxHeaderExtractor.Cli;

/// <summary>
/// Composition boundary for local CLI workflows. The CLI may accept an explicit file path, but
/// the harness still receives an opaque resource and resolves it through the same allowlisted
/// source adapter used by Web and MCP.
/// </summary>
internal static class CliHarnessComposition
{
    public static DocumentAgentHarness Create(
        IEnumerable<string> inputPaths,
        IDocumentExtractionTool extractionTool,
        IDocumentActionTool? actionTool = null)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentNullException.ThrowIfNull(extractionTool);

        var roots = inputPaths
            .Select(Path.GetFullPath)
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0)
            throw new ArgumentException("CLI cần ít nhất một input path hợp lệ.", nameof(inputPaths));

        var factory = new DocumentAgentHarnessFactory(
            new FileInputResourceResolver(roots),
            SemanticRegistryDefaults.Create());
        return factory.Create(extractionTool, actionTool: actionTool);
    }
}
