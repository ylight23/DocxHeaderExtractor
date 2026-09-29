using PDFtoImage;
using SkiaSharp;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Renders the DOC-0252 page containing ITEM-CCE2C592 ("Agenda") to a PNG file for
/// HAIKU_VLM_AGENDA_INDEPENDENT_ADJUDICATION_V1, so a blind Haiku subagent can Read it visually. Zero
/// model or provider calls - pure offline rasterization of the same frozen source PDF every other
/// arm in this lineage already reads, using the same deterministic builder/renderer already verified
/// against the real, committed FULL_STRUCTURED_CONTEXT_V2 execution
/// (<see cref="SelectiveSemanticEscalationV1V2ViewMaterializationTests"/>).
/// </summary>
public sealed class HaikuVlmAgendaIndependentAdjudicationV1RenderTests
{
    private const string EvidenceRoot = "eval/a99-closed-loop/haiku-vlm-agenda-independent-adjudication-v1/DOC-0252";
    private const string SourcePdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string TargetAlias = "L0519:S0";
    private const int Dpi = 200;

    [Fact]
    public void Render_and_freeze_the_agenda_page_image()
    {
        var pdfBytes = File.ReadAllBytes(TestRepository.Path(SourcePdf));
        var plan = DocxHeaderExtractor.DocumentProcessing.Pipeline.PdfStructuredSourceAuthorityBuilder
            .Build(TestRepository.Path(SourcePdf));
        var atom = plan.Atoms.Single(a => a.Alias == TargetAlias);
        var pageIndex = atom.Page - 1;
        Assert.True(pageIndex >= 0);

        using var bitmap = Conversion.ToImage(pdfBytes, page: pageIndex, options: new RenderOptions { Dpi = Dpi });
        using var pngData = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var pngBytes = pngData.ToArray();
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pngBytes));

        var outDir = TestRepository.Path(EvidenceRoot);
        Directory.CreateDirectory(outDir);
        var imagePath = Path.Combine(outDir, "agenda-page-render.png");
        if (!File.Exists(imagePath))
            File.WriteAllBytes(imagePath, pngBytes);
        else
            Assert.Equal(sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(imagePath))));

        FreezeArtifact.AssertJson(EvidenceRoot, "agenda-page-render-manifest.v1.json", new
        {
            artifactKind = "a99_haiku_vlm_agenda_independent_adjudication_render_manifest",
            schemaVersion = "a99-haiku-vlm-agenda-independent-adjudication-render-manifest-v1",
            status = "RENDERED_OFFLINE_ZERO_CALLS",
            modelCalls = 0,
            providerCalls = 0,
            targetItemId = "ITEM-CCE2C592",
            targetAlias = TargetAlias,
            pageIndex,
            dpi = Dpi,
            imagePath = $"{EvidenceRoot}/agenda-page-render.png",
            imageSha256 = sha256,
            imageBytes = pngBytes.Length,
        });
    }
}
