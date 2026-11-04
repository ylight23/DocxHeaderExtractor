using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The two standard test documents: 074 and 075 rebuilt from their PDFs by <see cref="PdfLineDocxConverter"/>, which keeps the PDF's layout. The pipeline
/// tests use these two files, not the older converted copies. Regenerate with A99_REGENERATE_STANDARD=1; otherwise each committed file must equal the
/// converter's output for its PDF, word for word and paragraph for paragraph.
/// </summary>
public sealed class StandardDocumentsTests
{
    public static TheoryData<string> Stems => new()
    {
        "074_FORTIS_GC_Minutes_Nov27_2024",
        "075_FORTIS_GC_Minutes_Nov21_2024",
    };

    internal static string PdfPath(string stem) => $"todo10_8/heading_corpus_100/05_bien_ban_hop/{stem}.pdf";
    internal static string DocxPath(string stem) => $"todo10_8/standard-docx-v3/05_bien_ban_hop/{stem}.docx";

    [Theory]
    [MemberData(nameof(Stems))]
    public void The_standard_docx_is_exactly_what_the_converter_writes_from_its_pdf(string stem)
    {
        var items = PdfLineDocxConverter.BuildItems(PdfLineDocxConverter.ReadLayout(TestRepository.Path(PdfPath(stem))));
        if (Environment.GetEnvironmentVariable("A99_REGENERATE_STANDARD") == "1")
            PdfLineDocxConverter.WriteDocx(items, TestRepository.Path(DocxPath(stem)));

        var expected = items.Select(item => item is PdfLineDocxConverter.TextParagraph p ? p.Block.Text : "").ToList();
        using var document = WordprocessingDocument.Open(TestRepository.Path(DocxPath(stem)), false);
        var actual = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().Select(p => p.InnerText).ToList();
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Stems))]
    public void Every_word_of_the_pdf_is_in_the_standard_docx(string stem)
    {
        var layout = PdfLineDocxConverter.ReadLayout(TestRepository.Path(PdfPath(stem)));
        var fromPdf = layout.Lines.SelectMany(line => line.Words).Select(word => word.Text).GroupBy(text => text).ToDictionary(g => g.Key, g => g.Count());
        using var document = WordprocessingDocument.Open(TestRepository.Path(DocxPath(stem)), false);
        var fromDocx = document.MainDocumentPart!.Document!.Body!.Descendants<Text>().SelectMany(t => t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .GroupBy(text => text).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(fromPdf.OrderBy(kv => kv.Key), fromDocx.OrderBy(kv => kv.Key));
    }

    [Theory]
    [MemberData(nameof(Stems))]
    public void The_rules_and_underlines_of_the_pdf_are_in_the_standard_docx(string stem)
    {
        var layout = PdfLineDocxConverter.ReadLayout(TestRepository.Path(PdfPath(stem)));
        using var document = WordprocessingDocument.Open(TestRepository.Path(DocxPath(stem)), false);
        var body = document.MainDocumentPart!.Document!.Body!;

        Assert.Equal(layout.Rules.Count, body.Descendants<BottomBorder>().Count());
        var underlinedInPdf = layout.Lines.Sum(line => line.Words.Count(word => word.Underline));
        var underlinedInDocx = body.Descendants<Run>().Where(run => run.RunProperties?.Underline is not null)
            .Sum(run => run.Descendants<Text>().Sum(t => t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length));
        Assert.Equal(underlinedInPdf, underlinedInDocx);
    }
}
