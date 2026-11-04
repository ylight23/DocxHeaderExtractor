using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using OxParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using PdfWord = UglyToad.PdfPig.Content.Word;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Rebuilds a DOCX from a PDF by its physical lines, keeping the layout the PDF shows: the words with their bold and underline, the font family and size,
/// the alignment of each paragraph (left, centred, justified), the space between paragraphs, and the horizontal rules as bordered empty paragraphs.
/// <para>
/// It writes no heading styles and no outline levels, so the source does not pre-answer the question it is later evaluated on. Paragraph boundaries and
/// layout come only from geometry and typography the PDF itself shows.
/// </para>
/// </summary>
internal static class PdfLineDocxConverter
{
    internal sealed record Word(string Text, bool Bold, bool Underline, string Font, double Size);

    internal sealed record Line(int Page, double CenterY, double Left, double Right, bool Bold, IReadOnlyList<Word> Words);

    /// <summary>A horizontal rule drawn in the PDF: a thin filled shape across the text block.</summary>
    internal sealed record Rule(int Page, double CenterY);

    internal sealed record Layout(IReadOnlyList<Line> Lines, IReadOnlyList<Rule> Rules);

    internal sealed record Block(int Page, IReadOnlyList<Line> Lines, double SpaceBefore)
    {
        public string Text => string.Join(" ", Lines.SelectMany(line => line.Words).Select(word => word.Text));
    }

    /// <summary>One item in reading order: a paragraph of text lines, or a rule.</summary>
    internal abstract record Item;
    internal sealed record TextParagraph(Block Block, string Alignment) : Item;
    internal sealed record RuleItem(Rule Rule) : Item;

    /// <summary>Extra vertical space above a line that marks a new paragraph (normal leading is ~13.5pt).</summary>
    private const double ParagraphGap = 20.0;
    private const double NormalLeading = 13.8;

    /// <summary>A line ending this far short of the text block's right edge closes its paragraph.</summary>
    private const double ShortLineMargin = 60.0;

    public static Layout ReadLayout(string pdfPath)
    {
        using var document = PdfDocument.Open(pdfPath);
        var lines = new List<Line>();
        var rules = new List<Rule>();
        foreach (var page in document.GetPages())
        {
            var words = page.GetWords().Where(word => !string.IsNullOrWhiteSpace(word.Text)).ToArray();
            if (words.Length == 0) continue;
            var tolerance = Median(words.Select(word => word.BoundingBox.Height)) * 0.5;
            var segments = page.Paths.Select(path => path.GetBoundingRectangle())
                .Where(box => box is not null && box.Value.Height <= 2.0 && box.Value.Width >= 2.0)
                .Select(box => box!.Value).ToArray();

            var groups = new List<(double Anchor, List<PdfWord> Words)>();
            foreach (var word in words.OrderByDescending(Center).ThenBy(word => word.BoundingBox.Left))
            {
                var open = groups.Count == 0 ? default : groups[^1];
                if (open.Words is not null && Math.Abs(open.Anchor - Center(word)) <= tolerance)
                    open.Words.Add(word);
                else
                    groups.Add((Center(word), [word]));
            }

            // The page's text block: where its lines reach. A segment across most of it is a rule; a short one is an underline (if under a word).
            var ordered = groups.Select(group => group.Words.OrderBy(word => word.BoundingBox.Left).ToArray()).ToList();
            var left = ordered.Count == 0 ? 0 : ordered.Min(group => group.Min(word => word.BoundingBox.Left));
            var right = ordered.Count == 0 ? 0 : Percentile(ordered.Select(group => group.Max(word => word.BoundingBox.Right)), 0.9);
            var blockWidth = Math.Max(1, right - left);
            foreach (var box in segments.Where(box => box.Width >= blockWidth * 0.4))
                rules.Add(new Rule(page.Number, (box.Top + box.Bottom) / 2));
            var underlines = segments.Where(box => box.Width < blockWidth * 0.4).ToArray();

            foreach (var group in ordered)
            {
                var letters = group.Sum(word => word.Letters.Count);
                var boldLetters = group.Where(IsBold).Sum(word => word.Letters.Count);
                lines.Add(new Line(
                    page.Number,
                    group.Average(Center),
                    group.Min(word => word.BoundingBox.Left),
                    group.Max(word => word.BoundingBox.Right),
                    boldLetters * 2 >= letters,
                    group.Select(word => new Word(word.Text, IsBold(word), IsUnderlined(word, underlines), FontFamily(word), FontSize(word))).ToArray()));
            }
        }
        return new Layout(lines, rules);
    }

    /// <summary>A word is underlined when a short segment lies just under it and across most of its width.</summary>
    private static bool IsUnderlined(PdfWord word, IReadOnlyList<PdfRectangle> underlines)
    {
        var width = word.BoundingBox.Right - word.BoundingBox.Left;
        return width > 0 && underlines.Any(box =>
            box.Top >= word.BoundingBox.Bottom - 2 && box.Top <= word.BoundingBox.Bottom + 4
            && Math.Min(box.Right, word.BoundingBox.Right) - Math.Max(box.Left, word.BoundingBox.Left) >= 0.8 * width);
    }

    /// <summary>Lines and rules in reading order: pages in order, top to bottom. A rule comes before the first line below it.</summary>
    public static IReadOnlyList<Item> BuildItems(Layout layout)
    {
        var bodyLeft = layout.Lines.Min(line => line.Left);
        var bodyRight = Percentile(layout.Lines.Select(line => line.Right), 0.9);
        var pageCenter = (bodyLeft + bodyRight) / 2;

        var items = new List<Item>();
        var pending = new List<Line>();
        Line? lastEmitted = null;
        var rules = layout.Rules.OrderBy(rule => rule.Page).ThenByDescending(rule => rule.CenterY).ToList();

        void Emit()
        {
            if (pending.Count == 0) return;
            var first = pending[0];
            items.Add(new TextParagraph(new Block(first.Page, pending.ToArray(), SpaceBefore(lastEmitted, first)), Alignment(pending, bodyLeft, bodyRight, pageCenter)));
            lastEmitted = pending[^1];
            pending.Clear();
        }

        foreach (var line in layout.Lines.OrderBy(line => line.Page).ThenByDescending(line => line.CenterY))
        {
            while (rules.Count > 0 && (rules[0].Page < line.Page || (rules[0].Page == line.Page && rules[0].CenterY > line.CenterY)))
            {
                Emit();
                items.Add(new RuleItem(rules[0]));
                rules.RemoveAt(0);
            }
            if (pending.Count > 0 && StartsNewParagraph(pending[^1], line, bodyLeft, bodyRight, pageCenter)) Emit();
            pending.Add(line);
        }
        Emit();
        foreach (var rule in rules) items.Add(new RuleItem(rule));
        return items;
    }

    public static void WriteDocx(IReadOnlyList<Item> items, string docxPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(docxPath)!);
        using var document = WordprocessingDocument.Create(docxPath, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new Body();
        foreach (var item in items)
        {
            switch (item)
            {
                case RuleItem:
                    body.AppendChild(new OxParagraph(new ParagraphProperties(
                        new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 6, Space = 1, Color = "000000" }),
                        new SpacingBetweenLines { Before = "0", After = "0" })));
                    break;
                case TextParagraph paragraph:
                    body.AppendChild(WriteParagraph(paragraph));
                    break;
            }
        }
        main.Document = new Document(body);
        main.Document.Save();
    }

    private static OxParagraph WriteParagraph(TextParagraph paragraph)
    {
        var block = paragraph.Block;
        var before = (int)Math.Round(block.SpaceBefore * 20);
        var result = new OxParagraph(new ParagraphProperties(
            new SpacingBetweenLines { Before = before.ToString(), After = "0" },
            new Justification { Val = paragraph.Alignment switch { "center" => JustificationValues.Center, "both" => JustificationValues.Both, _ => JustificationValues.Left } }));
        var words = block.Lines.SelectMany(line => line.Words).ToArray();
        var font = Dominant(words.Select(word => word.Font));
        var size = Dominant(words.Select(word => word.Size.ToString("0.0")));
        var runs = new List<(string Text, bool Bold, bool Underline)>();
        foreach (var word in words)
        {
            if (runs.Count > 0 && runs[^1].Bold == word.Bold && runs[^1].Underline == word.Underline)
                runs[^1] = (runs[^1].Text + " " + word.Text, word.Bold, word.Underline);
            else
                runs.Add((runs.Count == 0 ? word.Text : " " + word.Text, word.Bold, word.Underline));
        }
        foreach (var (text, bold, underline) in runs)
        {
            var run = new Run();
            var runProperties = new RunProperties(new RunFonts { Ascii = font, HighAnsi = font, EastAsia = font, ComplexScript = font });
            if (bold) runProperties.AppendChild(new Bold());
            if (underline) runProperties.AppendChild(new Underline { Val = UnderlineValues.Single });
            runProperties.AppendChild(new FontSize { Val = ((int)Math.Round(double.Parse(size) * 2)).ToString() });
            run.AppendChild(runProperties);
            run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            result.AppendChild(run);
        }
        return result;
    }

    private static string Alignment(IReadOnlyList<Line> lines, double bodyLeft, double bodyRight, double pageCenter)
    {
        if (lines.All(line => IsCentered(line, bodyLeft, bodyRight, pageCenter))) return "center";
        if (lines.Count > 1 && lines.Take(lines.Count - 1).All(line => line.Right >= bodyRight - 4 && line.Left <= bodyLeft + 4)) return "both";
        return "left";
    }

    private static double SpaceBefore(Line? previous, Line first)
    {
        if (previous is null || previous.Page != first.Page) return 0;
        var gap = previous.CenterY - first.CenterY;
        return gap > NormalLeading ? Math.Round(gap - NormalLeading, 1) : 0;
    }

    private static string Dominant(IEnumerable<string> values) =>
        values.GroupBy(value => value).OrderByDescending(group => group.Count()).First().Key;

    private static bool StartsNewParagraph(Line previous, Line line, double bodyLeft, double bodyRight, double pageCenter)
    {
        if (line.Page != previous.Page) return true;
        if (previous.CenterY - line.CenterY > ParagraphGap) return true;
        if (line.Bold != previous.Bold) return true;
        if (IsListItem(line)) return true;

        // A centred title wrapped over two lines is one paragraph, even though each line is short.
        if (IsCentered(previous, bodyLeft, bodyRight, pageCenter) && IsCentered(line, bodyLeft, bodyRight, pageCenter)) return false;

        return previous.Right < bodyRight - ShortLineMargin;
    }

    private static bool IsListItem(Line line)
    {
        var first = line.Words[0].Text;
        return first is "−" or "–" or "-" or "•" or "o" || first.StartsWith('−') || first.StartsWith('–');
    }

    /// <summary>A line centred on the page and not spanning the whole text block (a justified line that happens to be centred is not a centred line).</summary>
    private static bool IsCentered(Line line, double bodyLeft, double bodyRight, double pageCenter) =>
        Math.Abs((line.Left + line.Right) / 2 - pageCenter) < 6 && line.Right - line.Left < (bodyRight - bodyLeft) * 0.97;

    private static bool IsBold(PdfWord word) =>
        word.Letters.Count > 0 && word.Letters.All(letter =>
            (letter.FontName ?? string.Empty).Contains("Bold", StringComparison.OrdinalIgnoreCase));

    /// <summary>The font family without its subset prefix ("BCDEEE+Aptos" becomes "Aptos"; "TimesNewRomanPSMT" becomes "Times New Roman").</summary>
    private static string FontFamily(PdfWord word)
    {
        var name = word.Letters.Count == 0 ? "" : word.Letters[0].FontName ?? "";
        var plus = name.IndexOf('+');
        if (plus >= 0) name = name[(plus + 1)..];
        return name.StartsWith("TimesNewRoman", StringComparison.OrdinalIgnoreCase) ? "Times New Roman" : name;
    }

    private static double FontSize(PdfWord word) =>
        word.Letters.Count == 0 ? 12 : Math.Round(word.Letters.Average(letter => letter.FontSize) * 2) / 2;

    private static double Center(PdfWord word) => (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2;

    private static double Median(IEnumerable<double> values) => Percentile(values, 0.5);

    private static double Percentile(IEnumerable<double> values, double fraction)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        return ordered.Length == 0 ? 0 : ordered[(int)Math.Floor((ordered.Length - 1) * fraction)];
    }
}
