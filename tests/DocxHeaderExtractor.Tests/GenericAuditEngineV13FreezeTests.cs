using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using V13 = DocxHeaderExtractor.Tests.GenericAudit.V1_3;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// PDF_SOURCE_FACTS_V3 and GENERIC_AUDIT_ENGINE_V1.3, frozen together before their held-out source
/// (095_RFC9114_HTTP_3, a system-generated standard - another genre than every development document) is pre-registered.
/// A change to anything pinned here after this point is a new version.
/// </summary>
public sealed class GenericAuditEngineV13FreezeTests
{
    private const string FactsDir = "eval/a99-closed-loop/pdf-source-facts-v3";
    private const string Audit = FactsDir + "/pdf-source-facts-v3-audit.v1.json";
    private const string ContractV3 = "eval/a99-closed-loop/hardcode-audit-v1/MODEL_VISIBLE_CONTRACT_V2.PDF_SOURCE_FACTS_V3.freeze.json";
    internal const string HeldOut = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";

    private static readonly string[] FactsDefinitionFiles =
    [
        "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfLineExtraction.cs",
        "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfCanonicalSourceUniverse.cs",
        "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfCandidateContracts.cs",
    ];

    private static readonly string[] FactsTestFiles =
    [
        "tests/DocxHeaderExtractor.Tests/PdfSourceFactsV3Tests.cs",
        "tests/DocxHeaderExtractor.Tests/PdfSourceFactsV3AuditTests.cs",
    ];

    private static object Hashes(IEnumerable<string> files) =>
        files.Select(f => new { path = f, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(f)) }).ToArray();

    [Fact]
    public void Freeze_pdf_source_facts_v3()
    {
        Assert.Equal(PdfSourceFactsVersion.V3_RobustGlyphStatistics, PdfSourceFactsVersions.Current);
        using var audit = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Audit)));
        var summary = audit.RootElement.GetProperty("summary");
        Assert.Equal(summary.GetProperty("pdfs").GetInt32(), summary.GetProperty("atomUniverseUnchanged").GetInt32());

        FreezeArtifact.AssertJson(FactsDir, "PDF_SOURCE_FACTS_V3.freeze.json", new
        {
            artifactKind = "a99_pdf_source_facts_version_freeze",
            version = PdfSourceFactsVersions.Id(PdfSourceFactsVersion.V3_RobustGlyphStatistics),
            previous = PdfSourceFactsVersions.Id(PdfSourceFactsVersion.V2_EffectivePointSize),
            decidedBy = "user, 2026-09-26: robust raw glyph facts (dominant / median / min / max size, dominant font, bold and italic glyph ratios); a line's size is its dominant size, never its mean; typography stays evidence",
            definition = new
            {
                size = "the dominant effective point size - the size carrying most of the line's characters (spaces excluded; the smaller on a tie), read from Letter.PointSize as in V2",
                statistics = "median, minimum and maximum effective size; the font carrying most characters; the share of characters in a bold weight (declared or named, as V2) and in italic",
                modelVisible = "style facts keep Bold / Italic / RelativeFontSize / LineCount (from the dominant size) and carry Typography { sourceFacts, dominantPointSize, medianPointSize, minPointSize, maxPointSize, dominantFontName, fontBoldFlag, derivedBold, boldEvidenceSource, boldGlyphRatio, italicGlyphRatio }",
                unchanged = "glyph geometry and text (every atom universe equal to V2's in the audit); V1 and V2 byte-identical",
                routing = "production reads PdfSourceFactsVersions.Current = V3 from this freeze on; replays name their version",
            },
            knownLimitation = "a line that merges two texts set at different sizes (a 12pt column header beside a 16pt section title on one row) reports the size of whichever carries more characters; its minimum and maximum still show both. Seen on SRC-029 (development); no heuristic was added for it",
            parser = new { package = "PdfPig", version = typeof(UglyToad.PdfPig.PdfDocument).Assembly.GetName().Version!.ToString() },
            definitionFiles = Hashes(FactsDefinitionFiles),
            testFiles = Hashes(FactsTestFiles),
            contract = new { path = ContractV3, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ContractV3)) },
            audit = new { path = Audit, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Audit)), summary = JsonSerializer.Deserialize<object>(summary.GetRawText()) },
            heldOutExcluded = HeldOut,
            modelProviderVlmCalls = 0,
        });
    }

    [Fact]
    public void Freeze_generic_audit_engine_v1_3()
    {
        var rows = GenericAuditEngineV13Tests.Documents.Select(d =>
        {
            var id = (string)d[0];
            var path = $"{GenericAuditEngineV13Tests.Root}/{id}.dev-score.v1_3.json";
            using var score = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(path)));
            double F1(string key) => score.RootElement.GetProperty(key).GetProperty("headline").GetProperty("f1").GetDouble();
            return new
            {
                documentId = id,
                v1_2_overFactsV2 = F1("v1_2_overFactsV2"),
                v1_3_overFactsV3 = F1("v1_3_overFactsV3"),
                artifact = new { path, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(path)) },
            };
        }).ToArray();

        FreezeArtifact.AssertJson(GenericAuditEngineV13Tests.Root, "GENERIC_AUDIT_ENGINE_V1.3.freeze.json", new
        {
            artifactKind = "a99_generic_audit_engine_freeze",
            engineIdentity = GenericAuditEngineV13Tests.EngineIdentity(),
            decidedBy = "user, 2026-09-26: fix first - V1.3 closes the generic gaps SRC-054 classified as B_NEW; no text, document, Gold count or page special case",
            gaps = new[]
            {
                "13 table-local labels (dot-leader rows are table rows; a region ends only at prose or at a margin label leading into prose; a body-size label whose region holds table rows and no sentence labels that table)",
                "14 labels marked by layout alone (a short standalone margin line at body size, after a finished block, over its own prose; not a colon lead-in, not mid-phrase, not a text recurring on other pages, not in a form)",
                "15 a parent before its child or caption (a numbered caption does not end a region; a bold-italic label directly under a bold one of the same size is its child)",
                "16 mixed glyph sizes (PDF_SOURCE_FACTS_V3's dominant size)",
                "17 period lines are metadata like a date",
                "18 a centred title wrapped from a line that nearly fills its column is one title",
                "19 one chunk, two texts (a title line running into a date gives its head; a bold lead a column away whose own column goes on as prose is a label)",
                "20 a right-set label over short lines at its own left edge is a letterhead",
                "21 a line starting in lower case continues the sentence above",
                "22 a parenthetical status after a title belongs to the title",
            },
            leftToReviewByDesign = new[]
            {
                "an identically set label directly under another (sibling or child: ambiguous) keeps the review fail-safe",
                "italic labels and colon-ended labels (meaning decisions, bucket A)",
            },
            development = new
            {
                role = "DEVELOPMENT - every document here was developed on or revealed; nothing is a generalization result",
                rows,
                noRegression = "V1.3 over facts V3 is at or above V1.2 over facts V2 on every development document but SRC-044 (0.985 -> 0.982), where the one new non-Gold TRUE is a Gold candidate below",
            },
            goldCandidatesSurfaced = new[]
            {
                new { documentId = "SRC-044", alias = "L0236:S0", text = "Equity and Capital Adequacy", note = "a bold sub-label at the left margin over its own left-column prose, its chunk running on into a chart's unit line - the shape of SRC-054's 'Equity-to-Loans Ratio' (in Gold)" },
                new { documentId = "SRC-054", alias = "L0548:S0", text = "Provision for losses on loans and other exposures", note = "a page-top plain standalone label over its own prose - the S054_Q1 shape; Gold R2 lacks it" },
            },
            goldCandidatesStatus = "proposed to the user, not applied; no engine rule was tuned to them",
            heldOut = new { path = HeldOut, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(HeldOut)), status = "chosen before this freeze; excluded from the V3 audit; not inspected" },
            modelProviderVlmCalls = 0,
        });
    }
}
