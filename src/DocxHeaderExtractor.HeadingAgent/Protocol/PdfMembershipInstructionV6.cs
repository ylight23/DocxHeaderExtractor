namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>Local participant-subgroup labels versus individual entries and their metadata.</summary>
internal static class PdfMembershipInstructionV6
{
    public const string Instruction = PdfMembershipInstructionV5.Instruction + "\n" + SubgroupRule;
    internal const string SubgroupRule = """
        In participant or membership lists, a standalone organization or category label introducing
        its own following group of entries can be a structural subheading. Evaluate every such peer
        label at its own location by the same function: selecting the first subgroup label does not
        make the later peer labels ordinary list entries. Individual people, list markers, and an
        affiliation supplied as part of one person's entry remain body/metadata. Distinguish a label
        naming the following subgroup from an institution merely mentioned within an entry. Compare
        the surrounding entries and measured style/block pattern, without letting style alone decide.
        """;
}
