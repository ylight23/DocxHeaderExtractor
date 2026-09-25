using System.Text.Json;
using V12 = DocxHeaderExtractor.Tests.GenericAudit.V1_2;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The held-out protocol for GENERIC_AUDIT_ENGINE_V1.2, shared by every held-out document it is tested on
/// (SRC-042, then SRC-044, SRC-053, SRC-054 - the same frozen V1.2 for all of them).
/// <para>
/// Per document: the pre-registration pins the engine, the scorer, the production request contract and
/// packing, the source, the development evidence and the gaps known before it; the blind run refuses any
/// engine, source or scorer other than the pre-registered ones and freezes the proposals before any Gold.
/// </para>
/// </summary>
internal static class HeldOutProtocolV12
{
    internal const string Root = "eval/a99-closed-loop/generic-audit-v1_2";
    internal const string ScorerFile = "tests/DocxHeaderExtractor.Tests/GenericAudit/V1_1/ExactScorer.cs";
    internal const string OntologyFile = "eval/a99-closed-loop/policy/occurrence-semantic-axes.v3.json";
    internal const string ContractFreeze = "eval/a99-closed-loop/hardcode-audit-v1/MODEL_VISIBLE_CONTRACT_V2.freeze.json";

    private static readonly string[] DevelopmentScores = ["DOC-0123", "DOC-0133", "SRC-029", "SRC-041"];

    internal sealed record KnownGap(string Id, string Gap);

    internal static void Preregister(string id, string study, string pdf, string engineCommit, KnownGap[] knownGaps, string sourceNote)
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ContractFreeze)));
        Assert.Equal("V2_ATTENTION_FREE", contract.RootElement.GetProperty("requestVersion").GetString());
        Assert.Equal("FIXED_OWNED_COUNT_120", DocxHeaderExtractor.DocumentProcessing.Pipeline.SemanticEvidencePackingPolicies.Default.PolicyId);

        FreezeArtifact.AssertJson(Root, $"{id}.preregistration.v1_2.json", new
        {
            artifactKind = "a99_generic_audit_preregistration",
            study,
            engineCommit,
            engineIdentity = GenericAuditEngineV12Tests.EngineIdentity(),
            ontology = new { id = GenericAuditEngineV12Tests.OccurrenceSemanticAxesV3Id, path = OntologyFile, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(OntologyFile)) },
            scorer = new
            {
                id = GenericAudit.V1_1.ExactScorer.ScorerId,
                path = ScorerFile,
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ScorerFile)),
                validatedBy = "reproduces the committed V1 SRC-029 raw score (adf11f4); scored SRC-041 held out (ba93900)",
                matching = "claim-level and exact: equal ordered source-part tuples (alias:start-end) bound by the production binder; overlap without identity is a miss; NEEDS_REVIEW never counts as a heading",
            },
            productionRequest = new
            {
                requestVersion = "V2_ATTENTION_FREE",
                contractFreeze = new { path = ContractFreeze, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ContractFreeze)) },
                packingPolicy = "FIXED_OWNED_COUNT_120 (the default)",
                coherentRegionSegmentation = "NOT SELECTED",
                modelCallsAuthorized = 0,
            },
            source = new { path = pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(pdf)), media = "PDF" },
            sourceNote,
            developmentEvidence = DevelopmentScores.Select(d => new
            {
                documentId = d,
                path = $"{Root}/{d}.score.v1_2.json",
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Root}/{d}.score.v1_2.json")),
            }).ToArray(),
            knownBefore = knownGaps,
            forbidden = new[]
            {
                "model/provider/VLM calls",
                "opening this document's Gold file or any occurrence decision before the proposals are committed",
                "any existing count-only total as engine input or target",
                "engine code, constant, ontology or scorer changes after this pre-registration",
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

    internal static void Blind(string id, string study, string pdf)
    {
        using var prereg = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/{id}.preregistration.v1_2.json")));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(prereg.RootElement.GetProperty("engineIdentity").GetRawText()),
                System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(GenericAuditEngineV12Tests.EngineIdentity(), FreezeArtifact.Json))),
            "the engine is not the pre-registered one");
        Assert.Equal(prereg.RootElement.GetProperty("source").GetProperty("sha256").GetString(), CanonicalArtifactHash.OfBytes(TestRepository.Path(pdf)));
        Assert.Equal(prereg.RootElement.GetProperty("scorer").GetProperty("sha256").GetString(), CanonicalArtifactHash.OfTextFile(TestRepository.Path(ScorerFile)));

        var profile = V12.SourceEvidenceProfile.FromPdf(TestRepository.Path(pdf));
        var hypotheses = V12.SemanticAuditEngine.Propose(profile);
        Assert.Equal(JsonSerializer.Serialize(hypotheses),
            JsonSerializer.Serialize(V12.SemanticAuditEngine.Propose(V12.SourceEvidenceProfile.FromPdf(TestRepository.Path(pdf)))));

        var occ = profile.Occurrences;
        FreezeArtifact.AssertJson(Root, $"{id}.evidence-profile.v1_2.json", new
        {
            artifactKind = "a99_source_evidence_profile",
            study,
            source = new { path = pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(pdf)) },
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

        FreezeArtifact.AssertJson(Root, $"{id}.proposals.v1_2.json", new
        {
            artifactKind = "a99_generic_audit_proposals",
            study,
            engineIdentity = GenericAuditEngineV12Tests.EngineIdentity(),
            preregistrationSha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Root}/{id}.preregistration.v1_2.json")),
            documentId = id,
            role = "HELD_OUT (blind)",
            source = new { path = pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(pdf)), media = "PDF" },
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

/// <summary>SRC042_BLIND_GENERALIZATION_AUDIT_V1 - GENERIC_AUDIT_ENGINE_V1.2 on its first held-out source.</summary>
public sealed class Src042BlindGeneralizationTests
{
    internal const string Id = "SRC-042";
    internal const string Study = "SRC042_BLIND_GENERALIZATION_AUDIT_V1";
    internal const string Pdf = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/042_IDA_Financial_Statements_June_2025.pdf";

    [Fact]
    public void Preregister_src042() => HeldOutProtocolV12.Preregister(Id, Study, Pdf, "ad44d78",
    [
        new("B2", "TOC sequence continuity evidence"),
        new("B4", "outline level on list item"),
        new("K1", "a title continued across a page break is not assembled"),
        new("K2", "stacked title lines of different sizes are not assembled"),
        new("K3", "standalone connective lines between alternatives have no FALSE shape (V1.2 only keeps them inside titles)"),
        new("K4", "DOCX page-break boundary evidence is not read"),
        new("K5", "FORM scope needs fill-in fields"),
        new("K6", "a chart with no numbered caption has no figure region (its labels can read as headings)"),
        new("K7", "an unprefixed table-part label (\"K8.1\") has no caption shape"),
        new("A", "colon-ended labels over prose go to review; scope NOTE / FINANCIAL_STATEMENT / DOCUMENT_PART / SECTION and IDENTITY vs STRUCTURE are meaning decisions; one occurrence of a running-header text that opens a part is a meaning decision"),
    ],
        "SRC-042's Gold file is not opened; any count-only total it holds is neither engine input nor target. Occurrence Gold is authored on the original PDF after the blind proposals are committed.");

    [Fact]
    public void Blind_src042()
    {
        // Runs when triggered (A99_BLIND=SRC-042), and afterwards re-verifies the committed run.
        var ran = File.Exists(TestRepository.Path($"{HeldOutProtocolV12.Root}/{Id}.proposals.v1_2.json"));
        if (!ran && Environment.GetEnvironmentVariable("A99_BLIND") != Id) return;
        HeldOutProtocolV12.Blind(Id, Study, Pdf);
    }
}
