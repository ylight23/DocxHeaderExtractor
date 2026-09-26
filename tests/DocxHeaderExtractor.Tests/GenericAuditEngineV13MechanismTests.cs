using DocxHeaderExtractor.Tests.GenericAudit.V1_3;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// GENERIC_AUDIT_ENGINE_V1.3's new lexical shapes on constructed texts - shapes, never a list of headings - so each gap's
/// evidence is pinned apart from any document.
/// </summary>
public sealed class GenericAuditEngineV13MechanismTests
{
    [Theory]
    [InlineData("Unrestricted cash . . . . . . . . . . . .", true)]
    [InlineData("Total subscribed capital . . . . . 314", true)]
    [InlineData("A sentence that ends. Then another.", false)]
    [InlineData("Section 1.2.3", false)]
    public void Dot_leaders_run_to_a_figure(string text, bool leaders) => Assert.Equal(leaders, LexicalShape.HasDotLeaders(text));

    [Theory]
    [InlineData("As of June 30, 2025, unless otherwise indicated", true)]
    [InlineData("For the fiscal years ended June 30, 2025, June 30, 2024 and June 30, 2023", true)]
    [InlineData("June 30, 2025 and June 30, 2024", true)]
    [InlineData("June 30, 2025", true)]
    [InlineData("At June 30, 2025, the portfolio totaled $3 billion.", false)]
    [InlineData("Results from Lending Activities", false)]
    public void Period_lines_state_a_period_and_nothing_else(string text, bool period) => Assert.Equal(period, LexicalShape.IsPeriodLine(text));

    [Theory]
    [InlineData("SUMMARY STATEMENT OF LOANS (Continued)", "SUMMARY STATEMENT OF LOANS", true)]
    [InlineData("Revenue (Section III) and more text", "Revenue", false)]
    public void A_parenthetical_status_after_a_title_is_part_of_it(string text, string lead, bool suffix) =>
        Assert.Equal(suffix, LexicalShape.IsParentheticalSuffix(text, lead));

    [Theory]
    [InlineData("REPORTS JUNE 30, 2025", "REPORTS")]
    [InlineData("Annual Report March 31, 2024", "Annual Report")]
    [InlineData("June 30, 2025", null)]
    [InlineData("Capital Increases", null)]
    public void A_title_line_that_runs_into_a_date_gives_its_head(string text, string? head) => Assert.Equal(head, LexicalShape.BeforeTrailingDate(text));

    [Theory]
    [InlineData("Figure 14: Country Exposures as ofJune 30, 2025", true)]
    [InlineData("Liquidity Levels . . . . . 17", false)]
    [InlineData("Table 12", false)]
    public void A_line_that_ends_in_a_year_ends_in_a_date(string text, bool year) => Assert.Equal(year, LexicalShape.EndsWithYear(text));

    [Theory]
    [InlineData("and financial statements appearing elsewhere", true)]
    [InlineData("The above information is qualified", false)]
    public void A_lower_case_start_continues_the_line_above(string text, bool lower) => Assert.Equal(lower, LexicalShape.StartsLowerCase(text));
}
