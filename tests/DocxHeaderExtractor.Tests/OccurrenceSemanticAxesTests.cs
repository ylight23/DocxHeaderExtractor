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

    /// <summary>V3: V2 without repeatStatus. V2 stays the id of every artifact frozen under it.</summary>
    public const string OntologyV3Id = "OCCURRENCE_SEMANTIC_AXES_V3";

    public static readonly string[] OccurrenceRelations = ["PRIMARY", "REPEAT", "CONTINUATION"];

    /// <summary>
    /// OCCURRENCE_CLASSIFICATION_PRINCIPLES_V1 (user, 2026-09-25): how the ontology is applied to any
    /// document, of any genre and media type - and the test that the pipeline is general rather than
    /// tuned document by document. Frozen after DOC-0133 so the six remaining financial and procurement
    /// audits validate the ontology instead of growing a rule set of their own.
    /// </summary>
    [Fact]
    public void Freeze_the_classification_principles()
    {
        FreezeArtifact.AssertJson("eval/a99-closed-loop/policy", "occurrence-classification-principles.v1.json", new
        {
            artifactKind = "a99_occurrence_classification_principles",
            principlesId = "OCCURRENCE_CLASSIFICATION_PRINCIPLES_V1",
            ontology = OntologyId,
            approvedBy = "USER",
            approvedAt = "2026-09-25",
            firstPrinciple = "CLASSIFY OCCURRENCES, NOT STRINGS: the same text with a different document function can have a different isHeading (a date over a revision note vs a date on a cover; a part title on its contents page vs the same words as a running header).",
            decisionQuestions = new[]
            {
                "What is this?",
                "What object or scope does it refer to?",
                "What does this occurrence do here?",
                "Does it open or identify a semantic region?",
            },
            functionalRules = new[]
            {
                new { kind = "PAGE_FURNITURE", condition = "whose function is only page navigation or repetition", isHeading = "false" },
                new { kind = "ORDINARY_CAPTION", condition = "whose scope is only the figure or table object it names", isHeading = "false" },
                new { kind = "NAVIGATION_ENTRY", condition = "whose function is only to point to another occurrence", isHeading = "false" },
                new { kind = "REGION_OPENER", condition = "that establishes a semantic content region", isHeading = "candidate true" },
            },
            ordinaryMatters = "\"Ordinary\" and \"only\" are part of the rules: a title that looks like a table or list caption but opens a whole region (\"Annex 2: List of Participants\") is not excluded as a caption.",
            confirmedPatterns = new[]
            {
                "Running header/footer occurrences are false. No blacklist by text: the same text at an occurrence that truly opens a region can be true.",
                "Financial table row and row-group labels are false, bold or not, with or without a Note reference.",
                "Ordinary table/figure captions are false, whatever their font size.",
                "A note-local label is not auto-true by its typography; it is true when it stands alone and opens coherent prose or data below it.",
                "A cover or title block is decomposed first: title-bearing lines form one composite claim; issuer, date and audit status are metadata, not parts of that claim just because they share the cover.",
                "Unit lines and reporting-period column headings are false.",
                "Footnotes and letter-notes under tables are false.",
                "A contents region or sub-region opener is true; individual contents entries are false.",
                "Chart and panel internal labels are false when they only name a series, panel or chart-local object; font or bold does not promote them.",
            },
            compositeHeadings = "A semantic heading can span several source occurrences (cover titles, wrapped legal and PDF headings, form titles, statement titles, agenda titles): it is one claim with sourceParts, in every genre.",
            genre = "Genre may inform context packing or a prior; it never changes the definition of isHeading. No per-genre rule sets.",
            informationTypeAdditions = new
            {
                ISSUER_METADATA = "the issuing body named on a cover or part title page",
                STATUS_METADATA = "an audit or approval status such as \"(Unaudited)\"",
                note = "additive to OCCURRENCE_SEMANTIC_AXES_V2's informationType values",
            },
            generalizationPass = new[]
            {
                "No hardcoded document ID.",
                "No hardcoded literal heading text.",
                "No font, style or tableDepth used as a decision gate.",
                "No Gold count used as a target.",
                "No genre changing the definition of isHeading.",
                "One ontology explains DOCX and PDF, legal, meeting, procurement and financial documents.",
                "A new document adds evidence, not an exception rule.",
                "The model decides semantics; the binder/harness decides coordinates.",
                "isHeading exists before task projection.",
                "Gold is independent of model prediction.",
            },
            status = new
            {
                generalizedGoldOntologyDesign = "YES",
                generalizedOfflineAudit = "YES",
                productionLlmContractAligned = "NOT YET - the model still returns sourceAlias(es), isHeading, verbatim text/parts, semanticRole, structuralType, scope and relationHints, not the axes",
                endToEndPipelineGeneric = "NOT YET FULLY",
                next = "audit SRC-029, SRC-041, SRC-042, SRC-044, SRC-053, SRC-054 with this ontology as its validation; extend the ontology only for a genuinely general semantic concept, never for one document",
            },
        });
    }

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

    /// <summary>
    /// OCCURRENCE_SEMANTIC_AXES_V3 (user, 2026-09-25): repeatStatus leaves the semantic axes. Whether an
    /// occurrence repeats or continues another is not a property one occurrence can be judged to have: it
    /// is a relation between canonical claims, and exists only after semantic identity is resolved. The
    /// SRC-029 blind run showed the cost of deciding it early - the engine counted contents entries and
    /// prose mentions as earlier occurrences, the Gold rule did not, and neither was defined. Placing the
    /// relation after identity resolution needs no list of which kinds of occurrence may anchor a repeat:
    /// an occurrence that is not a canonical claim is not in the identity graph at all.
    /// </summary>
    [Fact]
    public void Freeze_the_ontology_v3()
    {
        FreezeArtifact.AssertJson("eval/a99-closed-loop/policy", "occurrence-semantic-axes.v3.json", new
        {
            artifactKind = "a99_occurrence_ontology",
            ontologyId = OntologyV3Id,
            supersedes = new
            {
                path = "eval/a99-closed-loop/policy/occurrence-semantic-axes.v2.json",
                ontologyId = OntologyId,
                defect = "repeatStatus (FIRST / REPEATED) was an occurrence-level axis: it asked each occurrence whether it repeats without saying what it repeats, before any semantic identity existed. That is a relation between canonical claims and belongs to the identity resolver, not to semantic prediction",
                evidence = "SRC029_BLIND_GENERALIZATION_AUDIT_V1: 11 exact true positives disagree on repeatStatus; each has an earlier same-text occurrence that is not a canonical claim (contents entry, list item, prose mention)",
            },
            status = "FROZEN",
            approvedBy = "USER",
            approvedAt = "2026-09-25",
            semanticAxes = new object[]
            {
                new
                {
                    axis = "semanticFunctions",
                    values = (object)Functions,
                    cardinality = "one or more, with one primaryFunction among them",
                },
                new
                {
                    axis = "scope",
                    values = (object)new[] { "DOCUMENT", "DOCUMENT_PART", "EMBEDDED_ARTIFACT", "SECTION", "CLAUSE", "FORM", "FORM_FIELD", "TABLE", "LIST", "TOC", "EVENT", "REVISION_ENTRY", "NOTE" },
                    cardinality = "the object the occurrence is about",
                },
                new
                {
                    axis = "occurrenceRoles",
                    values = (object)Roles,
                    cardinality = "one or more: how the occurrence behaves in the layout",
                },
                new
                {
                    axis = "titleRelation",
                    values = (object)TitleRelations,
                    cardinality = "TITLE names an artifact or unit; TITLE_PART is one line of a multi-line title and is a source part of that title's claim, never a claim of its own",
                },
                new
                {
                    axis = "informationType",
                    values = (object)new[] { "TEMPORAL_METADATA", "LOCATION", "VERSION", "AUTHOR", "STATUS", "QUANTITY" },
                    cardinality = "only when INFORMATION is among the functions",
                    openItem = "carried unchanged from V2. Approved Gold also uses ISSUER_METADATA and STATUS_METADATA (DOC-0133) and APPLICABILITY_METADATA (SRC-029 A1), which this list does not contain; reconciling the vocabulary is a separate user decision",
                },
                new
                {
                    axis = "isHeading",
                    values = (object)new[] { "true", "false" },
                    cardinality = "decided last - never implied by one axis alone",
                },
            },
            removedAxes = new[]
            {
                new { axis = "repeatStatus", replacedBy = "occurrenceRelation, derived after semantic identity resolution" },
            },
            derivedGraphProperties = new object[]
            {
                new
                {
                    property = "semanticNodeId",
                    values = (object)"opaque node id",
                    derivedBy = "semantic identity resolution over bound canonical claims",
                    rule = "claims are the same node when they manifest the same semantic object; same text is not same node (an outer report and an embedded report with near-identical titles are two nodes)",
                },
                new
                {
                    property = "occurrenceRelation",
                    values = (object)OccurrenceRelations,
                    derivedBy = "the global occurrence resolver, from the claims of one semantic node, their source order and context",
                    rule = "exists only between canonical semantic claims after identity resolution; an occurrence that is not a canonical claim never enters the graph and cannot anchor a repeat, so no list of excluded occurrence kinds is needed",
                },
            },
            neverPredicted = new[] { "semanticNodeId", "occurrenceRelation" },
            order = new[]
            {
                "What does it say? (semanticFunctions)",
                "What object is it about? (scope)",
                "How does it behave in the layout? (occurrenceRoles, titleRelation)",
                "Is this occurrence a heading? (isHeading)",
                "After binding and identity resolution: which node, and PRIMARY / REPEAT / CONTINUATION (derived)",
            },
            principles = new[]
            {
                "Semantic function is not heading status: IDENTITY of a TABLE in a CAPTION role is not a heading.",
                "METADATA is not an absolute non-heading: INFORMATION in a REGION_OPENER role can open a region.",
                "A repeat or continuation of a node is still a claim of its own: its heading status is decided like any other occurrence's.",
                "A title that wraps over lines is one claim: its lines are TITLE_PART source parts of that claim.",
                "The model proposes the semantic axes; identity, occurrence relation, hierarchy and projection are derived after binding.",
            },
            vocabularyPolicy = "fail closed: a value outside this ontology is refused and reported, never mapped to the nearest value",
            migration = new
            {
                v2Artifacts = "unchanged; repeatStatus in Gold frozen under V2 stays as a historical field",
                scoring = "not compared under V3; the SRC-029 raw score (adf11f4) keeps its V2 repeatStatus row as recorded",
                src029Residuals = "the 11 repeatStatus disagreements are RESPONSIBILITY_PLACEMENT_ERROR (a relation inferred before identity resolution), not an ontology gap",
            },
            modelContractChanged = false,
        });
    }
}
