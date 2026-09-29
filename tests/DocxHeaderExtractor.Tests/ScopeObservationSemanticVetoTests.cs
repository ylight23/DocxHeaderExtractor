using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using Xunit;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Authority precedence at the output boundary. The model decides whether something is a heading;
/// the harness decides whether it can be anchored in the source. A parser scope sits on the first
/// question, so it is not consulted at all.
/// <para>
/// Measured cost of the old behaviour: on DOC-0256 three headings the model had identified
/// correctly — valid alias, exact verbatim text, successful binding — disappeared because they sat
/// inside a table and the domain detector labelled them table titles.
/// </para>
/// </summary>
public class ScopeObservationSemanticVetoTests
{
    private static PdfFinalHeading Heading(
        string role = "SectionHeading",
        string scope = "document_body",
        string text = "DAY 2: WEDNESDAY, NOVEMBER 1, 2023",
        string validationDecision = "requires_review",
        DocxSourceAnchor? anchor = null,
        bool withoutAnchor = false,
        string groundingStatus = "grounded",
        string hierarchyStatus = "resolved") =>
        new("h1", withoutAnchor ? null : anchor ?? new DocxSourceAnchor(123, "body[1]/p[123]", new DocxTextSpan(0, text.Length)), null,
            text, role, scope, validationDecision, groundingStatus,
            1, null, hierarchyStatus, null, null, "model", text);

    [Fact]
    public void A_model_heading_inside_a_table_like_region_survives()
    {
        var decision = PdfOutputDecisions.Decide(Heading(scope: "table"));

        Assert.True(decision.Emit);
    }

    [Fact]
    public void A_scope_observation_cannot_silently_veto_the_model()
    {
        var decision = PdfOutputDecisions.Decide(
            Heading(scope: "appendix_table"));

        Assert.True(decision.Emit);
        Assert.DoesNotContain(decision.Reasons, reason => reason.StartsWith("scope_", StringComparison.Ordinal));
    }

    [Fact]
    public void A_heading_with_no_source_anchor_is_still_rejected()
    {
        // Source validity, not meaning: without an anchor it cannot be shown as an occurrence.
        var decision = PdfOutputDecisions.Decide(
            Heading(withoutAnchor: true, groundingStatus: "ungrounded"));

        Assert.False(decision.Emit);
        Assert.Contains("ungrounded", decision.Reasons);
    }

    [Fact]
    public void A_heading_with_empty_source_text_is_still_rejected()
    {
        var decision = PdfOutputDecisions.Decide(Heading(text: "   "));

        Assert.False(decision.Emit);
        Assert.Contains("empty_source_text", decision.Reasons);
    }

    [Fact]
    public void A_heading_that_failed_validation_is_still_rejected()
    {
        var decision = PdfOutputDecisions.Decide(
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
        var decisions = PdfOutputDecisions.Decide(
            new PdfFinalStructure("doc", "validated", "final",
                new PdfFinalStructureCounters(0, 0, 0, 0, 0, 0, 0), []));

        Assert.Empty(decisions);
    }

    [Fact]
    public void A_parser_scope_is_not_a_decision_reason()
    {
        var decision = PdfOutputDecisions.Decide(
            Heading(scope: "appendix_table"));

        Assert.True(decision.Emit);
        Assert.DoesNotContain(decision.Reasons, reason => reason.StartsWith("scope_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DAY 2: WEDNESDAY, NOVEMBER 1, 2023")]
    [InlineData("DAY 3: THURSDAY, NOVEMBER 2, 2023")]
    [InlineData("DAY 4: FRIDAY, NOVEMBER 3, 2023")]
    public void The_measured_regression_survives(string text)
    {
        // Shape taken from the real loss: scope "table", model proposal and binding both valid.
        var decision = PdfOutputDecisions.Decide(
            Heading(text: text, scope: "table"));

        Assert.True(decision.Emit);
    }
}
