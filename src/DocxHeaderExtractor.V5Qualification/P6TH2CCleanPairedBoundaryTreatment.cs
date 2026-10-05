namespace DocxHeaderExtractor.V5Qualification;

/// <summary>
/// Frozen semantic treatments for the clean paired H2-C qualification.
/// Everything outside <see cref="SemanticBoundaryInstruction"/> is deliberately shared.
/// </summary>
internal static class P6TH2CCleanPairedBoundaryTreatment
{
    public const string V1 = "V1";
    public const string V2 = "V2";

    public const string V1SemanticBoundaryInstruction = """
        Determine the exact source extent of the one heading that begins at the issued anchor occurrence. A heading may consist of one or more consecutive source occurrences. Return every and only consecutive occurrence that belongs literally to this exact heading. Do not include later body content merely because it belongs to the same section, topic, agenda item, or semantic region.
        """;

    public const string V2SemanticBoundaryInstruction = """
        Locate the exact boundary of the single heading occurrence that begins at the issued anchor occurrence. A heading may contain one or more consecutive source occurrences, but it ends immediately before the first occurrence that is not literally part of that same heading occurrence.

        Treat an occurrence as outside the heading when it begins a new heading, starts body or prose content, starts a table or other structured content, is page furniture, or otherwise is not literal heading text. Do not extend the heading merely because a later occurrence belongs to the same section, topic, agenda item, document region, or discusses the same subject.

        Choose the last literal heading occurrence and its immediate successor as one boundary pair: headingMembers must end at endOccurrence, and firstOutsideOccurrence must be the next issued occurrence immediately after it.
        """;

    public const string SharedContractInstruction = """
        For each request, copy the anchor value exactly from that request's input anchor field. Never substitute an identifier from instructions, prior requests, or another occurrence. Return exactly one decision for that anchor with these five properties: anchor, headingMembers, endOccurrence, firstOutsideOccurrence, and firstOutsideRole. headingMembers must begin with that exact anchor value and be one contiguous prefix of the ordered issued occurrences. endOccurrence must equal its final member. firstOutsideOccurrence must be the immediate successor after endOccurrence, never a skipped occurrence. If every issued occurrence belongs to the heading and there is no visible successor, use null for firstOutsideOccurrence and NO_VISIBLE_SUCCESSOR for firstOutsideRole.

        When firstOutsideOccurrence is present, firstOutsideRole must be exactly one of NEW_HEADING, BODY_CONTENT, PAGE_FURNITURE, TABLE_OR_STRUCTURED_CONTENT, OTHER_NON_HEADING. These are descriptive roles of the first occurrence outside the exact heading, not permission to extend the heading. Use source text and only the supplied neutral physical/style facts. Do not use hierarchy, candidate alternatives, relations, coordinates, aliases, rationale, confidence, or unissued evidence.

        Return one JSON object only with root property decisions and exactly one decision per input anchor. Each decision must have exactly the five required properties and no others. Copy only issued occurrence handles from the current request. Do not output source text or additional properties. This contract has no example identifiers; use the actual anchor and occurrence handles present in the current request.
        """;

    public static string SystemPrompt(string treatment) => treatment switch
    {
        V1 => V1SemanticBoundaryInstruction + "\n\n" + SharedContractInstruction,
        V2 => V2SemanticBoundaryInstruction + "\n\n" + SharedContractInstruction,
        _ => throw new ArgumentOutOfRangeException(nameof(treatment), treatment, "Unknown H2-C paired treatment."),
    };
}
