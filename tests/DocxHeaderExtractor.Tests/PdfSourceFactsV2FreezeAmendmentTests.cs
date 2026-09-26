using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Append-only amendment to the PDF_SOURCE_FACTS_V2 freeze (e69819b), made when PDF_SOURCE_FACTS_V3 was added to the
/// same two definition files. The freeze pinned those files' bytes; V3 had to change them, so the freeze artifact is
/// not rewritten (SRC-054's pre-registration pins its hash) and its test now treats the file hashes as the state at the
/// freeze. What V2 means is held by behaviour instead, every run:
/// the V2 synthetic tests (PdfSourceFactsV2Tests), the model-visible contract frozen over V2 (the exact request bytes
/// of a structured PDF), SRC-054's blind re-verification under V2 (its committed proposals are reproduced), and the
/// V3 test that V2 still reports the mean of the glyphs' effective sizes.
/// </summary>
public sealed class PdfSourceFactsV2FreezeAmendmentTests
{
    private const string Dir = "eval/a99-closed-loop/pdf-source-facts-v2";

    [Fact]
    public void Freeze_the_v2_freeze_amendment()
    {
        using var freeze = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Dir}/PDF_SOURCE_FACTS_V2.freeze.json")));
        FreezeArtifact.AssertJson(Dir, "PDF_SOURCE_FACTS_V2.freeze-amendment.json", new
        {
            artifactKind = "a99_pdf_source_facts_freeze_amendment",
            amends = new
            {
                path = $"{Dir}/PDF_SOURCE_FACTS_V2.freeze.json",
                sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Dir}/PDF_SOURCE_FACTS_V2.freeze.json")),
                commit = "e69819b",
            },
            cause = "PDF_SOURCE_FACTS_V3 is added to the same two definition files, so their bytes can no longer be V2's",
            definitionFilesAtFreeze = JsonSerializer.Deserialize<object>(freeze.RootElement.GetProperty("definitionFiles").GetRawText()),
            v2HeldByBehaviour = new[]
            {
                "PdfSourceFactsV2Tests: matrix-scaled type flat under V1, drawn size and derived weight under V2, text and geometry unchanged",
                "MODEL_VISIBLE_CONTRACT_V2.PDF_SOURCE_FACTS_V2.freeze.json: the exact provider input plan of the structured 072 PDF under V2",
                "Src054BlindGeneralizationTests.Blind_src054: V1.2 over V2 reproduces SRC-054's committed blind proposals",
                "PdfSourceFactsV3Tests.V2_is_unchanged_by_v3: V2 still reports the mean of the glyphs' effective sizes",
            },
            unchanged = "the V2 freeze artifact, the audit, the diagnostic and every V2 result; nothing is regenerated",
            modelProviderVlmCalls = 0,
        });
    }
}
