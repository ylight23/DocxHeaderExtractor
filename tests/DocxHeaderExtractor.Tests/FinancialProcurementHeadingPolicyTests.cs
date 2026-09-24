namespace DocxHeaderExtractor.Tests;

/// <summary>
/// FINANCIAL_PROCUREMENT_HEADING_POLICY_V1, as the user set it on 2026-09-24 for the eight financial
/// and procurement documents (DOC-0123, DOC-0133, SRC-029, SRC-041, SRC-042, SRC-044, SRC-053,
/// SRC-054). It is the canonical heading contract stated for these documents, not a variant of it:
/// ordinary table and figure captions stay non-headings exactly as the pipeline already treats them.
/// Frozen here so an audit cites one text; changing it means changing this file and its freeze.
/// </summary>
public sealed class FinancialProcurementHeadingPolicyTests
{
    public const string PolicyId = "FINANCIAL_PROCUREMENT_HEADING_POLICY_V1";

    /// <summary>The eight labels every model proposal is audited into.</summary>
    public static readonly string[] AuditCategories =
    [
        "STRUCTURAL_HEADING",
        "TABLE_LABEL",
        "TABLE_CAPTION",
        "FIELD_LABEL",
        "RUNNING_HEADER_FOOTER",
        "FOOTNOTE_NOTE",
        "NAVIGATION",
        "OTHER_NON_HEADING",
    ];

    /// <summary>
    /// The financial-statement distinctions the user froze on 2026-09-25 before the DOC-0133 audit, on
    /// the OCCURRENCE_SEMANTIC_AXES_V2 axes. Table depth is evidence, never the decision: a table can
    /// hold a statement title or a note heading as well as captions, column headers and row labels.
    /// </summary>
    [Fact]
    public void Freeze_the_financial_occurrence_distinctions()
    {
        FreezeArtifact.AssertJson("eval/a99-closed-loop/policy", "financial-occurrence-distinctions.v1.json", new
        {
            artifactKind = "a99_heading_policy_addendum",
            policyId = PolicyId,
            ontology = OccurrenceSemanticAxesTests.OntologyId,
            approvedBy = "USER",
            approvedAt = "2026-09-25",
            tableDepthRule = "tableDepth (or a table-like layout in a PDF) is evidence about an occurrence, not a decision gate: \"inside a table\" does not mean \"not a heading\"",
            distinctions = new object[]
            {
                new { kind = "STATEMENT_TITLE", example = "Statement of Financial Position", semanticFunctions = new[] { "IDENTITY" }, scope = "FINANCIAL_STATEMENT", isHeading = "true" },
                new { kind = "NOTE_TITLE", example = "Note 7 - Investments", semanticFunctions = new[] { "STRUCTURE", "IDENTITY" }, scope = "NOTE", isHeading = "true" },
                new { kind = "TABLE_CAPTION", example = "Table 4. Commitments by Region", semanticFunctions = new[] { "IDENTITY" }, scope = "TABLE", isHeading = "false by default (policy caption rule)" },
                new { kind = "COLUMN_HEADER", example = "2025 | 2024", semanticFunctions = new[] { "INFORMATION" }, scope = "TABLE_COLUMN", isHeading = "false" },
                new { kind = "ROW_LABEL", example = "Cash and cash equivalents", semanticFunctions = new[] { "INFORMATION" }, scope = "TABLE_ROW", isHeading = "false" },
            },
            expectation = "the final count is decided by the source: neither the old Gold total nor the model's proposal count is a target",
        });
    }

    [Fact]
    public void Freeze_the_policy()
    {
        FreezeArtifact.AssertJson("eval/a99-closed-loop/policy", "financial-procurement-heading-policy.v1.json", new
        {
            artifactKind = "a99_heading_policy",
            policyId = PolicyId,
            status = "FROZEN",
            approvedBy = "USER",
            approvedAt = "2026-09-24",
            scope = new[] { "DOC-0123", "DOC-0133", "SRC-029", "SRC-041", "SRC-042", "SRC-044", "SRC-053", "SRC-054" },
            definition = "CANONICAL HEADING = occurrence whose primary document function is to identify or organize a semantic region of the document.",
            include = new[]
            {
                "Document / report title",
                "Part / Chapter / Section / Subsection headings",
                "Numbered contractual/procurement sections and clauses",
                "Annex / Appendix / Schedule headings",
                "Financial statement titles when they identify a statement artifact",
                "\"Note X – ...\" / \"Note X: ...\" when it opens a complete note section",
                "MD&A section/subsection headings",
                "Form/template section headings when they organize a region",
                "Local headings inside a form/document when subsequent content belongs under them",
            },
            exclude = new[]
            {
                "Table row labels",
                "Table column headers",
                "Ordinary table captions/titles",
                "Ordinary figure captions/titles",
                "Repeated running headers/footers",
                "Page numbers",
                "Footnotes / explanatory notes such as \"a. Includes...\"",
                "Source lines",
                "Field labels: Name, Address, Employer, Bidder, Date, Amount, etc.",
                "Bullet/list item labels that do not open a structural region",
                "TOC/navigation entries when they merely point elsewhere",
                "Repeated metadata",
            },
            hardCaseTest = new
            {
                question = "If I remove the table/form/body content immediately following this text, does this text still identify or organize a document region?",
                yes = "heading candidate",
                no = "label/caption/metadata",
            },
            examples = new[]
            {
                new { text = "Note 7 – Investments", followedBy = "several paragraphs + tables", verdict = "HEADING" },
                new { text = "Table 7. Investments by Country", followedBy = "one table", verdict = "CAPTION, NOT HEADING" },
                new { text = "Contract Data", followedBy = "multiple fields/sections underneath", verdict = "HEADING" },
                new { text = "Employer:", followedBy = "World Bank", verdict = "FIELD LABEL, NOT HEADING" },
            },
            captionRule = "A numbered table/figure title (\"Table D1: ...\") is not accepted by default. It is a heading only when that occurrence opens an independent structural region rather than naming the table or figure immediately below it.",
            consistentWith = new[]
            {
                "src/DocxHeaderExtractor.Infrastructure/AI/HeaderPrompt.cs (captions of figures/tables are not headings)",
                "src/DocxHeaderExtractor.DocumentProcessing/OpenXmlLayer/HeadingHeuristics.cs (IsObjectCaption)",
                "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/StyleDeclaredOutline.cs (caption exclusion X2)",
            },
            auditCategories = AuditCategories,
            method = new
            {
                candidates = "a deterministic candidate list built from each document's structure and source",
                modelRole = "the eight frozen qwen3.7-flash runs in eval/a99-closed-loop/scaleup-real-harness-v1 are evidence to compare against, never an automatic accept",
                newModelCalls = 0,
                goldWritesInThisPhase = false,
                perDocumentReport = new[]
                {
                    "OLD_GOLD_TOTAL", "STRUCTURAL_REVIEW_TOTAL", "KEEP_FROM_OLD", "REMOVE_FROM_OLD", "ADD_TO_OLD",
                    "AMBIGUOUS", "FINAL_CANDIDATE_TOTAL", "USER_APPROVAL_REQUIRED",
                },
                order = new[] { "DOC-0123", "DOC-0133", "SRC-029", "SRC-041", "SRC-042", "SRC-044", "SRC-053", "SRC-054" },
            },
        });
    }
}
