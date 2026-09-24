using DocumentFormat.OpenXml.Packaging;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// DOC-0258's source, rebuilt from its original PDF.
/// <para>
/// The earlier pdf2docx conversion (todo10_8/heading_corpus_95_word, sha256 7d4661e4…) merged every
/// standalone label line into the text after it: the six region names into their paragraphs, DAY 1
/// into the first agenda row, "Annex 2" and each organisation into its attendee list. Those sixteen
/// merges are exactly the sixteen approved headings the qwen3.7-flash DOCX harness missed in all
/// three repeats. The PDF itself keeps each of them on its own line.
/// </para>
/// <para>
/// The old file is left in place - twenty-odd historical artifacts pin it - and only the live
/// authority is repointed to this one. Regenerate with A99_REGENERATE_DOCX=1; otherwise the committed
/// file is checked paragraph by paragraph against what the converter produces.
/// </para>
/// </summary>
public sealed class Doc0258SourceRegenerationTests
{
    internal const string PdfPath = "todo10_8/heading_corpus_100/05_bien_ban_hop/078_ICP_IACG07_Minutes_May_2023.pdf";
    internal const string DocxPath = "todo10_8/generated-docx-v2/05_bien_ban_hop/078_ICP_IACG07_Minutes_May_2023.docx";
    internal const string SupersededDocxPath = "todo10_8/heading_corpus_95_word/05_bien_ban_hop/078_ICP_IACG07_Minutes_May_2023.docx";
    internal const string SupersededDocxSha256 = "7d4661e4f40cfb80cf3d1bb01d465842862d8a8ba54a6476d3e8fab8d8d95e30";

    [Fact]
    public void Committed_docx_is_exactly_what_the_converter_writes_from_the_pdf()
    {
        var blocks = PdfLineDocxConverter.BuildBlocks(PdfLineDocxConverter.ReadLines(TestRepository.Path(PdfPath)));
        if (Environment.GetEnvironmentVariable("A99_REGENERATE_DOCX") == "1")
            PdfLineDocxConverter.WriteDocx(blocks, TestRepository.Path(DocxPath));

        var paragraphs = ReadParagraphs(TestRepository.Path(DocxPath));
        Assert.Equal(blocks.Select(block => block.Text), paragraphs.Select(p => p.Text));

        FreezeArtifact.AssertJson("eval/a99-closed-loop/generated-docx-v2", "DOC-0258.conversion-manifest.v1.json", new
        {
            artifactKind = "a99_source_regeneration_manifest",
            schemaVersion = "a99-source-regeneration-manifest-v1",
            documentId = "DOC-0258",
            modelCalls = 0,
            providerCalls = 0,
            originalPdf = new { path = PdfPath, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(PdfPath)) },
            regeneratedDocx = new { path = DocxPath, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(DocxPath)), paragraphs = paragraphs.Count },
            supersedes = new { path = SupersededDocxPath, sha256 = SupersededDocxSha256, producedBy = "pdf2docx" },
            converter = new
            {
                implementation = "tests/DocxHeaderExtractor.Tests/PdfLineDocxConverter.cs",
                reads = "PdfPig words; a word joins a line when its vertical centre is within half the page's median word height",
                carries = "words and bold runs only - no heading styles, no outline levels, no annotations",
                newParagraphWhen = new[]
                {
                    "the page changes",
                    "the gap to the previous line exceeds 20pt (normal leading is ~13.5pt)",
                    "bold changes between lines",
                    "the line starts a list item (−, –, o)",
                    "the previous line ends more than 60pt short of the text block's right edge - unless both lines are centred (a wrapped title)",
                },
            },
            reason = "The superseded conversion merged sixteen standalone label lines of the PDF into the text after them; this rebuild keeps each on its own paragraph.",
            approvedBy = "USER",
            approvedAt = "2026-09-24",
        });
    }

    internal static IReadOnlyList<(string StableId, string Text)> ReadParagraphs(string docxPath)
    {
        using var document = WordprocessingDocument.Open(docxPath, false);
        var body = document.MainDocumentPart!.Document!.Body!;
        return ParagraphWalker.Enumerate(body, new ExtractionOptions())
            .Select(p => (p.StableId, p.Element.InnerText))
            .ToArray();
    }
}
