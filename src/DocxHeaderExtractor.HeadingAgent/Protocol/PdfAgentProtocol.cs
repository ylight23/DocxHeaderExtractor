namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>PDF selection with optional exact extents in the parser's source-line text.</summary>
internal static class PdfAgentProtocol
{
    internal const string ExtentLine = "A source line can contain a heading together with body text. On a SELECT, give headingExtent when only part of the line is heading text: start and end are UTF-16 offsets into the exact target text, end exclusive, and text is exactly that substring. Omit headingExtent only when the whole source line is heading text. The program checks the span against the source; it never moves an extent or replaces an invalid one with the whole line.";
    public static readonly string SystemPrompt = AgentProtocol.SystemPrompt.Replace(
        AgentProtocol.BodyWithAnalysis(AgentProtocol.SelectObjectsVersion), DocxAgentProtocol.ExtentBody, StringComparison.Ordinal) + "\n" + ExtentLine;
    public const string Instruction = AgentProtocol.Instruction + "\n" + ExtentLine;
}
