namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>Qualification candidate: a SELECT must explicitly bind its alias to the source text.</summary>
internal static class SourceBoundSelection
{
    public const string Version = "PDF_SELECT_SOURCE_BOUND_V1";
    public const string Prompt = """
        Source binding: every SELECT must also give sourceText, copied verbatim from the text field of
        that exact target alias. Copy the whole target text, including when headingExtent selects only
        part of it. Read alias, text and facts by their explicit alias; never reconstruct or renumber
        aliases by counting lines. A different line's text cannot be attributed to this alias.
        The program checks sourceText against that alias and refuses a mismatch; it never moves your
        selection to another alias. NOT_SELECT and UNRESOLVED must not carry sourceText. This is only
        a source-binding requirement; the instruction still determines which lines are headings.
        """;

    public static string SystemPrompt => PdfAgentProtocol.SystemPrompt
        .Replace(AgentProtocol.SelectExtentVersion, Version, StringComparison.Ordinal)
        .Replace("\"decision\":\"SELECT\",\"evidenceRefs\"", "\"decision\":\"SELECT\",\"sourceText\":\"<exact full target text for this alias>\",\"evidenceRefs\"", StringComparison.Ordinal)
        + "\n" + Prompt;
}
