using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit;
using V12 = DocxHeaderExtractor.Tests.GenericAudit.V1_2;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC054_BLIND_GENERALIZATION_AUDIT_V1 - the first held-out measurement of PDF_SOURCE_FACTS_V2 (frozen in e69819b),
/// under GENERIC_AUDIT_ENGINE_V1.2's unchanged logic (ad44d78).
/// <para>
/// SRC-053 failed its gate on a source representation fault, not on V1.2's logic (4a7d13f); the fix is the facts
/// version and SRC-053 became its development evidence. SRC-054 was held back from everything the fix was built and
/// audited on, so this measures the pipeline that is kept. The protocol is the V1.2 one (<see cref="HeldOutProtocolV12"/>)
/// with the evidence read under V2 through <see cref="PdfSourceFactsV2Evidence"/>, and its artifacts kept apart.
/// </para>
/// </summary>
public sealed class Src054BlindGeneralizationTests
{
    internal const string Id = "SRC-054";
    internal const string Study = "SRC054_BLIND_GENERALIZATION_AUDIT_V1";
    internal const string Pdf = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/054_IBRD_Information_Statement_FY25.pdf";
    internal const string Root = "eval/a99-closed-loop/generic-audit-v1_2-pdf-facts-v2";
    internal const string FactsFreeze = "eval/a99-closed-loop/pdf-source-facts-v2/PDF_SOURCE_FACTS_V2.freeze.json";
    internal const string ContractFreeze = "eval/a99-closed-loop/hardcode-audit-v1/MODEL_VISIBLE_CONTRACT_V2.PDF_SOURCE_FACTS_V2.freeze.json";
    internal const string EvidenceFile = "tests/DocxHeaderExtractor.Tests/GenericAudit/PdfSourceFactsV2Evidence.cs";
    private const string Diagnostic = "eval/a99-closed-loop/pdf-source-facts-v2/pdf-source-facts-v2-diagnostic.v1.json";

    /// <summary>V1.2's own identity, plus the facts version and the entry point that reads it.</summary>
    internal static object EngineIdentity() => new
    {
        engine = PdfSourceFactsV2Evidence.EngineId,
        logic = GenericAuditEngineV12Tests.EngineIdentity(),
        sourceFacts = new
        {
            version = PdfSourceFactsVersions.Id(PdfSourceFactsVersion.V2_EffectivePointSize),
            freeze = new { path = FactsFreeze, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(FactsFreeze)) },
            evidenceEntryPoint = new { path = EvidenceFile, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(EvidenceFile)) },
        },
    };

    [Fact]
    public void Preregister_src054()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ContractFreeze)));
        Assert.Equal("V2_ATTENTION_FREE", contract.RootElement.GetProperty("contract").GetProperty("requestVersion").GetString());
        Assert.Equal("FIXED_OWNED_COUNT_120", SemanticEvidencePackingPolicies.Default.PolicyId);
        // A registration-time precondition: V2 was production when SRC-054 was registered (6182a81). PDF_SOURCE_FACTS_V3
        // took over later; the artifact below is re-verified as it was written.
        if (!File.Exists(TestRepository.Path($"{Root}/{Id}.preregistration.json")))
            Assert.Equal(PdfSourceFactsVersion.V2_EffectivePointSize, PdfSourceFactsVersions.Current);

        string[] developmentScores = ["DOC-0123", "DOC-0133", "SRC-029", "SRC-041"];
        FreezeArtifact.AssertJson(Root, $"{Id}.preregistration.json", new
        {
            artifactKind = "a99_generic_audit_preregistration",
            study = Study,
            engineCommit = "ad44d78 (logic) + e69819b (PDF_SOURCE_FACTS_V2)",
            engineIdentity = EngineIdentity(),
            ontology = new { id = GenericAuditEngineV12Tests.OccurrenceSemanticAxesV3Id, path = HeldOutProtocolV12.OntologyFile, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(HeldOutProtocolV12.OntologyFile)) },
            scorer = new
            {
                id = GenericAudit.V1_1.ExactScorer.ScorerId,
                path = HeldOutProtocolV12.ScorerFile,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(HeldOutProtocolV12.ScorerFile)),
                matching = "claim-level and exact: equal ordered source-part tuples (alias:start-end) bound by the production binder; overlap without identity is a miss; NEEDS_REVIEW never counts as a heading",
            },
            productionRequest = new
            {
                requestVersion = "V2_ATTENTION_FREE",
                pdfSourceFacts = "PDF_SOURCE_FACTS_V2",
                contractFreeze = new { path = ContractFreeze, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ContractFreeze)) },
                packingPolicy = "FIXED_OWNED_COUNT_120 (the default)",
                coherentRegionSegmentation = "NOT SELECTED",
                modelCallsAuthorized = 0,
            },
            source = new { path = Pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Pdf)), media = "PDF" },
            sourceNote = "SRC-054's Gold file is not opened; any count-only total it holds is neither engine input nor target. It was excluded from the PDF_SOURCE_FACTS_V2 audit and diagnostic. Occurrence Gold is authored on the original PDF after the blind proposals are committed.",
            developmentEvidence = new
            {
                v1_2 = developmentScores.Select(d => new
                {
                    documentId = d,
                    path = $"{HeldOutProtocolV12.Root}/{d}.score.v1_2.json",
                    sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{HeldOutProtocolV12.Root}/{d}.score.v1_2.json")),
                }).ToArray(),
                heldOutUnderV1Facts = new[] { "SRC-042", "SRC-044", "SRC-053" }.Select(d => new
                {
                    documentId = d,
                    path = $"{HeldOutProtocolV12.Root}/{d}.blind-score.v1_2.json",
                    sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{HeldOutProtocolV12.Root}/{d}.blind-score.v1_2.json")),
                }).ToArray(),
                pdfSourceFactsV2Diagnostic = new { path = Diagnostic, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Diagnostic)) },
            },
            knownBefore = new HeldOutProtocolV12.KnownGap[]
            {
                new("B2", "TOC sequence continuity evidence"),
                new("B4", "outline level on list item"),
                new("K1", "a title continued across a page break is not assembled"),
                new("K2", "stacked title lines of different sizes are not assembled"),
                new("K3", "standalone connective lines between alternatives have no FALSE shape"),
                new("K4", "DOCX page-break boundary evidence is not read"),
                new("K5", "FORM scope needs fill-in fields"),
                new("K6", "a chart with no numbered caption has no figure region"),
                new("K7", "an unprefixed table-part label has no caption shape"),
                new("N1", "SRC-042: a metadata line set like the title below it counts as a peer (METADATA_LINE_COUNTED_AS_PEER)"),
                new("N2", "SRC-042: a bold bullet glyph alone in its segment (BARE_BULLET_GLYPH)"),
                new("N3", "SRC-042: the figure region stops at a figure's small subtitle line (FIGURE_REGION_STOPPED_BY_SUBTITLE)"),
                new("N4", "SRC-042: a page notice ('This page left intentionally blank') reads as a page title (PAGE_NOTICE)"),
                new("A", "colon-ended labels over prose go to review; scope and IDENTITY vs STRUCTURE are meaning decisions; one occurrence of a running-header text that opens a part; continued titles over image pages; italic standalone labels and back-cover titles are headings by the SRC-053 user decisions"),
            },
            fixedBefore = new[] { "SRC-053 PDF_TEXT_MATRIX_TYPOGRAPHY_NOT_READ: addressed by PDF_SOURCE_FACTS_V2, measured here for the first time" },
            forbidden = new[]
            {
                "model/provider/VLM calls",
                "opening this document's Gold file or any occurrence decision before the proposals are committed",
                "any existing count-only total as engine input or target",
                "engine logic, source facts, constant, ontology or scorer changes after this pre-registration",
            },
            protocol = new[]
            {
                "1 pre-register (this artifact)", "2 blind run; commit evidence profile and proposals before any Gold",
                "3 source-only review of the original PDF; user decisions on ambiguous patterns", "4 freeze occurrence Gold",
                "5 pin and reveal once; commit the raw score as written", "6 classify residuals: known recurring / new B / C / D",
            },
            modelCalls = 0,
        });
    }

    [Fact]
    public void Blind_src054()
    {
        // Runs when triggered (A99_BLIND=SRC-054), and afterwards re-verifies the committed run.
        var ran = File.Exists(TestRepository.Path($"{Root}/{Id}.proposals.json"));
        if (!ran && Environment.GetEnvironmentVariable("A99_BLIND") != Id) return;

        using var prereg = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/{Id}.preregistration.json")));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(prereg.RootElement.GetProperty("engineIdentity").GetRawText()),
                System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(EngineIdentity(), FreezeArtifact.Json))),
            "the engine is not the pre-registered one");
        Assert.Equal(prereg.RootElement.GetProperty("source").GetProperty("sha256").GetString(), CanonicalArtifactHash.OfBytes(TestRepository.Path(Pdf)));
        Assert.Equal(prereg.RootElement.GetProperty("scorer").GetProperty("sha256").GetString(), CanonicalArtifactHash.OfTextFile(TestRepository.Path(HeldOutProtocolV12.ScorerFile)));

        V12.SourceEvidenceProfile Profile() => PdfSourceFactsV2Evidence.FromPdf(TestRepository.Path(Pdf), PdfSourceFactsVersion.V2_EffectivePointSize);
        var profile = Profile();
        var hypotheses = V12.SemanticAuditEngine.Propose(profile);
        Assert.Equal(JsonSerializer.Serialize(hypotheses), JsonSerializer.Serialize(V12.SemanticAuditEngine.Propose(Profile())));

        var occ = profile.Occurrences;
        FreezeArtifact.AssertJson(Root, $"{Id}.evidence-profile.json", new
        {
            artifactKind = "a99_source_evidence_profile",
            study = Study,
            pdfSourceFacts = "PDF_SOURCE_FACTS_V2",
            source = new { path = Pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Pdf)) },
            media = profile.Media,
            occurrences = occ.Count,
            pages = profile.PageCount,
            body = new { fontSize = profile.BodyFontSize, bold = profile.BodyBold, italic = profile.BodyItalic },
            typographyClusters = occ.GroupBy(o => (o.FontSize, o.Bold, o.Italic)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.FontSize)
                .Select(g => new { fontSize = g.Key.FontSize, bold = g.Key.Bold, italic = g.Key.Italic, occurrences = g.Count(), meanWords = Math.Round(g.Average(o => o.WordCount), 1) }).ToArray(),
            boldLeads = occ.Count(o => o.BoldLead is not null),
            multiSegmentRows = occ.Count(o => o.RowSegmentCount > 1),
            rowsWithFigures = occ.Count(o => o.RowHasFigures),
        });

        FreezeArtifact.AssertJson(Root, $"{Id}.proposals.json", new
        {
            artifactKind = "a99_generic_audit_proposals",
            study = Study,
            engineIdentity = EngineIdentity(),
            preregistrationSha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Root}/{Id}.preregistration.json")),
            documentId = Id,
            role = "HELD_OUT (blind)",
            source = new { path = Pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Pdf)), media = "PDF" },
            modelCalls = 0,
            goldReadBeforeFreeze = false,
            counts = new
            {
                sourceParts = hypotheses.Sum(h => h.Parts.Length),
                hypotheses = hypotheses.Count,
                leadParts = hypotheses.Sum(h => h.Parts.Count(p => p.Verbatim is not null)),
                byProposal = hypotheses.GroupBy(h => h.ProposedIsHeading).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                byRoles = hypotheses.GroupBy(h => string.Join("+", h.OccurrenceRoles)).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            },
            hypotheses = hypotheses.Where(h => h.ProposedIsHeading != "FALSE" || h.Prominence >= 1000).ToArray(),
        });
    }
}
