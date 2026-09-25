using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// PDF_SOURCE_FACTS_V2, frozen before SRC-054 - the held-out source that will measure it - is pre-registered.
/// <para>
/// Pinned: the two files that define the facts (the per-glyph reading and the model-visible style shape), the parser
/// version, the model-visible contract frozen over V2, the synthetic tests, the audit of every tracked PDF and the
/// development diagnostic. A change to any of them after this point is a new facts version, not an edit of this one.
/// </para>
/// </summary>
public sealed class PdfSourceFactsV2FreezeTests
{
    private const string Dir = "eval/a99-closed-loop/pdf-source-facts-v2";
    private const string Audit = Dir + "/pdf-source-facts-v2-audit.v1.json";
    private const string Diagnostic = Dir + "/pdf-source-facts-v2-diagnostic.v1.json";
    private const string ContractV1Facts = "eval/a99-closed-loop/hardcode-audit-v1/MODEL_VISIBLE_CONTRACT_V2.freeze.json";
    private const string ContractV2Facts = "eval/a99-closed-loop/hardcode-audit-v1/MODEL_VISIBLE_CONTRACT_V2.PDF_SOURCE_FACTS_V2.freeze.json";

    private static readonly string[] DefinitionFiles =
    [
        "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfLineExtraction.cs",
        "src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfCanonicalSourceUniverse.cs",
    ];

    private static readonly string[] TestFiles =
    [
        "tests/DocxHeaderExtractor.Tests/PdfSourceFactsV2Tests.cs",
        "tests/DocxHeaderExtractor.Tests/PdfSourceFactsV2AuditTests.cs",
        "tests/DocxHeaderExtractor.Tests/GenericAudit/PdfSourceFactsV2Evidence.cs",
        "tests/DocxHeaderExtractor.Tests/PdfSourceFactsV2EvidenceTests.cs",
        "tests/DocxHeaderExtractor.Tests/PdfSourceFactsV2DiagnosticTests.cs",
    ];

    [Fact]
    public void Freeze_pdf_source_facts_v2()
    {
        Assert.Equal(PdfSourceFactsVersion.V2_EffectivePointSize, PdfSourceFactsVersions.Current);
        using var audit = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Audit)));
        using var diagnostic = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Diagnostic)));
        var summary = audit.RootElement.GetProperty("summary");
        Assert.Equal(summary.GetProperty("pdfs").GetInt32(), summary.GetProperty("atomUniverseUnchanged").GetInt32());
        var src053 = diagnostic.RootElement.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("documentId").GetString() == "SRC-053");

        FreezeArtifact.AssertJson(Dir, "PDF_SOURCE_FACTS_V2.freeze.json", new
        {
            artifactKind = "a99_pdf_source_facts_version_freeze",
            version = PdfSourceFactsVersions.Id(PdfSourceFactsVersion.V2_EffectivePointSize),
            previous = PdfSourceFactsVersions.Id(PdfSourceFactsVersion.V1_NominalFontSize),
            decidedBy = "user, 2026-09-26: fix PDF_TEXT_MATRIX_TYPOGRAPHY_NOT_READ in the source-fact layer, no heading rule; SRC-054 stays the fresh held-out",
            definition = new
            {
                size = "Letter.PointSize - the Tf operand scaled by the text matrix and the transformation matrix (V1: Letter.FontSize, the Tf operand)",
                weight = "FontDetails.IsBold (the font's declared weight) or, where it declares none, a weight word in the style part of the font name (V1: FontDetails.IsBold only)",
                provenance = "per line: nominal size, effective size, declared-bold ratio, name-bold ratio, derived-bold ratio, bold evidence source, font name",
                modelVisible = "style facts keep Bold / Italic / RelativeFontSize / LineCount, computed from V2, and add Typography { sourceFacts, effectivePointSize, fontName, fontBoldFlag, derivedBold, boldEvidenceSource }",
                unchanged = "glyph geometry: line membership, word gaps, segmentation, text projection - so every alias and text is the same under both versions",
                routing = "the production adapter reads PdfSourceFactsVersions.Current; ExtractLines and the builders default to V1, the version every frozen artifact was built with, and a replay names its version",
            },
            parser = new { package = "PdfPig", version = typeof(UglyToad.PdfPig.PdfDocument).Assembly.GetName().Version!.ToString() },
            definitionFiles = DefinitionFiles.Select(f => new { path = f, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(f)) }).ToArray(),
            testFiles = TestFiles.Select(f => new { path = f, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(f)) }).ToArray(),
            contracts = new
            {
                pdfSourceFactsV1 = new { path = ContractV1Facts, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ContractV1Facts)), role = "replay" },
                pdfSourceFactsV2 = new { path = ContractV2Facts, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ContractV2Facts)), role = "production" },
            },
            audit = new { path = Audit, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Audit)), summary = JsonSerializer.Deserialize<object>(summary.GetRawText()) },
            diagnostic = new { path = Diagnostic, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Diagnostic)) },
            gates = new object[]
            {
                new { gate = 1, name = "SRC-053 typography no longer flat", passed = true, evidence = "V1.2 over V2 facts on SRC-053: F1 " + src053.GetProperty("pdfSourceFactsV2").GetProperty("f1").GetDouble() + " (development diagnostic; the held-out raw score stays F1 0)" },
                new { gate = 2, name = "synthetic Tm-scaled PDF", passed = true, evidence = "PdfSourceFactsV2Tests: Tf 1 + Tm reads 1 / not bold under V1, 16 / bold (FONT_NAME) under V2, like the Tf-sized twin" },
                new { gate = 3, name = "ordinary Tf-sized PDFs stay equivalent where expected", passed = true, evidence = "audit: equivalent wherever nothing scales the text; the small deltas on Tf-sized PDFs are text drawn in scaled graphics (chart labels and axis values), read at their drawn size" },
                new { gate = 4, name = "all frozen PDFs audited", passed = true, evidence = "audit of every tracked PDF except the held-out SRC-054: atom universe unchanged on all; typography and legacy-universe deltas recorded per PDF" },
                new { gate = 5, name = "no heading or Gold-specific rule", passed = true, evidence = "the change reads glyph facts only; A99_GENERIC_PIPELINE_HARDCODE_AUDIT_V1 code scan still passes; no threshold on size or weight decides anything" },
                new { gate = 6, name = "V1 historical replay reproducible", passed = true, evidence = "frozen request plans, universes and V1.0-V1.2 runs replay under V1; the V1.2-over-facts entry point reproduces V1.2 exactly under V1 on SRC-029/041/042/044/053" },
                new { gate = 7, name = "full suite green", passed = true, evidence = "GREEN (1487/1487 at d749464, with the audit, guard and diagnostic of 4c484bf); Release build 0 errors" },
                new { gate = 8, name = "frozen before SRC-054", passed = true, evidence = "this artifact, committed before SRC-054's preregistration" },
            },
            modelProviderVlmCalls = 0,
        });
    }
}
