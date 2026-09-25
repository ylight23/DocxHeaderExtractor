using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC041_BLIND_GENERALIZATION_AUDIT_V1 - GENERIC_AUDIT_ENGINE_V1.1 on a held-out source, pre-registered.
/// <para>
/// Everything the run depends on is pinned here before it runs: the engine's files, constants and
/// ontology; the scorer that will join its proposals with a Gold that does not exist yet; the production
/// request contract and packing policy any model run on this source would use; the source itself; and
/// the gaps known before SRC-041, so that its residuals can be split into known and new. Nothing is
/// read from SRC-041's Gold file, which holds only a count-only total recorded on a retired source.
/// </para>
/// </summary>
public sealed class Src041BlindGeneralizationTests
{
    internal const string Root = "eval/a99-closed-loop/generic-audit-v1_1";
    internal const string Pdf = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf";
    internal const string ScorerFile = "tests/DocxHeaderExtractor.Tests/GenericAudit/V1_1/ExactScorer.cs";
    internal const string OntologyFile = "eval/a99-closed-loop/policy/occurrence-semantic-axes.v3.json";
    internal const string ContractFreeze = "eval/a99-closed-loop/hardcode-audit-v1/MODEL_VISIBLE_CONTRACT_V2.freeze.json";

    [Fact]
    public void Preregister_src041()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ContractFreeze)));
        Assert.Equal("V2_ATTENTION_FREE", contract.RootElement.GetProperty("requestVersion").GetString());
        Assert.Equal("FIXED_OWNED_COUNT_120", DocxHeaderExtractor.DocumentProcessing.Pipeline.SemanticEvidencePackingPolicies.Default.PolicyId);

        FreezeArtifact.AssertJson(Root, "SRC-041.preregistration.v1_1.json", new
        {
            artifactKind = "a99_generic_audit_preregistration",
            study = "SRC041_BLIND_GENERALIZATION_AUDIT_V1",
            engineIdentity = GenericAuditEngineV11Tests.EngineIdentity(),
            ontology = new { id = GenericAuditEngineV11Tests.OccurrenceSemanticAxesV3Id, path = OntologyFile, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(OntologyFile)) },
            scorer = new
            {
                id = DocxHeaderExtractor.Tests.GenericAudit.V1_1.ExactScorer.ScorerId,
                path = ScorerFile,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ScorerFile)),
                validatedBy = "reproduces the committed V1 SRC-029 raw score (adf11f4): TP, FP, every Gold bucket, review noise",
                matching = new[]
                {
                    "claim-level and exact: equal ordered source-part tuples (alias:start-end), bound by the production binder (GENERIC_MULTIPART_BINDER_V2) over the PDF's structured atom universe",
                    "a hypothesis part is a whole atom or its exact leading text; nothing is reordered or repaired, and an unbindable hypothesis matches nothing",
                    "overlap without identity is a claim-level miss (PARTIAL_*), never a true positive",
                    "NEEDS_REVIEW never counts as a heading; it has its own capture rate",
                },
                reports = new[]
                {
                    "TRUE precision / recall / F1", "NEEDS_REVIEW capture of Gold headings", "FALSE-on-Gold count", "review noise outside Gold",
                    "axis disagreements on exact TRUE matches (OCCURRENCE_SEMANTIC_AXES_V3; no repeatStatus)",
                    "then: known gaps recurring, new B gaps, C ontology gaps, D document-specific exceptions",
                },
            },
            productionRequest = new
            {
                requestVersion = "V2_ATTENTION_FREE",
                contractFreeze = new { path = ContractFreeze, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ContractFreeze)) },
                packingPolicy = "FIXED_OWNED_COUNT_120 (the default)",
                coherentRegionSegmentation = "NOT SELECTED - it splits packs on the appendix label (A99_GENERIC_PIPELINE_HARDCODE_AUDIT_V1 open item)",
                modelCallsAuthorized = 0,
            },
            source = new { path = Pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Pdf)), media = "PDF" },
            sourceNote = "SRC-041's Gold file holds only a count-only total recorded on a retired pdf2docx DOCX; it is not opened, and that total is neither engine input nor target. Occurrence Gold is authored on the original PDF after the blind proposals are committed.",
            developmentEvidence = new
            {
                calibration = new[] { "DOC-0123", "DOC-0133" },
                development = "SRC-029 (V1's blind test; its V1.1 numbers are not a generalization result)",
                scores = new[] { "DOC-0123", "DOC-0133", "SRC-029" }.Select(id => new
                {
                    path = $"{Root}/{id}.score.v1_1.json",
                    sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Root}/{id}.score.v1_1.json")),
                }).ToArray(),
            },
            fixedInV1_1 = new[]
            {
                "1 wrapped / multipart claim assembly (column-following chain; wrap judged by fit where the column bound is observable)",
                "2 run-in heading span + trailing body (bold lead or label before a bracketed instruction as its own part)",
                "3 parent opener immediately followed by child heading (numbered structural label; first item of a different enumeration)",
                "4 multi-line instruction blocks (open bracket spans; italic prose runs; strongly set lines excepted)",
                "5 table / two-column topology (each segment of a two-segment row stands alone; bare enumerator joins the segment beside it; colon-ended cell beside a value is a field label)",
                "6 embedded-artifact boundary evidence (page-initial title; FORM only with fill-in fields in its region)",
            },
            knownBeforeSrc041 = new[]
            {
                new { id = "B1", gap = "TOC opener with repeated text" },
                new { id = "B2", gap = "TOC sequence continuity evidence" },
                new { id = "B4", gap = "outline level on list item" },
                new { id = "K1", gap = "a title continued across a page break is not assembled (the binder accepts it; the engine does not propose it)" },
                new { id = "K2", gap = "stacked title lines of different sizes (cover title blocks) are not assembled" },
                new { id = "K3", gap = "connective lines between alternatives (OR / AND) have no lexical shape" },
                new { id = "K4", gap = "DOCX page-break boundary evidence is not read (only paragraph pageBreakBefore)" },
                new { id = "K5", gap = "FORM scope needs fill-in fields: schedules and tables without placeholders stay SECTION" },
                new { id = "A", gap = "scope CLAUSE / SECTION / NOTE / FINANCIAL_STATEMENT and IDENTITY vs STRUCTURE are meaning decisions the engine has no evidence for" },
            },
            forbidden = new[]
            {
                "model/provider/VLM calls",
                "opening SRC-041's Gold file or any occurrence decision before the proposals are committed",
                "the count-only total as engine input or target",
                "engine code, constant, ontology or scorer changes after this pre-registration",
            },
            protocol = new[]
            {
                "1 freeze V1.1, its development evidence and this pre-registration",
                "2 run V1.1 blind on SRC-041; commit the evidence profile and proposals before any Gold",
                "3 independent source-only review of the original PDF; human decisions (proposals are not an authority)",
                "4 freeze SRC-041 occurrence Gold",
                "5 score with the pinned scorer; commit the raw score unchanged",
                "6 classify residuals: known recurring / new B / C / D",
            },
            modelCalls = 0,
        });
    }
}
