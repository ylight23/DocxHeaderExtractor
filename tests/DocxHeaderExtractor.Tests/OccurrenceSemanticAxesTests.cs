namespace DocxHeaderExtractor.Tests;

/// <summary>
/// OCCURRENCE_SEMANTIC_AXES, the user's ontology: a source occurrence is described on independent axes -
/// what it does semantically, what object it is about, how it behaves in the layout, how it relates
/// to a title and whether it repeats - and only then is heading membership decided. It replaces asking
/// which one of DOCUMENT_LABEL / STRUCTURAL_UNIT / NON_STRUCTURAL an occurrence is; that closed label
/// survives only as a lossy legacy projection at the end of the pipeline.
/// <para>
/// V2 (2026-09-24) corrects V1, which listed REPEATED_TITLE and SUBTITLE as occurrence roles beside
/// REGION_OPENER. They are not the same kind of fact: a repeated "Particular Conditions" above
/// "Part C" still opens a region, and a two-line form title is one claim of two parts. So layout
/// behaviour is now multi-valued (occurrenceRoles), and title relation and repeat status are axes of
/// their own. V1's artifact stays as history.
/// </para>
/// <para>
/// It does not change the model contract: moving the LLM's semantic output to these axes is a
/// production change and needs its own approval.
/// </para>
/// </summary>
public sealed class OccurrenceSemanticAxesTests
{
    public const string OntologyId = "OCCURRENCE_SEMANTIC_AXES_V2";

    public static readonly string[] Functions = ["IDENTITY", "STRUCTURE", "INFORMATION"];

    public static readonly string[] Roles =
        ["REGION_OPENER", "LOCAL_LABEL", "CAPTION", "FIELD_LABEL", "METADATA", "NAVIGATION", "BODY_CONTENT", "PAGE_FURNITURE"];

    public static readonly string[] TitleRelations = ["NONE", "TITLE", "TITLE_PART"];

    public static readonly string[] RepeatStatuses = ["FIRST", "REPEATED"];

    [Fact]
    public void Freeze_the_ontology()
    {
        FreezeArtifact.AssertJson("eval/a99-closed-loop/policy", "occurrence-semantic-axes.v2.json", new
        {
            artifactKind = "a99_occurrence_ontology",
            ontologyId = OntologyId,
            supersedes = new
            {
                path = "eval/a99-closed-loop/policy/occurrence-semantic-axes.v1.json",
                defect = "REPEATED_TITLE and SUBTITLE were occurrence roles beside REGION_OPENER, so a repeated title that opens a region, or a title part, could not be described",
            },
            status = "FROZEN",
            approvedBy = "USER",
            approvedAt = "2026-09-24",
            axes = new object[]
            {
                new
                {
                    axis = "semanticFunctions",
                    values = (object)new
                    {
                        IDENTITY = "says what this is: identifies the semantic artifact or unit the reader is entering",
                        STRUCTURE = "says where the following content sits: organizes hierarchy inside an artifact already identified",
                        INFORMATION = "says something about the artifact or its context: date, place, version, author, status, figures",
                    },
                    cardinality = "one or more, with one primaryFunction (\"Agenda\" is IDENTITY of the embedded agenda and STRUCTURE of the meeting document)",
                },
                new
                {
                    axis = "scope",
                    values = (object)new[] { "DOCUMENT", "DOCUMENT_PART", "EMBEDDED_ARTIFACT", "SECTION", "CLAUSE", "FORM", "FORM_FIELD", "TABLE", "LIST", "TOC", "EVENT", "REVISION_ENTRY", "NOTE" },
                    cardinality = "the object the occurrence is about; open-ended",
                },
                new
                {
                    axis = "occurrenceRoles",
                    values = (object)Roles,
                    cardinality = "one or more: how the occurrence behaves in the layout (\"Examples of ...\" over its bullets is REGION_OPENER and LOCAL_LABEL)",
                },
                new
                {
                    axis = "titleRelation",
                    values = (object)TitleRelations,
                    cardinality = "TITLE names an artifact or unit; TITLE_PART is one line of a multi-line title and belongs to that title's claim as a source part, never a claim of its own",
                },
                new
                {
                    axis = "repeatStatus",
                    values = (object)RepeatStatuses,
                    cardinality = "REPEATED restates text already used as a title; it says nothing about membership - a repeat can still open a region",
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
                    cardinality = "decided last, by policy and the human reviewer - never implied by one axis alone",
                },
            },
            order = new[]
            {
                "What does it say? (semanticFunctions)",
                "What object is it about? (scope)",
                "How does it behave in the layout? (occurrenceRoles, titleRelation, repeatStatus)",
                "Only then: is this occurrence a heading? (isHeading)",
            },
            principles = new[]
            {
                "Semantic function is not heading status: IDENTITY of a TABLE in a CAPTION role is not a heading.",
                "METADATA is not an absolute non-heading: INFORMATION in a REGION_OPENER role can open a region (a revision date over its revision note).",
                "Repetition is not exclusion: a REPEATED title that opens a region is a heading occurrence.",
                "A title that wraps over lines is one claim: its lines are TITLE_PART source parts of that claim.",
                "The LLM proposes axes; the harness/policy layer decides or checks isHeading, canonical role, hierarchy and projection.",
            },
            examples = new object[]
            {
                new { text = "MINUTES OF ...", semanticFunctions = new[] { "IDENTITY" }, scope = "DOCUMENT", occurrenceRoles = new[] { "REGION_OPENER" }, titleRelation = "TITLE", repeatStatus = "FIRST", isHeading = true },
                new { text = "SESSION III", semanticFunctions = new[] { "STRUCTURE" }, scope = "SECTION", occurrenceRoles = new[] { "REGION_OPENER" }, titleRelation = "TITLE", repeatStatus = "FIRST", isHeading = true },
                new { text = "March 7–8, 2025", semanticFunctions = new[] { "INFORMATION" }, scope = "EVENT", occurrenceRoles = new[] { "METADATA" }, titleRelation = "NONE", repeatStatus = "FIRST", isHeading = false },
                new { text = "July 2023 (over its revision note)", semanticFunctions = new[] { "INFORMATION", "STRUCTURE" }, scope = "REVISION_ENTRY", occurrenceRoles = new[] { "REGION_OPENER" }, titleRelation = "NONE", repeatStatus = "FIRST", isHeading = true },
                new { text = "Agenda", semanticFunctions = new[] { "IDENTITY", "STRUCTURE" }, scope = "EMBEDDED_ARTIFACT", occurrenceRoles = new[] { "REGION_OPENER" }, titleRelation = "TITLE", repeatStatus = "FIRST", isHeading = true },
                new { text = "Date: (day month year)", semanticFunctions = new[] { "INFORMATION" }, scope = "FORM_FIELD", occurrenceRoles = new[] { "FIELD_LABEL" }, titleRelation = "NONE", repeatStatus = "FIRST", isHeading = false },
                new { text = "Table A. Local Currency", semanticFunctions = new[] { "IDENTITY" }, scope = "TABLE", occurrenceRoles = new[] { "CAPTION" }, titleRelation = "TITLE", repeatStatus = "FIRST", isHeading = false },
                new { text = "Part C – Fraud and Corruption", semanticFunctions = new[] { "IDENTITY", "STRUCTURE" }, scope = "DOCUMENT_PART", occurrenceRoles = new[] { "REGION_OPENER" }, titleRelation = "TITLE", repeatStatus = "FIRST", isHeading = true },
                new { text = "Particular Conditions (again, above Part C)", semanticFunctions = new[] { "IDENTITY" }, scope = "DOCUMENT", occurrenceRoles = new[] { "REGION_OPENER" }, titleRelation = "TITLE", repeatStatus = "REPEATED", isHeading = true },
                new { text = "ES Demand Guarantee (under its form title)", semanticFunctions = new[] { "IDENTITY" }, scope = "FORM", occurrenceRoles = new[] { "REGION_OPENER" }, titleRelation = "TITLE_PART", repeatStatus = "FIRST", isHeading = true },
            },
            legacyProjection = new
            {
                mapping = new { IDENTITY = "DOCUMENT_LABEL", STRUCTURE = "STRUCTURAL_UNIT", INFORMATION = "NON_STRUCTURAL" },
                lossy = true,
                lostDistinctions = new[] { "INFORMATION + REGION_OPENER", "IDENTITY + STRUCTURE", "IDENTITY + CAPTION", "REPEATED + REGION_OPENER" },
            },
            modelContractChanged = false,
        });
    }
}
