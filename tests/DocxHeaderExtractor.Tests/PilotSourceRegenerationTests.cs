namespace DocxHeaderExtractor.Tests;

/// <summary>
/// DOC-0255 and DOC-0259's sources, rebuilt from their PDFs by <see cref="PdfLineDocxConverter"/> -
/// the same conversion DOC-0258 got, for the same defect. Their pdf2docx files merged standalone
/// label lines into neighbouring text: DOC-0255's "MEETING MINUTES", "Opening:" and "AGENDA for FIRST
/// MEETING"; DOC-0259's agenda cover page ("Agenda", the meeting-name and date lines). Regenerate with
/// A99_REGENERATE_DOCX=1; otherwise each committed file is checked against the converter's output.
/// </summary>
public sealed class PilotSourceRegenerationTests
{
    public static TheoryData<string, string> Documents => new()
    {
        { "DOC-0255", "075_FORTIS_GC_Minutes_Nov21_2024" },
        { "DOC-0259", "079_ICP_TAG_Minutes_Apr_2024" },
    };

    internal static string PdfPath(string stem) => $"todo10_8/heading_corpus_100/05_bien_ban_hop/{stem}.pdf";
    internal static string DocxPath(string stem) => $"todo10_8/generated-docx-v2/05_bien_ban_hop/{stem}.docx";
    internal static string SupersededPath(string stem) => $"todo10_8/heading_corpus_95_word/05_bien_ban_hop/{stem}.docx";

    [Theory]
    [MemberData(nameof(Documents))]
    public void Committed_docx_is_exactly_what_the_converter_writes_from_the_pdf(string id, string stem)
    {
        var blocks = PdfLineDocxConverter.BuildBlocks(PdfLineDocxConverter.ReadLines(TestRepository.Path(PdfPath(stem))));
        if (Environment.GetEnvironmentVariable("A99_REGENERATE_DOCX") == "1")
            PdfLineDocxConverter.WriteDocx(blocks, TestRepository.Path(DocxPath(stem)));

        var paragraphs = Doc0258SourceRegenerationTests.ReadParagraphs(TestRepository.Path(DocxPath(stem)));
        Assert.Equal(blocks.Select(block => block.Text), paragraphs.Select(p => p.Text));

        FreezeArtifact.AssertJson("eval/a99-closed-loop/generated-docx-v2", $"{id}.conversion-manifest.v1.json", new
        {
            artifactKind = "a99_source_regeneration_manifest",
            schemaVersion = "a99-source-regeneration-manifest-v1",
            documentId = id,
            modelCalls = 0,
            providerCalls = 0,
            originalPdf = new { path = PdfPath(stem), sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(PdfPath(stem))) },
            regeneratedDocx = new
            {
                path = DocxPath(stem),
                sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(DocxPath(stem))),
                paragraphs = paragraphs.Count,
            },
            supersedes = new
            {
                path = SupersededPath(stem),
                sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(SupersededPath(stem))),
                producedBy = "pdf2docx",
            },
            converter = "tests/DocxHeaderExtractor.Tests/PdfLineDocxConverter.cs (the rules recorded in DOC-0258.conversion-manifest.v1.json)",
            approvedBy = "USER",
            approvedAt = "2026-09-24",
        });
    }
}
