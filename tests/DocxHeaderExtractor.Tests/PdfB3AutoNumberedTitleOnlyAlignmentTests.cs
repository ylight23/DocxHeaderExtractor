using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// B3: unit-level lock for <see cref="PdfLayoutEvidenceOutline.FindAutoNumberedTitleOnlyMatch"/>
/// against every eligibility condition and every negative control named before implementation - never
/// a general strip-and-fuzzy-match, never a fallback to a non-<c>NumberingId</c> paragraph, never
/// resolving an ambiguous duplicate. No provider call: every scenario is synthetic.
/// </summary>
public sealed class PdfB3AutoNumberedTitleOnlyAlignmentTests
{
    [Fact]
    public void RecognizedMarkerAndUniqueNumberedTitleAnchorsSuccessfully()
    {
        var paragraphs = Paragraphs(("Approximate the derivative of a function", numbered: true));
        var block = Block("9.4. Approximate the derivative of a function");

        var match = PdfLayoutEvidenceOutline.FindAutoNumberedTitleOnlyMatch(paragraphs, block, headingSpan: null);

        Assert.NotNull(match);
        Assert.Equal(0, match!.Value.Paragraph.Index);
    }

    [Fact]
    public void NoMarkerRecognizedAbstains()
    {
        var paragraphs = Paragraphs(("Approximate the derivative of a function", numbered: true));
        var block = Block("Approximate the derivative of a function"); // no leading marker at all

        Assert.Null(PdfLayoutEvidenceOutline.FindAutoNumberedTitleOnlyMatch(paragraphs, block, headingSpan: null));
    }

    [Fact]
    public void MarkerConsumesTheWholeTextAbstainsOnEmptyTitle()
    {
        var paragraphs = Paragraphs(("x", numbered: true));
        var block = Block("9."); // marker only, nothing left to be a title

        Assert.Null(PdfLayoutEvidenceOutline.FindAutoNumberedTitleOnlyMatch(paragraphs, block, headingSpan: null));
    }

    [Fact]
    public void TitleTooShortAfterMarkerStripAbstains()
    {
        var paragraphs = Paragraphs(("ab", numbered: true));
        var block = Block("9. ab"); // remaining title below the length-4 floor

        Assert.Null(PdfLayoutEvidenceOutline.FindAutoNumberedTitleOnlyMatch(paragraphs, block, headingSpan: null));
    }

    [Fact]
    public void NeverFallsBackToAParagraphWithoutNumberingId()
    {
        // Manual-numbering control: a paragraph literally typed as "9.4 Some Heading" (no NumberingId)
        // must never be accepted just because its text also matches. Only the NumberingId paragraph
        // whose OWN text is title-only ("Some Heading", auto-numbered) may anchor.
        var paragraphs = Paragraphs(
            ("9.4. Some Heading", numbered: false),
            ("Some Heading", numbered: true));
        var block = Block("9.4. Some Heading");

        var match = PdfLayoutEvidenceOutline.FindAutoNumberedTitleOnlyMatch(paragraphs, block, headingSpan: null);

        Assert.NotNull(match);
        Assert.Equal(1, match!.Value.Paragraph.Index); // the NumberingId paragraph, not the manually-typed one
    }

    [Fact]
    public void ZeroNumberingIdMatchesAbstains()
    {
        // The title only exists in a manually-typed paragraph - condition 4 (has NumberingId) fails,
        // and this must not silently accept the non-numbered paragraph as a substitute.
        var paragraphs = Paragraphs(("Some Heading", numbered: false));
        var block = Block("9.4. Some Heading");

        Assert.Null(PdfLayoutEvidenceOutline.FindAutoNumberedTitleOnlyMatch(paragraphs, block, headingSpan: null));
    }

    [Fact]
    public void DuplicateTitleAcrossTwoNumberingIdParagraphsAbstainsRatherThanResolvingTheFirst()
    {
        // The exact negative evidence the collateral check found on 057: a recurring section title
        // (e.g. "Linear regression" repeated across chapters) must never be resolved by picking the
        // first match.
        var paragraphs = Paragraphs(
            ("Linear regression", numbered: true),
            ("Linear regression", numbered: true));
        var block = Block("3.1 Linear regression");

        Assert.Null(PdfLayoutEvidenceOutline.FindAutoNumberedTitleOnlyMatch(paragraphs, block, headingSpan: null));
    }

    [Fact]
    public void BodyParagraphThatLooksNumberedButIsNotHeadingStyleStillEligibleOnlyByNumberingId()
    {
        // A body paragraph carrying NumberingId (an ordinary numbered list item, not a heading) whose
        // text happens to equal the title is indistinguishable from a real heading by this check alone
        // - that risk is bounded elsewhere (candidate must already be HeadingTopic-validated to reach
        // this code at all), not something this unit can or should adjudicate. This test only confirms
        // the mechanism does what it claims: match by NumberingId + exact title, nothing more.
        var paragraphs = Paragraphs(("Cleaning the imported data set", numbered: true));
        var block = Block("12.3. Cleaning the imported data set");

        var match = PdfLayoutEvidenceOutline.FindAutoNumberedTitleOnlyMatch(paragraphs, block, headingSpan: null);
        Assert.NotNull(match);
    }

    [Fact]
    public void NestedMultiLevelMarkerStillResolvesViaTheRecognizedPrefix()
    {
        // "10.0.1" in PDF-extracted text often carries a stray inserted space ("10.0. 1") from kerning
        // extraction; the production marker parser recognizes the "10.0." prefix, leaving "1 The Role
        // of Sample Size" as the title - matching the divergence taxonomy's own finding on 057.
        var paragraphs = Paragraphs(("1 The Role of Sample Size", numbered: true));
        var block = Block("10.0. 1 The Role of Sample Size");

        var match = PdfLayoutEvidenceOutline.FindAutoNumberedTitleOnlyMatch(paragraphs, block, headingSpan: null);
        Assert.NotNull(match);
    }

    /// <summary>
    /// A real, pre-existing characteristic of the shared marker parser this fix must use as-is: its
    /// decimal regex requires a terminating "." or ")" immediately after the full numeric value, so
    /// "9.4 Title" (no period before the space) only recognizes "9." as the marker, leaving "4 Title"
    /// as the stray-prefixed remainder - which then correctly fails to match a DOCX paragraph reading
    /// just "Title". This is not worked around with a new regex; it is measured and reported by
    /// <see cref="PdfB3RealTargetValidationProbe"/> as the real recovery rate this limitation produces
    /// on 057's actual candidates, rather than assumed away.
    /// </summary>
    [Fact]
    public void UnterminatedMultiDigitMarkerIsOnlyPartlyRecognizedAndCorrectlyAbstains()
    {
        var paragraphs = Paragraphs(("Approximate the derivative of a function", numbered: true));
        var block = Block("9.4 Approximate the derivative of a function"); // no terminating "." before the space

        Assert.Null(PdfLayoutEvidenceOutline.FindAutoNumberedTitleOnlyMatch(paragraphs, block, headingSpan: null));
    }

    [Fact]
    public void MarkerParseFailureOnPlainProseAbstains()
    {
        var paragraphs = Paragraphs(("This is just prose with no marker", numbered: true));
        var block = Block("This is just prose with no marker");

        Assert.Null(PdfLayoutEvidenceOutline.FindAutoNumberedTitleOnlyMatch(paragraphs, block, headingSpan: null));
    }

    private static PdfSemanticBlock Block(string text) =>
        new("b1", [], new PdfStyleKey(12, "Arial", "none"), 1, 100, 90, 10, 100, text);

    private static IReadOnlyList<PdfLayoutEvidenceOutline.CanonParagraph> Paragraphs(params (string Text, bool numbered)[] items) =>
        items.Select((item, index) => new PdfLayoutEvidenceOutline.CanonParagraph(
            new SlimParagraph { Index = index, Text = item.Text, NumberingId = item.numbered ? 1 : null },
            PdfLayoutEvidenceOutline.CanonicalMap(item.Text)))
            .ToArray();
}
