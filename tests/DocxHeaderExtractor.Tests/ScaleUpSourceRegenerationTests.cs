using System.Text;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The scale-up's three damaged sources, fixed in place at the user's direction ("sửa và ghi đè nguồn
/// cũ ... bỏ nguồn cũ", 2026-09-24): the damaged file is overwritten or removed, not kept beside the
/// fix. Git history keeps the old bytes; each manifest pins their sha256.
/// <list type="bullet">
/// <item>DOC-0264: the DOCX was a legacy text dump - the whole law in one paragraph. Overwritten with
/// LibreOffice's conversion of the original .doc, as DOC-0205 was.</item>
/// <item>SRC-003: pdf2docx merged chapter and article headings ("Chương I", "Điều 1. Phạm vi điều
/// chỉnh") into neighbouring text. Overwritten with <see cref="PdfLineDocxConverter"/>'s output from
/// the PDF, whose blocks carry 10 chapters, 2 sections and articles 1-218 each on its own.</item>
/// <item>SRC-029: pdf2docx interleaved the ITP margin column's clause names with the clause bodies,
/// and <see cref="PdfLineDocxConverter"/> splits small capitals ("S ECTION I") and does the same
/// interleaving. The PDF lane's visual-line segments keep the margin column apart, so the Gold
/// source becomes the original PDF and the damaged DOCX is removed.</item>
/// </list>
/// Rewrite the sources with A99_SCALEUP_REGENERATE=1; repoint the authored Gold once with
/// A99_SCALEUP_REPOINT_GOLD=1 (refuses to run twice).
/// </summary>
public sealed class ScaleUpSourceRegenerationTests
{
    private const string LibreOffice = "LibreOffice (buildid cd7284b4cbbfeb507e630c1aac019f4157393acb)";

    private const string Doc0264Original = "todo10_8/heading_corpus_100/06_dich_song_ngu/084_Luat_Chung_khoan_2019_EN.doc";
    private const string Doc0264Docx = "todo10_8/heading_corpus_95_word/06_dich_song_ngu/084_Luat_Chung_khoan_2019_EN.docx";
    private const string Doc0264ReplacedSha256 = "1e7b4408c4ffb11587f8f453ef6484fe193eeb51844ffa9279018323503e2117";

    private const string Src003Pdf = "todo10_8/heading_corpus_100/01_phap_quy/003_Luat_Doanh_nghiep_59-2020-QH14.pdf";
    private const string Src003Docx = "todo10_8/heading_corpus_95_word/01_phap_quy/003_Luat_Doanh_nghiep_59-2020-QH14.docx";
    private const string Src003ReplacedSha256 = "dedc7827c8d6930a2cd174fe95aa943c0ad958281a689369dd028e21dd765b0c";

    private const string Src029Pdf = "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/029_WB_RFP_Works_DesignBuild_2021.pdf";
    private const string Src029RemovedDocx = "todo10_8/heading_corpus_95_word/02_hop_dong_mua_sam/029_WB_RFP_Works_DesignBuild_2021.docx";
    private const string Src029RemovedSha256 = "475f76a5d3915f4ceb13d6a6bae5fac0485e66fade6c36ddd927cddaadd0fe9e";

    private const string ManifestDirectory = "eval/a99-closed-loop/generated-docx-v2";
    private static string Manifest(string id) => $"{ManifestDirectory}/{id}.conversion-manifest.v1.json";

    [Fact]
    public void Src003_docx_is_exactly_what_the_converter_writes_from_the_pdf()
    {
        var blocks = PdfLineDocxConverter.BuildBlocks(PdfLineDocxConverter.ReadLines(TestRepository.Path(Src003Pdf)));
        if (Environment.GetEnvironmentVariable("A99_SCALEUP_REGENERATE") == "1")
            PdfLineDocxConverter.WriteDocx(blocks, TestRepository.Path(Src003Docx));

        var paragraphs = Doc0258SourceRegenerationTests.ReadParagraphs(TestRepository.Path(Src003Docx));
        Assert.Equal(blocks.Select(block => block.Text), paragraphs.Select(p => p.Text));

        FreezeArtifact.AssertJson(ManifestDirectory, "SRC-003.conversion-manifest.v1.json", new
        {
            artifactKind = "a99_source_regeneration_manifest",
            schemaVersion = "a99-source-regeneration-manifest-v1",
            documentId = "SRC-003",
            modelCalls = 0,
            providerCalls = 0,
            originalPdf = new { path = Src003Pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Src003Pdf)) },
            regeneratedDocx = new
            {
                path = Src003Docx,
                sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Src003Docx)),
                paragraphs = paragraphs.Count,
            },
            replacesInPlace = new { path = Src003Docx, sha256 = Src003ReplacedSha256, producedBy = "pdf2docx", defect = "chapter and article headings merged into neighbouring text" },
            converter = "tests/DocxHeaderExtractor.Tests/PdfLineDocxConverter.cs (the rules recorded in DOC-0258.conversion-manifest.v1.json)",
            approvedBy = "USER",
            approvedAt = "2026-09-24",
        });
    }

    [Fact]
    public void Doc0264_docx_is_libreoffice_reading_the_original_doc()
    {
        var paragraphs = Doc0258SourceRegenerationTests.ReadParagraphs(TestRepository.Path(Doc0264Docx));
        Assert.True(paragraphs.Count > 1000, $"{paragraphs.Count} paragraphs: still the one-paragraph text dump?");

        FreezeArtifact.AssertJson(ManifestDirectory, "DOC-0264.conversion-manifest.v1.json", new
        {
            artifactKind = "a99_source_regeneration_manifest",
            schemaVersion = "a99-source-regeneration-manifest-v1",
            documentId = "DOC-0264",
            modelCalls = 0,
            providerCalls = 0,
            originalDocument = new { path = Doc0264Original, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Doc0264Original)), format = "Word 97-2003 (.doc)" },
            regeneratedDocx = new
            {
                path = Doc0264Docx,
                sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Doc0264Docx)),
                paragraphs = paragraphs.Count,
            },
            replacesInPlace = new { path = Doc0264Docx, sha256 = Doc0264ReplacedSha256, producedBy = "legacy DOC text extraction", defect = "the whole law in one paragraph" },
            converter = new
            {
                tool = LibreOffice,
                command = "soffice --headless --norestore --convert-to docx <original .doc>",
                carries = "the .doc's own paragraphs, runs and styles as LibreOffice reads them; nothing added",
                bytesAreNotReproducible = "the package embeds conversion timestamps; the committed file is the authority and its sha256 is pinned here",
            },
            approvedBy = "USER",
            approvedAt = "2026-09-24",
        });
    }

    [Fact]
    public void Src029_source_is_the_original_pdf_and_the_damaged_docx_is_gone()
    {
        Assert.False(File.Exists(TestRepository.Path(Src029RemovedDocx)));

        FreezeArtifact.AssertJson(ManifestDirectory, "SRC-029.conversion-manifest.v1.json", new
        {
            artifactKind = "a99_source_regeneration_manifest",
            schemaVersion = "a99-source-regeneration-manifest-v1",
            documentId = "SRC-029",
            modelCalls = 0,
            providerCalls = 0,
            source = new { path = Src029Pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Src029Pdf)), mediaType = "PDF" },
            removed = new
            {
                path = Src029RemovedDocx,
                sha256 = Src029RemovedSha256,
                producedBy = "pdf2docx",
                defect = "the ITP margin column's clause names interleaved with clause bodies "
                    + "(\"31. Evaluation of 31.1 The Employer's evaluation ... Technical out as ...\")",
            },
            whyNotReconverted = "PdfLineDocxConverter splits small capitals (\"S ECTION I - I NSTRUCTIONS\") and "
                + "interleaves the margin column the same way; the PDF lane's visual-line segments keep each "
                + "margin line as its own atom (\"22. Submission,\" / \"Sealing and\" / \"Marking of\" / \"Proposals\")",
            approvedBy = "USER",
            approvedAt = "2026-09-24",
        });
    }

    [Fact]
    public void Repoint_authored_gold_to_the_fixed_sources()
    {
        if (Environment.GetEnvironmentVariable("A99_SCALEUP_REPOINT_GOLD") != "1") return;

        Repoint("DOC-0264", Doc0264ReplacedSha256, Doc0264Docx, "DOCX");
        Repoint("SRC-003", Src003ReplacedSha256, Src003Docx, "DOCX");
        Repoint("SRC-029", Src029RemovedSha256, Src029Pdf, "PDF");
    }

    private static void Repoint(string id, string expectedOldSha, string newPath, string mediaType)
    {
        var goldPath = TestRepository.Path($"eval/a99-closed-loop/gold/{id}.gold.json");
        var goldText = File.ReadAllText(goldPath);
        var gold = JsonNode.Parse(goldText)!;
        var source = gold["source"]!;
        Assert.Equal(expectedOldSha, source["sourceSha256"]!.GetValue<string>());
        Assert.Null(gold["occurrence"]);

        source["fileName"] = Path.GetFileName(newPath);
        source["mediaType"] = mediaType;
        source["sourcePath"] = newPath;
        source["sourceSha256"] = CanonicalArtifactHash.OfBytes(TestRepository.Path(newPath));
        var provenance = gold["provenance"]!.AsArray();
        provenance.Add(new JsonObject
        {
            ["path"] = $"gold-correction:{id}:source-fixed-in-place:2026-09-24",
            ["sha256"] = CanonicalArtifactHash.OfText(goldText),
            ["role"] = "GOLD_CORRECTION_PREDECESSOR",
        });
        provenance.Add(new JsonObject
        {
            ["path"] = Manifest(id),
            ["sha256"] = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Manifest(id))),
            ["role"] = "SOURCE_REGENERATION",
        });

        File.WriteAllBytes(goldPath, new UTF8Encoding(false).GetBytes(
            gold.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n")));
    }
}
