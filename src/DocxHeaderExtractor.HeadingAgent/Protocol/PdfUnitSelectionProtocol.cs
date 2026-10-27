namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>
/// Candidate V14 contract. Units are parser-derived geometry regions; chosen identities remain
/// original source aliases and must be quoted verbatim for source binding.
/// </summary>
internal static class PdfUnitSelectionProtocol
{
    public const string Version = "PDF_UNIT_SELECT_SOURCE_BOUND_V2";
    public const string SystemPrompt = """
        Select structural headings from parser-derived structural units. Each target unit has a unitId,
        geometryKind, measured geometry, and member source occurrences. The unit is a geometry grouping,
        never a semantic label. A paragraph-region follows continuous page flow; a table-region contains
        repeated visual columns. Neither kind means that its members are headings or non-headings.
        Decide each unit's structural function from its member text, surrounding source context and
        evidence. Select only member aliases that are heading text; aliases remain the only output
        identities and must be quoted verbatim for source binding.

        Do not infer heading status merely because text begins, ends, or is alone in a layout unit.
        Conversely, a heading can use one or more members of a unit. Determine structural function
        from what the occurrence names, introduces, or divides at its actual location. Do not use
        document genre, vocabulary, typography, numbering, or layout alone as a decision.

        Return one JSON object with exactly one decision for every target unit. SELECT has one or more selected members;
        NOT_SELECT has none and must include a concise reason, at most 300 characters, grounded in the supplied
        text, context, facts, or image. State the decisive evidence, not hidden chain-of-thought. Avoid generic
        restatements such as "not a heading". Do not add a rejection reason to SELECT decisions.
        Every selected member needs its exact sourceText and evidenceRefs made of aliases shown in the request
        or a tool result. Do not select a member outside its unit.

        Use this exact JSON shape (repeat the decision item for every target unit):
        {"protocolVersion":"PDF_UNIT_SELECT_SOURCE_BOUND_V2","status":"COMPLETE","decisions":[
          {"unitId":"U00001","decision":"SELECT","selected":[
            {"alias":"L0000:S0","sourceText":"verbatim source text","evidenceRefs":["L0000:S0"]}]},
          {"unitId":"U00002","decision":"NOT_SELECT","selected":[],"reason":"Continues the surrounding paragraph; it does not introduce a separate content unit."}
        ]}
        """;
}
