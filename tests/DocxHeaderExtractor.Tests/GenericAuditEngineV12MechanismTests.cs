using DocxHeaderExtractor.Tests.GenericAudit.V1_2;

namespace DocxHeaderExtractor.Tests;

/// <summary>GENERIC_AUDIT_ENGINE_V1.2, one synthetic case per new gap (7-12), on text that belongs to no corpus document.</summary>
public sealed class GenericAuditEngineV12MechanismTests
{
    private const double Body = 10;

    private static SourceOccurrence Line(int ordinal, int page, int row, int segment, int segments, double left, double right, double y,
        string text, bool bold = false, double size = Body, double band = 0.5, bool rowHasFigures = false) =>
        new($"L{row:0000}:S{segment}", $"line-{ordinal}", ordinal, text, "PDF", page, size, bold, false,
            false, false, null, null, null, 0, false, segments, segment, rowHasFigures, band)
        {
            Row = row,
            Left = left,
            Right = right,
            Y = y,
        };

    private static SourceEvidenceProfile Profile(int pages, params SourceOccurrence[] occurrences) => new()
    {
        Media = "PDF",
        Occurrences = occurrences,
        BodyFontSize = Body,
        BodyBold = false,
        BodyItalic = false,
        BodyStyle = null,
        PageCount = pages,
        Repetition = occurrences.GroupBy(o => LexicalShape.RepetitionKey(o.Text)).ToDictionary(
            g => g.Key,
            g => (g.Count(), g.Select(o => o.Page ?? -1).Distinct().Count(), g.Count(o => o.PageBand is <= 0.06 or >= 0.94) / (double)g.Count()),
            StringComparer.Ordinal),
        PointedTo = new HashSet<string>(StringComparer.Ordinal),
    };

    private const string Prose = "the river carries silt downstream every spring and the banks move a little each year";

    [Fact]
    public void A_large_title_repeated_at_page_tops_is_a_title_and_a_small_one_is_furniture()
    {
        // Gap 7: the same 16pt title and the same 9pt header top three pages.
        var lines = new List<SourceOccurrence>();
        var row = 0;
        for (var page = 1; page <= 3; page++)
        {
            lines.Add(Line(lines.Count, page, row++, 0, 1, 72, 300, 780, "Quarterly Ledger Report", size: 9, band: 0.01));
            lines.Add(Line(lines.Count, page, row++, 0, 1, 72, 400, 750, page == 1 ? "STATEMENT OF RESERVES" : "STATEMENT OF RESERVES (CONTINUED)", size: 16, band: 0.03));
            lines.Add(Line(lines.Count, page, row++, 0, 1, 72, 540, 700, Prose, band: 0.2));
        }
        var hypotheses = SemanticAuditEngine.Propose(Profile(3, lines.ToArray()));

        Assert.All(hypotheses.Where(h => h.Text == "Quarterly Ledger Report"), h => Assert.Equal(["PAGE_FURNITURE"], h.OccurrenceRoles));
        Assert.All(hypotheses.Where(h => h.Text.StartsWith("STATEMENT OF RESERVES", StringComparison.Ordinal)),
            h => Assert.DoesNotContain("PAGE_FURNITURE", h.OccurrenceRoles));
    }

    [Fact]
    public void An_equally_set_later_occurrence_is_a_repeat_not_a_pointer_target()
    {
        // Gap 8 (B1): three titles, each repeated later at the same setting, are not contents entries.
        var lines = new List<SourceOccurrence>();
        var row = 0;
        foreach (var t in new[] { "Audit Opinion", "Basis for Opinion", "Key Audit Matters" })
            lines.Add(Line(lines.Count, 1, row++, 0, 1, 72, 300, 700 - row * 30, t, bold: true, size: 14));
        lines.Add(Line(lines.Count, 1, row++, 0, 1, 72, 540, 500, Prose));
        foreach (var t in new[] { "Audit Opinion", "Basis for Opinion", "Key Audit Matters" })
        {
            lines.Add(Line(lines.Count, 2, row++, 0, 1, 72, 300, 700 - row * 5, t, bold: true, size: 14));
            lines.Add(Line(lines.Count, 2, row++, 0, 1, 72, 540, 690 - row * 5, Prose));
        }
        var hypotheses = SemanticAuditEngine.Propose(Profile(2, lines.ToArray()));

        Assert.DoesNotContain(hypotheses, h => h.OccurrenceRoles.Contains("NAVIGATION"));
    }

    [Fact]
    public void A_chart_panel_beside_text_is_not_a_column_of_headings()
    {
        // Gap 9: a bold panel title in the right half, over chart values, beside a heading and its prose.
        var hypotheses = SemanticAuditEngine.Propose(Profile(1,
            Line(0, 1, 0, 0, 2, 72, 200, 700, "Funding Overview", bold: true, size: 12),
            Line(1, 1, 0, 1, 2, 330, 430, 700, "Net Issuance", bold: true),
            Line(2, 1, 1, 0, 1, 330, 360, 685, "120"),
            Line(3, 1, 2, 0, 1, 330, 360, 670, "95"),
            Line(4, 1, 3, 0, 1, 72, 540, 600, Prose)));

        Assert.Equal("TRUE", Assert.Single(hypotheses, h => h.Text == "Funding Overview").ProposedIsHeading);
        Assert.NotEqual("TRUE", hypotheses.Single(h => h.Text == "Net Issuance").ProposedIsHeading);
    }

    [Fact]
    public void Lines_under_a_numbered_caption_belong_to_its_figure()
    {
        // Gap 10: a caption, its second line, a chart title set large, then prose and a margin heading.
        var hypotheses = SemanticAuditEngine.Propose(Profile(1,
            Line(0, 1, 0, 0, 1, 72, 300, 700, "Figure 4: Reserve Composition", bold: true),
            Line(1, 1, 1, 0, 1, 72, 200, 688, "as of the reporting date", bold: true),
            Line(2, 1, 2, 0, 1, 200, 380, 660, "Top Holdings", bold: true, size: 13),
            Line(3, 1, 3, 0, 1, 72, 540, 600, Prose),
            Line(4, 1, 4, 0, 1, 72, 250, 560, "Liquidity Position", bold: true, size: 12),
            Line(5, 1, 5, 0, 1, 72, 540, 540, Prose)));

        Assert.Equal("FALSE", hypotheses.Single(h => h.Text == "as of the reporting date").ProposedIsHeading);
        Assert.Equal("FALSE", hypotheses.Single(h => h.Text == "Top Holdings").ProposedIsHeading);
        Assert.Equal("TRUE", hypotheses.Single(h => h.Text == "Liquidity Position").ProposedIsHeading);
    }

    [Fact]
    public void A_title_broken_by_design_stays_one_claim()
    {
        // Gap 11: a line that is only "and", and a line ending in "OF", do not end a title that would have fitted.
        var hypotheses = SemanticAuditEngine.Propose(Profile(1,
            Line(0, 1, 0, 0, 1, 72, 250, 700, "SCHEDULE OF", size: 16),
            Line(1, 1, 1, 0, 1, 72, 260, 682, "MEMBER RESERVES", size: 16),
            Line(2, 1, 2, 0, 1, 72, 540, 640, Prose)));

        Assert.Contains(hypotheses, h => h.Text == "SCHEDULE OF MEMBER RESERVES" && h.Parts.Length == 2);
    }

    [Fact]
    public void A_contents_line_with_its_page_number_is_complete()
    {
        // Gap 12: a group label over two contents lines whose rows carry page numbers.
        var hypotheses = SemanticAuditEngine.Propose(Profile(1,
            Line(0, 1, 0, 0, 1, 72, 200, 700, "Supplementary Tables", bold: true),
            Line(1, 1, 1, 0, 2, 90, 250, 686, "Summary of Reserves", bold: true, rowHasFigures: true),
            Line(2, 1, 1, 1, 2, 520, 530, 686, "12", bold: true),
            Line(3, 1, 2, 0, 2, 90, 280, 672, "Schedule of Members", bold: true, rowHasFigures: true),
            Line(4, 1, 2, 1, 2, 520, 530, 672, "14", bold: true)));

        Assert.Single(hypotheses.Single(h => h.Text.StartsWith("Supplementary Tables", StringComparison.Ordinal)).Parts);
    }
}
