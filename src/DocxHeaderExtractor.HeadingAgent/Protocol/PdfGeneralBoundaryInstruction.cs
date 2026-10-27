namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>Production document-independent boundaries for already selected source text.</summary>
internal static class PdfGeneralBoundaryInstruction
{
    public const string Instruction = PdfMembershipInstructionV7.Definition + "\n" + BoundaryRule;
    internal const string BoundaryRule = """
        Membership is already fixed. Divide selected source texts into individual headings.
        A run means only that selected lines are adjacent; it is not a proposed heading.
        Join selected texts when source context establishes that they contribute to the same
        heading. Keep them separate when they introduce independent content units at their own
        locations, including when one unit is contained within another and their headings are adjacent.
        A complete phrase does not by itself establish an independent heading. Related subject
        matter, adjacency, containment or matching formatting alone does not establish continuation.
        Use the selected text, surrounding content and available source evidence together.
        A genuine heading spanning several source occurrences stays together. Preserve every selected
        alias exactly once and decide boundaries only; do not add or remove heading membership.
        """;
}
