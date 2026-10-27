namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>Shared production structural policy plus parser block evidence; response contract unchanged.</summary>
internal static class PdfMembershipInstructionV13
{
    public const string Instruction = PdfMembershipInstructionV12.Instruction + "\n" + """
        Each targetFact also carries layoutBlockEvidence from the PDF parser. It gives the visual
        block containing that source occurrence, its member aliases, and the immediately preceding
        and following visual blocks in reading order. These are geometry measurements, not heading
        labels or a decision. Use them with the source text and surrounding context to establish
        structural function. Do not select a line merely because it starts, ends, or is alone in a
        visual block; preserve and decide every source alias independently.
        """;
}
