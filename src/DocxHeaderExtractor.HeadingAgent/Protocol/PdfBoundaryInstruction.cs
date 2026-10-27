namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>Existing V6 boundary instruction, retained for the previously qualified profile.</summary>
internal static class PdfBoundaryInstruction
{
    public const string Instruction = PdfAgentProtocol.Instruction + "\n" + BoundaryRule;
    internal const string BoundaryRule = """
        Membership is already fixed. Divide selected source texts into individual headings.
        A run means only that selected lines are adjacent; it is not a proposed heading.
        Join lines when the later text continues the same title or supplies a contributing part
        of that title. Keep independently named content units separate even when one immediately
        contains the next: a section and its subsection, an agenda and its day divider, or a day
        and its session each retain their own heading when they introduce different local units.
        Related subject matter, consecutive placement, or a containing unit does not establish
        title continuation. Use wording, labels, layout and the surrounding content together.
        A genuine title wrapped over several lines still forms one heading. Preserve every selected
        alias exactly once and decide boundaries only; do not add or remove heading membership.
        """;
}
