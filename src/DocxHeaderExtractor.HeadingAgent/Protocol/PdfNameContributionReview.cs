namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>A fresh, bounded check of name contributions; no document-specific categories or expected answers.</summary>
internal static class PdfNameContributionReview
{
    internal const string Purpose = """
        The candidate aliases are an opening contiguous run routed for a check of names and their attributes. Routing is not a label.
        Read the source independently; no earlier model explanation is supplied. A visual block is a layout container, not an automatically
        selected heading. For each candidate occurrence, distinguish words naming a structural unit from properties of a unit already named.
        Keep the document's name, genuine contributing parts of a multi-line name, and any independent structural heading. A property remains
        NOT_SELECT even when it identifies a particular instance, completes the visual block, or matches the title's typography.
        Proximity, matching formatting, or 'part of the title block' alone cannot establish a contribution to the name. Cite source aliases
        supporting the name-versus-property relationship. Use UNRESOLVED if the source does not establish it.
        """;
    internal const string Instruction = PdfMembershipInstructionV9.Instruction + "\n" + Purpose;
}
