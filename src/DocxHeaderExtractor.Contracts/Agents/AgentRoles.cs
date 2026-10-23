namespace DocxHeaderExtractor.Contracts.Agents;

/// <summary>
/// The names of the agent slots a host can fill through <c>IModelProvider.Create</c>. A new agent takes a new name here; a host asks for
/// the slot by name and gets null when it has no agent for it, and the composition root then uses the production default.
/// </summary>
public static class AgentRoles
{
    public const string PdfSelector = "heading.pdf";
    public const string DocxSelector = "heading.docx";
    public const string ScanSelector = "heading.scan";
    public const string Placer = "heading.placer";
    /// <summary>Reads a scan into text lines for any problem (<see cref="IScanReader"/>).</summary>
    public const string ScanReader = "source.scan";
}
