namespace DocxHeaderExtractor.Tests;

/// <summary>
/// OCCURRENCE_SEMANTIC_AXES_V1, the user's ontology of 2026-09-24: a source occurrence is described on
/// independent axes - what it does semantically, what object it is about, how it behaves in the
/// layout - and only then is heading membership decided. It replaces asking which one of
/// DOCUMENT_LABEL / STRUCTURAL_UNIT / NON_STRUCTURAL an occurrence is; that closed label survives
/// only as a lossy legacy projection at the end of the pipeline.
/// <para>
/// Frozen here as the text audits cite. It does not change the model contract: moving the LLM's
/// semantic output to these axes is a production change and needs its own approval.
/// </para>
/// </summary>
public sealed class OccurrenceSemanticAxesTests
{
    public const string OntologyId = "OCCURRENCE_SEMANTIC_AXES_V1";

    public static readonly string[] Functions = ["IDENTITY", "STRUCTURE", "INFORMATION"];

    public static readonly string[] Roles =
    [
        "REGION_OPENER", "LOCAL_LABEL", "CAPTION", "FIELD_LABEL", "METADATA", "NAVIGATION", "BODY_CONTENT",
        "PAGE_FURNITURE", "SUBTITLE", "REPEATED_TITLE",
    ];

    [Fact]
    public void Freeze_the_ontology()
    {
        FreezeArtifact.AssertJson("eval/a99-closed-loop/policy", "occurrence-semantic-axes.v1.json", new
        {
            artifactKind = "a99_occurrence_ontology",
            ontologyId = OntologyId,
            status = "FROZEN",
            approvedBy = "USER",
            approvedAt = "2026-09-24",
            axes = new object[]
            {
                new
                {
                    axis = "semanticFunctions",
                    values = new
                    {
                        IDENTITY = "says what this is: identifies the semantic artifact or unit the reader is entering",
                        STRUCTURE = "says where the following content sits: organizes hierarchy inside an artifact already identified",
                        INFORMATION = "says something about the artifact or its context: date, place, version, author, status, figures",
                    },
                    cardinality = "one or more, with one primaryFunction; an occurrence can carry several (\"Agenda\" is IDENTITY of the embedded agenda and STRUCTURE of the meeting document)",
                },
                new
                {
                    axis = "scope",
                    values = (object)new[] { "DOCUMENT", "DOCUMENT_PART", "EMBEDDED_ARTIFACT", "SECTION", "CLAUSE", "FORM", "FORM_FIELD", "TABLE", "LIST", "TOC", "EVENT", "REVISION_ENTRY", "NOTE" },
                    cardinality = "the object the occurrence is about; open-ended",
                },
                new
                {
                    axis = "occurrenceRole",
                    values = (object)Roles,
                    cardinality = "how it behaves in the layout; SUBTITLE qualifies an adjacent opener, REPEATED_TITLE restates an identity already opened",
                },
                new
                {
                    axis = "informationType",
                    values = (object)new[] { "TEMPORAL_METADATA", "LOCATION", "VERSION", "AUTHOR", "STATUS", "QUANTITY" },
                    cardinality = "only when INFORMATION is among the functions",
                },
                new
                {
                    axis = "isHeading",
                    values = (object)new[] { "true", "false" },
                    cardinality = "decided last, by policy and the human reviewer - never implied by a function alone",
                },
            },
            order = new[]
            {
                "What does it say? (semanticFunctions)",
                "What object is it about? (scope)",
                "How does it behave in the layout? (occurrenceRole)",
                "Only then: is this occurrence a heading? (isHeading)",
            },
            principles = new[]
            {
                "Semantic function is not heading status: IDENTITY of a TABLE in a CAPTION role is not a heading.",
                "METADATA is not an absolute non-heading: INFORMATION in a REGION_OPENER role can open a region (a revision date over its revision note).",
                "The LLM proposes axes; the harness/policy layer decides or checks isHeading, canonical role, hierarchy and projection.",
            },
            examples = new object[]
            {
                new { text = "MINUTES OF ...", semanticFunctions = new[] { "IDENTITY" }, scope = "DOCUMENT", occurrenceRole = "REGION_OPENER", isHeading = true },
                new { text = "SESSION III", semanticFunctions = new[] { "STRUCTURE" }, scope = "SECTION", occurrenceRole = "REGION_OPENER", isHeading = true },
                new { text = "March 7–8, 2025", semanticFunctions = new[] { "INFORMATION" }, scope = "EVENT", occurrenceRole = "METADATA", isHeading = false },
                new { text = "Agenda", semanticFunctions = new[] { "IDENTITY", "STRUCTURE" }, scope = "EMBEDDED_ARTIFACT", occurrenceRole = "REGION_OPENER", isHeading = true },
                new { text = "Date: (day month year)", semanticFunctions = new[] { "INFORMATION" }, scope = "FORM_FIELD", occurrenceRole = "FIELD_LABEL", isHeading = false },
                new { text = "Table A. Local Currency", semanticFunctions = new[] { "IDENTITY" }, scope = "TABLE", occurrenceRole = "CAPTION", isHeading = false },
                new { text = "Part C – Fraud and Corruption", semanticFunctions = new[] { "IDENTITY", "STRUCTURE" }, scope = "DOCUMENT_PART", occurrenceRole = "REGION_OPENER", isHeading = true },
            },
            legacyProjection = new
            {
                mapping = new { IDENTITY = "DOCUMENT_LABEL", STRUCTURE = "STRUCTURAL_UNIT", INFORMATION = "NON_STRUCTURAL" },
                lossy = true,
                lostDistinctions = new[] { "INFORMATION + REGION_OPENER", "IDENTITY + STRUCTURE", "IDENTITY + CAPTION" },
            },
            modelContractChanged = false,
        });
    }
}
