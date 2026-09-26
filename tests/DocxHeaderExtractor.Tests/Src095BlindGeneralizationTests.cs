using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using V13 = DocxHeaderExtractor.Tests.GenericAudit.V1_3;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC095_BLIND_GENERALIZATION_AUDIT_V1 - the first held-out measurement of GENERIC_AUDIT_ENGINE_V1.3 over
/// PDF_SOURCE_FACTS_V3 (both frozen together), on a source of another genre than every development document: RFC 9114
/// (HTTP/3), a system-generated standard. It has no Gold of any kind, was never used in development, and was excluded
/// from the V3 facts audit.
/// </summary>
public sealed class Src095BlindGeneralizationTests
{
    internal const string Id = "SRC-095";
    internal const string Study = "SRC095_BLIND_GENERALIZATION_AUDIT_V1";
    internal const string Pdf = GenericAuditEngineV13FreezeTests.HeldOut;
    internal const string Root = "eval/a99-closed-loop/generic-audit-v1_3-held-out";
    private const string EngineFreeze = GenericAuditEngineV13Tests.Root + "/GENERIC_AUDIT_ENGINE_V1.3.freeze.json";
    private const string FactsFreeze = "eval/a99-closed-loop/pdf-source-facts-v3/PDF_SOURCE_FACTS_V3.freeze.json";
    internal const string ContractFreeze = "eval/a99-closed-loop/hardcode-audit-v1/MODEL_VISIBLE_CONTRACT_V2.PDF_SOURCE_FACTS_V3.freeze.json";

    internal static object EngineIdentity() => new
    {
        engine = GenericAuditEngineV13Tests.EngineIdentity(),
        engineFreeze = new { path = EngineFreeze, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(EngineFreeze)) },
        factsFreeze = new { path = FactsFreeze, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(FactsFreeze)) },
    };

    [Fact]
    public void Preregister_src095()
    {
        var artifact = TestRepository.Path($"{Root}/{Id}.preregistration.json");
        if (!File.Exists(artifact))
            Assert.Equal(PdfSourceFactsVersion.V3_RobustGlyphStatistics, PdfSourceFactsVersions.Current); // registration-time precondition
        Assert.Equal("FIXED_OWNED_COUNT_120", SemanticEvidencePackingPolicies.Default.PolicyId);

        FreezeArtifact.AssertJson(Root, $"{Id}.preregistration.json", new
        {
            artifactKind = "a99_generic_audit_preregistration",
            study = Study,
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
                pdfSourceFacts = "PDF_SOURCE_FACTS_V3",
                contractFreeze = new { path = ContractFreeze, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ContractFreeze)) },
                packingPolicy = "FIXED_OWNED_COUNT_120 (the default)",
                modelCallsAuthorized = 0,
            },
            source = new { path = Pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Pdf)), media = "PDF", genre = "system-generated technical standard (RFC)" },
            sourceNote = "no Gold, count or occurrence list exists for this source; never used in development; excluded from the PDF_SOURCE_FACTS_V3 audit. Occurrence Gold is authored on the original PDF after the blind proposals are committed.",
            developmentEvidence = "the V1.3 freeze's development table (DOC-0123, DOC-0133, SRC-029, SRC-041, SRC-042, SRC-044, SRC-053, SRC-054) - all financial or procurement documents; none a technical standard",
            knownBefore = new HeldOutProtocolV12.KnownGap[]
            {
                new("B2", "TOC sequence continuity evidence"),
                new("B4", "outline level on list item"),
                new("K1", "a title continued across a page break is not assembled"),
                new("K2", "stacked title lines of different sizes are not assembled"),
                new("K3", "standalone connective lines between alternatives have no FALSE shape"),
                new("K5", "FORM scope needs fill-in fields"),
                new("K7", "an unprefixed table-part label has no caption shape"),
                new("N1", "a metadata line set like the title below it counts as a peer"),
                new("N2", "a bold bullet glyph alone in its segment"),
                new("F3", "PDF_SOURCE_FACTS_V3: a line merging two texts of different sizes reports the dominant one"),
                new("A", "colon-ended labels and italic labels go to review; an identically set label directly under another keeps the review fail-safe; scope and IDENTITY vs STRUCTURE are meaning decisions"),
            },
            forbidden = new[]
            {
                "model/provider/VLM calls",
                "any Gold or occurrence decision before the proposals are committed",
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
    public void Blind_src095()
    {
        var ran = File.Exists(TestRepository.Path($"{Root}/{Id}.proposals.json"));
        if (!ran && Environment.GetEnvironmentVariable("A99_BLIND") != Id) return;

        using var prereg = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/{Id}.preregistration.json")));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(prereg.RootElement.GetProperty("engineIdentity").GetRawText()),
                System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(EngineIdentity(), FreezeArtifact.Json))),
            "the engine is not the pre-registered one");
        Assert.Equal(prereg.RootElement.GetProperty("source").GetProperty("sha256").GetString(), CanonicalArtifactHash.OfBytes(TestRepository.Path(Pdf)));

        V13.SourceEvidenceProfile Profile() => V13.SourceEvidenceProfile.FromPdf(TestRepository.Path(Pdf), PdfSourceFactsVersion.V3_RobustGlyphStatistics);
        var profile = Profile();
        var hypotheses = V13.SemanticAuditEngine.Propose(profile);
        Assert.Equal(JsonSerializer.Serialize(hypotheses), JsonSerializer.Serialize(V13.SemanticAuditEngine.Propose(Profile())));

        var occ = profile.Occurrences;
        FreezeArtifact.AssertJson(Root, $"{Id}.evidence-profile.json", new
        {
            artifactKind = "a99_source_evidence_profile",
            study = Study,
            pdfSourceFacts = "PDF_SOURCE_FACTS_V3",
            source = new { path = Pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Pdf)) },
            occurrences = occ.Count,
            pages = profile.PageCount,
            body = new { fontSize = profile.BodyFontSize, bold = profile.BodyBold, italic = profile.BodyItalic },
            typographyClusters = occ.GroupBy(o => (o.FontSize, o.Bold, o.Italic)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.FontSize)
                .Select(g => new { fontSize = g.Key.FontSize, bold = g.Key.Bold, italic = g.Key.Italic, occurrences = g.Count(), meanWords = Math.Round(g.Average(o => o.WordCount), 1) }).ToArray(),
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
                hypotheses = hypotheses.Count,
                byProposal = hypotheses.GroupBy(h => h.ProposedIsHeading).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            },
            hypotheses = hypotheses.Where(h => h.ProposedIsHeading != "FALSE" || h.Prominence >= 1000).ToArray(),
        });
    }
}
