namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>Short affirmative local-unit criteria; no blanket cover or institutional-name exclusion.</summary>
internal static class PdfMembershipInstructionV4
{
    public const string Instruction = PdfAgentProtocol.Instruction + "\n" + Clarification;
    internal const string Clarification = """
        Select the title of the main document and the titles of self-contained documents embedded
        later in it. Include every line contributing to each title. A title may consist of the name
        of a program, organization or group when that name identifies the content unit starting there.
        Select a standalone label at the opening of a table, schedule or other content unit when it
        names or introduces that unit. Judge the label separately from the entries below it.
        For each target, identify which content unit it names, introduces or divides at this location.
        A date, venue, address or meeting-format line may instead describe an attribute of an already
        named unit; classify that line separately. Use the source function and the content that follows.
        Heading membership includes local titles whether or not they later have outline parents.
        """;
}
