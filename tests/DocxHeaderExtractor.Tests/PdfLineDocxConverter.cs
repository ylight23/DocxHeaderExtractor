using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Rebuilds a DOCX from a PDF by its physical lines, for corpus sources whose earlier pdf2docx
/// conversion merged standalone label lines ("Africa", "DAY 1: ...", an organisation name) into the
/// text that follows them.
/// <para>
/// Faithful, not annotated: it carries the PDF's words and its bold runs and nothing else - no
/// heading styles, no outline levels - so the source does not pre-answer the question it is later
/// evaluated on. Paragraph boundaries come only from geometry and typography the PDF itself shows.
/// </para>
/// </summary>
internal static class PdfLineDocxConverter
{
    internal sealed record Line(int Page, double CenterY, double Left, double Right, bool Bold,
        IReadOnlyList<(string Text, bool Bold)> Words);

    internal sealed record Block(int Page, IReadOnlyList<(string Text, bool Bold)> Words)
    {
        public string Text => string.Join(" ", Words.Select(word => word.Text));
    }

    /// <summary>Extra vertical space above a line that marks a new paragraph (normal leading is ~13.5pt).</summary>
    private const double ParagraphGap = 20.0;

    /// <summary>A line ending this far short of the text block's right edge closes its paragraph.</summary>
    private const double ShortLineMargin = 60.0;

    public static IReadOnlyList<Line> ReadLines(string pdfPath)
    {
        using var document = PdfDocument.Open(pdfPath);
        var lines = new List<Line>();
        foreach (var page in document.GetPages())
        {
            var words = page.GetWords().Where(word => !string.IsNullOrWhiteSpace(word.Text)).ToArray();
            if (words.Length == 0) continue;
            var tolerance = Median(words.Select(word => word.BoundingBox.Height)) * 0.5;

            var groups = new List<(double Anchor, List<Word> Words)>();
            foreach (var word in words.OrderByDescending(Center).ThenBy(word => word.BoundingBox.Left))
            {
                var open = groups.Count == 0 ? default : groups[^1];
                if (open.Words is not null && Math.Abs(open.Anchor - Center(word)) <= tolerance)
                    open.Words.Add(word);
                else
                    groups.Add((Center(word), [word]));
            }

            foreach (var (_, group) in groups)
            {
                var ordered = group.OrderBy(word => word.BoundingBox.Left).ToArray();
                var letters = ordered.Sum(word => word.Letters.Count);
                var boldLetters = ordered.Where(IsBold).Sum(word => word.Letters.Count);
                lines.Add(new Line(
                    page.Number,
                    ordered.Average(Center),
                    ordered.Min(word => word.BoundingBox.Left),
                    ordered.Max(word => word.BoundingBox.Right),
                    boldLetters * 2 >= letters,
                    ordered.Select(word => (word.Text, IsBold(word))).ToArray()));
            }
        }
        return lines;
    }

    public static IReadOnlyList<Block> BuildBlocks(IReadOnlyList<Line> lines)
    {
        var bodyLeft = lines.Min(line => line.Left);
        var bodyRight = Percentile(lines.Select(line => line.Right), 0.9);
        var pageCenter = (bodyLeft + bodyRight) / 2;

        var blocks = new List<Block>();
        List<(string, bool)>? words = null;
        Line? previous = null;
        foreach (var line in lines)
        {
            if (previous is null || words is null || StartsNewParagraph(previous, line, bodyLeft, bodyRight, pageCenter))
            {
                if (words is not null) blocks.Add(new Block(previous!.Page, words));
                words = [];
            }
            words.AddRange(line.Words);
            previous = line;
        }
        if (words is not null && previous is not null) blocks.Add(new Block(previous.Page, words));
        return blocks;
    }

    public static void WriteDocx(IReadOnlyList<Block> blocks, string docxPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(docxPath)!);
        using var document = WordprocessingDocument.Create(docxPath, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new Body();
        foreach (var block in blocks)
        {
            var paragraph = new Paragraph();
            var runs = new List<(string Text, bool Bold)>();
            foreach (var (text, bold) in block.Words)
            {
                if (runs.Count > 0 && runs[^1].Bold == bold)
                    runs[^1] = (runs[^1].Text + " " + text, bold);
                else
                    runs.Add((runs.Count == 0 ? text : " " + text, bold));
            }
            foreach (var (text, bold) in runs)
            {
                var run = new Run();
                if (bold) run.AppendChild(new RunProperties(new Bold()));
                run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
                paragraph.AppendChild(run);
            }
            body.AppendChild(paragraph);
        }
        main.Document = new Document(body);
        main.Document.Save();
    }

    private static bool StartsNewParagraph(Line previous, Line line, double bodyLeft, double bodyRight, double pageCenter)
    {
        if (line.Page != previous.Page) return true;
        if (previous.CenterY - line.CenterY > ParagraphGap) return true;
        if (line.Bold != previous.Bold) return true;
        if (IsListItem(line)) return true;

        // A centred title wrapped over two lines is one paragraph, even though each line is short.
        if (IsCentered(previous, bodyLeft, pageCenter) && IsCentered(line, bodyLeft, pageCenter)) return false;

        return previous.Right < bodyRight - ShortLineMargin;
    }

    private static bool IsListItem(Line line)
    {
        var first = line.Words[0].Text;
        return first is "−" or "–" or "-" or "•" or "o" || first.StartsWith('−') || first.StartsWith('–');
    }

    private static bool IsCentered(Line line, double bodyLeft, double pageCenter) =>
        line.Left > bodyLeft + 18 && Math.Abs((line.Left + line.Right) / 2 - pageCenter) < 6;

    private static bool IsBold(Word word) =>
        word.Letters.Count > 0 && word.Letters.All(letter =>
            (letter.FontName ?? string.Empty).Contains("Bold", StringComparison.OrdinalIgnoreCase));

    private static double Center(Word word) => (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2;

    private static double Median(IEnumerable<double> values) => Percentile(values, 0.5);

    private static double Percentile(IEnumerable<double> values, double fraction)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        return ordered.Length == 0 ? 0 : ordered[(int)Math.Floor((ordered.Length - 1) * fraction)];
    }
}
