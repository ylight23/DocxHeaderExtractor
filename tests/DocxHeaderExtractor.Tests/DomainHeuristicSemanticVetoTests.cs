using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using Xunit;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Authority precedence at the output boundary. The model decides whether something is a heading;
/// the harness decides whether it can be anchored in the source. A role or scope heuristic sits on
/// the first question, so it may record a disagreement but never suppress the heading.
/// <para>
/// Measured cost of the old behaviour: on DOC-0256 three headings the model had identified
/// correctly — valid alias, exact verbatim text, successful binding — disappeared because they sat
/// inside a table and the domain detector labelled them table titles.
/// </para>
/// </summary>
public class DomainHeuristicSemanticVetoTests
{
    private static PdfFinalHeading Heading(
        string role = "SectionHeading",
        string scope = "document_body",
        bool domainExclusion = false,
        string text = "DAY 2: WEDNESDAY, NOVEMBER 1, 2023",
        string validationDecision = "requires_review",
        DocxSourceAnchor? anchor = null,
        bool withoutAnchor = false,
        string groundingStatus = "grounded",
        string hierarchyStatus = "resolved") =>
        new("h1", withoutAnchor ? null : anchor ?? new DocxSourceAnchor(123, "body[1]/p[123]", new DocxTextSpan(0, text.Length)), null,
            text, role, scope, validationDecision, groundingStatus,
            1, null, hierarchyStatus, null, null, "model", text)
        { DomainExclusionProposed = domainExclusion };

    [Fact]
    public void A_model_heading_inside_a_table_like_region_survives()
    {
        var decision = PdfOutputDecisionPolicy.Decide(Heading(scope: "table", domainExclusion: true));

        Assert.True(decision.Emit);
    }

    [Fact]
    public void A_table_title_heuristic_cannot_silently_veto_the_model()
    {
        var decision = PdfOutputDecisionPolicy.Decide(
            Heading(role: "TableTitle", domainExclusion: true));

        Assert.True(decision.Emit);
        Assert.Contains(decision.Reasons, reason => reason.StartsWith("domain_role_disagreement:"));
    }

    [Fact]
    public void A_heading_with_no_source_anchor_is_still_rejected()
    {
        // Source validity, not meaning: without an anchor it cannot be shown as an occurrence.
        var decision = PdfOutputDecisionPolicy.Decide(
            Heading(withoutAnchor: true, groundingStatus: "ungrounded"));

        Assert.False(decision.Emit);
        Assert.Contains("ungrounded", decision.Reasons);
    }

    [Fact]
    public void A_heading_with_empty_source_text_is_still_rejected()
    {
        var decision = PdfOutputDecisionPolicy.Decide(Heading(text: "   "));

        Assert.False(decision.Emit);
        Assert.Contains("empty_source_text", decision.Reasons);
    }

    [Fact]
    public void A_heading_that_failed_validation_is_still_rejected()
    {
        var decision = PdfOutputDecisionPolicy.Decide(
            Heading(validationDecision: "binding_failed"));

        Assert.False(decision.Emit);
        Assert.Contains(decision.Reasons, reason => reason.StartsWith("unexpected_validation_decision:"));
    }

    [Fact]
    public void The_heuristic_cannot_promote_anything_the_model_did_not_propose()
    {
        // This policy only ever reads facts the model proposed and the binder anchored. Handing it
        // an empty structure must produce no decisions at all: there is no path by which a domain
        // heuristic invents a heading.
        var decisions = PdfOutputDecisionPolicy.Decide(
            new PdfFinalStructure("doc", "validated", "final",
                new PdfFinalStructureCounters(0, 0, 0, 0, 0, 0, 0), []));

        Assert.Empty(decisions);
    }

    [Fact]
    public void A_heuristic_disagreement_stays_visible_on_the_decision()
    {
        var decision = PdfOutputDecisionPolicy.Decide(
            Heading(role: "TableTitle", scope: "appendix_table", domainExclusion: true));

        Assert.True(decision.Emit);
        Assert.Contains(decision.Reasons, reason => reason.StartsWith("domain_role_disagreement:"));
        Assert.Contains(decision.Reasons, reason => reason.StartsWith("scope_disagreement:"));
    }

    [Theory]
    [InlineData("DAY 2: WEDNESDAY, NOVEMBER 1, 2023")]
    [InlineData("DAY 3: THURSDAY, NOVEMBER 2, 2023")]
    [InlineData("DAY 4: FRIDAY, NOVEMBER 3, 2023")]
    public void The_measured_regression_survives(string text)
    {
        // Shape taken from the real loss: scope "table", role classified as a table title, model
        // proposal and binding both valid.
        var decision = PdfOutputDecisionPolicy.Decide(
            Heading(text: text, role: "TableTitle", scope: "table", domainExclusion: true));

        Assert.True(decision.Emit);
    }
}
