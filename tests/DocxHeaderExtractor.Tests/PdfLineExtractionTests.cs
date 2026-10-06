using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Glyph = DocxHeaderExtractor.DocumentProcessing.Pipeline.PdfVisualLineBucket.Glyph;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Visual-line reconstruction: which glyphs a PDF puts on one line, and how this repository decides.
/// <para>
/// The geometry cases below are written as coordinates rather than as documents on purpose. A rule
/// justified only by the document that exposed it is a rule fitted to that document; these say what
/// the rule claims about typography, and a real PDF is then measured against the claim rather than
/// being the claim.
/// </para>
/// </summary>
public sealed class PdfLineExtractionTests
{
    private const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const int AuthoritativeTotal = 41;
    private const string Artifacts = "eval/a99-closed-loop/representation";

    // 11pt body text: cap box roughly 7.3 high, single leading 13.4, a period barely 1.7.
    private const double Body = 11.0;

    // ---- the rule, stated as geometry ---------------------------------------------------------

    [Fact]
    public void A_punctuation_box_inside_a_text_line_belongs_to_that_line()
    {
        // The defect this exists for. A period rests on the baseline and rises barely above it, so
        // its midpoint sits a third of a cap height below the midpoint of the text beside it.
        var line = Line(Glyph(baseline: 425.5, top: 435.02, bottom: 425.51));
        var period = Glyph(baseline: 425.5, top: 429.07, bottom: 427.41, fontSize: Body);

        Assert.True(line.Accepts(period));
    }

    [Fact]
    public void Two_ordinary_lines_stay_apart_even_when_their_boxes_touch()
    {
        // Set tightly enough that a descender on the upper line reaches into the ascenders below.
        // The boxes overlap; the baselines are a full leading apart, and that is what decides.
        var upper = Line(Glyph(baseline: 430.0, top: 437.3, bottom: 427.0));
        var lower = Glyph(baseline: 416.6, top: 428.0, bottom: 413.6);

        Assert.False(upper.Accepts(lower));
    }

    [Fact]
    public void A_superscript_stays_on_the_line_it_is_raised_from()
    {
        // Raised by about a third of an em and set smaller. Baseline alone would call it a new
        // line; it is inside the line's band, and the tolerance is scaled to the line, not to it.
        var line = Line(Glyph(baseline: 300.0, top: 307.3, bottom: 300.0));
        var superscript = Glyph(baseline: 303.6, top: 308.3, bottom: 303.6, fontSize: 7.0);
        var subscript = Glyph(baseline: 297.2, top: 301.9, bottom: 297.2, fontSize: 7.0);

        Assert.True(line.Accepts(superscript));
        Assert.True(Line(Glyph(baseline: 300.0, top: 307.3, bottom: 300.0)).Accepts(subscript));
    }

    [Fact]
    public void A_line_opened_by_a_raised_glyph_still_takes_its_baseline_from_the_body_text()
    {
        // Glyphs arrive by baseline, so a superscript can open a line. If it kept that raised
        // baseline as the line's own, the body text would join and then the next line would look
        // only half a leading away. The line takes its baseline from its largest glyph instead.
        var line = new PdfVisualLineBucket();
        line.Add(Glyph(baseline: 303.6, top: 308.3, bottom: 303.6, fontSize: 7.0));
        var body = Glyph(baseline: 300.0, top: 307.3, bottom: 300.0);
        Assert.True(line.Accepts(body));
        line.Add(body);

        Assert.False(line.Accepts(Glyph(baseline: 286.6, top: 293.9, bottom: 286.6)));
    }

    [Fact]
    public void A_taller_font_on_the_next_line_does_not_reach_back_into_this_one()
    {
        // A heading below body text: its box is tall enough to overlap the line above, and its own
        // height makes the overlap a large fraction of the smaller box. Baselines keep them apart.
        var body = Line(Glyph(baseline: 400.0, top: 407.3, bottom: 400.0));
        var heading = Glyph(baseline: 385.0, top: 398.0, bottom: 385.0, fontSize: 18.0);

        Assert.False(body.Accepts(heading));
    }

    [Fact]
    public void Splitting_is_a_pure_function_of_the_order_it_is_given()
    {
        var glyphs = new[]
        {
            Glyph(baseline: 430.0, top: 437.3, bottom: 430.0),
            Glyph(baseline: 430.0, top: 431.7, bottom: 430.0, fontSize: Body),   // a period
            Glyph(baseline: 416.6, top: 423.9, bottom: 416.6),
        };

        var first = PdfVisualLineBucket.Split(glyphs, glyph => glyph);
        var second = PdfVisualLineBucket.Split(glyphs, glyph => glyph);

        Assert.Equal([2, 1], first.Select(line => line.Count).ToArray());
        Assert.Equal(first.Select(line => line.ToArray()), second.Select(line => line.ToArray()));
    }

    // ---- where a row stops being one region ----------------------------------------------------

    [Fact]
    public void A_corridor_that_the_rows_around_it_keep_open_divides_a_row()
    {
        // A two-sided masthead. Two rows, text on both sides of the same whitespace, and nothing
        // crossing it - which is what makes it a corridor rather than a wide space.
        var rows = new[]
        {
            Row((60, 120), (300, 520)),
            Row((60, 110), (300, 500)),
        };

        var cuts = PdfVisualRegion.Cuts(rows, wordGap: 3);

        Assert.Equal([0], cuts[0]);
        Assert.Equal([0], cuts[1]);
    }

    [Fact]
    public void Ordinary_word_spacing_never_divides_a_row()
    {
        var rows = new[]
        {
            Row((60, 100), (104, 150), (154, 200), (204, 260)),
            Row((60, 110), (114, 170), (174, 230)),
        };

        Assert.All(PdfVisualRegion.Cuts(rows, wordGap: 4), row => Assert.Empty(row));
    }

    [Fact]
    public void A_gap_no_neighbouring_row_agrees_with_is_left_alone()
    {
        // Tab-aligned metadata between two ordinary lines of prose. The gap is wide, and the rows
        // above and below run straight through where it sits, so there is no region boundary here.
        var rows = new[]
        {
            Row((60, 500)),
            Row((60, 120), (300, 520)),
            Row((60, 500)),
        };

        Assert.All(PdfVisualRegion.Cuts(rows, wordGap: 3), row => Assert.Empty(row));
    }

    [Fact]
    public void Two_columns_divide_every_row_that_spans_them()
    {
        var rows = Enumerable.Range(0, 6).Select(_ => Row((60, 280), (320, 540))).ToArray();
        var cuts = PdfVisualRegion.Cuts(rows, wordGap: 3);

        Assert.All(cuts, row => Assert.Equal([0], row));
    }

    [Fact]
    public void A_centred_heading_is_one_region()
    {
        // Wide margins either side, and no text beyond them. A margin is not a corridor: there is
        // nothing on the far side of it for this row to be separate from.
        var rows = new[]
        {
            Row((60, 520)),
            Row((200, 380)),
            Row((60, 520)),
        };

        Assert.All(PdfVisualRegion.Cuts(rows, wordGap: 3), row => Assert.Empty(row));
        Assert.Equal(0, PdfVisualRegion.ClearWidthAround(Row((200, 380)), middle: 100));
    }

    [Fact]
    public void A_table_row_is_divided_where_its_columns_are()
    {
        var rows = new[]
        {
            Row((60, 140), (200, 300), (360, 460)),
            Row((60, 130), (200, 290), (360, 450)),
            Row((60, 135), (200, 295), (360, 455)),
        };

        Assert.All(PdfVisualRegion.Cuts(rows, wordGap: 4), row => Assert.Equal([0, 1], row));
    }

    [Fact]
    public void Cutting_is_a_pure_function_of_its_input()
    {
        var rows = new[]
        {
            Row((60, 120), (300, 520)),
            Row((60, 110), (300, 500)),
            Row((60, 500)),
        };

        Assert.Equal(
            PdfVisualRegion.Cuts(rows, wordGap: 3).Select(row => row.ToArray()),
            PdfVisualRegion.Cuts(rows, wordGap: 3).Select(row => row.ToArray()));
        Assert.Empty(PdfVisualRegion.Cuts(rows, wordGap: 0)[0]);
    }

    [Fact]
    public void A_raised_glyph_stays_in_the_segment_it_belongs_to()
    {
        // Segmentation is horizontal and the vertical rule is untouched, so a superscript sitting
        // inside one region's x-range must travel with that region and not with the other.
        var rows = new[]
        {
            Row((60, 120), (121, 126), (300, 520)),
            Row((60, 110), (300, 500)),
        };

        var cuts = PdfVisualRegion.Cuts(rows, wordGap: 3);

        Assert.Equal([1], cuts[0]);
    }

    private static IReadOnlyList<PdfVisualRegion.Box> Row(params (double Left, double Right)[] boxes) =>
        boxes.Select(box => new PdfVisualRegion.Box(box.Left, box.Right)).ToArray();

    // ---- the rule, measured on a real document ------------------------------------------------

    [Fact]
    public void The_adjudicated_layout_cases_come_out_as_a_reader_would_read_them()
    {
        // The rule is judged against rows a person looked at, held in their own artifact so the
        // rule cannot be written to agree with them. Each case is re-derived from its document
        // rather than compared with a stored outcome.
        using var adjudication = JsonDocument.Parse(File.ReadAllText(
            System.IO.Path.Combine(TestRepository.Root(),
                $"{Artifacts}/visual-line-segment-adjudication.v1.json"
                    .Replace('/', System.IO.Path.DirectorySeparatorChar))));

        var results = new List<(string Case, string Expected, string Actual)>();
        foreach (var item in adjudication.RootElement.GetProperty("cases").EnumerateArray())
        {
            var caseId = item.GetProperty("caseId").GetString()!;
            var expected = item.GetProperty("expected").GetString()!;
            var parts = caseId.Split('|');
            var row = FindRow(parts[0], int.Parse(parts[1][1..]), double.Parse(parts[2]));
            Assert.True(row is not null, $"{caseId} no longer names a row in its document");
            results.Add((caseId, expected, row!.Count > 1 ? "DIVIDED" : "WHOLE"));
        }

        // A case the reader marked as two regions that the rule leaves whole is a miss, and it is
        // declared as one in the artifact. Dividing a row the reader called single is the failure
        // this gate exists for, because an atom that splits one region cannot be bound to it.
        var falseSplits = results
            .Where(item => item.Expected == "WHOLE" && item.Actual == "DIVIDED")
            .ToArray();
        var missed = results
            .Where(item => item.Expected == "DIVIDED" && item.Actual == "WHOLE")
            .ToArray();
        var declaredMisses = results.Count(item => item.Expected == "UNDER_SPLIT");

        Assert.Empty(falseSplits.Select(item => item.Case));
        Assert.Empty(missed.Select(item => item.Case));
        Assert.All(results.Where(item => item.Expected == "UNDER_SPLIT"),
            item => Assert.Equal("WHOLE", item.Actual));
        Assert.True(declaredMisses > 0, "the artifact should keep recording what this rule misses");
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>Both candidate reconstructions. Neither is active; both must hold the invariants.</summary>
    private static PdfVisualLineBucket Line(Glyph first)
    {
        var bucket = new PdfVisualLineBucket();
        bucket.Add(first);
        return bucket;
    }

    private static Glyph Glyph(double baseline, double top, double bottom, double fontSize = Body) =>
        new(baseline, top, bottom, fontSize);

    private static string Path_ => System.IO.Path.Combine(
        TestRepository.Root(), Pdf.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static IReadOnlyList<Letter> Letters()
    {
        using var document = PdfDocument.Open(Path_);
        return document.GetPages()
            .SelectMany(page => page.Letters.Where(letter => !string.IsNullOrWhiteSpace(letter.Value)))
            .ToArray();
    }

    private static IReadOnlyList<PdfLine> Lines()
    {
        using var document = PdfDocument.Open(Path_);
        return PdfLineExtraction.ExtractLines(document);
    }

    /// <summary>One extracted glyph, identified by what it is and where it was drawn.</summary>
    private static string Atom(Letter letter) =>
        $"{letter.Value}|{letter.BoundingBox.Left:F3}|{letter.BoundingBox.Bottom:F3}|{letter.BoundingBox.Top:F3}";

    private static readonly IComparer<string> AtomOrder = StringComparer.Ordinal;

    private static string[] Atoms(IReadOnlyList<PdfLine> lines) =>
        lines
            .SelectMany(line => line.Projection.SpanMap.Select(entry =>
                $"{line.Projection.RawParserText.Substring(entry.RawStart, entry.RawLength)}" +
                $"|{entry.Left:F3}|{entry.Bottom:F3}|{entry.Top:F3}"))
            .OrderBy(atom => atom, AtomOrder)
            .ToArray();

    /// <summary>
    /// How much room the baseline tolerance actually had on this document.
    /// <para>
    /// A threshold is only meaningful next to the distance between the two populations it
    /// separates. The spread inside a reconstructed line should be near zero, and the gap to the
    /// next line should clear the tolerance that applied to it - reported as a ratio, because the
    /// tolerance is scaled per line and a raw gap in points cannot be compared with it.
    /// </para>
    /// </summary>
    private static object Separation()
    {
        using var document = PdfDocument.Open(Path_);
        var within = new List<double>();
        var safety = new List<double>();

        foreach (var page in document.GetPages())
        {
            var ordered = page.Letters
                .Where(letter => !string.IsNullOrWhiteSpace(letter.Value))
                .Select(letter => PdfGlyph.Of(letter, PdfFontEmbedding.ModeFor(document)))
                .OrderByDescending(letter => letter.Baseline)
                .ThenBy(letter => letter.Left)
                .ThenBy(letter => letter.Value, StringComparer.Ordinal)
                .ToArray();

            var grouped = PdfVisualLineBucket.Split(ordered, PdfVisualLineBucket.Of);
            foreach (var line in grouped)
                within.Add(line.Max(l => l.Baseline) - line.Min(l => l.Baseline));

            for (var index = 1; index < grouped.Count; index++)
            {
                var above = grouped[index - 1];
                var scale = above.Max(l => Math.Max(l.FontSize, l.Height));
                var tolerance = Math.Max(1.0, scale * PdfVisualLineBucket.BaselineTolerance);
                var gap = above.Min(l => l.Baseline) - grouped[index].Max(l => l.Baseline);
                safety.Add(gap / tolerance);
            }
        }

        return new
        {
            baselineSpreadWithinALine = new
            {
                max = Math.Round(within.Max(), 3),
                median = Math.Round(Median(within), 3),
            },
            // Gap to the next line, divided by the tolerance that line was judged with. Above 1
            // means the pair was never close to merging; the minimum is how close this document
            // came to the threshold anywhere.
            gapToNextLineInToleranceUnits = new
            {
                min = Math.Round(safety.Min(), 3),
                median = Math.Round(Median(safety), 3),
                pairsWithin25PercentOfTheThreshold = safety.Count(ratio => ratio < 1.25),
                pairs = safety.Count,
            },
        };
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    /// <summary>
    /// One reconstructed row, recovered from the segments it was divided into. Segments are emitted
    /// row by row and left to right, so a segment beginning to the right of the one before it on an
    /// overlapping band belongs to the same row.
    /// </summary>
    private static List<List<PdfLine>> Rows(IReadOnlyList<PdfLine> segments)
    {
        var rows = new List<List<PdfLine>>();
        foreach (var segment in segments)
        {
            var open = rows.Count > 0 ? rows[^1] : null;
            var beside = open is not null &&
                open[^1].Page == segment.Page &&
                segment.Left > open[^1].Right &&
                segment.Top > open[^1].Bottom && segment.Bottom < open[^1].Top;
            if (beside) open!.Add(segment);
            else rows.Add([segment]);
        }

        return rows;
    }

    /// <summary>Every horizontal gap inside a row, in line-heights, however the row was divided.</summary>
    private static IEnumerable<double> RowGaps(IReadOnlyList<PdfLine> row)
    {
        var scale = row.Max(segment => Math.Max(segment.FontSize, (segment.Top ?? 0) - (segment.Bottom ?? 0)));
        if (scale <= 0) yield break;

        foreach (var segment in row)
        {
            var spans = segment.Projection.SpanMap;
            for (var index = 1; index < spans.Count; index++)
                yield return (spans[index].Left - spans[index - 1].Right) / scale;
        }

        for (var index = 1; index < row.Count; index++)
            yield return (row[index].Left - row[index - 1].Right) / scale;
    }

    /// <summary>
    /// The whole corpus under the candidate, so the rule is judged on documents it was not designed
    /// against. Samples are taken deterministically, by file name and row order, and are evidence
    /// for adjudication rather than an answer.
    /// </summary>
    private static object CorpusCensus()
    {
        var pdfs = Directory
            .GetFiles(System.IO.Path.Combine(TestRepository.Root(), "todo10_8", "heading_corpus_100"),
                "*.pdf", SearchOption.AllDirectories)
            .OrderBy(System.IO.Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        int documents = 0, rowsTotal = 0, segmentsTotal = 0, unreadable = 0;
        int one = 0, two = 0, three = 0, riskRows = 0, riskSplit = 0, documentsWithDivided = 0;
        var divided = new List<object>();
        var wideButWhole = new List<object>();

        foreach (var path in pdfs)
        {
            IReadOnlyList<PdfLine> segments;
            try
            {
                using var document = PdfDocument.Open(path);
                segments = PdfLineExtraction.ExtractLines(document);
            }
            catch (Exception)
            {
                // A document this parser cannot open says nothing about segmentation. It is counted
                // out rather than counted as clean.
                unreadable++;
                continue;
            }

            documents++;
            segmentsTotal += segments.Count;
            var rows = Rows(segments);
            rowsTotal += rows.Count;

            var name = System.IO.Path.GetFileName(path);
            var dividedHere = 0;
            var wideHere = 0;
            foreach (var row in rows)
            {
                if (row.Count == 1) one++;
                else if (row.Count == 2) two++;
                else three++;

                var wide = RowGaps(row).Any(gap => gap > 3.0);
                if (wide) riskRows++;

                if (row.Count > 1)
                {
                    dividedHere++;
                    if (wide) riskSplit++;
                    if (dividedHere <= 2) divided.Add(Case(name, row));
                }
                else if (wide && ++wideHere <= 2)
                {
                    wideButWhole.Add(Case(name, row));
                }
            }

            if (dividedHere > 0) documentsWithDivided++;
        }

        return new
        {
            documents,
            unreadableDocuments = unreadable,
            visualRows = rowsTotal,
            totalSegments = segmentsTotal,
            rowsWith1Segment = one,
            rowsWith2Segments = two,
            rowsWith3PlusSegments = three,
            documentsWithMultiSegmentRows = documentsWithDivided,
            riskRowsWithGapOver3LineHeights = riskRows,
            riskRowsActuallyDivided = riskSplit,
            note = "The two risk numbers are not the same measurement. The first counts rows with wide whitespace; the second counts rows the corridor rule divided.",
            dividedSamples = divided,
            wideButUndividedSamples = wideButWhole,
        };
    }

    private static object Case(string document, IReadOnlyList<PdfLine> row) => new
    {
        caseId = $"{document}|p{row[0].Page}|{row[0].Y:F1}",
        page = row[0].Page,
        rowText = string.Join("  ", row.Select(segment => segment.Text)),
        segments = row.Select(segment => new
        {
            text = segment.Text,
            left = Math.Round(segment.Left, 1),
            right = Math.Round(segment.Right, 1),
        }).ToArray(),
    };

    /// <summary>The row a case names, found again in its own document by page and height.</summary>
    private static List<PdfLine>? FindRow(string document, int page, double y)
    {
        var path = Directory
            .GetFiles(System.IO.Path.Combine(TestRepository.Root(), "todo10_8", "heading_corpus_100"),
                document, SearchOption.AllDirectories)
            .OrderBy(item => item, StringComparer.Ordinal)
            .FirstOrDefault();
        if (path is null) return null;

        using var pdf = PdfDocument.Open(path);
        var rows = Rows(PdfLineExtraction.ExtractLines(pdf));
        return rows.FirstOrDefault(row =>
            row[0].Page == page && Math.Abs(row[0].Y - y) < 0.05);
    }

    private static object[] Serialize(IReadOnlyList<PdfSourceOccurrenceBoundary.PdfPunctuationRow> rows) =>
        rows.Select(object (row) => new
        {
            mark = row.Mark,
            occurrences = row.Occurrences,
            sharingAVisualLineWithText = row.SharingAVisualLineWithText,
            immediatelyBeforeAGoldOccurrence = row.ImmediatelyBeforeAGoldOccurrence,
        }).ToArray();
}
