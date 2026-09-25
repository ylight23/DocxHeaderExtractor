using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// GENERIC_AUDIT_ENGINE_V1.1, one synthetic page per gap: the mechanisms pinned by construction, on text
/// that belongs to no corpus document.
/// </summary>
public sealed class GenericAuditEngineV11MechanismTests
{
    private const double Body = 10;

    /// <summary>A PDF line: page 1, a row, left/right edges, a baseline, bold or not, optional bold lead.</summary>
    private static SourceOccurrence Line(int ordinal, int row, int segment, int segments, double left, double right, double y, string text,
        bool bold = false, bool italic = false, double size = Body, string? lead = null) =>
        new($"L{row:0000}:S{segment}", $"line-{ordinal}", ordinal, text, "PDF", 1, size, bold, italic,
            false, false, null, null, null, 0, false, segments, segment, false, 0.5)
        {
            Row = row,
            Left = left,
            Right = right,
            Y = y,
            BoldLead = lead,
        };

    private static SourceEvidenceProfile Profile(params SourceOccurrence[] occurrences) => new()
    {
        Media = "PDF",
        Occurrences = occurrences,
        BodyFontSize = Body,
        BodyBold = false,
        BodyItalic = false,
        BodyStyle = null,
        PageCount = 1,
        Repetition = occurrences.GroupBy(o => LexicalShape.RepetitionKey(o.Text))
            .ToDictionary(g => g.Key, g => (g.Count(), 1, 0.0), StringComparer.Ordinal),
        PointedTo = new HashSet<string>(StringComparer.Ordinal),
    };

    private const string Prose = "the river carries silt downstream every spring and the banks move a little each year";

    [Fact]
    public void A_margin_label_is_followed_down_its_column_past_the_other_column()
    {
        // Gap 1 + 5: a label wrapped over three margin lines, a body line of the right column on each row.
        var hypotheses = SemanticAuditEngine.Propose(Profile(
            Line(0, 0, 0, 2, 50, 118, 700, "7. Ownership of", bold: true),
            Line(1, 0, 1, 2, 150, 520, 700, Prose),
            Line(2, 1, 0, 2, 64, 120, 687, "Plant and", bold: true),
            Line(3, 1, 1, 2, 150, 520, 687, Prose),
            Line(4, 2, 0, 2, 64, 104, 674, "Materials", bold: true),
            Line(5, 2, 1, 2, 150, 520, 674, Prose),
            Line(6, 3, 1, 1, 150, 520, 661, Prose)));

        var label = Assert.Single(hypotheses, h => h.ProposedIsHeading == "TRUE");
        Assert.Equal(["L0000:S0", "L0001:S0", "L0002:S0"], label.Parts.Select(p => p.Alias));
        Assert.Equal("7. Ownership of Plant and Materials", label.Text);
    }

    [Fact]
    public void Two_stacked_headings_in_one_column_stay_two_claims()
    {
        // Gap 1: the first line stops well short of the column, so the next is not its wrap.
        var hypotheses = SemanticAuditEngine.Propose(Profile(
            Line(0, 0, 0, 1, 50, 200, 700, "Annual Summary", bold: true, size: 14),
            Line(1, 1, 0, 1, 50, 190, 683, "Scope of Review", bold: true, size: 14),
            Line(2, 2, 0, 1, 50, 540, 660, Prose),
            Line(3, 3, 0, 1, 50, 540, 647, Prose)));

        Assert.Equal(["Annual Summary", "Scope of Review"], hypotheses.Where(h => h.Prominence >= 1000).Select(h => h.Text));
    }

    [Fact]
    public void A_run_in_label_is_its_lead_and_the_body_it_shares_a_line_with_is_not()
    {
        // Gap 2: a numbered bold lead, then body on the same line.
        var hypotheses = SemanticAuditEngine.Propose(Profile(
            Line(0, 0, 0, 1, 50, 540, 700, "4. Warranty Period The period runs from the date of handover", lead: "4. Warranty Period"),
            Line(1, 1, 0, 1, 50, 540, 687, Prose),
            Line(2, 2, 0, 1, 50, 540, 674, "an unnumbered Emphasis inside a sentence continues here as ordinary text", lead: "an unnumbered Emphasis")));

        var label = Assert.Single(hypotheses, h => h.ProposedIsHeading == "TRUE");
        Assert.Equal("4. Warranty Period", Assert.Single(label.Parts).Verbatim);
        Assert.Equal("FALSE", Assert.Single(hypotheses, h => h.Text == "an unnumbered Emphasis").ProposedIsHeading);
    }

    [Fact]
    public void A_parent_opener_followed_by_its_first_child_opens_its_region()
    {
        // Gap 3: same setting, but the next label starts a different enumeration at its first item.
        var hypotheses = SemanticAuditEngine.Propose(Profile(
            Line(0, 0, 0, 1, 50, 200, 700, "B. Financial Terms", bold: true, size: 14),
            Line(1, 1, 0, 1, 50, 200, 680, "1. Payment Schedule", bold: true, size: 14),
            Line(2, 2, 0, 1, 50, 540, 660, Prose)));

        Assert.Equal("TRUE", Assert.Single(hypotheses, h => h.Text == "B. Financial Terms").ProposedIsHeading);
    }

    [Fact]
    public void Lines_inside_a_multi_line_bracket_are_instructions()
    {
        // Gap 4: a bracket opened on one line and closed three lines later; the middle lines look like labels.
        var hypotheses = SemanticAuditEngine.Propose(Profile(
            Line(0, 0, 0, 1, 50, 540, 700, "[Note to the preparer: complete this section", italic: true),
            Line(1, 1, 0, 1, 50, 300, 687, "Delivery Terms", italic: true, bold: true),
            Line(2, 2, 0, 1, 50, 300, 674, "Site Access", bold: false, italic: true),
            Line(3, 3, 0, 1, 50, 540, 661, "before issuing the document.]", italic: true),
            Line(4, 4, 0, 1, 50, 540, 640, Prose)));

        Assert.Equal("FALSE", Assert.Single(hypotheses, h => h.Text == "Site Access").ProposedIsHeading);
        Assert.Contains(hypotheses.Single(h => h.Text == "Site Access").Evidence, e => e.Contains("bracketed instruction", StringComparison.Ordinal));
    }

    [Fact]
    public void A_colon_ended_cell_beside_a_value_is_a_field_label()
    {
        // Gap 5: two segments on a row, the first a bold colon-ended label.
        var hypotheses = SemanticAuditEngine.Propose(Profile(
            Line(0, 0, 0, 2, 50, 140, 700, "Contract number:", bold: true),
            Line(1, 0, 1, 2, 160, 300, 700, "to be assigned"),
            Line(2, 1, 0, 1, 50, 540, 680, Prose)));

        var cell = Assert.Single(hypotheses, h => h.Text == "Contract number:");
        Assert.Equal("FALSE", cell.ProposedIsHeading);
        Assert.Equal(["FIELD_LABEL"], cell.OccurrenceRoles);
    }

    [Fact]
    public void A_page_initial_title_over_fill_in_fields_names_a_form()
    {
        // Gap 6: the page's first line, large, and a region of placeholders.
        var hypotheses = SemanticAuditEngine.Propose(Profile(
            Line(0, 0, 0, 1, 150, 400, 760, "Bank Guarantee", bold: true, size: 18),
            Line(1, 1, 0, 1, 50, 540, 730, "Beneficiary: [insert name of the beneficiary]"),
            Line(2, 2, 0, 1, 50, 540, 717, "Date: [insert date of issue]"),
            Line(3, 3, 0, 1, 50, 540, 704, Prose)));

        var title = Assert.Single(hypotheses, h => h.Text == "Bank Guarantee");
        Assert.Equal("TRUE", title.ProposedIsHeading);
        Assert.Equal("FORM", title.Scope);
        Assert.Equal("IDENTITY", title.PrimaryFunction);
    }
}
