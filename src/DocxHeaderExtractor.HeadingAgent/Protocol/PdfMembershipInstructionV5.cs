namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>Local titles/labels versus attributes of a unit already named.</summary>
internal static class PdfMembershipInstructionV5
{
    public const string Instruction = PdfMembershipInstructionV4.Instruction + "\n" + AttributeRule;
    internal const string AttributeRule = """
        Separate a unit's name from its attributes. An occurrence that only states when or where
        that already named unit's event takes place, its postal address, or its meeting format is
        NOT_SELECT unless it introduces its own distinct content section at that source location.
        Those attributes are not contributing title lines merely because they are next to the title
        or share its formatting. A day/session divider may include a date while introducing its own
        following content; that is a different local function from a date supplied as an event attribute.
        For every SELECT, state in reason which content unit the occurrence names, introduces or divides.
        """;
}
